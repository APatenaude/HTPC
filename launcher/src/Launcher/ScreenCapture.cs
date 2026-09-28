using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// The Home menu's backdrop: the screen as it is under the menu (the app), 1920 wide (the UI's
/// size), as a JPEG the page loads. Called off the UI thread; one capture at a time.
///
/// Desktop Duplication (DXGI) first: the GPU copies the frame and halves it (a mip level), and
/// only that small copy is read back, 20-60 ms on the N97 for its 4K screen, the JPEG included.
/// A GDI BitBlt of the screen took 100-190 ms for the 4K read alone (reading from the GPU's
/// memory is that slow); with the HALFTONE StretchBlt and the JPEG on the UI thread it was
/// 230-1700 ms over Twitch, 3.6-5 s at worst, and the menu waited. GDI stays as the fallback
/// (no duplication for this screen, a rotated screen, a lost device, a UAC prompt).
///
/// Both see every window on screen, layered ones too: the launcher's own layers over apps
/// (brightness, alerts, volume) are kept out of the picture with LeaveOut, or the backdrop would
/// carry them (the brightness twice, under the brightness layer itself).
/// </summary>
static unsafe class ScreenCapture
{
    /// <summary>The backdrop's width: the UI's 1920x1080 stage.</summary>
    public const int Width = 1920;

    /// <summary>A saved capture: the file, how it was made (for the log), how long it took.</summary>
    public sealed record Shot(string File, string How, long Milliseconds);

    static readonly object Gate = new();
    static Duplicator? duplicator;               // kept: creating the device takes 60-100 ms
    static long noDuplicationUntil;              // after a failure to set it up: GDI for a while
    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>
    /// Keeps one of the launcher's windows out of every screen capture, this one included
    /// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004 and later). It still shows on the TV.
    /// </summary>
    public static void LeaveOut(Form form)
    {
        void Apply()
        {
            if (!SetWindowDisplayAffinity(form.Handle, WDA_EXCLUDEFROMCAPTURE))
                Log.Warn($"{form.GetType().Name} is not left out of screen captures (error {Marshal.GetLastWin32Error()})");
        }
        if (form.IsHandleCreated) Apply();
        form.HandleCreated += (_, _) => Apply();
    }

