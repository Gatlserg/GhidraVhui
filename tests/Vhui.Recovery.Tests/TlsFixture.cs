using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Short-lived, local interoperability certificates. Never installed in Windows trust stores.
internal static class TlsFixture
{
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        using RSA rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Vhui local test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem());
        foreach (bool server in new[] { true, false })
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(server ? "CN=localhost" : "CN=Vhui native test client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
            if (server)
            {
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
                request.CertificateExtensions.Add(san.Build());
            }
            using X509Certificate2 issued = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(Path.Combine(directory, server ? "server.pem" : "client.pem"), key.ExportRSAPrivateKeyPem() + "\n" + issued.ExportCertificatePem());
        }
    }
}
