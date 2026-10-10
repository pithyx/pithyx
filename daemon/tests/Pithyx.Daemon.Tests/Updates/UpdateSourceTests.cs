using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

/// <summary>
/// Switching the update source (D339): the corpus in <c>tools/release/fixtures/source</c>, which
/// <c>tools/release/update-file.test.ts</c> runs as well, the candidate Settings saves, the trust after a switch and the
/// recovery menu, the only place that switches.
/// </summary>
public sealed class UpdateSourceTests : IDisposable
{
    private const string Release = "sixNtoP5AFf5nH7eNZ5NerpiyWVk5qSoPb6wT5gO64M=";
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-source-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Corpus(string name) => Path.Combine(RepositoryFiles.Root, "tools", "release", "fixtures", "source", name);

    public static TheoryData<string> Cases =>
        [.. JsonDocument.Parse(File.ReadAllText(Corpus("cases.json"))).RootElement.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SourcesGetTheExpectedVerdict(string name)
    {
        var entry = JsonDocument.Parse(File.ReadAllText(Corpus("cases.json"))).RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == name);

        var issues = UpdateSources.Check(File.ReadAllBytes(Corpus(entry.GetProperty("document").GetString()!))).Issues;

        Apps.Manifest.Corpus.AssertExactly(issues, [.. entry.GetProperty("expected").EnumerateArray()]);
    }

    [Fact]
    public async Task SettingsSavesACheckedCandidateWithoutChangingTheSource()
    {
        using var factory = new DaemonFactory();
        using var admin = factory.CreateApiClient();
        await admin.SetupAdminAsync();

        using (var refused = await admin.PutAsync("/api/system/updates/source", new ByteArrayContent(File.ReadAllBytes(Corpus("http.json"))), Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("update.source.invalid", await refused.ErrorCodeAsync());
        }
        using var saved = await admin.PutAsync("/api/system/updates/source", new ByteArrayContent(File.ReadAllBytes(Corpus("valid.json"))), Token);
        var status = await saved.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Example fork", status.GetProperty("sourceCandidate").GetProperty("name").GetString());
        Assert.True(status.GetProperty("source").GetProperty("official").GetBoolean());
        Assert.Equal(UpdateSources.ProjectName, status.GetProperty("source").GetProperty("name").GetString());

        using var discarded = await admin.DeleteAsync("/api/system/updates/source", Token);
        Assert.Equal(JsonValueKind.Null, (await discarded.JsonAsync()).GetProperty("sourceCandidate").ValueKind);
    }

    [Fact]
    public async Task AfterASwitchOnlyTheSourcesKeysCountAndAdministratorsHearOfIt()
    {
        var data = Directory.CreateTempSubdirectory("pithyx-test-").FullName;
        using (var before = new DaemonFactory { DataDirectory = data, KeepDataDirectory = true })
        {
            using var admin = before.CreateApiClient();
            await admin.SetupAdminAsync();
        }
        // What the recovery menu writes: a source whose only key is not the one the test file is signed with.
        await UpdateSources.SwitchAsync(Path.Combine(data, "update-source.json"), Path.Combine(data, "update", UpdateSources.CandidateFileName),
            UpdateSources.Check(File.ReadAllBytes(Corpus("valid.json"))).Document, Token);

        using var after = new DaemonFactory { DataDirectory = data };
        using var client = after.CreateApiClient();
        (await client.LoginAsync("admin", AuthApi.AdminPassword)).Dispose();
        (await client.PutAsJsonAsync("/api/system/updates/settings", new { checkEnabled = true, channel = "beta" }, Token)).Dispose();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/updates", Token);
        using var upload = await client.PutAsync("/api/system/updates/file", new ByteArrayContent(TestUpdate.Build(minimum: "0.0.1")), Token);

        Assert.False(status.GetProperty("source").GetProperty("official").GetBoolean());
        Assert.Equal("Example fork", status.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(Release, Assert.Single(status.GetProperty("source").GetProperty("keys").EnumerateArray()).GetProperty("publicKey").GetString());
        // The built-in test-beta key would take this file; after the switch it is refused.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Equal("update.file.signature_invalid", await upload.ErrorCodeAsync());
        var notifications = await client.GetFromJsonAsync<JsonElement>("/api/notifications", Token);
        Assert.Single(notifications.GetProperty("items").EnumerateArray(), item => item.GetProperty("code").GetString() == UpdateService.SourceChangedCode);
    }

    private RecoveryMenu Menu(string input, StringWriter output)
    {
        var paths = new UpdatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "cache"), Path.Combine(_root, "run"), Path.Combine(_root, "top"),
            Path.Combine(_root, "release"), Path.Combine(_root, "daemon"));
        File.WriteAllText(paths.Release, "PITHYX_VERSION=0.1.0\n");
        var commands = new ScriptedCommandRunner { Handler = _ => new CommandResult(0, "", "", TimeSpan.Zero) };
        var system = new UpdateSystem(commands, paths, new FakeTimeProvider(), NullLogger<UpdateSystem>.Instance);
        return new RecoveryMenu(system, commands, NullLoggerFactory.Instance, new StringReader(input), output, Path.Combine(_root, "update-source.json"));
    }

