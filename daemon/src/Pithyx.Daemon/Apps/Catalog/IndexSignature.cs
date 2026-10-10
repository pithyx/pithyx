using NSec.Cryptography;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>A key whose signatures a box accepts on the given channels.</summary>
/// <param name="KeyId">The name of the key, as <c>index.json.sig</c> names it.</param>
/// <param name="PublicKey">The 32 bytes of the Ed25519 public key in base64.</param>
/// <param name="Channels">The channels (<c>stable</c>, <c>beta</c>) the key is trusted for.</param>
public sealed record TrustedKey(string KeyId, string PublicKey, IReadOnlyList<string> Channels);

/// <summary>
/// Verifies <c>index.json.sig</c> (<c>sdk/schema/index-signature.v1.schema.json</c>) over the exact bytes of
/// <c>index.json</c>, before anything parses the index, with the same verdicts as <c>@pithyx/cli</c>.
/// </summary>
public static class IndexSignature
{
    private static readonly Lazy<JsonSchemaCheck> _schema = new(() => JsonSchemaCheck.Load("index-signature.v1.schema.json"));

    /// <summary>
    /// At least one signature by a key trusted for <paramref name="channel"/>, and every signature by such a key valid;
    /// signatures by other keys are ignored, so a catalog can sign with an old and a new key while boxes move to the new
    /// one. <paramref name="signature"/> is null when the branch has no signature file.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Verify(
        ReadOnlyMemory<byte> index,
        byte[]? signature,
        IReadOnlyList<TrustedKey> keyring,
        string channel) =>
        Reason(index, signature, keyring, channel) is { } reason ? Invalid(reason) : [];

    /// <summary>
    /// Why a signature file in this format does not prove <paramref name="document"/> for <paramref name="channel"/>
    /// (<c>missing</c>, <c>malformed</c>, <c>untrusted</c> or <c>mismatch</c>), or null when it does. Update files and
    /// channels are signed the same way (D338).
    /// </summary>
    public static string? Reason(
        ReadOnlyMemory<byte> document,
        byte[]? signature,
        IReadOnlyList<TrustedKey> keyring,
        string channel)
    {
        ArgumentNullException.ThrowIfNull(keyring);
        if (signature is null)
        {
            return "missing";
        }
        using var parsed = JsonSchemaCheck.ParseStrict(signature);
        if (parsed is null || !_schema.Value.IsValid(parsed.RootElement))
        {
            return "malformed";
        }
        var trusted = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keyring.Where(key => key.Channels.Contains(channel)))
        {
            trusted[key.KeyId] = key;
        }
        var relevant = parsed.RootElement.GetProperty("signatures").EnumerateArray()
            .Select(entry => (KeyId: entry.GetProperty("keyId").GetString()!, Signature: entry.GetProperty("signature").GetString()!))
            .Where(entry => trusted.ContainsKey(entry.KeyId))
            .ToList();
        if (relevant.Count == 0)
        {
            return "untrusted";
        }
        foreach (var (keyId, value) in relevant)
        {
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, Convert.FromBase64String(trusted[keyId].PublicKey), KeyBlobFormat.RawPublicKey);
            if (!SignatureAlgorithm.Ed25519.Verify(publicKey, document.Span, Convert.FromBase64String(value)))
            {
                return "mismatch";
            }
        }
        return null;
    }

    /// <summary>Reads a signed index as the box does: the signature first, then the index itself.</summary>
    public static CatalogValidationResult CheckSignedIndex(
        ReadOnlyMemory<byte> index,
        byte[]? signature,
        IReadOnlyList<TrustedKey> keyring,
        IndexExpectation expected)
    {
        var issues = Verify(index, signature, keyring, expected.Channel);
        return issues.Count > 0 ? new CatalogValidationResult(issues, null) : CatalogValidator.ValidateIndex(index, expected);
    }

    private static List<ValidationIssue> Invalid(string reason) => [new(ErrorCodes.StoreCatalogSignatureInvalid, ("reason", reason))];
}
