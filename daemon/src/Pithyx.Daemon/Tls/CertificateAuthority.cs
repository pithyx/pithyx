using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pithyx.Daemon.Tls;

/// <summary>A certificate with the private key that signs with it.</summary>
public sealed record SigningCertificate(X509Certificate2 Certificate, ECDsa Key);

/// <summary>A certificate the authority issued, with its key as PEM; the key is handed out once and never kept.</summary>
public sealed record IssuedPair(X509Certificate2 Certificate, string CertificatePem, string PrivateKeyPem);

/// <summary>The key of a certificate the authority issues.</summary>
public enum CertificateKeyType
{
    /// <summary>ECDSA on P-256, the default.</summary>
    Ecdsa,

    /// <summary>RSA with 2048 bits, for devices that take nothing else.</summary>
    Rsa,
}

/// <summary>
/// The box's own certificate authority (D346), as pure functions: a root (ECDSA P-384, 20 years) that devices trust
/// once, an intermediate (ECDSA P-256, 5 years, path length 0) that signs every certificate, and the certificates
/// it issues. Both authorities carry the same Name Constraints (<see cref="NameScope"/>).
/// </summary>
public static class CertificateAuthority
{
    public static readonly TimeSpan RootLifetime = TimeSpan.FromDays(365 * 20);
    public static readonly TimeSpan IntermediateLifetime = TimeSpan.FromDays(365 * 5);

    /// <summary>The box's own certificate: 397 days, the longest every browser accepts for a server certificate.</summary>
    public const int ServerDays = 397;

    /// <summary>The longest an issued certificate may live; Apple's platforms refuse longer ones even from a trusted private root.</summary>
    public const int MaxIssuedDays = 825;

    /// <summary>The intermediate is renewed this long before it ends; the box's certificate 30 days before.</summary>
    public static readonly TimeSpan IntermediateRenewal = TimeSpan.FromDays(365);
    public static readonly TimeSpan ServerRenewal = TimeSpan.FromDays(30);

    // Certificates start an hour early, so a device whose clock is a little behind accepts them at once.
    private static readonly TimeSpan _backdate = TimeSpan.FromHours(1);

    private static readonly Oid _serverAuth = new("1.3.6.1.5.5.7.3.1");

    public static SigningCertificate CreateRoot(string hostName, NameScope scope, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        ArgumentNullException.ThrowIfNull(scope);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest(AuthorityName("Root CA", hostName, key), key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        AddAuthorityExtensions(request, scope);
        using var created = request.CreateSelfSigned(now - _backdate, now + RootLifetime);
        return new SigningCertificate(X509CertificateLoader.LoadCertificate(created.RawData), key);
    }

    public static SigningCertificate CreateIntermediate(SigningCertificate root, string hostName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(AuthorityName("Intermediate CA", hostName, key), key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        AddAuthorityExtensions(request, NameScope.FromCertificate(root.Certificate));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root.Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var notAfter = Min(now + IntermediateLifetime, root.Certificate.NotAfter);
        var certificate = request.Create(root.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(root.Key), now - _backdate, notAfter, Serial());
        return new SigningCertificate(certificate, key);
    }

    /// <summary>A server certificate for <paramref name="names"/> (DNS names and addresses, the first one also the common name).</summary>
    public static IssuedPair Issue(SigningCertificate issuer, IReadOnlyList<string> names, int days, CertificateKeyType keyType, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfZero(names.Count);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(names[0]);
        var alternative = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var address))
            {
                alternative.AddIpAddress(address);
            }
            else
            {
                alternative.AddDnsName(name);
            }
        }
        using var ecdsa = keyType == CertificateKeyType.Ecdsa ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        using var rsa = keyType == CertificateKeyType.Rsa ? RSA.Create(2048) : null;
        var request = ecdsa is not null
            ? new CertificateRequest(subject.Build(), ecdsa, HashAlgorithmName.SHA256)
            : new CertificateRequest(subject.Build(), rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            rsa is null ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([_serverAuth], critical: false));
        request.CertificateExtensions.Add(alternative.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer.Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var notAfter = Min(now + TimeSpan.FromDays(days), issuer.Certificate.NotAfter);
        using var signed = request.Create(issuer.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(issuer.Key), now - _backdate, notAfter, Serial());
        var withKey = ecdsa is not null ? signed.CopyWithPrivateKey(ecdsa) : signed.CopyWithPrivateKey(rsa!);
        var privateKey = ecdsa is not null ? ecdsa.ExportPkcs8PrivateKeyPem() : rsa!.ExportPkcs8PrivateKeyPem();
        return new IssuedPair(withKey, Pem(withKey), privateKey + "\n");
    }

    /// <summary>The names a certificate covers: its DNS names, then its addresses.</summary>
    public static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate.Extensions["2.5.29.17"] is not { } extension)
        {
            return [];
        }
        var names = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
        return [.. names.EnumerateDnsNames(), .. names.EnumerateIPAddresses().Select(address => address.ToString())];
    }

    /// <summary>Whether <paramref name="certificate"/> was signed by <paramref name="issuer"/>'s key.</summary>
    public static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(issuer);
        var authorityKey = certificate.Extensions["2.5.29.35"] is { } aki ? new X509AuthorityKeyIdentifierExtension(aki.RawData, aki.Critical).KeyIdentifier : null;
        var subjectKey = issuer.Extensions["2.5.29.14"] is { } ski ? new X509SubjectKeyIdentifierExtension(new AsnEncodedData(ski.Oid, ski.RawData), ski.Critical).SubjectKeyIdentifierBytes : (ReadOnlyMemory<byte>?)null;
        return certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)
            && authorityKey is { } a && subjectKey is { } s && a.Span.SequenceEqual(s.Span);
    }

    /// <summary>The certificate in PEM with a final line break, so several of them can follow each other.</summary>
    public static string Pem(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.ExportCertificatePem() + "\n";
    }

    /// <summary>The SHA-256 fingerprint as clients show it: hex pairs with colons.</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(part => part.ToString("X2", CultureInfo.InvariantCulture)));
    }

    // "Pithyx Root CA pithyx 3f2a": the host name and the start of the key id tell several boxes apart in a trust store.
    private static X500DistinguishedName AuthorityName(string kind, string hostName, ECDsa key)
    {
        var keyId = new X509SubjectKeyIdentifierExtension(new PublicKey(key), critical: false).SubjectKeyIdentifier![..4].ToLowerInvariant();
        var name = new X500DistinguishedNameBuilder();
        name.AddOrganizationName("Pithyx");
        name.AddCommonName($"Pithyx {kind} {hostName} {keyId}");
        return name.Build();
    }

    private static void AddAuthorityExtensions(CertificateRequest request, NameScope scope)
    {
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (scope.ToExtension() is { } constraints)
        {
            request.CertificateExtensions.Add(constraints);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset wanted, DateTime issuerEnd)
    {
        var end = new DateTimeOffset(issuerEnd.ToUniversalTime(), TimeSpan.Zero);
        return wanted < end ? wanted : end;
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
        return serial;
    }
}
