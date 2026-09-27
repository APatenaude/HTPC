using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// A Google TV speaking Android TV Remote v2, as androidtvremote2 describes it: pairing on 6467
/// (request, options, configuration, then the code it "shows", then the secret, checked here
/// with its own computation), the remote channel on 6466 (configure, set-active, power pushed,
/// keys), both over TLS requiring the client's certificate; only a paired client gets the remote.
/// Its mDNS answers carry its Bluetooth MAC and, for the cast service, its model.
/// </summary>
sealed class FakeAtv : IDisposable
{
    readonly Trace trace;
    readonly X509Certificate2Holder cert = new();
    readonly System.Net.Sockets.TcpListener pairing, remote;
    readonly HashSet<string> pairedClients = new();
    SslStream? live;

    public string Label { get; }
    public IPAddress Ip { get; }
    public string Name { get; set; } = "Living room Google TV";
    public string Model { get; set; } = "TCL 65Q651G";
    public string Bt { get; set; } = "02:00:00:00:2b:01";
    public bool On { get; set; } = true;
    public bool Reachable { get; set; } = true;
    /// <summary>The code on its screen during pairing.</summary>
    public string? Code { get; private set; }
    public int Connections;
    public readonly List<int> Keys = new();

    public FakeAtv(string label, IPAddress ip, Trace trace)
    {
        Label = label; Ip = ip; this.trace = trace;
        pairing = LabTls.Listen(ip, 6467, () => cert.Get(), true, () => !Reachable, ServePairing, Note);
        remote = LabTls.Listen(ip, 6466, () => cert.Get(), true, () => !Reachable, ServeRemote, Note);
    }

    void Note(string n) { if (n == "connection") Interlocked.Increment(ref Connections); }

    /// <summary>Another TV took this address: a different certificate from now on.</summary>
    public void BecomeAnotherTv() => cert.Replace();

    public IEnumerable<MdnsService> Mdns(string service) => !Reachable ? Array.Empty<MdnsService>() : service switch
    {
        AndroidTvDriver.Service => new[] { new MdnsService(Name, Ip, 6466, Bt.Length > 0 ? new Dictionary<string, string> { ["bt"] = Bt } : new Dictionary<string, string>()) },
        "_googlecast._tcp.local" => new[] { new MdnsService(Name, Ip, 8009, new Dictionary<string, string> { ["md"] = Model, ["fn"] = Name }) },
        _ => Array.Empty<MdnsService>(),
    };

    static byte[] Outer(int field, ProtoWriter payload, int status = 200) => new ProtoWriter().Varint(1, 2).Varint(2, status).Message(field, payload).Framed();

    async Task ServePairing(SslStream ssl, X509Certificate2? client)
    {
        if (client is null) return;
        byte[] nonce = RandomNumberGenerator.GetBytes(2);
        while (true)
        {
            var m = await ProtoMessage.ReadFramed(ssl, CancellationToken.None);
            if (m is null) return;
            if (m.Has(10)) { trace.Add($"{Label} pairing request"); await ssl.WriteAsync(Outer(11, new ProtoWriter().String(1, "fake"))); }
            else if (m.Has(20)) await ssl.WriteAsync(Outer(20, new ProtoWriter().Message(1, new ProtoWriter().Varint(1, 3).Varint(2, 6)).Varint(3, 1)));
            else if (m.Has(30))
            {
                Code = Convert.ToHexString(new[] { Hash(client, nonce)[0] }) + Convert.ToHexString(nonce);
                trace.Add($"{Label} shows a pairing code");
                await ssl.WriteAsync(Outer(31, new ProtoWriter()));
            }
            else if (m.Message(40) is { } secret)
            {
                var got = secret.Bytes(1) ?? Array.Empty<byte>();
                var ok = got.SequenceEqual(Hash(client, nonce));
                trace.Add($"{Label} secret {(ok ? "right" : "wrong")}");
                if (ok) lock (pairedClients) pairedClients.Add(LabTls.Pin(client));
                await ssl.WriteAsync(ok ? Outer(41, new ProtoWriter().Bytes(1, got)) : Outer(41, new ProtoWriter(), 402));
                return;
            }
        }
    }

    /// <summary>The TV's own side of the secret: SHA-256(client modulus, exponent, own modulus, exponent, nonce).</summary>
    byte[] Hash(X509Certificate2 client, byte[] nonce)
    {
        var c = client.GetRSAPublicKey()!.ExportParameters(false);
        var s = cert.Get().GetRSAPublicKey()!.ExportParameters(false);
        return SHA256.HashData(c.Modulus!.Concat(c.Exponent!).Concat(s.Modulus!).Concat(s.Exponent!).Concat(nonce).ToArray());
    }

    async Task ServeRemote(SslStream ssl, X509Certificate2? client)
    {
        bool paired;
        lock (pairedClients) paired = client is not null && pairedClients.Contains(LabTls.Pin(client));
        if (!paired) { trace.Add($"{Label} remote refused (not paired)"); return; }
        live = ssl;
        await ssl.WriteAsync(new ProtoWriter().Message(1, new ProtoWriter().Varint(1, 622)).Framed());
        while (true)
        {
            var m = await ProtoMessage.ReadFramed(ssl, CancellationToken.None);
            if (m is null) return;
            if (m.Has(1)) await ssl.WriteAsync(new ProtoWriter().Message(2, new ProtoWriter().Varint(1, 622)).Framed());
            else if (m.Has(2)) await PushPower(ssl);
            else if (m.Message(10) is { } key)
            {
                var code = (int)key.Varint(1);
                lock (Keys) Keys.Add(code);
                trace.Add($"{Label} key {code}");
                if (code == 224) On = true;
                if (code == 223) On = false;
                await PushPower(ssl);
            }
        }
    }

    Task PushPower(SslStream ssl) => ssl.WriteAsync(new ProtoWriter().Message(40, new ProtoWriter().Bool(1, On)).Framed()).AsTask();

    public void RemotePower(bool on)
    {
        On = on;
        if (live is { } s) _ = PushPower(s).ContinueWith(_ => { });
    }

    public void Dispose()
    {
        pairing.Stop();
        remote.Stop();
        live?.Dispose();
    }
}
