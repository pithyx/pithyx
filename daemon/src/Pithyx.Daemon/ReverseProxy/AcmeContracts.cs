namespace Pithyx.Daemon.ReverseProxy;

/// <summary>The ACME account of the box (D378): which authority, an optional contact, and whether its terms are agreed.</summary>
/// <param name="Directory">The authority's directory URL; Let's Encrypt unless an administrator names another.</param>
/// <param name="Email">A contact address the authority may write to, or none.</param>
/// <param name="TermsAgreed">The address of the terms of service as they were agreed, or none.</param>
public sealed record AcmeSettings(string Directory, string? Email, string? TermsAgreed)
{
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";
    public static readonly AcmeSettings Default = new(LetsEncrypt, null, null);
}

/// <summary>Settings &gt; Reverse proxy, the certificates from an ACME authority.</summary>
/// <param name="Directory">The authority's directory URL.</param>
/// <param name="Email">The contact address, or none.</param>
/// <param name="TermsOfService">The authority's current terms of service, if its directory could be read.</param>
/// <param name="TermsAgreed">An administrator agreed to the terms; hosts may use the authority.</param>
/// <param name="Reachable">The directory could be read just now.</param>
public sealed record AcmeAccount(string Directory, string? Email, string? TermsOfService, bool TermsAgreed, bool Reachable);

/// <summary>New ACME settings.</summary>
/// <param name="Directory">An <c>https</c> directory URL, or none for Let's Encrypt.</param>
/// <param name="Email">A contact address, or none.</param>
/// <param name="AgreeToTerms">The administrator agrees to the authority's current terms of service.</param>
public sealed record AcmeAccountRequest(string? Directory = null, string? Email = null, bool AgreeToTerms = false);

/// <summary>Where the certificate of a host from the ACME authority stands.</summary>
/// <param name="Status"><c>pending</c> before the first certificate, <c>valid</c> or <c>failed</c> after the last try.</param>
/// <param name="NotAfter">The end of the certificate the host presents, if it has one.</param>
/// <param name="AttemptedAt">When the box last asked the authority.</param>
/// <param name="RetryAt">When the box asks again by itself: before the end of the certificate, or later after failures.</param>
/// <param name="Running">A request to the authority runs now.</param>
/// <param name="ErrorCode">Why the last try failed (an error code of <c>proxy.acme.*</c>).</param>
/// <param name="ErrorParams">Its parameters, for example the name and the authority's own words.</param>
public sealed record AcmeCertificateStatus(
    string Status,
    DateTimeOffset? NotAfter,
    DateTimeOffset? AttemptedAt,
    DateTimeOffset? RetryAt,
    bool Running,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?>? ErrorParams);
