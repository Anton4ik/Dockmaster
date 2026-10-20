using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dockmaster.Server.Services;

/// <summary>Self-signed TLS certificate for the agent port. Generated once, stored in the data dir. Agents pin its SHA-256 fingerprint.</summary>
public sealed class CertificateService
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public CertificateService(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "agent-tls.pfx");
        if (File.Exists(path))
        {
            Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, "", X509KeyStorageFlags.Exportable);
        }
        else
        {
            using var rsa = RSA.Create(3072);
            var req = new CertificateRequest("CN=dockmaster-agent-hub", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("dockmaster");
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            var pfx = cert.Export(X509ContentType.Pfx, "");
            File.WriteAllBytes(path, pfx);
            Certificate = X509CertificateLoader.LoadPkcs12(pfx, "", X509KeyStorageFlags.Exportable);
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    }

    public string FingerprintPretty => string.Join(':', Enumerable.Range(0, Fingerprint.Length / 2).Select(i => Fingerprint.Substring(i * 2, 2)));
}
