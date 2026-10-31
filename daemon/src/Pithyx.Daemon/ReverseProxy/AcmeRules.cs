using System.Net;
using System.Net.Mail;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>What the box asks an ACME authority for and when (D378); pure functions.</summary>
public static class AcmeRules
{
    /// <summary>The longest wait after failures; the authority allows a few failed validations per name and hour.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(1);

    private static readonly TimeSpan _firstBackoff = TimeSpan.FromHours(1);

    // Names an authority never certifies: they only exist in local networks.
    private static readonly string[] _localSuffixes = [".local", ".lan", ".home", ".internal", ".localdomain", ".home.arpa", ".corp", ".test", ".invalid", ".localhost", ".example"];

    /// <summary>
    /// Refuses names no public authority certifies over HTTP-01: a wildcard (needs DNS-01), an address, a single label
    /// or a local suffix.
    /// </summary>
    public static void CheckDomains(IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        foreach (var domain in domains)
        {
            if (domain.StartsWith("*.", StringComparison.Ordinal))
            {
                throw new PithyxException(ErrorCodes.ProxyAcmeWildcard, new Dictionary<string, object?> { ["name"] = domain });
            }
            if (IPAddress.TryParse(domain, out _) || !domain.Contains('.', StringComparison.Ordinal)
                || _localSuffixes.Any(suffix => domain.EndsWith(suffix, StringComparison.Ordinal)))
            {
                throw new PithyxException(ErrorCodes.ProxyAcmeNameNotPublic, new Dictionary<string, object?> { ["name"] = domain });
            }
        }
    }

    /// <summary>The settings with the request applied; the directory must be <c>https</c>, the contact an address.</summary>
    public static AcmeSettings Apply(AcmeSettings saved, AcmeAccountRequest request, string? currentTerms)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(request);
        var directory = string.IsNullOrWhiteSpace(request.Directory) ? AcmeSettings.LetsEncrypt : request.Directory.Trim();
        if (!Uri.TryCreate(directory, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PithyxException(ErrorCodes.ProxyAcmeDirectoryInvalid);
        }
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (email is not null && (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 254))
        {
            throw new PithyxException(ErrorCodes.ProxyAcmeEmailInvalid);
        }
        // Another authority has other terms; an agreement never carries over.
        var terms = directory != saved.Directory ? null : saved.TermsAgreed;
        if (request.AgreeToTerms)
        {
            terms = currentTerms ?? throw new PithyxException(ErrorCodes.ProxyAcmeUnreachable, new Dictionary<string, object?> { ["detail"] = directory });
        }
        return new AcmeSettings(directory, email, terms);
    }

    /// <summary>
    /// When the box asks for a certificate next: at once without one or with other names, a third of its lifetime
    /// before its end (30 days of Let's Encrypt's 90), or after failures with a backoff that doubles from one hour to a day.
    /// </summary>
    public static DateTimeOffset NextAttempt(
        IReadOnlyList<string> domains,
        IReadOnlyList<string>? orderedDomains,
        DateTimeOffset? notBefore,
        DateTimeOffset? notAfter,
        DateTimeOffset? attemptedAt,
        int failures)
    {
        ArgumentNullException.ThrowIfNull(domains);
        DateTimeOffset due;
        if (notAfter is not { } end || orderedDomains is null || !orderedDomains.Order(StringComparer.Ordinal).SequenceEqual(domains.Order(StringComparer.Ordinal)))
        {
            due = DateTimeOffset.MinValue;
        }
        else
        {
            var lifetime = end - (notBefore ?? end.AddDays(-90));
            due = end - (lifetime / 3);
        }
        if (failures > 0 && attemptedAt is { } attempted)
        {
            var wait = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, _firstBackoff.Ticks << Math.Min(failures - 1, 10)));
            due = Max(due, attempted + wait);
        }
        return due;
    }

    /// <summary>
    /// The error code and parameters for an ACME problem type (RFC 8555, section 6.7) of the authority, naming the
    /// domain and the authority's own words.
    /// </summary>
    public static (string Code, IReadOnlyDictionary<string, object?> Params) Problem(string? type, string? detail, string? domain)
    {
        var code = (type ?? "").Replace("urn:ietf:params:acme:error:", "", StringComparison.Ordinal) switch
        {
            "dns" => ErrorCodes.ProxyAcmeDns,
            "connection" or "tls" or "incorrectResponse" => ErrorCodes.ProxyAcmeConnection,
            "unauthorized" => ErrorCodes.ProxyAcmeUnauthorized,
            "rateLimited" => ErrorCodes.ProxyAcmeRateLimited,
            "caa" => ErrorCodes.ProxyAcmeCaa,
            "rejectedIdentifier" or "unsupportedIdentifier" => ErrorCodes.ProxyAcmeNameRejected,
            _ => ErrorCodes.ProxyAcmeFailed,
        };
        return (code, new Dictionary<string, object?> { ["name"] = domain ?? "", ["detail"] = Shorten(detail) });
    }

    private static string Shorten(string? detail)
    {
        var text = (detail ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
}
