using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Htpc.Launcher;

/// <summary>
/// Google TV / Android TV (beta: written from the open protocol, androidtvremote2, without such a
/// TV at hand; see TvLab's fake): the Android TV Remote protocol v2. Found by mDNS
/// (_androidtvremote2._tcp); paired once over TLS on port 6467 with the box's client certificate
/// and the 6-character code the TV shows; then a TLS channel on 6466 (protobuf) for keys, with the
/// TV's power pushed to it. On is WAKEUP, off is SLEEP (neither toggles); the input is an HDMI key
/// (unverified on real TVs), and the TV does not tell which input it shows.
///
/// Only ever the bound TV: the TV's TLS public key is pinned when the user pairs it; a later
/// connection whose key differs fails in the handshake (nothing of ours is sent) and the TV reads
/// as silent. Keys go only over that pinned connection. Chromecasts, Google TV Streamers and Nest
/// devices answer the same service and are left out (their cast name gives them away).
/// </summary>
sealed class AndroidTvDriver : ITvDriver, ITvPairing
{
    public const string Service = "_androidtvremote2._tcp.local";
    const string ClientKey = "androidtv:client";
    const int KeyWakeUp = 224, KeySleep = 223, KeyHdmi1 = 243;
    static readonly TimeSpan Retry = TimeSpan.FromSeconds(4); // between tries on a TV that is off: a turn-on's 1 s checks get some
    static readonly string[] NotTvs = { "Chromecast", "Streamer", "Nest", "Google Home", "Google TV Streamer" };

    readonly ITvNet net;
    readonly ITvClock clock;
    readonly int pairingPort;
    readonly ConcurrentDictionary<string, AtvSession> sessions = new();
    readonly ConcurrentDictionary<string, DateTime> failedAt = new();
    TvCredentials? credentials;
    X509Certificate2? client;

    public AndroidTvDriver(ITvNet net, ITvClock? clock = null, int pairingPort = 6467)
    {
        this.net = net;
        this.clock = clock ?? SystemTvClock.Instance;
        this.pairingPort = pairingPort;
    }

    public TvMethodInfo Info { get; } = new(
        "androidtv", "Google TV / Android TV", "Google TV", Beta: true,
        TvCaps.ReadPower | TvCaps.PowerOn | TvCaps.PowerOff | TvCaps.SelectInput | TvCaps.NeedsPairing,
        Quiet: TimeSpan.FromSeconds(30),
        Checklist: new[]
        {
            "Same network|The TV and the box on the same network (the TV's remote service comes with Google TV).",
            "Stay reachable when off|Settings › System › Power and energy: Screenless service (TCL), Network standby or Wake on network: On. Else the box cannot turn it on.",
        },
        How: "Over your network. Type the code the TV shows, once.");

    public void UseCredentials(TvCredentials c) => credentials = c;
    public int CodeLength => 6;

    string? Pin(TvDevice tv) => credentials?.Get(tv.Key)?.Value;
    public bool IsPaired(TvDevice tv) => Pin(tv) is { Length: > 0 };

    public void Forget(TvDevice tv)
    {
        credentials?.Forget(tv.Key);
        if (sessions.TryRemove(tv.Key, out var s)) s.Dispose();
    }

