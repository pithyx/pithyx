using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

/// <summary>The updater through its API in simulation: the check, an upload, the install job, the history and a rollback.</summary>
public sealed class UpdateApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public UpdateApiTests()
    {
        _admin = _factory.CreateApiClient();
        File.WriteAllText(Path.Combine(_factory.DataDirectory, "release"), "PITHYX_VERSION=0.1.0-dev.3\n");
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<JsonElement> StatusAsync() => await _admin.GetFromJsonAsync<JsonElement>("/api/system/updates", Token);

    private async Task SetChannelAsync(string channel, bool check = true)
    {
        using var response = await _admin.PutAsJsonAsync("/api/system/updates/settings", new { checkEnabled = check, channel }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> UploadAsync(byte[] file) =>
        await _admin.PutAsync("/api/system/updates/file", new ByteArrayContent(file), Token);

    private async Task<Job> FinishedAsync(HttpResponseMessage started, JobState expected = JobState.Succeeded)
    {
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.JsonAsync()).GetProperty("id").GetInt64();
        var jobs = _factory.Services.GetRequiredService<JobService>();
        Job? job = null;
        await JobTestSupport.WaitUntilAsync(async () => (job = await jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.True(job!.State == expected, $"{job.Kind} ended {job.State}: {job.ErrorCode}");
        return job;
    }

    /// <summary>A channel as the release tools publish it, signed with the corpus's beta key, in the simulation's source folder.</summary>
    private void PublishChannel(string fixture = "channel-beta")
    {
        var folder = Path.Combine(_factory.DataDirectory, "update-channels", "channel-beta");
        Directory.CreateDirectory(folder);
        var corpus = Path.Combine(RepositoryFiles.Root, "tools", "release", "fixtures");
        File.Copy(Path.Combine(corpus, fixture + ".json"), Path.Combine(folder, "channel.json"), overwrite: true);
        File.Copy(Path.Combine(corpus, fixture + ".sig"), Path.Combine(folder, "channel.json.sig"), overwrite: true);
    }

    [Fact]
    public async Task OnlyAdministratorsSeeUpdates()
    {
        await _admin.SetupAdminAsync();
        using var user = _factory.CreateApiClient();
        using var response = await user.GetAsync("/api/system/updates", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheCheckIsOnByDefaultOnTheStableChannel()
    {
        await _admin.SetupAdminAsync();

        var status = await StatusAsync();

        Assert.True(status.GetProperty("checkEnabled").GetBoolean());
        Assert.Equal("stable", status.GetProperty("channel").GetString());
        Assert.True(status.GetProperty("supported").GetBoolean());
        Assert.Equal(0, status.GetProperty("history").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("nextCheckAt").ValueKind);
    }

    [Fact]
    public async Task AnUnreachableSourceIsAProblemNotAnError()
    {
        await _admin.SetupAdminAsync();

        using var response = await _admin.PostAsync("/api/system/updates/check", null, Token);
        var status = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("update.source.unreachable", status.GetProperty("problem").GetProperty("code").GetString());
        Assert.Equal("missing", status.GetProperty("problem").GetProperty("params").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("available").ValueKind);
    }

    [Fact]
    public async Task TheCheckFindsANewerVersionAndNotifiesAdministratorsOnce()
    {
        await _admin.SetupAdminAsync();
        await SetChannelAsync("beta");
        PublishChannel();

        using (await _admin.PostAsync("/api/system/updates/check", null, Token))
        {
        }
        using (await _admin.PostAsync("/api/system/updates/check", null, Token))
        {
        }
        var status = await StatusAsync();

        Assert.Equal("0.2.0-beta.3", status.GetProperty("available").GetProperty("version").GetString());
        // The corpus's channel needs at least 0.1.0 and offers 0.2.0-beta.3, and the box under test is a 0.1.0 prerelease.
        Assert.False(status.GetProperty("available").GetProperty("installable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("problem").ValueKind);
        var notifications = await _admin.GetFromJsonAsync<JsonElement>("/api/notifications", Token);
        Assert.Single(notifications.GetProperty("items").EnumerateArray(), item => item.GetProperty("code").GetString() == UpdateService.AvailableCode);
    }

    [Fact]
    public async Task AnOlderChannelIsAReplay()
    {
        await _admin.SetupAdminAsync();
        await SetChannelAsync("beta");
        PublishChannel();
        using (await _admin.PostAsync("/api/system/updates/check", null, Token))
        {
        }
        await using (var connection = await _factory.Services.GetRequiredService<Pithyx.Daemon.Database.SqliteConnectionFactory>().OpenAsync(Token))
        {
            await Dapper.SqlMapper.ExecuteAsync(connection, "UPDATE settings SET value = '41' WHERE key = 'updates.sequence.beta'");
        }

        using var response = await _admin.PostAsync("/api/system/updates/check", null, Token);
        var status = await response.JsonAsync();

        Assert.Equal("update.channel.sequence_old", status.GetProperty("problem").GetProperty("code").GetString());
        // What the box knew stays.
        Assert.Equal("0.2.0-beta.3", status.GetProperty("available").GetProperty("version").GetString());
    }

    [Fact]
    public async Task AStableBoxRefusesABetaFile()
    {
        await _admin.SetupAdminAsync();

        using var response = await UploadAsync(TestUpdate.Build(minimum: "0.0.1"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("update.file.signature_invalid", await response.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, (await StatusAsync()).GetProperty("staged").ValueKind);
    }

    [Fact]
    public async Task AnUploadedFileInstallsAsAJobAndLandsInTheHistory()
    {
        await _admin.SetupAdminAsync();
        await SetChannelAsync("beta");

        using var uploaded = await UploadAsync(TestUpdate.Build(minimum: "0.0.1"));
        var staged = (await uploaded.JsonAsync()).GetProperty("staged");
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal("0.2.0-beta.3", staged.GetProperty("version").GetString());
        Assert.True(staged.GetProperty("trustedSignature").GetBoolean());

        var job = await FinishedAsync(await _admin.PostAsJsonAsync("/api/system/updates/install", new { source = "file", includeDebian = true }, Token));
        var status = await StatusAsync();

        Assert.Equal(UpdateService.UpdateJob, job.Kind);
        var entry = Assert.Single(status.GetProperty("history").EnumerateArray());
        Assert.Equal(("update", "file", "succeeded", "0.2.0-beta.3"),
            (entry.GetProperty("kind").GetString(), entry.GetProperty("source").GetString(), entry.GetProperty("state").GetString(), entry.GetProperty("toVersion").GetString()));
        Assert.Single(status.GetProperty("snapshots").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("runningJobId").ValueKind);
    }

    [Fact]
    public async Task InstallingWithoutAnUploadOrANewerVersionIsRefused()
    {
        await _admin.SetupAdminAsync();

        using var file = await _admin.PostAsJsonAsync("/api/system/updates/install", new { source = "file", includeDebian = false }, Token);
        using var online = await _admin.PostAsJsonAsync("/api/system/updates/install", new { source = "online", includeDebian = false }, Token);

        Assert.Equal("update.file.not_found", await file.ErrorCodeAsync());
        Assert.Equal("update.none.available", await online.ErrorCodeAsync());
    }

    [Fact]
    public async Task ARollbackNeedsTheSnapshotsVersionTyped()
    {
        await _admin.SetupAdminAsync();
        await SetChannelAsync("beta");
        using (await UploadAsync(TestUpdate.Build(minimum: "0.0.1")))
        {
        }
        await FinishedAsync(await _admin.PostAsJsonAsync("/api/system/updates/install", new { source = "file", includeDebian = false }, Token));
        var snapshot = (await StatusAsync()).GetProperty("snapshots")[0];
        var name = snapshot.GetProperty("name").GetString();

        using var wrong = await _admin.PostAsJsonAsync("/api/system/updates/rollback", new { snapshot = name, version = "9.9.9" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("update.rollback.unconfirmed", await wrong.ErrorCodeAsync());

        await FinishedAsync(await _admin.PostAsJsonAsync("/api/system/updates/rollback", new { snapshot = name, version = snapshot.GetProperty("version").GetString() }, Token));
        var history = (await StatusAsync()).GetProperty("history");

        Assert.Equal(("rollback", "manual", "succeeded"),
            (history[0].GetProperty("kind").GetString(), history[0].GetProperty("source").GetString(), history[0].GetProperty("state").GetString()));
    }

    [Fact]
    public async Task AnUnknownSnapshotIsNotFound()
    {
        await _admin.SetupAdminAsync();

        using var response = await _admin.PostAsJsonAsync("/api/system/updates/rollback", new { snapshot = "20261009T000000Z", version = "0.1.0" }, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("update.snapshot.not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task SwitchingTheCheckOffClearsTheNextCheck()
    {
        await _admin.SetupAdminAsync();

        await SetChannelAsync("stable", check: false);
        var status = await StatusAsync();

        Assert.False(status.GetProperty("checkEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("nextCheckAt").ValueKind);
    }
}
