using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// The certificate for HTTPS: the user's own (.pfx, or .crt/.pem plus key, e.g. from
    /// "tailscale cert"), or a self-signed one made once and kept in the data folder. Browsers warn
    /// about self-signed certificates once per device; a Tailscale certificate is trusted everywhere.
    /// </summary>
    public static class HttpsCertificate
    {
        private static string SelfSignedPath => Path.Combine(AppPaths.DataDir, "https-selfsigned.pfx");

        public static X509Certificate2? Load(ConfigService config, AppLog log)
        {
            var c = config.Current;
            try
            {
                if (!string.IsNullOrWhiteSpace(c.HttpsCertPath))
                {
                    string path = c.HttpsCertPath.Trim().Trim('"');
                    X509Certificate2 cert;
                    if (path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
                        cert = new X509Certificate2(path, c.HttpsCertPassword, X509KeyStorageFlags.Exportable);
                    else
                    {
                        string key = string.IsNullOrWhiteSpace(c.HttpsKeyPath) ? Path.ChangeExtension(path, ".key") : c.HttpsKeyPath.Trim().Trim('"');
                        using var pem = X509Certificate2.CreateFromPemFile(path, key);
                        // Windows' TLS needs the key in a persisted form: round-trip through PFX.
                        cert = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
                    }
                    if (cert.NotAfter < DateTime.Now) log.Write($"HTTPS: the certificate {Path.GetFileName(path)} has expired ({cert.NotAfter:d}).");
                    return cert;
                }

                if (File.Exists(SelfSignedPath))
                {
                    var existing = new X509Certificate2(SelfSignedPath, (string?)null, X509KeyStorageFlags.Exportable);
                    if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
                }
                var made = CreateSelfSigned();
                File.WriteAllBytes(SelfSignedPath, made.Export(X509ContentType.Pkcs12));
                log.Write("HTTPS: made a self-signed certificate. Browsers will warn once per device; choose \"continue\".");
                return new X509Certificate2(SelfSignedPath, (string?)null, X509KeyStorageFlags.Exportable);
            }
            catch (Exception ex)
            {
                log.Write($"HTTPS is off: the certificate couldn't be loaded ({ex.Message}).");
                return null;
            }
        }

        private static X509Certificate2 CreateSelfSigned()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN=LocalStream on {Environment.MachineName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddDnsName(Environment.MachineName);
            names.AddIpAddress(IPAddress.Loopback);
            foreach (var (ip, _) in NetworkHelper.GetActiveIPv4Addresses().Where(a => a.Ip != "127.0.0.1"))
                if (IPAddress.TryParse(ip, out var addr)) names.AddIpAddress(addr);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        }
    }
}