    public async Task<IReadOnlyList<TvDevice>> Find(CancellationToken cancel)
    {
        var remotes = net.Mdns(Service, TimeSpan.FromSeconds(2), cancel);
        var casts = net.Mdns("_googlecast._tcp.local", TimeSpan.FromSeconds(2), cancel);
        await Task.WhenAll(remotes, casts);
        var list = new List<TvDevice>();
        foreach (var s in remotes.Result)
        {
            var model = casts.Result.FirstOrDefault(c => c.Address.Equals(s.Address))?.Txt.GetValueOrDefault("md") ?? "";
            if (NotTvs.Any(n => model.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
            // A stable id: the Bluetooth MAC the service advertises, else its name.
            var id = TvNet.NormalizeMac(s.Txt.GetValueOrDefault("bt")) is { } bt ? "bt-" + bt.Replace(":", "") : s.Instance.ToLowerInvariant();
            var maker = model.StartsWith("BRAVIA", StringComparison.OrdinalIgnoreCase) ? "Sony" : model.Split(' ').FirstOrDefault() ?? "";
            list.Add(new TvDevice
            {
                Method = "androidtv", Id = id, Name = s.Instance, Model = model, Maker = maker,
                Address = new Uri($"atv://{s.Address}:{s.Port}/"),
            });
        }
        return list;
    }

    /// <summary>The box's client certificate (one per box, made on first use, kept with the pairing keys).</summary>
    X509Certificate2 Client()
    {
        if (client is not null) return client;
        var pfx = credentials?.Get(ClientKey)?.Pfx;
        if (pfx is null)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=TV Box", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            pfx = made.Export(X509ContentType.Pfx);
            credentials?.Set(ClientKey, new TvCredentials.Secret { Pfx = pfx });
        }
        // A key TLS client authentication (Schannel) can use: loaded from the PFX into the user's key set.
        return client = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
    }

    static string PinOf(X509Certificate cert) =>
        Convert.ToBase64String(SHA256.HashData(new X509Certificate2(cert).PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>
    /// TLS to the TV with the box's certificate. Pinned: only to the TV with that key, and never
    /// without a pin (an empty one refuses outright). Unpinned only for pairing.
    /// </summary>
    async Task<(SslStream Stream, X509Certificate2 Server)?> Tls(string host, int port, string? pin, bool pinned, CancellationToken cancel)
    {
        if (pinned && string.IsNullOrEmpty(pin)) return null;
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, port, timeout.Token);
            X509Certificate2? server = null;
            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = new X509CertificateCollection { Client() },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    server = new X509Certificate2(cert);
                    return !pinned || PinOf(cert) == pin; // another TV at this address: the handshake stops here
                },
            }, timeout.Token);
            return (ssl, server!);
        }
        catch (Exception e)
        {
            if (pinned && e is AuthenticationException) Log.Warn($"Android TV at {host} is not the paired TV (its key differs); nothing sent to it");
            tcp.Dispose();
            return null;
        }
    }

    public async Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel)
    {
        if (sessions.TryGetValue(tv.Key, out var live) && live.Open) return tv with { State = live.State };
        var pin = Pin(tv); // read once: a Forget meanwhile must not leave a check against nothing
        if (passive || string.IsNullOrEmpty(pin)) return tv with { State = TvState.Unknown };
        if (failedAt.TryGetValue(tv.Key, out var failed) && clock.Now - failed >= TimeSpan.Zero && clock.Now - failed < Retry) return null;
        var tls = await Tls(tv.Address.Host, tv.Address.Port, pin, pinned: true, cancel);
        if (tls is null) { failedAt[tv.Key] = clock.Now; return null; }
        var session = new AtvSession(tls.Value.Stream);
        if (sessions.TryRemove(tv.Key, out var old)) old.Dispose();
        sessions[tv.Key] = session;
        failedAt.TryRemove(tv.Key, out _);
        await session.WaitForPower(TimeSpan.FromSeconds(3), cancel);
        return tv with { State = session.State };
    }

    public async Task<bool> Pair(TvDevice tv, Action<string, string> step, Func<CancellationToken, Task<string?>> nextCode, CancellationToken cancel)
    {
        step("working", $"Connecting to {tv.Name}…");
        if (sessions.TryRemove(tv.Key, out var old)) old.Dispose(); // nothing from before this pairing is used after it
        // The pairing connection is not pinned (there is no key yet), so the TV must be the one
        // picked, there, now: a fresh search must find exactly this id answering from that address
        // (an address remembered from before can be another TV's since; a name shared by two TVs
        // without a Bluetooth id cannot tell them apart). Else no TV shows a code.
        var now = (await Find(cancel)).Where(t => t.Id == tv.Id).ToList();
        if (now.Count != 1 || now[0].Address.Host != tv.Address.Host)
        {
            step("failed", now.Count > 1
                ? $"Two TVs are called “{tv.Name}”: rename one on the TV (Settings › System › About › Device name), then try again."
                : $"{tv.Name} is not answering where it was. Search again, then pick it.");
            return false;
        }
        var tls = await Tls(tv.Address.Host, pairingPort, null, pinned: false, cancel);
        if (tls is null) { step("failed", $"{tv.Name} did not answer. Is it on, and on the same network?"); return false; }
        using var ssl = tls.Value.Stream;
        var server = tls.Value.Server;
        static byte[] Outer(int field, ProtoWriter payload) => new ProtoWriter().Varint(1, 2).Varint(2, 200).Message(field, payload).Framed();
        async Task<ProtoMessage?> Exchange(byte[] message, int expected)
        {
            await ssl.WriteAsync(message, cancel);
            var answer = await ProtoMessage.ReadFramed(ssl, cancel);
            return answer is not null && answer.Varint(2) == 200 && answer.Has(expected) ? answer : null;
        }
        var encoding = new ProtoWriter().Varint(1, 3).Varint(2, 6); // hexadecimal, 6 symbols
        try
        {
            if (await Exchange(Outer(10, new ProtoWriter().String(1, "atvremote").String(2, "TV Box")), 11) is null ||
                await Exchange(Outer(20, new ProtoWriter().Message(1, encoding).Message(2, encoding).Varint(3, 1)), 20) is null ||
                await Exchange(Outer(30, new ProtoWriter().Message(1, encoding).Varint(2, 1)), 31) is null)
            {
                step("failed", $"{tv.Name} did not start pairing. Try again.");
                return false;
            }
            step("code", $"Type the code {tv.Name} shows.");
            while (true)
            {
                var code = (await nextCode(cancel))?.Trim().ToUpperInvariant();
                if (code is null) { step("failed", "Pairing cancelled."); return false; }
                if (Secret(Client(), server, code) is not { } secret)
                {
                    step("code", "That code does not match what the TV shows. Type it again.");
                    continue;
                }
                step("working", "Checking the code…");
                if (await Exchange(Outer(40, new ProtoWriter().Bytes(1, secret)), 41) is null)
                {
                    step("failed", $"{tv.Name} refused the code. Try again.");
                    return false;
                }
                credentials?.Set(tv.Key, new TvCredentials.Secret { Value = PinOf(server) });
                step("done", $"{tv.Name} is paired.");
                return true;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            step("failed", "The TV closed the pairing. Try again.");
            return false;
        }
    }

    /// <summary>
    /// The pairing secret: SHA-256 over the client's and the TV's RSA modulus and exponent and the
    /// code's last 4 characters; the code's first 2 are the hash's first byte (a typo check).
    /// Null for a code that is not 6 hex characters or fails that check.
    /// </summary>
    public static byte[]? Secret(X509Certificate2 client, X509Certificate2 server, string code)
    {
        if (code.Length != 6 || !code.All(Uri.IsHexDigit)) return null;
        using var c = client.GetRSAPublicKey();
        using var s = server.GetRSAPublicKey();
        if (c is null || s is null) return null;
        var cp = c.ExportParameters(false);
        var sp = s.ExportParameters(false);
        var hash = SHA256.HashData(cp.Modulus!.Concat(cp.Exponent!).Concat(sp.Modulus!).Concat(sp.Exponent!).Concat(Convert.FromHexString(code[2..])).ToArray());
        return hash[0] == Convert.FromHexString(code[..2])[0] ? hash : null;
    }

    AtvSession? Live(TvDevice tv) => sessions.TryGetValue(tv.Key, out var s) && s.Open ? s : null;

    public async Task<bool> PowerOn(TvDevice tv, CancellationToken cancel) => Live(tv) is { } s && await s.Key(KeyWakeUp, cancel);
    public async Task<bool> PowerOff(TvDevice tv, CancellationToken cancel) => Live(tv) is { } s && await s.Key(KeySleep, cancel);
    public async Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel) =>
        input is >= 1 and <= 4 && Live(tv) is { } s && await s.Key(KeyHdmi1 + input - 1, cancel);
}

