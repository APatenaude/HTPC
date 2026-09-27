using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Htpc.TvLab;

sealed record FakeRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);
sealed record FakeResponse(int Status, string Body = "", string ContentType = "text/xml; charset=utf-8");

/// <summary>
/// A small HTTP/1.1 server on 127.0.0.1 for fake TVs (no http.sys, so no URL reservations or
/// admin rights). One request per connection. <see cref="Unreachable"/> resets connections, the
/// way an unplugged or deeply asleep TV fails.
/// </summary>
sealed class FakeHttp : IDisposable
{
    readonly TcpListener listener;
    readonly Func<FakeRequest, FakeResponse> handler;
    readonly CancellationTokenSource stop = new();

    public int Port { get; }
    public volatile bool Unreachable;
    /// <summary>Runs as each connection arrives, before <see cref="Unreachable"/> is looked at (a fake's timed changes).</summary>
    public Action? Arriving;
    /// <summary>A connection was refused (while <see cref="Unreachable"/>).</summary>
    public Action? Refused;
    public Uri BaseUrl => new($"http://{Bind}:{Port}/");
    public IPAddress Bind { get; }

    public FakeHttp(Func<FakeRequest, FakeResponse> handler, IPAddress? bind = null)
    {
        this.handler = handler;
        Bind = bind ?? IPAddress.Loopback;
        listener = new TcpListener(Bind, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        Arriving?.Invoke();
        if (Unreachable)
        {
            Refused?.Invoke();
            client.Client.LingerState = new LingerOption(true, 0); // RST: "connection refused" on the client
            return;
        }
        var stream = client.GetStream();
        var request = await Read(stream);
        if (request is null) return;
        FakeResponse response;
        try { response = handler(request); }
        catch (Exception e) { response = new FakeResponse(500, e.Message, "text/plain"); }
        var body = Encoding.UTF8.GetBytes(response.Body);
        var head = $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\nContent-Type: {response.ContentType}\r\n" +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public static async Task<FakeRequest?> Read(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
            if (n > 65536) return null;
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var body = "";
        if (headers.TryGetValue("Content-Length", out var length) && int.TryParse(length, out var count) && count > 0)
        {
            var data = new byte[count];
            var read = 0;
            while (read < count)
            {
                var got = await stream.ReadAsync(data.AsMemory(read));
                if (got == 0) break;
                read += got;
            }
            body = Encoding.UTF8.GetString(data, 0, read);
        }
        return new FakeRequest(first[0], first.Length > 1 ? first[1] : "/", headers, body);
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