    [Fact]
    public async Task TheRecoveryMenuSwitchesToATypedSourceAndBack()
    {
        var sourceFile = Path.Combine(_root, "update-source.json");
        using var output = new StringWriter();

        // Change the update source, type it, confirm with its name; then the menu ends with the input.
        await Menu($"5\n3\nMy fork\nhttps://example.org/channel-{{channel}}/channel.json\nmy-key\n{Release}\n\n\nMy fork\n\n", output).RunAsync(Token);

        var written = UpdateSourceConfig.Load(sourceFile, UpdateSourceConfig.Default);
        Assert.Equal(("My fork", "https://example.org/channel-{channel}/channel.json"), (written.Name, written.Url));
        Assert.Equal("stable,beta", string.Join(',', Assert.Single(written.Keys!).Channels));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sourceFile));
        }

        await Menu("5\n4\nPithyx\n\n", output).RunAsync(Token);

        Assert.False(File.Exists(sourceFile));
        Assert.Contains("The box follows the Pithyx project from its next start.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecoveryMenuUsesTheFileSavedInSettingsOnlyWithItsNameTyped()
    {
        var sourceFile = Path.Combine(_root, "update-source.json");
        var candidate = Path.Combine(_root, "state", UpdateSources.CandidateFileName);
        await UpdateSources.SaveCandidateAsync(candidate, File.ReadAllBytes(Corpus("valid.json")), "admin", DateTimeOffset.UnixEpoch, Token);
        using var output = new StringWriter();

        await Menu("5\n2\nexample fork\n\n", output).RunAsync(Token);
        Assert.False(File.Exists(sourceFile));
        Assert.Contains("The name did not match; nothing changed.", output.ToString(), StringComparison.Ordinal);

        await Menu("5\n2\nExample fork\n\n", output).RunAsync(Token);
        Assert.Equal("Example fork", UpdateSourceConfig.Load(sourceFile, UpdateSourceConfig.Default).Name);
        Assert.False(File.Exists(candidate));
    }

    [Fact]
    public async Task ATypedSourceThatFailsItsCheckChangesNothing()
    {
        using var output = new StringWriter();

        await Menu($"5\n3\nMy fork\nhttp://example.org/channel-{{channel}}/channel.json\nmy-key\n{Release}\n\n\n\n", output).RunAsync(Token);

        Assert.False(File.Exists(Path.Combine(_root, "update-source.json")));
        Assert.Contains("update.source.invalid", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("{\"reason\":\"url\"}", output.ToString(), StringComparison.Ordinal);
    }
}
