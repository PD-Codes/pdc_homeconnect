using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace PdcHomeConnect.Core.Security;

/// <summary>
/// Creates and loads the self-signed TLS certificate. Home Assistant pins its SHA-256 fingerprint
/// during pairing (trust on first use), so the certificate must stay stable.
/// </summary>
public static class CertificateManager
{
    public static X509Certificate2 LoadOrCreate(string pfxPath, string hostName, bool machineKeySet, ILogger logger)
    {
        var flags = OperatingSystem.IsWindows()
            ? (machineKeySet ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet)
            : X509KeyStorageFlags.EphemeralKeySet;

        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null, flags);
                if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(30))
                {
                    return existing;
                }

                logger.LogWarning("TLS certificate is expired or invalid, creating a new one (Home Assistant must re-pair)");
                existing.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load TLS certificate from {Path}, creating a new one", pfxPath);
            }
        }

        var pfx = CreatePfx(hostName);
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);
        logger.LogInformation("Created TLS certificate {Path}", pfxPath);

        // Re-import from PKCS#12 so the private key is usable by SChannel on Windows.
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, flags);
    }

    /// <summary>SHA-256 fingerprint of the DER encoded certificate (lowercase hex).</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private static byte[] CreatePfx(string hostName)
    {
        using var rsa = RSA.Create(2048);
        var safeHost = string.IsNullOrWhiteSpace(hostName) ? "localhost" : hostName;
        var request = new CertificateRequest($"CN={safeHost}, O=PDC HomeConnect", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(safeHost);
        san.AddDnsName($"{safeHost}.local");
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}
