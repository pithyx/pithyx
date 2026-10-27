using System.Text.Json;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Tests.Apps.Manifest;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps.Compose;

/// <summary>Golden outputs in <c>daemon/tests/fixtures/compose-rewrite</c>: a case names a corpus file, a manifest and the host's decisions.</summary>
public sealed class ComposeRewriterTests
{
    private static string Fixtures => Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "compose-rewrite");

    public static TheoryData<string> Cases =>
        [.. Directory.GetFiles(Fixtures, "*.json").Where(path => !path.EndsWith(".expected.json", StringComparison.Ordinal)).Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal)];

    private static (ComposeMap Document, JsonElement Manifest, ComposeHost Host) Load(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, name + ".json")));
        var root = document.RootElement;
        var manifest = ComposeValidatorTests.Manifest(root.GetProperty("manifest").GetString()!);
        var result = ComposeValidator.Validate(File.ReadAllBytes(Corpus.Directory("compose", root.GetProperty("compose").GetString()!)), manifest);
        Assert.True(result.IsValid);
        var host = root.GetProperty("host");
        return (result.Document!, manifest, new ComposeHost(
            host.GetProperty("uid").GetInt32(),
            host.GetProperty("appDirectory").GetString()!,
            host.GetProperty("bridgeName").GetString()!,
            host.GetProperty("imageIds").EnumerateObject().ToDictionary(image => image.Name, image => image.Value.GetString()!),
            host.GetProperty("storage").EnumerateObject().ToDictionary(
                mapping => mapping.Name,
                mapping => new StorageMapping(
                    mapping.Value.GetProperty("hostPath").GetString()!,
                    mapping.Value.GetProperty("readOnly").GetBoolean(),
                    mapping.Value.GetProperty("gid").ValueKind == JsonValueKind.Number ? mapping.Value.GetProperty("gid").GetInt32() : null)),
            host.TryGetProperty("internetBridgeName", out var internet) ? internet.GetString() : null,
            host.TryGetProperty("elevated", out var elevated) ? elevated.EnumerateArray().Select(right => right.GetString()!).ToHashSet(StringComparer.Ordinal) : null,
            host.TryGetProperty("deviceGroups", out var groups) ? groups.EnumerateObject().ToDictionary(group => group.Name, group => group.Value.GetInt32()) : null));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RewritesToTheGoldenOutput(string name)
    {
        var (document, manifest, host) = Load(name);

        var output = ComposeRewriter.Rewrite(document, manifest, host);

        Assert.Equal(File.ReadAllText(Path.Combine(Fixtures, name + ".expected.json")), output);
    }

    [Fact]
    public void ARootServiceKeepsDockersCapabilities()
    {
        var (document, manifest, host) = Load("full");
        var backend = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(manifest.GetProperty("backend").GetRawText())!;
        backend["root"] = JsonSerializer.SerializeToElement(true);
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(manifest.GetRawText())!;
        fields["backend"] = JsonSerializer.SerializeToElement(backend);

        using var output = JsonDocument.Parse(ComposeRewriter.Rewrite(document, JsonSerializer.SerializeToElement(fields), host));

        var services = output.RootElement.GetProperty("services");
        Assert.Equal("0:0", services.GetProperty("db").GetProperty("user").GetString());
        Assert.False(services.GetProperty("db").TryGetProperty("cap_drop", out _));
        Assert.Equal(["ALL"], services.GetProperty("server").GetProperty("cap_drop").EnumerateArray().Select(item => item.GetString()!));
    }

    [Fact]
    public void ARequiredStorageMustBeMapped()
    {
        var (document, manifest, host) = Load("storage");

        Assert.Throws<ArgumentException>(() => ComposeRewriter.Rewrite(document, manifest, host with { Storage = new Dictionary<string, StorageMapping>() }));
    }

    [Fact]
    public void EveryImageNeedsACheckedId()
    {
        var (document, manifest, host) = Load("full");

        Assert.Throws<ArgumentException>(() => ComposeRewriter.Rewrite(document, manifest, host with { ImageIds = new Dictionary<string, string>() }));
    }

    [Theory]
    [InlineData("com.example.chat", "pithyx-com_example_chat")]
    [InlineData("io.github.jan.my-app", "pithyx-io_github_jan_my-app")]
    public void ProjectNamesReplaceDots(string appId, string project) => Assert.Equal(project, ComposeRewriter.ProjectName(appId));
}
