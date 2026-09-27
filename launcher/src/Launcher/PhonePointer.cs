namespace Htpc.Launcher;

/// <summary>
/// The phone touchpad's pointer speed: finger travel (CSS pixels) to screen pixels. Slow moves
/// are exact (about 1.2 screen pixels per CSS pixel on a 1080p screen, twice that at 4K); quick
/// flicks go up to 3 times further, so one swipe crosses most of the screen.
/// </summary>
static class PointerAcceleration
{
    const double Slow = 0.15, Fast = 1.6;   // finger speed, CSS pixels per millisecond
    const double MaxGain = 3.0;
    const double ScrollPerPixel = 3;        // wheel units per CSS pixel: 40 px of travel is one notch

    /// <summary>1 at slow speeds, easing up to MaxGain for fast ones.</summary>
    public static double Gain(double speed)
    {
        if (!(speed > Slow)) return 1;
        if (speed >= Fast) return MaxGain;
        var t = (speed - Slow) / (Fast - Slow);
        return 1 + (MaxGain - 1) * t * t * (3 - 2 * t);
    }

    /// <summary>Screen pixels for a finger move of (dx, dy) CSS pixels over ms milliseconds.</summary>
    public static (double X, double Y) ToScreen(double dx, double dy, double ms, int screenHeight)
    {
        var speed = Math.Sqrt(dx * dx + dy * dy) / Math.Max(ms, 4);
        var gain = Gain(speed) * screenHeight / 900.0;
        return (dx * gain, dy * gain);
    }

    /// <summary>
    /// Wheel units for two-finger travel, the way phones scroll: the page follows the fingers
    /// (fingers down = scroll up = wheel up, positive; fingers right = scroll left = negative).
    /// </summary>
    public static (double X, double Y) ToWheel(double dx, double dy) => (-dx * ScrollPerPixel, dy * ScrollPerPixel);
}

/// <summary>
/// Pointer and scroll motion from the phone's touchpad, handed to PadMapper's frame thread, which
/// moves the pointer once per displayed frame for the controller's sticks too (one writer, in
/// step with the TV). Batches from the phone arrive unevenly over Wi-Fi: each frame takes all of
/// a small amount, else about 60% of what is waiting, so a batch spreads over 1-2 frames.
/// </summary>
sealed class PointerFeed
{
    const double Share = 0.6;
    const double TakeAllBelow = 8;   // pixels (or wheel units) small enough to move at once

    readonly object gate = new();
    readonly Action wake;
    double pointerX, pointerY, wheelX, wheelY;            // waiting
    double restX, restY, restWheelX, restWheelY;          // below one pixel or unit, kept for the next frame

    /// <param name="wake">Wakes the frame thread (it sleeps while nothing moves).</param>
    public PointerFeed(Action wake) => this.wake = wake;

    public void AddPointer(double dx, double dy)
    {
        lock (gate) { pointerX += dx; pointerY += dy; }
        wake();
    }

    public void AddScroll(double dx, double dy)
    {
        lock (gate) { wheelX += dx; wheelY += dy; }
        wake();
    }

    /// <summary>Something is waiting to be moved.</summary>
    public bool Pending
    {
        get { lock (gate) return Math.Abs(pointerX) + Math.Abs(pointerY) >= 0.5 || Math.Abs(wheelX) + Math.Abs(wheelY) >= 1; }
    }

    /// <summary>Drops whatever is waiting (standby: nothing may move the pointer).</summary>
    public void Clear()
    {
        lock (gate) pointerX = pointerY = wheelX = wheelY = restX = restY = restWheelX = restWheelY = 0;
    }

    /// <summary>This frame's share, in whole pixels and wheel units.</summary>
    public (int Dx, int Dy, int WheelX, int WheelY) Take()
    {
        lock (gate)
        {
            static double Part(ref double waiting)
            {
                var part = Math.Abs(waiting) <= TakeAllBelow ? waiting : waiting * Share;
                waiting -= part;
                return part;
            }
            static int Whole(double amount, ref double rest)
            {
                rest += amount;
                var whole = (int)rest;   // toward zero; the fraction waits for the next frame
                rest -= whole;
                return whole;
            }
            var dx = Whole(Part(ref pointerX), ref restX);
            var dy = Whole(Part(ref pointerY), ref restY);
            var wx = Whole(Part(ref wheelX), ref restWheelX);
            var wy = Whole(Part(ref wheelY), ref restWheelY);
            return (dx, dy, wx, wy);
        }
    }

    /// <summary>Called by the frame thread once per frame: moves the pointer and scrolls.</summary>
    public void Frame()
    {
        var (dx, dy, wx, wy) = Take();
        Input.MoveBy(dx, dy);
        Input.Wheel(wy);
        Input.Wheel(wx, horizontal: true);
    }
}

/// <summary>
/// Over the launcher's own screens the touchpad moves the focus (the launcher has no pointer):
/// every 56 CSS pixels of travel along the main direction is one D-pad step. A pause of
/// 300 ms starts a new swipe.
/// </summary>
sealed class SwipeStepper
{
    public const double Step = 56;
    const long NewSwipeAfterMs = 300;

    double x, y;
    long last = long.MinValue;

    /// <summary>The D-pad steps ("up", "down", "left", "right") this bit of travel adds up to.</summary>
    public List<string> Add(double dx, double dy, long nowMs)
    {
        if (nowMs - last > NewSwipeAfterMs) x = y = 0;
        last = nowMs;
        x += dx;
        y += dy;
        var steps = new List<string>();
        while (steps.Count < 20)
        {
            if (Math.Abs(x) >= Math.Abs(y) && Math.Abs(x) >= Step)
            {
                steps.Add(x > 0 ? "right" : "left");
                x -= Math.Sign(x) * Step;
                y = 0; // one direction at a time: a slightly slanted swipe stays in its row
            }
            else if (Math.Abs(y) > Math.Abs(x) && Math.Abs(y) >= Step)
            {
                steps.Add(y > 0 ? "down" : "up");
                y -= Math.Sign(y) * Step;
                x = 0;
            }
            else break;
        }
        return steps;
    }
}
