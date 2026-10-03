using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// The certificate FTPS presents (D291): made once, self-signed for the host name, kept with the database's secrets
/// so clients that trusted it keep trusting it after a restart, and part of the configuration backup. The trust step
/// of M5 replaces it with the box's certificate.
/// </summary>
public static class TlsCertificate
{
    public const string FileName = "ftps.pfx";

    /// <summary>The certificate in <paramref name="directory"/>, made first when there is none.</summary>
    public static X509Certificate2 LoadOrCreate(string directory, string hostName, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(time);
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        }
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = time.GetUtcNow();
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        var bytes = created.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path, bytes);
        // Only root reads the key; the daemon runs on Linux only.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return X509CertificateLoader.LoadPkcs12(bytes, null);
    }

    /// <summary>The SHA-256 fingerprint as clients show it: hex pairs with colons.</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(part => part.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
