using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Vhui.Core.Protocol;

internal sealed class ServerTlsConfiguration : IDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly X509Certificate2Collection authorities = [];
    private readonly bool clientCertificateRequired;
    private ServerTlsConfiguration(X509Certificate2 certificate, bool required) { this.certificate = certificate; clientCertificateRequired = required; }
    public static ServerTlsConfiguration? Load(UsbServerOptions options)
    {
        if (options.Enabled("RemoteAdmin") && options.Configuration.GetString("RemoteAdminPassword") is { Length: > 0 })
        { throw new NotSupportedException("The native RemoteAdminPassword handshake is not recovered. Use RemoteAdmin=0 or explicit AdminAllowedClients without this setting."); }
        if (options.Enabled("SSLUseCRL")) { throw new NotSupportedException("SSLUseCRL is not implemented; revocation checks cannot be silently omitted."); }
        string? file = options.FilePath("SSLCert");
        if (string.IsNullOrEmpty(file))
        {
            if (options.Configuration.GetString("SSLReverseClients") is { Length: > 0 }) { throw new InvalidDataException("SSLReverseClients requires SSLCert."); }
            if (options.Enabled("SSLUseClientCerts") || options.Configuration.GetString("SSLCAFile") is { Length: > 0 })
            { throw new InvalidDataException("TLS client policies require SSLCert."); }
            return null;
        }
        if (options.Configuration.GetString("SSLCRLFile") is { Length: > 0 })
        { throw new NotSupportedException("A standalone SSLCRLFile is not supported; certificate validation cannot silently omit it."); }
        X509Certificate2 certificate;
        if (Path.GetExtension(file).Equals(".pfx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".p12", StringComparison.OrdinalIgnoreCase))
        { certificate = X509CertificateLoader.LoadPkcs12FromFile(file, options.Configuration.GetString("SSLCertPassword")); }
        else
        {
            string keyFile = options.FilePath("SSLKeyFile", file)!;
            using X509Certificate2 pem = options.Configuration.GetString("SSLCertPassword") is { Length: > 0 } password
                ? X509Certificate2.CreateFromEncryptedPemFile(file, password, keyFile)
                : X509Certificate2.CreateFromPemFile(file, keyFile);
            certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        if (!certificate.HasPrivateKey) { certificate.Dispose(); throw new InvalidDataException("TLS certificate has no private key."); }
        var result = new ServerTlsConfiguration(certificate, options.Enabled("SSLUseClientCerts"));
        try
        {
            if (options.FilePath("SSLCAFile") is { Length: > 0 } ca) { result.authorities.ImportFromPemFile(ca); }
            if (result.clientCertificateRequired && result.authorities.Count == 0) { throw new InvalidDataException("SSLUseClientCerts requires SSLCAFile."); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public SslServerAuthenticationOptions AuthenticationOptions() => new()
    {
        ServerCertificate = certificate, ClientCertificateRequired = clientCertificateRequired,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    };
    public bool ValidateClient(object sender, X509Certificate? candidate, X509Chain? ignored, SslPolicyErrors errors)
    {
        if (!clientCertificateRequired) { return true; }
        if (candidate is null) { return false; }
        using X509Certificate2 client = X509CertificateLoader.LoadCertificate(candidate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
        chain.ChainPolicy.ExtraStore.AddRange(authorities);
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2"));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(client);
    }
    public void Dispose() { certificate.Dispose(); foreach (X509Certificate2 ca in authorities) { ca.Dispose(); } }
}
