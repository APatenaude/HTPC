using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>The launcher as the server sees it: records what arrives.</summary>
sealed class FakeHost : IPhoneHost
{
    public readonly ConcurrentQueue<string> Events = new();
    public volatile string? Code;
    public int Shown;
    public bool Asleep;
    public (byte[] Data, string ContentType)? Art;
    public void OnCommand(PhoneClient phone, PhoneCommand command) => Events.Enqueue(command switch
    {
        KeyCommand k => $"key {k.Key}",
        TypeCommand t => $"type back={t.Back} text={t.Text}",
        _ => command.GetType().Name,
    });
    public void OnConnected(PhoneClient phone) => Events.Enqueue("connected");
    public void OnDisconnected(PhoneClient phone) => Events.Enqueue("disconnected");
    public bool ShowPairingCode(string code) { if (Asleep) return false; Code = code; Shown++; return true; }
    public void HidePairingCode(bool paired) { Code = null; Events.Enqueue($"hide paired={paired}"); }
    public void PhonesChanged() { }
    public void ShortcutKeyMade(string phoneName) => Events.Enqueue("shortcut " + phoneName);
    public (byte[] Data, string ContentType)? Artwork() => Art;
    public void OpenShared(string url) => Events.Enqueue("shared " + url);
}

/// <summary>
/// A folder or file of the test's own in %TEMP% (a new name each run, not made yet), gone with
/// whatever is in it when disposed. One still open elsewhere is left, and said (a virus scanner may hold it).
/// </summary>
sealed class TempPath(string name, string extension = "") : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"htpc-{name}-{Guid.NewGuid():N}{extension}");

    public static implicit operator string(TempPath t) => t.Path;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            else File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"  WARNING: left in %TEMP%: {Path} ({e.Message})"); }
    }
}

// What the server checks use: a server of the test's own, sockets and requests as a phone makes them.
static partial class Program
{
    static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, relative);
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The web app's folder (launcher\phone), as the launcher serves it.</summary>
    static string PhoneFolder => FindUp(Path.Combine("launcher", "phone")) ?? throw new DirectoryNotFoundException($"no launcher\\phone above {AppContext.BaseDirectory}");

    /// <summary>
    /// The phone server on 127.0.0.1 with a fake launcher, a pairing file in %TEMP% and a clock the
    /// test moves (Now); stopped, and its file gone, when disposed.
    /// </summary>
    sealed class TestServer : IAsyncDisposable
    {
        readonly TempPath file = new("phones-test", ".json");
        public DateTime Now = DateTime.Now;
        public FakeHost Host { get; } = new();
        public PhonePairing Pairing { get; }
        public PhoneServer Server { get; }
        public int Port { get; }
        public string Origin => $"http://127.0.0.1:{Port}";
        public HttpClient Http { get; }
        public string PairingFile => file;

        TestServer(TimeSpan? silence)
        {
            Pairing = new PhonePairing(file, () => Now);
            Server = silence is { } s
                ? new PhoneServer(Host, PhoneFolder, Pairing, IPAddress.Loopback, clock: () => Now) { Silence = s }
                : new PhoneServer(Host, PhoneFolder, Pairing, IPAddress.Loopback, clock: () => Now);
            Port = FreePort();
            Http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(Origin) };
        }

        /// <param name="silence">How long a silent phone stays (the launcher's 15 s when not given).</param>
        public static async Task<TestServer> StartAsync(TimeSpan? silence = null)
        {
            var box = new TestServer(silence);
            if (await box.Server.StartAsync(new[] { box.Port }) == box.Port) return box;
            await box.DisposeAsync();
            throw new InvalidOperationException($"the phone server did not start on 127.0.0.1:{box.Port}");
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Server.StopAsync();
            file.Dispose();
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task<string> Raw(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buf = new byte[4096];
        var n = await s.ReadAsync(buf);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    static async Task<(ClientWebSocket? Socket, int Status)> Ws(int port, string? origin, string? cookie)
    {
        var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        if (cookie is not null) ws.Options.SetRequestHeader("Cookie", cookie);
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
            return (ws, 101);
        }
        catch (WebSocketException) { return (null, (int)ws.HttpStatusCode); }
    }

    static async Task<JsonElement?> Receive(ClientWebSocket ws, int ms = 3000)
    {
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(ms);
        try
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            return JsonDocument.Parse(buf.AsMemory(0, r.Count)).RootElement.Clone();
        }
        catch (Exception) { return null; }
    }

    static Task SendText(ClientWebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    static Task<bool> WaitFor(FakeHost host, string item, int ms = 2000) => Until(() => host.Events.Contains(item), ms);

    /// <summary>Whether something comes true within ms (checked every 20 ms).</summary>
    static async Task<bool> Until(Func<bool> ok, int ms = 2000)
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < ms; await Task.Delay(20))
            if (ok()) return true;
        return ok();
    }
}
