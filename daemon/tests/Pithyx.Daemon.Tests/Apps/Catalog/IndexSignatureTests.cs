using System.Text.Json;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Tests.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Catalog;

public sealed class IndexSignatureTests
{
    public static TheoryData<string> Cases =>
        [.. Corpus.Json("catalog", "signatures", "cases.json").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];

    /// <summary>Every test key of the corpus with its channels; test-rogue is trusted on none.</summary>
    private static List<TrustedKey> Keyring() =>
        [.. Corpus.Json("catalog", "keys.json").GetProperty("keys").EnumerateArray().Select(key => new TrustedKey(
            key.GetProperty("keyId").GetString()!,
            key.GetProperty("publicKey").GetString()!,
            [.. key.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()!)]))];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SignedIndexesGetTheExpectedVerdict(string name)
    {
        var entry = Corpus.Json("catalog", "signatures", "cases.json").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var signature = entry.GetProperty("signature");
        var expected = entry.GetProperty("expected").EnumerateArray().ToList();

        var result = IndexSignature.CheckSignedIndex(
            Corpus.Read("catalog", "signatures", entry.GetProperty("index").GetString()!),
            signature.ValueKind == JsonValueKind.Null ? null : Corpus.Read("catalog", "signatures", signature.GetString()!),
            Keyring(),
            new IndexExpectation(
                entry.GetProperty("catalog").GetString()!,
                entry.GetProperty("channel").GetString()!,
                entry.TryGetProperty("knownSequence", out var known) ? known.GetInt64() : null));

        Corpus.AssertExactly(result.Issues, expected);
        Assert.Equal(expected.Count > 0, result.Document is null);
    }
}
