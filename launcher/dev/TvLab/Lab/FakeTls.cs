using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Htpc.TvLab;

/// <summary>Self-signed certificates for fake TVs and a TLS listener.</summary>
static class LabTls
{
    /// <summary>
    /// A TLS server key Windows' Schannel can use: loaded from a PFX into the user's key set, so a
    /// key file in the profile until the certificate is disposed. ECDSA P-256 (fast to make), RSA
    /// only where the protocol reads the key's modulus (Google TV's pairing secret).
    /// </summary>
    public static X509Certificate2 Certificate(string name, bool rsa = false)
    {
        using AsymmetricAlgorithm key = rsa ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = key is RSA r
            ? new CertificateRequest($"CN={name}", r, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest($"CN={name}", (ECDsa)key, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>
    /// Accepts TLS connections on ip:port and runs <paramref name="serve"/> for each (the client's
    /// certificate is required when <paramref name="clientCertificate"/>). While
    /// <paramref name="refuse"/> says so, connections are dropped at once (a TV that is off).
    /// <paramref name="note"/> hears "connection" as each arrives and "closed" once it is over. (A
    /// finished handshake says nothing: .NET's client checks the certificate after it, so a fake
    /// knows the client took its certificate only when the client speaks.)
    /// </summary>
    public static TcpListener Listen(IPAddress ip, int port, Func<X509Certificate2> certificate, bool clientCertificate,
        Func<bool> refuse, Func<SslStream, X509Certificate2?, Task> serve, Action<string>? note = null)
    {
        var listener = new TcpListener(ip, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // a fake replaced at the same address
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    note?.Invoke("connection");
                    X509Certificate2? peer = null;
                    try
                    {
                        using var _ = client;
                        if (refuse()) { client.Client.LingerState = new LingerOption(true, 0); return; }
                        using var ssl = new SslStream(client.GetStream(), false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate(),
                            ClientCertificateRequired = clientCertificate,
                            RemoteCertificateValidationCallback = (_, c, _, _) => { if (c is not null) peer = new X509Certificate2(c); return !clientCertificate || c is not null; },
                        });
                        await serve(ssl, peer);
                    }
                    catch (Exception e) { note?.Invoke($"tls ended: {e.GetType().Name}"); }
                    finally { peer?.Dispose(); note?.Invoke("closed"); }
                });
            }
        });
        return listener;
    }

    public static string Pin(X509Certificate2 cert) => Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
}

/// <summary>
/// A fake's certificate, made when first needed; every one it had is disposed with it (their key
/// files go). RSA keys take 100 ms each to make: a Google TV fake shares its key with the fakes of
/// the same label in other worlds (the same TV), never with another label (a stranger must not
/// pass the pin with the TV's key); those are disposed at the run's end.
/// </summary>
sealed class LabCertificate(string label, bool rsa = false) : IDisposable
{
    static readonly Dictionary<string, X509Certificate2> shared = new();
    readonly List<X509Certificate2> made = new();
    X509Certificate2? current;

    public static void DisposeShared()
    {
        lock (shared) { foreach (var c in shared.Values) c.Dispose(); shared.Clear(); }
    }

    public X509Certificate2 Get()
    {
        lock (made) return current ??= Make(label);
    }

    /// <summary>Another key from now on (another TV answering at this address).</summary>
    public void Replace()
    {
        lock (made) current = Make(label + "-another-key");
    }

    X509Certificate2 Make(string name)
    {
        if (rsa) lock (shared) return shared.TryGetValue(name, out var one) ? one : shared[name] = LabTls.Certificate(name, rsa: true);
        var cert = LabTls.Certificate(name);
        made.Add(cert);
        return cert;
    }

    public void Dispose()
    {
        lock (made) { made.ForEach(c => c.Dispose()); made.Clear(); current = null; }
    }
}
