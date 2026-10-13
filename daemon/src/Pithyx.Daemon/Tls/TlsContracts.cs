namespace Pithyx.Daemon.Tls;

/// <summary>Where the certificates live and what the daemon serves with them.</summary>
/// <param name="Directory">The daemon's secrets for TLS (<c>/var/lib/pithyx/tls</c> on a box).</param>
/// <param name="Serve">The web port speaks HTTPS (real mode); simulation and the installer stay on plain HTTP.</param>
/// <param name="ReadHost">Take the box's addresses from its network cards and its search domains from <c>/etc/resolv.conf</c>, and follow their changes.</param>
public sealed record TlsOptions(string Directory, bool Serve, bool ReadHost);

/// <summary>One certificate as Settings shows it.</summary>
/// <param name="Subject">The subject's distinguished name.</param>
/// <param name="Issuer">The issuer's distinguished name.</param>
/// <param name="Names">The DNS names and addresses it covers.</param>
/// <param name="NotBefore">The start of its validity.</param>
/// <param name="NotAfter">The end of its validity.</param>
/// <param name="Fingerprint">SHA-256 as hex pairs with colons.</param>
public sealed record CertificateInfo(string Subject, string Issuer, IReadOnlyList<string> Names, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Fingerprint);

/// <summary>The box's certificate authority.</summary>
/// <param name="Root">The root that devices trust; download it from <c>/api/system/tls/root-ca.crt</c>.</param>
/// <param name="Intermediate">The intermediate that signs every certificate; it is renewed a year before it ends.</param>
/// <param name="Restricted">Name Constraints limit what the authority may sign.</param>
/// <param name="Domains">The DNS suffixes it may sign, each with every name below it; empty when unrestricted.</param>
/// <param name="AddressRanges">The address ranges it may sign; empty when unrestricted.</param>
public sealed record AuthorityInfo(CertificateInfo Root, CertificateInfo Intermediate, bool Restricted, IReadOnlyList<string> Domains, IReadOnlyList<string> AddressRanges);

/// <summary>Which certificate the web interface and FTPS present.</summary>
public enum ActiveCertificateSource
{
    /// <summary>The box's own certificate from its authority.</summary>
    Authority,

    /// <summary>A certificate an administrator uploaded.</summary>
    Custom,
}

/// <summary>A certificate the authority issued for another device. Its key was handed out once and is not kept.</summary>
/// <param name="Id">Its number on the box.</param>
/// <param name="Certificate">The certificate.</param>
/// <param name="KeyType">The key it was made with.</param>
/// <param name="CreatedAt">When it was issued.</param>
/// <param name="CreatedBy">The administrator who issued it, if the account still had that name.</param>
/// <param name="CurrentAuthority">It chains to the current root; false after a new authority replaced the old one.</param>
public sealed record IssuedCertificate(long Id, CertificateInfo Certificate, CertificateKeyType KeyType, DateTimeOffset CreatedAt, string? CreatedBy, bool CurrentAuthority);

/// <summary>Settings > Certificates.</summary>
/// <param name="Https">The web interface speaks HTTPS on 7484 (80 and 443 redirect there); false only in development and simulation.</param>
/// <param name="Source">Which certificate the box presents.</param>
/// <param name="Active">The certificate the box presents now.</param>
/// <param name="Authority">The box's own authority.</param>
/// <param name="BoxNames">The names and addresses the box's own certificate covers.</param>
/// <param name="ExtraNames">Names an administrator added to the box's own certificate.</param>
/// <param name="SuggestedDomains">The DNS suffixes a new restricted authority starts with: the local ones, the host name and the network's search domains.</param>
/// <param name="Custom">The uploaded certificate, if any; it is presented while it is valid.</param>
/// <param name="Issued">The certificates issued for other devices, newest first.</param>
public sealed record TlsSettings(
    bool Https,
    ActiveCertificateSource Source,
    CertificateInfo Active,
    AuthorityInfo Authority,
    IReadOnlyList<string> BoxNames,
    IReadOnlyList<string> ExtraNames,
    IReadOnlyList<string> SuggestedDomains,
    CertificateInfo? Custom,
    IReadOnlyList<IssuedCertificate> Issued);

/// <summary>Names the box's own certificate covers besides its host name and addresses.</summary>
/// <param name="ExtraNames">DNS names or addresses inside the authority's scope, at most 20.</param>
public sealed record TlsNamesRequest(IReadOnlyList<string> ExtraNames);

/// <summary>A new authority; every device has to trust its root again.</summary>
/// <param name="Restricted">Limit the authority to local names and private addresses (recommended).</param>
/// <param name="Domains">With <paramref name="Restricted"/>: DNS suffixes besides the suggested ones, such as a domain of your own.</param>
public sealed record CreateAuthorityRequest(bool Restricted, IReadOnlyList<string> Domains);

/// <summary>A certificate for another device.</summary>
/// <param name="Names">DNS names (a leading <c>*.</c> for a wildcard) and addresses, 1 to 100, inside the authority's scope.</param>
/// <param name="ValidityDays">1 to 825 days, never past the intermediate's end.</param>
/// <param name="KeyType">ECDSA (default) or RSA for devices that take nothing else.</param>
public sealed record IssueCertificateRequest(IReadOnlyList<string> Names, int ValidityDays, CertificateKeyType KeyType);

/// <summary>A newly issued certificate with its files; the private key is in this answer only.</summary>
/// <param name="Certificate">The entry in the list.</param>
/// <param name="CertificatePem">The certificate followed by the intermediate, as servers take it.</param>
/// <param name="PrivateKeyPem">The private key (PKCS #8, unencrypted).</param>
public sealed record IssuedCertificateFiles(IssuedCertificate Certificate, string CertificatePem, string PrivateKeyPem);

/// <summary>A certificate of your own for the web interface and FTPS.</summary>
/// <param name="CertificatePem">The certificate, followed by its intermediates, in PEM.</param>
/// <param name="PrivateKeyPem">Its private key in PEM (PKCS #8, PKCS #1 or SEC 1), unencrypted.</param>
public sealed record CustomCertificateRequest(string CertificatePem, string PrivateKeyPem);