/// <summary>The remote channel (6466): answers the TV's configure, set-active and pings; keeps the pushed power.</summary>
sealed class AtvSession : IDisposable
{
    readonly SslStream ssl;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sending = new(1, 1);
    readonly TaskCompletionSource powerKnown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool? on;
    volatile bool open = true;

    public AtvSession(SslStream ssl)
    {
        this.ssl = ssl;
        _ = Loop();
    }

    public bool Open => open;
    public TvState State => on is { } o ? new TvState(o ? TvPower.On : TvPower.Off, 0, o ? "on" : "standby") : TvState.Unknown;

    public async Task WaitForPower(TimeSpan wait, CancellationToken cancel)
    {
        try { await powerKnown.Task.WaitAsync(wait, cancel); } catch (Exception) { }
    }

    async Task Loop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var m = await ProtoMessage.ReadFramed(ssl, stop.Token);
                if (m is null) break;
                if (m.Has(1)) // configure: say who we are
                    await Send(new ProtoWriter().Message(1, new ProtoWriter().Varint(1, 622).Message(2, new ProtoWriter()
                        .String(1, "TV Box").String(2, "HTPC").Varint(3, 1).String(4, "1").String(5, "atvremote").String(6, "1.0.0"))));
                else if (m.Has(2)) await Send(new ProtoWriter().Message(2, new ProtoWriter().Varint(1, 622)));
                else if (m.Message(8) is { } ping) await Send(new ProtoWriter().Message(9, new ProtoWriter().Varint(1, ping.Varint(1))));
                else if (m.Message(40) is { } start) { on = start.Varint(1) != 0; powerKnown.TrySetResult(); }
            }
        }
        catch (Exception) { }
        finally { open = false; }
    }

    async Task Send(ProtoWriter message)
    {
        await sending.WaitAsync(stop.Token);
        try { await ssl.WriteAsync(message.Framed(), stop.Token); }
        finally { sending.Release(); }
    }

    public async Task<bool> Key(int code, CancellationToken cancel)
    {
        try { await Send(new ProtoWriter().Message(10, new ProtoWriter().Varint(1, code).Varint(2, 3))); return true; }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        open = false;
        stop.Cancel();
        ssl.Dispose();
    }
}
