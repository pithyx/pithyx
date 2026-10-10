using System.Text.Json;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Tests.Apps.Manifest;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

/// <summary>The corpus in <c>tools/release/fixtures</c>, which <c>tools/release/update-file.test.ts</c> runs as well.</summary>
public sealed class UpdateCorpusTests
{
    private static string File(string name) => Path.Combine(RepositoryFiles.Root, "tools", "release", "fixtures", name);

    private static JsonElement Json(string path)
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static TheoryData<string> Cases =>
        [.. Json(File("cases.json")).EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];

    internal static List<TrustedKey> Keyring(string path) =>
        [.. Json(path).GetProperty("keys").EnumerateArray().Select(key => new TrustedKey(
            key.GetProperty("keyId").GetString()!,
            key.GetProperty("publicKey").GetString()!,
            [.. key.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()!)]))];

    [Theory]
    [MemberData(nameof(Cases))]
    public void DocumentsGetTheExpectedVerdict(string name)
    {
        var entry = Json(File("cases.json")).EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var bytes = System.IO.File.ReadAllBytes(File(entry.GetProperty("document").GetString()!));
        var signatureName = entry.GetProperty("signature");
        var signature = signatureName.ValueKind == JsonValueKind.Null ? null : System.IO.File.ReadAllBytes(File(signatureName.GetString()!));
        var boxChannel = entry.GetProperty("boxChannel").GetString()!;
        var keyring = Keyring(File("keys.json"));
        var expected = entry.GetProperty("expected").EnumerateArray().ToList();

        var issues = entry.GetProperty("kind").GetString() == "manifest"
            ? UpdateDocuments.CheckManifest(bytes, signature, keyring, boxChannel,
                entry.TryGetProperty("allowUnsigned", out var allow) && allow.GetBoolean()).Issues
            : UpdateDocuments.CheckChannel(bytes, signature, keyring, boxChannel,
                entry.TryGetProperty("knownSequence", out var known) ? known.GetInt64() : null).Issues;

        Corpus.AssertExactly(issues, expected);
    }

    [Fact]
    public void TheBuiltInKeysAreTheKeysOfTheReleaseTools()
    {
        var released = Keyring(Path.Combine(RepositoryFiles.Root, "tools", "release", "keys.json"));

        Assert.Equal(
            released.Select(key => (key.KeyId, key.PublicKey, string.Join(',', key.Channels))),
            UpdateKeys.Project.Select(key => (key.KeyId, key.PublicKey, string.Join(',', key.Channels))));
    }
}
