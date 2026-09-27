using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Htpc.TvLab;

/// <summary>Self-signed certificates for fake TVs (a TLS server key Windows' Schannel can use) and a TLS listener.</summary>
static class LabTls
{
    public static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>
    /// Accepts TLS connections on ip:port and runs <paramref name="serve"/> for each (the client's
    /// certificate is required when <paramref name="clientCertificate"/>). While
    /// <paramref name="refuse"/> says so, connections are dropped at once (a TV that is off).
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
                    using var _ = client;
                    note?.Invoke("connection");
                    if (refuse()) { client.Client.LingerState = new LingerOption(true, 0); return; }
                    X509Certificate2? peer = null;
                    try
                    {
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
                });
            }
        });
        return listener;
    }

    public static string Pin(X509Certificate2 cert) => Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
}