    /// <summary>
    /// At start, on a worker thread: a whole capture into dir, thrown away, so the first Home is
    /// quick too. The device alone was not enough: the first Home after a restart still took
    /// 1452 ms to capture on the box (the duplication, the GPU's mip shader, the JPEG encoder and
    /// this code's first run), the next ones 60-300 ms.
    /// </summary>
    public static void Prepare(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "warm-up.jpg");
            var shot = Save(path);
            File.Delete(path);
            Log.Info($"Screen capture ready: a first capture in {shot.Milliseconds} ms ({shot.How})");
        }
        catch (Exception e) { Log.Warn($"Screen capture: the first capture failed ({e.Message})"); }
    }

    /// <summary>Captures the primary screen into a JPEG at path (1920 wide). Throws when neither way works.</summary>
    public static Shot Save(string path)
    {
        lock (Gate)
        {
            var clock = Stopwatch.StartNew();
            var screen = Screen.PrimaryScreen!.Bounds;
            var size = TargetSize(screen.Size);
            string how;
            try
            {
                if (Environment.TickCount64 < noDuplicationUntil) throw new CaptureException("off for a minute after a failure");
                var device = duplicator is null ? "device made, " : "";
                var steps = Duplication(screen).Save(size, path);
                // Where the time went, when there was much of it (the log is read on the box).
                how = clock.ElapsedMilliseconds > 200 ? $"duplication: {device}{steps}" : "duplication";
            }
            catch (Exception e)
            {
                // The device may be gone (a driver update, a GPU reset): made again next time.
                if (e is not CaptureException { KeepDevice: true }) { duplicator?.Dispose(); duplicator = null; }
                if (e is CaptureException { Setup: true }) noDuplicationUntil = Environment.TickCount64 + 60_000;
                SaveGdi(screen, size, path);
                how = $"GDI, no duplication: {e.Message}";
            }
            return new Shot(path, how, clock.ElapsedMilliseconds);
        }
    }

    /// <summary>1920 wide at the screen's proportions; a screen narrower than that keeps its size.</summary>
    public static Size TargetSize(Size screen) =>
        screen.Width <= Width ? screen : new Size(Width, (int)Math.Round((double)screen.Height * Width / screen.Width));

    static Duplicator Duplication(Rectangle screen)
    {
        if (duplicator is { } d && d.Bounds == screen) return d;
        duplicator?.Dispose();
        duplicator = null;
        return duplicator = new Duplicator(screen);
    }

    // --- GDI, the fallback -------------------------------------------------------------------

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
    const int SRCCOPY = 0x00CC0020;

    // The whole screen into a 32-bit top-down DIB, then scaled down here (a HALFTONE StretchBlt
    // from the screen took twice as long).
    static void SaveGdi(Rectangle screen, Size size, string path)
    {
        var header = new BitmapInfoHeader { Size = 40, Width = screen.Width, Height = -screen.Height, Planes = 1, BitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var dib = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(dc); throw new InvalidOperationException("GDI: no bitmap for the screen"); }
        var old = SelectObject(dc, dib);
        try
        {
            var screenDc = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(dc, 0, 0, screen.Width, screen.Height, screenDc, screen.X, screen.Y, SRCCOPY)) throw new InvalidOperationException("GDI: BitBlt failed");
                GdiFlush();
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
            SaveScaled((byte*)bits, screen.Width, screen.Height, screen.Width * 4, size, path);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    // --- Scaling and the JPEG (checked in LauncherTests) ------------------------------------------

    /// <summary>32-bit pixels scaled to size (when they are not that size already) and saved as a JPEG.</summary>
    internal static void SaveScaled(byte* pixels, int width, int height, int stride, Size size, string path)
    {
        if (width == size.Width && height == size.Height) { SaveJpeg(pixels, width, height, stride, path); return; }
        var scaled = new byte[size.Width * size.Height * 4];
        fixed (byte* p = scaled)
        {
            Downscale(pixels, width, height, stride, p, size.Width, size.Height, size.Width * 4);
            SaveJpeg(p, size.Width, size.Height, size.Width * 4, path);
        }
    }

    /// <summary>32-bit BGR(A) pixels as a JPEG (quality 80; the alpha byte is ignored).</summary>
    internal static void SaveJpeg(byte* pixels, int width, int height, int stride, string path)
    {
        using var bitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, (IntPtr)pixels);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
        bitmap.Save(path, Jpeg, parameters);
    }

    /// <summary>
    /// Scales 32-bit pixels down: each output pixel is the average of the source pixels in its
    /// box (2x2 for a 4K screen, exactly; 1x1 to 2x2 for 2560 wide). Alpha comes out opaque.
    /// </summary>
    internal static void Downscale(byte* src, int srcWidth, int srcHeight, int srcStride, byte* dst, int dstWidth, int dstHeight, int dstStride)
    {
        var s = (IntPtr)src;
        var d = (IntPtr)dst;
        if (srcWidth == dstWidth * 2 && srcHeight == dstHeight * 2)
        {
            // Two pixels' red and blue, and green, in one uint each: the sums of four fit.
            Parallel.For(0, dstHeight, y =>
            {
                var row0 = (uint*)((byte*)s + (long)2 * y * srcStride);
                var row1 = (uint*)((byte*)s + (long)(2 * y + 1) * srcStride);
                var o = (uint*)((byte*)d + (long)y * dstStride);
                for (var x = 0; x < dstWidth; x++)
                {
                    uint a = row0[2 * x], b = row0[2 * x + 1], c = row1[2 * x], e = row1[2 * x + 1];
                    var rb = (((a & 0xFF00FF) + (b & 0xFF00FF) + (c & 0xFF00FF) + (e & 0xFF00FF) + 0x20002) >> 2) & 0xFF00FF;
                    var g = (((a & 0xFF00) + (b & 0xFF00) + (c & 0xFF00) + (e & 0xFF00) + 0x200) >> 2) & 0xFF00;
                    o[x] = rb | g | 0xFF000000;
                }
            });
            return;
        }
        var xs = new int[dstWidth + 1];
        var ys = new int[dstHeight + 1];
        for (var x = 0; x <= dstWidth; x++) xs[x] = (int)((long)x * srcWidth / dstWidth);
        for (var y = 0; y <= dstHeight; y++) ys[y] = (int)((long)y * srcHeight / dstHeight);
        Parallel.For(0, dstHeight, y =>
        {
            int y0 = ys[y], y1 = Math.Max(ys[y + 1], y0 + 1);
            var o = (byte*)d + (long)y * dstStride;
            for (var x = 0; x < dstWidth; x++)
            {
                int x0 = xs[x], x1 = Math.Max(xs[x + 1], x0 + 1);
                int b = 0, g = 0, r = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var p = (byte*)s + (long)sy * srcStride + x0 * 4;
                    for (var sx = x0; sx < x1; sx++, p += 4) { b += p[0]; g += p[1]; r += p[2]; }
                }
                var n = (x1 - x0) * (y1 - y0);
                o[0] = (byte)((b + n / 2) / n); o[1] = (byte)((g + n / 2) / n); o[2] = (byte)((r + n / 2) / n); o[3] = 255;
                o += 4;
            }
        });
    }

    // --- Desktop Duplication -------------------------------------------------------------------

    /// <summary>A failed step. Setup: this screen cannot be duplicated (GDI for a minute). KeepDevice: only this frame failed.</summary>
    sealed class CaptureException(string message, bool setup = false, bool keepDevice = false) : Exception(message)
    {
        public bool Setup { get; } = setup;
        public bool KeepDevice { get; } = keepDevice;
    }

    static void Ok(int hr, string what, bool setup = false, bool keepDevice = false)
    {
        if (hr < 0) throw new CaptureException($"{what} 0x{hr:X8}", setup, keepDevice);
    }

    // COM by hand (vtable slots from d3d11.h and dxgi1_2.h): the few calls needed, no package.
    static void** Slots(IntPtr p) => *(void***)p;
    static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slots(p)[2])(p); }
    static IntPtr Query(IntPtr p, Guid iid, string what)
    {
        IntPtr result;
        Ok(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slots(p)[0])(p, &iid, &result), what, setup: true);
        return result;
    }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count, uint sdkVersion,
        out IntPtr device, out int level, out IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)]
    struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

    const uint B8G8R8A8 = 87, UsageStaging = 3, BindShaderResource = 0x8, BindRenderTarget = 0x20, CpuRead = 0x20000, GenerateMipsFlag = 0x1;
    const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary>A D3D11 device and the DXGI output that shows the screen; a duplication per capture.</summary>
    sealed class Duplicator : IDisposable
    {
        static readonly Guid IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        static readonly Guid IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        public readonly Rectangle Bounds;
        IntPtr device, context, output;

        public Duplicator(Rectangle screen)
        {
            Bounds = screen;
            IntPtr dxgiDevice = IntPtr.Zero, adapter = IntPtr.Zero;
            try
            {
                Ok(D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out device, out _, out context), "D3D11CreateDevice", setup: true);
                dxgiDevice = Query(device, IDXGIDevice, "IDXGIDevice");
                Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slots(dxgiDevice)[7])(dxgiDevice, &adapter), "GetAdapter", setup: true);
                // The output showing this screen (DXGI_OUTPUT_DESC: the name, then its desktop rectangle, then the rotation).
                var desc = stackalloc byte[96];
                for (uint i = 0; output == IntPtr.Zero; i++)
                {
                    IntPtr o;
                    Ok(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slots(adapter)[7])(adapter, i, &o), "no output shows this screen", setup: true);
                    try
                    {
                        Ok(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slots(o)[7])(o, desc), "IDXGIOutput.GetDesc", setup: true);
                        var r = (int*)(desc + 64);
                        if (new Rectangle(r[0], r[1], r[2] - r[0], r[3] - r[1]) != screen) continue;
                        var rotation = *(int*)(desc + 84);
                        if (rotation > 1) throw new CaptureException("the screen is rotated", setup: true);
                        output = Query(o, IDXGIOutput1, "IDXGIOutput1");
                    }
                    finally { Release(o); }
                }
            }
            catch { Dispose(); throw; }
            finally { Release(adapter); Release(dxgiDevice); }
        }

        /// <summary>The screen now, scaled to size, into a JPEG. Returns how long each step took, for the log.</summary>
        public string Save(Size size, string path)
        {
            IntPtr dup = IntPtr.Zero, resource = IntPtr.Zero, frame = IntPtr.Zero, mips = IntPtr.Zero, view = IntPtr.Zero, staging = IntPtr.Zero;
            var held = false;
            var clock = Stopwatch.StartNew();
            long duplicated, framed, copied;
            try
            {
                Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slots(output)[22])(output, device, &dup), "DuplicateOutput", keepDevice: true);
                duplicated = clock.ElapsedMilliseconds;
                // The first frame of a new duplication can be the pointer only (no image yet): the next one.
                var info = stackalloc byte[64];   // DXGI_OUTDUPL_FRAME_INFO: AccumulatedFrames at 16
                var until = Environment.TickCount64 + 250;
                while (true)
                {
                    var left = (uint)Math.Max(0, until - Environment.TickCount64);
                    var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)Slots(dup)[8])(dup, left, info, &resource);
                    if (hr == WaitTimeout) throw new CaptureException("no frame within 250 ms", keepDevice: true);
                    Ok(hr, "AcquireNextFrame", keepDevice: true);
                    held = true;
                    if (*(uint*)(info + 16) > 0) break;
                    Release(resource);
                    resource = IntPtr.Zero;
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slots(dup)[14])(dup);   // ReleaseFrame
                    held = false;
                }
                frame = Query(resource, ID3D11Texture2D, "ID3D11Texture2D");
                framed = clock.ElapsedMilliseconds;

                // Halved on the GPU as long as it stays at least the target's width (a 4K screen:
                // once, to 1920), so only that is read back.
                var levels = 0;
                while (Bounds.Width >> (levels + 1) >= size.Width) levels++;
                int width = Bounds.Width >> levels, height = Bounds.Height >> levels;
                var stagingDesc = new TextureDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = B8G8R8A8, SampleCount = 1, Usage = UsageStaging, CpuAccess = CpuRead };
                Ok(((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)Slots(device)[5])(device, &stagingDesc, IntPtr.Zero, &staging), "CreateTexture2D (staging)");
                if (levels == 0)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slots(context)[47])(context, staging, frame);   // CopyResource
                }
                else
                {
                    var mipsDesc = new TextureDesc { Width = (uint)Bounds.Width, Height = (uint)Bounds.Height, MipLevels = (uint)levels + 1, ArraySize = 1, Format = B8G8R8A8, SampleCount = 1,
                        BindFlags = BindShaderResource | BindRenderTarget, Misc = GenerateMipsFlag };
                    Ok(((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)Slots(device)[5])(device, &mipsDesc, IntPtr.Zero, &mips), "CreateTexture2D (mips)");
                    Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slots(device)[7])(device, mips, IntPtr.Zero, &view), "CreateShaderResourceView");
                    // CopySubresourceRegion into level 0, GenerateMips, then the last level into staging.
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)Slots(context)[46])(context, mips, 0, 0, 0, 0, frame, 0, IntPtr.Zero);
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slots(context)[54])(context, view);
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)Slots(context)[46])(context, staging, 0, 0, 0, 0, mips, (uint)levels, IntPtr.Zero);
                }
                Mapped mapped;
                Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, Mapped*, int>)Slots(context)[14])(context, staging, 0, 1 /* read */, 0, &mapped), "Map");
                // The GPU is done with the frame: DWM may have it back before the JPEG.
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slots(dup)[14])(dup);
                held = false;
                copied = clock.ElapsedMilliseconds;
                try { SaveScaled((byte*)mapped.Data, width, height, (int)mapped.RowPitch, size, path); }
                finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slots(context)[15])(context, staging, 0); }   // Unmap
                return $"duplicate {duplicated} ms, frame {framed - duplicated} ms, GPU copy {copied - framed} ms, JPEG {clock.ElapsedMilliseconds - copied} ms";
            }
            finally
            {
                if (held) ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slots(dup)[14])(dup);
                Release(staging); Release(view); Release(mips); Release(frame); Release(resource); Release(dup);
            }
        }

        public void Dispose()
        {
            Release(output); Release(context); Release(device);
            output = context = device = IntPtr.Zero;
        }
    }
}
