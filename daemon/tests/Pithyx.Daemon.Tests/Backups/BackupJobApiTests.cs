using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>Backup jobs through the API (#394): repositories, runs from snapshots, retention, checks and their events.</summary>
public sealed class BackupJobApiTests : IAsyncLifetime
{
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
    private static readonly CommandResult Ok = new(0, "", "", TimeSpan.Zero);

    private readonly DaemonFactory _factory = new();
    private readonly ScriptedCommandRunner _commands = new();
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-backup-").FullName;
    private WebApplicationFactory<Program> _daemon = null!;
    private HttpClient _admin = null!;
    private long _targetId;
    private long _shareId;

    private CommandResult _init = Ok;
    private CommandResult _verify = Ok;
    private CommandResult _check = Ok;
    private CommandResult _backup = Ok;
    private string _snapshots = "";

    private string SharePath => Path.Combine(_root, "media");
    private string CacheRoot => Path.Combine(_root, "cache");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(SharePath, "Photos"));
        Directory.CreateDirectory(CacheRoot);
        _backup = new CommandResult(0, Fixture("restic-backup-warning.stdout"), "", TimeSpan.Zero);
        _commands.Handler = Answer;
        _daemon = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new BackupSsh(_commands));
            services.AddSingleton(new BackupRestic(_commands));
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<BackupJobService>(provider, (ICommandRunner)_commands));
        }));
        _admin = _daemon.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        await _admin.SetupAdminAsync();
        using (var target = await _admin.PostAsync("/api/backups/targets", new { name = "Box", host = "u1.example.de", user = "u1", path = "backups", hostKey = HostKey }))
        {
            Assert.Equal(HttpStatusCode.Created, target.StatusCode);
            _targetId = (await target.JsonAsync()).GetProperty("id").GetInt64();
        }
        // The simulation's pool pithyxtestm is mounted, so a share on it is available.
        _shareId = await _factory.ScalarAsync<long>(
            "INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES ('Media', @path, 'pithyxtestm', 'x', 'x') RETURNING id", new { path = SharePath });
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _daemon.DisposeAsync();
        await _factory.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ANewJobMakesItsRepositoryAndShowsItsKeyOnlyOnce()
    {
        var created = await CreateAsync("Media");

        var key = created.GetProperty("key").GetString()!;
        Assert.True(key.Length >= 40);
        var job = created.GetProperty("job");
        Assert.Equal("backups-media", job.GetProperty("repository").GetString());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("nextRunAt").ValueKind);
        var init = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("init"));
        Assert.Contains("sftp:pithyx-target:backups/backups-media", init.Arguments);
        Assert.DoesNotContain(key, string.Join(' ', init.Arguments), StringComparison.Ordinal);
        var list = await _admin.GetStringAsync("/api/backups/jobs", Token);
        Assert.DoesNotContain(key, list, StringComparison.Ordinal);
        Assert.Contains(("backup.job.created", "Media"), await AuditAsync());

        using var again = await _admin.PostAsync("/api/backups/jobs", Request("Other", repository: "backups-media"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ErrorCodes.BackupRepositoryExists, await again.ErrorCodeAsync());
        using var taken = await _admin.PostAsync("/api/backups/jobs", Request("MEDIA", repository: "other"));
        Assert.Equal(ErrorCodes.BackupJobNameTaken, await taken.ErrorCodeAsync());
    }

    [Fact]
    public async Task AnExistingRepositoryIsConnectedWithItsKeyAndNoKeyIsShown()
    {
        _verify = new CommandResult(12, "", "Fatal: wrong password or no key found\n", TimeSpan.Zero);
        using (var wrong = await _admin.PostAsync("/api/backups/jobs", Request("Media", existingKey: "nope")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal(ErrorCodes.BackupRepositoryWrongKey, await wrong.ErrorCodeAsync());
        }
        _verify = Ok;

        using var connected = await _admin.PostAsync("/api/backups/jobs", Request("Media", existingKey: "the old key"));

        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await connected.JsonAsync()).GetProperty("key").ValueKind);
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments.Contains("init"));
        Assert.Contains(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("cat"));
    }

    [Fact]
    public async Task ARepositoryThatIsAlreadyThereIsNotMadeAgain()
    {
        _init = new CommandResult(1, "", "Fatal: create repository at sftp:pithyx-target:backups/x failed: config file already exists\n", TimeSpan.Zero);

        using var response = await _admin.PostAsync("/api/backups/jobs", Request("Media"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.BackupRepositoryExists, await response.ErrorCodeAsync());
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM backup_jobs"));
    }

    [Fact]
    public async Task InvalidSettingsNameTheirField()
    {
        using var schedule = await _admin.PostAsync("/api/backups/jobs", Request("Media", schedule: "every day"));
        using var sources = await _admin.PostAsync("/api/backups/jobs", Request("Media", sources: new[] { new { shareId = 999, path = "" } }));
        using var path = await _admin.PostAsync("/api/backups/jobs", Request("Media", sources: new[] { new { shareId = _shareId, path = "../etc" } }));
        using var repository = await _admin.PostAsync("/api/backups/jobs", Request("Media", repository: "a/../b"));

        Assert.Equal("schedule", await FieldAsync(schedule));
        Assert.Equal("sources", await FieldAsync(sources));
        Assert.Equal("sources", await FieldAsync(path));
        Assert.Equal("repository", await FieldAsync(repository));
    }

    [Fact]
    public async Task ARunBacksUpTheSnapshotUnderTheLivePathAndAppliesTheRetention()
    {
        var id = await CreateJobAsync("Media", sources: new[] { new { shareId = _shareId, path = "Photos" } });
        _snapshots = "pithyxtestm@pithyx-daily-20261101T000000Z\npithyxtestm@pithyx-backup-20261101T000000Z\n";

        var run = await RunAsync(id);

        Assert.Equal("succeeded", run.GetProperty("state").GetString());
        Assert.Equal(33, run.GetProperty("files").GetInt64());
        Assert.Equal(90_600_000, run.GetProperty("bytes").GetInt64());
        Assert.Equal(64, run.GetProperty("snapshot").GetString()!.Length);
        var snapshot = Assert.Single(_commands.Calls, call => call is { FileName: "zfs", Arguments: ["snapshot", ..] }).Arguments[1];
        Assert.Matches("^pithyxtestm@pithyx-backup-[0-9]{8}T[0-9]{6}Z$", snapshot);
        var backup = Assert.Single(_commands.Calls, call => call.FileName == "unshare");
        Assert.Equal([snapshot, SharePath, "--", "env"], backup.Arguments.Skip(8).Take(4));
        Assert.Equal(Path.Combine(SharePath, "Photos"), backup.Arguments[^1]);
        Assert.Contains(Path.Combine(CacheRoot, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), backup.Arguments);
        var destroyed = _commands.Calls.Where(call => call is { FileName: "zfs", Arguments: ["destroy", ..] }).Select(call => call.Arguments[1]).ToList();
        Assert.Equal(["pithyxtestm@pithyx-backup-20261101T000000Z", snapshot], destroyed);
        var forget = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("forget"));
        Assert.Equal(["--keep-daily", "7", "--keep-weekly", "4", "--keep-monthly", "12"], forget.Arguments.SkipWhile(argument => argument != "--keep-daily"));
        var job = await JobAsync(id);
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("lastSuccessAt").ValueKind);
        Assert.False(job.GetProperty("overdue").GetBoolean());
        Assert.Equal(0, await NotificationsAsync());
    }

    [Fact]
    public async Task UnreadableFilesMakeAWarningAndTellTheAdministrators()
    {
        var id = await CreateJobAsync("Media");
        _backup = new CommandResult(3, Fixture("restic-backup-warning.stdout"), Fixture("restic-backup-warning.stderr"), TimeSpan.Zero);

        var run = await RunAsync(id);

        Assert.Equal("warning", run.GetProperty("state").GetString());
        Assert.Equal(1, run.GetProperty("warnings").GetInt32());
        Assert.Equal(1, await NotificationsAsync(BackupJobService.RunWarningCode));
        Assert.Contains(_commands.Calls, call => call.Arguments.Contains("forget"));
    }

    [Fact]
    public async Task AFailedRunKeepsTheRepositoryAndTellsTheAdministrators()
    {
        var id = await CreateJobAsync("Media");
        _backup = new CommandResult(1, "", "subprocess ssh: " + Fixture("sftp-hostkey.stderr") + "Fatal: unable to open repository\n", TimeSpan.Zero);

        var run = await RunAsync(id, JobState.Failed);

        Assert.Equal("failed", run.GetProperty("state").GetString());
        Assert.Equal(ErrorCodes.BackupTargetHostKeyChanged, run.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments.Contains("forget"));
        Assert.Contains(_commands.Calls, call => call is { FileName: "zfs", Arguments: ["destroy", ..] });
        Assert.Equal(1, await NotificationsAsync(BackupJobService.RunFailedCode));
        Assert.Equal(1, await NotificationsAsync(BackupJobService.HostKeyChangedCode));
        var target = JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/targets", Token)).RootElement.GetProperty("targets")[0];
        Assert.Equal(ErrorCodes.BackupTargetHostKeyChanged, target.GetProperty("lastTest").GetProperty("errorCode").GetString());

        await RunAsync(id, JobState.Failed);
        Assert.Equal(1, await NotificationsAsync(BackupJobService.HostKeyChangedCode));
    }

    [Fact]
    public async Task AFolderOutsideAPoolCannotBeBackedUp()
    {
        var plain = await _factory.ScalarAsync<long>(
            "INSERT INTO shares (name, path, created_at, updated_at) VALUES ('Plain', @path, 'x', 'x') RETURNING id", new { path = _root });
        var id = await CreateJobAsync("Plain", sources: new[] { new { shareId = plain, path = "" } });

        var run = await RunAsync(id, JobState.Failed);

        Assert.Equal(ErrorCodes.BackupSourceUnsupported, run.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(_commands.Calls, call => call.FileName == "unshare");
        var sources = JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/sources", Token)).RootElement.GetProperty("sources");
        Assert.False(sources.EnumerateArray().Single(source => source.GetProperty("name").GetString() == "Plain").GetProperty("supported").GetBoolean());
        Assert.True(sources.EnumerateArray().Single(source => source.GetProperty("name").GetString() == "Media").GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task OneRunAtATimeAndACancelledRunDropsItsSnapshot()
    {
        var id = await CreateJobAsync("Media");
        _commands.Hanging.Add("unshare --mount");

        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/run", new { });
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        var jobs = _daemon.Services.GetRequiredService<JobService>();
        await jobs.WaitForStateAsync(boardId, JobState.Running);
        await JobTestSupport.WaitUntilAsync(() => Task.FromResult(_commands.Calls.Any(call => call.FileName == "unshare")), "the backup to start");
        using (var busy = await _admin.PostAsync($"/api/backups/jobs/{id}/check", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal(ErrorCodes.BackupJobBusy, await busy.ErrorCodeAsync());
        }
        using (var deleted = await _admin.DeleteAsync($"/api/backups/jobs/{id}", Token))
        {
            Assert.Equal(ErrorCodes.BackupJobBusy, await deleted.ErrorCodeAsync());
        }
        using (var inUse = await _admin.DeleteAsync($"/api/backups/targets/{_targetId}", Token))
        {
            Assert.Equal(ErrorCodes.BackupTargetInUse, await inUse.ErrorCodeAsync());
        }

        using var cancel = await _admin.PostAsync($"/api/jobs/{boardId}/cancel", new { });
        await jobs.WaitForStateAsync(boardId, JobState.Cancelled);

        var run = (await RunsAsync(id))[0];
        Assert.Equal("cancelled", run.GetProperty("state").GetString());
        Assert.Contains(_commands.Calls, call => call is { FileName: "zfs", Arguments: ["destroy", ..] });
        Assert.Equal(0, await NotificationsAsync());
        using var free = await _admin.DeleteAsync($"/api/backups/jobs/{id}", Token);
        Assert.Equal(HttpStatusCode.NoContent, free.StatusCode);
    }

    [Fact]
    public async Task ACheckReadsItsShareOfTheDataAndAFailedCheckIsItsOwnEvent()
    {
        var id = await CreateJobAsync("Media", checkSchedule: "0 4 * * 0", checkReadPercent: 5);
        _check = new CommandResult(1, """{"message_type":"summary","num_errors":2}""" + "\n", "", TimeSpan.Zero);

        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/check", new { });
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, JobState.Failed);

        var check = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("check"));
        Assert.Contains("--read-data-subset=5%", check.Arguments);
        var job = await JobAsync(id);
        Assert.Equal(ErrorCodes.BackupCheckFailed, job.GetProperty("lastCheck").GetProperty("errorCode").GetString());
        Assert.Equal("check", job.GetProperty("lastCheck").GetProperty("kind").GetString());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("nextCheckAt").ValueKind);
        Assert.Equal(1, await NotificationsAsync(BackupJobService.CheckFailedCode));
    }

    [Fact]
    public async Task TheSchedulerStartsARunWhenItIsDueAndOnlyOnce()
    {
        var id = await CreateJobAsync("Media", schedule: "*/5 * * * *");
        var scheduler = _daemon.Services.GetRequiredService<BackupScheduler>();
        var due = await scheduler.TickAsync(Token);
        Assert.Empty(await RunsAsync(id));

        _factory.Time.Advance(due - _factory.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        await scheduler.TickAsync(Token);
        await JobTestSupport.WaitUntilAsync(async () => (await RunsAsync(id)) is [{ } run] && run.GetProperty("state").GetString() == "succeeded", "the scheduled run");
        await scheduler.TickAsync(Token);

        var runs = await RunsAsync(id);
        Assert.Equal("schedule", Assert.Single(runs).GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task AJobWithoutASuccessfulRunIsOverdueOnceUntilItSucceeds()
    {
        var id = await CreateJobAsync("Media", schedule: "0 3 * * *");
        var service = _daemon.Services.GetRequiredService<BackupJobService>();
        Assert.NotNull(await service.NotifyOverdueAsync(Token));

        // The scheduled runs of these days fail, so the job falls behind.
        _backup = new CommandResult(1, "", "Fatal: unexpected\n", TimeSpan.Zero);
        _factory.Time.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(1));
        await _daemon.Services.GetRequiredService<BackupScheduler>().TickAsync(Token);
        await JobTestSupport.WaitUntilAsync(async () => (await RunsAsync(id)).All(run => run.GetProperty("state").GetString() != "running"), "the scheduled run to end");
        await service.NotifyOverdueAsync(Token);
        await service.NotifyOverdueAsync(Token);

        Assert.Equal(1, await NotificationsAsync(BackupJobService.OverdueCode));
        Assert.True((await JobAsync(id)).GetProperty("overdue").GetBoolean());
        _backup = new CommandResult(0, Fixture("restic-backup-warning.stdout"), "", TimeSpan.Zero);
        await RunAsync(id);
        Assert.False((await JobAsync(id)).GetProperty("overdue").GetBoolean());
    }

    [Fact]
    public async Task ARunLeftRunningByARestartBecomesInterrupted()
    {
        var id = await CreateJobAsync("Media");
        await _factory.ScalarAsync<long>(
            "INSERT INTO backup_runs (job_id, kind, trigger, state, started_at) VALUES (@id, 'backup', 'schedule', 'running', '2026-11-01T00:00:00.000Z') RETURNING id", new { id });

        await _daemon.Services.GetRequiredService<BackupJobService>().RecoverAsync(Token);

        var run = Assert.Single(await RunsAsync(id));
        Assert.Equal("interrupted", run.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("finishedAt").ValueKind);
    }

    private object Request(
        string name,
        string? repository = null,
        string? existingKey = null,
        string schedule = "0 3 * * *",
        object? sources = null,
        string? checkSchedule = null,
        int checkReadPercent = 0) => new
        {
            targetId = _targetId,
            repository = repository ?? "backups-" + name.ToLowerInvariant(),
            existingKey,
            settings = new
            {
                name,
                sources = sources ?? new[] { new { shareId = _shareId, path = "" } },
                schedule,
                enabled = true,
                retention = new { last = 0, hourly = 0, daily = 7, weekly = 4, monthly = 12, yearly = 0 },
                excludes = new[] { "*.tmp" },
                checkSchedule,
                checkReadPercent,
            },
        };

    private async Task<JsonElement> CreateAsync(string name, string schedule = "0 3 * * *", object? sources = null, string? checkSchedule = null, int checkReadPercent = 0)
    {
        using var response = await _admin.PostAsync("/api/backups/jobs", Request(name, schedule: schedule, sources: sources, checkSchedule: checkSchedule, checkReadPercent: checkReadPercent));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.JsonAsync();
    }

    private async Task<long> CreateJobAsync(string name, string schedule = "0 3 * * *", object? sources = null, string? checkSchedule = null, int checkReadPercent = 0)
    {
        var id = (await CreateAsync(name, schedule, sources, checkSchedule, checkReadPercent)).GetProperty("job").GetProperty("id").GetInt64();
        _commands.ClearCalls();
        return id;
    }

    private async Task<JsonElement> RunAsync(long id, JobState state = JobState.Succeeded)
    {
        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/run", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, state);
        return (await RunsAsync(id))[0];
    }

    private async Task<JsonElement> JobAsync(long id) => JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}", Token)).RootElement;

    private async Task<List<JsonElement>> RunsAsync(long id) =>
        [.. JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/runs", Token)).RootElement.GetProperty("runs").EnumerateArray()];

    private async Task<long> NotificationsAsync(string? code = null) =>
        await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE code LIKE 'backup.%' AND (@code IS NULL OR code = @code)", new { code });

    private async Task<List<(string Action, string Target)>> AuditAsync() =>
        [.. await _factory.QueryAsync<(string, string)>("SELECT action, target FROM audit_log ORDER BY id")];

    private static async Task<string?> FieldAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("params").GetProperty("field").GetString();
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "backups", name));

    private CommandResult? Answer(CommandRequest request)
    {
        var arguments = request.Arguments;
        switch (request.FileName)
        {
            case "ssh-keygen":
                var key = arguments[arguments.ToList().IndexOf("-f") + 1];
                File.WriteAllText(key, "PRIVATE\n");
                File.WriteAllText(key + ".pub", "ssh-ed25519 PUBLIC pithyx-backup@test\n");
                return Ok;
            case "zfs" when arguments is ["get", ..]:
                return new CommandResult(0, "yes\n" + (arguments[^1].EndsWith("/backup-cache", StringComparison.Ordinal) ? CacheRoot : SharePath) + "\n", "", TimeSpan.Zero);
            case "zfs" when arguments is ["list", ..]:
                return new CommandResult(0, _snapshots, "", TimeSpan.Zero);
            case "zfs":
                return Ok;
            case "env" when arguments.Contains("init"):
                return _init;
            case "env" when arguments.Contains("cat"):
                return _verify;
            case "env" when arguments.Contains("forget"):
                return Ok;
            case "env" when arguments.Contains("check"):
                return _check;
            case "unshare" when !_commands.Hanging.Contains("unshare --mount"):
                return _backup;
            default:
                return null;
        }
    }
}
