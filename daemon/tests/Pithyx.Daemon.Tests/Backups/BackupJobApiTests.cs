using System.Net;
using System.Net.Http.Json;
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
    private string _versions = "[]";
    private string _forget = "";
    private string _diff = "";
    private string _tag = "";

    // What each version holds below the share's Photos folder, as the mount and a restore serve it.
    private readonly Dictionary<string, Dictionary<string, string>> _stored = [];

    private string SharePath => Path.Combine(_root, "media");
    private string CacheRoot => Path.Combine(_root, "cache");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(SharePath, "Photos"));
        Directory.CreateDirectory(CacheRoot);
        _backup = new CommandResult(0, Fixture("restic-backup-warning.stdout"), "", TimeSpan.Zero);
        _commands.Handler = Answer;
        _commands.Running = Serve;
        _daemon = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new BackupSsh(_commands));
            services.AddSingleton(new BackupRestic(_commands));
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<BackupBrowser>(provider, (ICommandRunner)_commands));
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
        Assert.Equal([snapshot, SharePath, "--", "env"], backup.Arguments.Skip(9).Take(4));
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
        Assert.Equal(300_310, run.GetProperty("repositoryBytes").GetInt64());
        Assert.Equal(300_310, job.GetProperty("repositoryBytes").GetInt64());
        Assert.Equal(90_600_000, job.GetProperty("protectedBytes").GetInt64());
        var stats = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("stats"));
        Assert.Equal(["stats", "--mode", "raw-data"], stats.Arguments.TakeLast(3));
        var target = JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/targets", Token)).RootElement.GetProperty("targets")[0];
        Assert.Equal(15_718_360L * 1024, target.GetProperty("space").GetProperty("freeBytes").GetInt64());
    }

    [Fact]
    public async Task TheDaysCountTheRunsOfEachDayAndCarriesTheRepositorySizes()
    {
        var media = await CreateJobAsync("Media");
        var other = await CreateJobAsync("Other");
        var first = await RunAsync(media);
        var second = await RunAsync(other);
        _check = new CommandResult(1, "", "Fatal: repository contains errors\n", TimeSpan.Zero);
        await CheckAsync(media, JobState.Failed);
        // A day without runs keeps the sizes of the days before.
        await _factory.ScalarAsync<long>("UPDATE backup_runs SET started_at = '2026-01-10T12:00:00.000Z' WHERE id = @id RETURNING id", new { id = first.GetProperty("id").GetInt64() });

        var summary = JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/days?from=2026-01-09&to=2026-01-11", Token)).RootElement;
        var days = summary.GetProperty("days").EnumerateArray().ToList();
        var recent = JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/days", Token)).RootElement.GetProperty("days").EnumerateArray().ToList();

        Assert.Equal(TimeZoneInfo.Local.Id, summary.GetProperty("timeZone").GetString());
        Assert.Equal(["2026-01-09", "2026-01-10", "2026-01-11"], days.Select(day => day.GetProperty("date").GetString()));
        Assert.Equal(days[1].GetProperty("start").GetDateTimeOffset(), days[0].GetProperty("end").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, days[0].GetProperty("repositoryBytes").ValueKind);
        Assert.Equal((1, 1, 1, 300_310L), (days[1].GetProperty("succeeded").GetInt32(), days[1].GetProperty("jobs").GetInt32(), days[1].GetProperty("jobsOk").GetInt32(), days[1].GetProperty("repositoryBytes").GetInt64()));
        Assert.Equal(first.GetProperty("addedBytes").GetInt64(), days[1].GetProperty("addedBytes").GetInt64());
        Assert.Equal((0, 300_310L), (days[2].GetProperty("jobs").GetInt32(), days[2].GetProperty("repositoryBytes").GetInt64()));
        Assert.Equal(30, recent.Count);
        var today = recent[^1];
        Assert.Equal((1, 0, 1, 2, 1), (today.GetProperty("succeeded").GetInt32(), today.GetProperty("warnings").GetInt32(), today.GetProperty("failed").GetInt32(), today.GetProperty("jobs").GetInt32(), today.GetProperty("jobsOk").GetInt32()));
        Assert.Equal(second.GetProperty("addedBytes").GetInt64(), today.GetProperty("addedBytes").GetInt64());
        Assert.Equal(2 * 300_310L, today.GetProperty("repositoryBytes").GetInt64());
        foreach (var query in new[] { "?from=2026-01-11&to=2026-01-09", "?from=2026-1-9", "?from=2025-01-01&to=2026-12-31" })
        {
            using var invalid = await _admin.GetAsync("/api/backups/days" + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(ErrorCodes.RequestBodyInvalid, await invalid.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task UnreadableFilesMakeAWarningAndTellTheAdministrators()
    {
        var id = await CreateJobAsync("Media");
        var locked = Path.Combine(SharePath, "Photos", "locked.db");
        var gone = Path.Combine(SharePath, "Photos", "tmp");
        var errors = string.Concat(
            JsonSerializer.Serialize(new { message_type = "error", error = new { message = $"open {locked}: permission denied" }, during = "archival", item = locked }), "\n",
            JsonSerializer.Serialize(new { message_type = "error", error = new { message = $"lstat {gone}: no such file or directory" }, during = "archival", item = gone }), "\n");
        _backup = new CommandResult(3, Fixture("restic-backup-warning.stdout") + errors, Fixture("restic-backup-warning.stderr"), TimeSpan.Zero);

        var run = await RunAsync(id);

        Assert.Equal("warning", run.GetProperty("state").GetString());
        Assert.Equal(3, run.GetProperty("warnings").GetInt32());
        var unreadable = run.GetProperty("unreadable").EnumerateArray().Select(item => (item.GetProperty("path").GetString(), item.GetProperty("reason").GetString())).ToList();
        // restic writes the fixture's error to stderr, which is read after stdout.
        Assert.Equal([("Media/Photos/locked.db", "permission"), ("Media/Photos/tmp", "missing"), ("/tank/shares/media/src/sub/locked", "permission")], unreadable);
        Assert.Equal(1, await NotificationsAsync(BackupJobService.RunWarningCode));
        Assert.Contains(_commands.Calls, call => call.Arguments.Contains("forget"));
    }

    [Fact]
    public async Task TheHistoryListsTheRunsOfEveryJobNewestFirstAndFilters()
    {
        var media = await CreateJobAsync("Media");
        var other = await CreateJobAsync("Other");
        await RunAsync(media);
        await RunAsync(other);
        _check = new CommandResult(1, "", "Fatal: repository contains errors\n", TimeSpan.Zero);
        await CheckAsync(media, JobState.Failed);

        var all = await HistoryAsync("");
        Assert.Equal([(media, "check"), (other, "backup"), (media, "backup")], all.Select(run => (run.GetProperty("jobId").GetInt64(), run.GetProperty("kind").GetString())));
        Assert.Equal([media, media], (await HistoryAsync($"?jobId={media}")).Select(run => run.GetProperty("jobId").GetInt64()));
        Assert.Equal(["backup", "backup"], (await HistoryAsync("?kind=backup")).Select(run => run.GetProperty("kind").GetString()));
        Assert.Equal(["failed"], (await HistoryAsync("?state=failed")).Select(run => run.GetProperty("state").GetString()));
        Assert.Empty(await HistoryAsync("?since=2999-01-01T00:00:00Z"));
        Assert.Empty(await HistoryAsync("?until=2000-01-01T00:00:00Z"));
        Assert.Equal(3, (await HistoryAsync("?until=2999-01-01T00:00:00Z")).Count);
        using (var unknown = await _admin.GetAsync("/api/backups/runs?kind=Nope", Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        Assert.Equal(2, (await HistoryAsync("?kind=Backup")).Count);
        var first = Assert.Single(await HistoryAsync("?limit=1"));
        var next = await HistoryAsync($"?limit=2&before={first.GetProperty("id").GetInt64()}");
        Assert.Equal(all.Skip(1).Select(run => run.GetProperty("id").GetInt64()), next.Select(run => run.GetProperty("id").GetInt64()));
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

    [Fact]
    public async Task ARunKeepsTheVersionsWithWhyTheRetentionKeptThemAndWhatChanged()
    {
        var id = await CreateJobAsync("Media", sources: new[] { new { shareId = _shareId, path = "Photos" } });
        var photos = Path.Combine(SharePath, "Photos");
        _versions = Versions((Older, Older, "2026-11-22T03:00:00+01:00", false), (Made, Made, "2026-11-23T03:00:00+01:00", false));
        _forget = $$"""[{"reasons":[{"snapshot":{"id":"{{Made}}"},"matches":["last snapshot","daily snapshot"]},{"snapshot":{"id":"{{Older}}"},"matches":["daily snapshot"]}]}]""" + "\nloading indexes...\n";
        _diff = $$$"""
            {"message_type":"change","path":"{{{photos}}}/","modifier":"M"}
            {"message_type":"change","path":"{{{photos}}}/new.jpg","modifier":"+"}
            {"message_type":"change","path":"{{{photos}}}/old.jpg","modifier":"-"}
            {"message_type":"statistics","changed_files":0,"added":{"files":1},"removed":{"files":1}}
            """;

        await RunAsync(id);

        var versions = await VersionsAsync(id);
        Assert.Equal(2, versions.Count);
        var newest = versions[0];
        Assert.Equal(Made[..8], newest.GetProperty("snapshot").GetString());
        Assert.Equal("2026-11-23T02:00:00+00:00", newest.GetProperty("time").GetString());
        Assert.Equal(["daily", "last"], newest.GetProperty("keep").EnumerateArray().Select(reason => reason.GetString()!).Order());
        Assert.Equal(1, newest.GetProperty("filesRemoved").GetInt64());
        Assert.Equal(
            ["added Media/Photos/new.jpg", "removed Media/Photos/old.jpg"],
            newest.GetProperty("changes").EnumerateArray().Select(change => $"{change.GetProperty("change").GetString()} {change.GetProperty("path").GetString()}"));
        Assert.Equal(JsonValueKind.Null, versions[1].GetProperty("changes").ValueKind);
        var diff = Assert.Single(_commands.Calls, call => call.Arguments.Contains("diff"));
        Assert.Equal(["diff", "--", Older, Made], diff.Arguments.TakeLast(4));
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments.Contains("tag"));

        // A version the retention deleted leaves the list; one it kept stays with its id.
        _versions = Versions((Made, Made, "2026-11-23T03:00:00+01:00", false));
        await RunAsync(id);
        var kept = Assert.Single(await VersionsAsync(id));
        Assert.Equal(newest.GetProperty("id").GetInt64(), kept.GetProperty("id").GetInt64());
        Assert.Equal(2, kept.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task AProtectedVersionGetsResticsTagAndKeepsItsIdUnderItsNewSnapshotId()
    {
        var id = await CreateJobAsync("Media");
        _versions = Versions((Older, Older, "2026-11-22T03:00:00Z", false), (Made, Made, "2026-11-23T03:00:00Z", false));
        await RunAsync(id);
        var older = (await VersionsAsync(id))[1].GetProperty("id").GetInt64();
        const string Tagged = "d22c342de47a8e02374d6f877593c5430a1c80cbe121ec520784b2d95c7877b2";
        _tag = $$"""{"message_type":"changed","old_snapshot_id":"{{Older}}","new_snapshot_id":"{{Tagged}}"}""" + "\n";
        _commands.ClearCalls();

        using (var response = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}/versions/{older}/protection", new { @protected = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var version = await response.JsonAsync();
            Assert.True(version.GetProperty("protected").GetBoolean());
            Assert.Equal("d22c342d", version.GetProperty("snapshot").GetString());
            Assert.Contains("protected", version.GetProperty("keep").EnumerateArray().Select(reason => reason.GetString()));
        }
        var tag = Assert.Single(_commands.Calls, call => call.Arguments.Contains("tag"));
        Assert.Equal(["tag", "--add", "pithyx-protected", "--", Older], tag.Arguments.TakeLast(5));
        Assert.Contains(("backup.version.protected", "Media"), await AuditAsync());

        // The next run finds it under its new id, made from the old one.
        _versions = Versions((Tagged, Older, "2026-11-22T03:00:00Z", true), (Made, Made, "2026-11-23T03:00:00Z", false));
        await RunAsync(id);
        var again = (await VersionsAsync(id))[1];
        Assert.Equal(older, again.GetProperty("id").GetInt64());
        Assert.True(again.GetProperty("protected").GetBoolean());

        _tag = "";
        using (var released = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}/versions/{older}/protection", new { @protected = false }, Token))
        {
            Assert.False((await released.JsonAsync()).GetProperty("protected").GetBoolean());
        }
        Assert.Contains(("backup.version.released", "Media"), await AuditAsync());
        using var missing = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}/versions/999/protection", new { @protected = true }, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(ErrorCodes.BackupVersionNotFound, await missing.ErrorCodeAsync());
    }

    [Fact]
    public async Task TheRepositoryKeyIsShownAgainOnlyWithThePassword()
    {
        var created = await CreateAsync("Media");
        var id = created.GetProperty("job").GetProperty("id").GetInt64();

        using (var wrong = await _admin.PostAsJsonAsync($"/api/backups/jobs/{id}/key", new { password = "wrong" }, Token))
        {
            Assert.Equal("auth.credentials.invalid", await wrong.ErrorCodeAsync());
        }
        using var shown = await _admin.PostAsJsonAsync($"/api/backups/jobs/{id}/key", new { password = AdminPassword }, Token);

        Assert.Equal(created.GetProperty("key").GetString(), (await shown.JsonAsync()).GetProperty("key").GetString());
        Assert.Contains(("backup.job.key_revealed", "Media"), await AuditAsync());
    }

    [Fact]
    public async Task ADeletedJobTakesItsCacheAlongAndCachesOfJobsLongGoneAreSwept()
    {
        var id = await CreateJobAsync("Media");
        await RunAsync(id);
        var cache = Path.Combine(CacheRoot, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Directory.Exists(cache));
        Directory.CreateDirectory(Path.Combine(CacheRoot, "999", "data"));
        Directory.CreateDirectory(Path.Combine(CacheRoot, "notes"));
        var other = await CreateJobAsync("Other", sources: new[] { new { shareId = _shareId, path = "Photos" } });
        await RunAsync(other);

        using var deleted = await _admin.DeleteAsync($"/api/backups/jobs/{id}", Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Directory.Exists(cache));
        Assert.False(Directory.Exists(Path.Combine(CacheRoot, "999")));
        Assert.True(Directory.Exists(Path.Combine(CacheRoot, "notes")));
        Assert.True(Directory.Exists(Path.Combine(CacheRoot, other.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task AVersionIsBrowsedThroughItsMountAndRestoredNextToWhatIsThere()
    {
        var id = await CreateJobAsync("Media", sources: new[] { new { shareId = _shareId, path = "Photos" } });
        var photos = Path.Combine(SharePath, "Photos");
        _versions = JsonSerializer.Serialize(new[] { (Older, "2026-11-22T03:00:00Z"), (Made, "2026-11-23T03:00:00Z") }.Select(version => new
        {
            id = version.Item1,
            time = version.Item2,
            paths = new[] { photos },
            summary = new { files_new = 1 },
        })) + "\n";
        _stored[Older] = new() { ["a.txt"] = "first", ["same.txt"] = "same" };
        _stored[Made] = new() { ["a.txt"] = "second!", ["same.txt"] = "same", ["new/b.txt"] = "b" };
        File.WriteAllText(Path.Combine(photos, "a.txt"), "live");
        await RunAsync(id);
        var versions = await VersionsAsync(id);
        var (newest, older) = (versions[0].GetProperty("id").GetInt64(), versions[1].GetProperty("id").GetInt64());

        using (var closed = await _admin.GetAsync($"/api/backups/jobs/{id}/versions/{newest}/files", Token))
        {
            Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
            Assert.Equal(ErrorCodes.BackupBrowseClosed, await closed.ErrorCodeAsync());
        }
        using (var browse = await _admin.PostAsync($"/api/backups/jobs/{id}/browse", null, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, browse.StatusCode);
        }
        var top = await FilesAsync(id, newest, "");
        Assert.Equal(["Media/Photos"], top.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()));
        var folder = await FilesAsync(id, newest, "Media/Photos");
        Assert.True(folder.GetProperty("compared").GetBoolean());
        Assert.Equal(
            ["new folder added", "a.txt file modified", "same.txt file "],
            folder.GetProperty("entries").EnumerateArray().Select(entry =>
                $"{entry.GetProperty("name").GetString()} {entry.GetProperty("kind").GetString()} {(entry.GetProperty("change").ValueKind == JsonValueKind.Null ? "" : entry.GetProperty("change").GetString())}"));
        Assert.False((await FilesAsync(id, older, "Media/Photos")).GetProperty("compared").GetBoolean());

        var history = JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/history?path=Media/Photos/a.txt", Token)).RootElement;
        Assert.Equal(4, history.GetProperty("current").GetProperty("size").GetInt64());
        Assert.Equal([7L, 5L], history.GetProperty("versions").EnumerateArray().Select(version => version.GetProperty("size").GetInt64()));
        Assert.Equal("second!", await _admin.GetStringAsync($"/api/backups/jobs/{id}/versions/{newest}/content?path=Media/Photos/a.txt", Token));
        using (var zip = await _admin.GetAsync($"/api/backups/jobs/{id}/versions/{newest}/content?path=Media/Photos/new", Token))
        {
            Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
            using var archive = new System.IO.Compression.ZipArchive(await zip.Content.ReadAsStreamAsync(Token));
            Assert.Equal(["new/b.txt"], archive.Entries.Select(entry => entry.FullName).Where(name => !name.EndsWith('/')));
        }
        using (var outside = await _admin.GetAsync($"/api/backups/jobs/{id}/versions/{newest}/files?path=Media/../etc", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        }
        Assert.Contains(("backup.version.downloaded", "Media"), await AuditAsync());

        using (var none = await _admin.PostAsJsonAsync($"/api/backups/jobs/{id}/restore", new { versionId = newest, paths = Array.Empty<string>(), conflict = "keepBoth" }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        }
        await RestoreAsync(id, new { versionId = newest, paths = RestoredTogether, conflict = "keepBoth" });

        Assert.Equal("live", File.ReadAllText(Path.Combine(photos, "a.txt")));
        Assert.Equal("second!", File.ReadAllText(Path.Combine(photos, "a (version 2026-11-23 03.00 UTC).txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(photos, "new", "b.txt")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(SharePath), folder => Path.GetFileName(folder).StartsWith(".pithyx-restore-", StringComparison.Ordinal));
        Assert.Contains(("backup.version.restored", "Media"), await AuditAsync());
        var restore = Assert.Single(_commands.Calls, call => call.Arguments.Contains("restore"));
        Assert.Equal(["--include", photos + "/a.txt", "--include", photos + "/new", "--", Made], restore.Arguments.TakeLast(6));

        // Into another folder each entry goes under its name; overwrite replaces the file there.
        Directory.CreateDirectory(Path.Combine(SharePath, "Restored"));
        File.WriteAllText(Path.Combine(SharePath, "Restored", "a.txt"), "old copy");
        await RestoreAsync(id, new { versionId = older, paths = RestoredAlone, destination = new { shareId = _shareId, path = "Restored" }, conflict = "overwrite" });
        Assert.Equal("first", File.ReadAllText(Path.Combine(SharePath, "Restored", "a.txt")));

        // A run closes the session, so its prune is not held off by the mount.
        await RunAsync(id);
        using var after = await _admin.GetAsync($"/api/backups/jobs/{id}/versions/{newest}/files", Token);
        Assert.Equal(ErrorCodes.BackupBrowseClosed, await after.ErrorCodeAsync());
    }

    private async Task<JsonElement> FilesAsync(long id, long versionId, string path) =>
        JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/versions/{versionId}/files?path={Uri.EscapeDataString(path)}", Token)).RootElement;

    private async Task RestoreAsync(long id, object request)
    {
        using var started = await _admin.PostAsJsonAsync($"/api/backups/jobs/{id}/restore", request, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, JobState.Succeeded);
    }

    // restic mount: every version under ids/<id>/<stored path>, ready once written, serving until it is stopped.
    private Task<CommandResult>? Serve(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.FileName != "env" || !request.Arguments.Contains("mount"))
        {
            return null;
        }
        var mountpoint = request.Arguments[^1];
        foreach (var (snapshot, files) in _stored)
        {
            WriteVersion(Path.Combine(mountpoint, "ids", snapshot), snapshot);
        }
        request.OutputLine?.Invoke("Now serving the repository at " + mountpoint);
        return ServeAsync(cancellationToken);

        static async Task<CommandResult> ServeAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Ok;
        }
    }

    private void WriteVersion(string root, string snapshot, IReadOnlyList<string>? only = null)
    {
        var photos = Path.Combine(SharePath, "Photos");
        foreach (var (name, content) in _stored[snapshot])
        {
            var stored = photos + "/" + name;
            if (only is not null && !only.Any(path => stored == path || stored.StartsWith(path + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            var file = root + stored;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            // restic's mount shows the stored time; files written a moment apart would look modified.
            File.SetLastWriteTimeUtc(file, StoredTime);
        }
    }

    private static readonly DateTime StoredTime = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] RestoredTogether = ["Media/Photos/a.txt", "Media/Photos/new"];
    private static readonly string[] RestoredAlone = ["Media/Photos/a.txt"];

    private static readonly string[] ProtectedTags = ["pithyx-protected"];

    private const string Older = "87a0dfd8476bc24184b206c7749be24060391a4e45be004cad25ce7344530962";

    // The run's own version, as restic-backup-warning.stdout reports it.
    private const string Made = "6e42ebcde1709a43f15cd17b0bf6e0c04264ef6ec609fe1ad99641caac92081a";

    private static string Versions(params (string Id, string Origin, string Time, bool Protected)[] versions) =>
        JsonSerializer.Serialize(versions.Select(version => new Dictionary<string, object?>
        {
            ["id"] = version.Id,
            ["original"] = version.Id == version.Origin ? null : version.Origin,
            ["time"] = version.Time,
            ["tags"] = version.Protected ? ProtectedTags : null,
            ["summary"] = new { files_new = 1, files_changed = 2, data_added_packed = 300, total_files_processed = 33, total_bytes_processed = 90_600_000 },
        })) + "\n";

    private async Task<List<JsonElement>> VersionsAsync(long id) =>
        [.. JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/versions", Token)).RootElement.GetProperty("versions").EnumerateArray()];

    private async Task<JsonElement> RunAsync(long id, JobState state = JobState.Succeeded)
    {
        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/run", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, state);
        return (await RunsAsync(id))[0];
    }

    private async Task CheckAsync(long id, JobState state)
    {
        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/check", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, state);
    }

    private async Task<List<JsonElement>> HistoryAsync(string query) =>
        [.. JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/runs" + query, Token)).RootElement.GetProperty("runs").EnumerateArray()];

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
            case "zfs" when arguments is ["list", .., "filesystem"]:
                return new CommandResult(0, $"pithyxtestm\tyes\t/pithyxtestm\npithyxtestm/pithyx/backup-cache\tyes\t{CacheRoot}\n", "", TimeSpan.Zero);
            case "zfs" when arguments is ["list", ..]:
                return new CommandResult(0, _snapshots, "", TimeSpan.Zero);
            case "zfs":
                return Ok;
            case "env" when arguments.Contains("init"):
                return _init;
            case "env" when arguments.Contains("cat"):
                return _verify;
            case "env" when arguments.Contains("forget"):
                return new CommandResult(0, _forget, "", TimeSpan.Zero);
            case "env" when arguments.Contains("snapshots"):
                return new CommandResult(0, _versions, "", TimeSpan.Zero);
            case "env" when arguments.Contains("diff"):
                return new CommandResult(0, _diff, "", TimeSpan.Zero);
            case "env" when arguments.Contains("tag"):
                return new CommandResult(0, _tag, "", TimeSpan.Zero);
            case "env" when arguments.Contains("restore"):
                var list = arguments.ToList();
                var includes = list.Select((argument, index) => (argument, index)).Where(pair => pair.argument == "--include").Select(pair => list[pair.index + 1]).ToList();
                WriteVersion(list[list.IndexOf("--target") + 1], list[^1], includes);
                return Ok;
            case "fusermount3":
                foreach (var entry in Directory.EnumerateFileSystemEntries(arguments[^1]))
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                }
                return Ok;
            case "env" when arguments.Contains("check"):
                return _check;
            case "env" when arguments.Contains("stats"):
                return new CommandResult(0, Fixture("restic-stats.stdout"), "", TimeSpan.Zero);
            case "env" when arguments.Contains("sftp"):
                return new CommandResult(0, Fixture("sftp-ok.stdout"), "", TimeSpan.Zero);
            case "unshare" when !_commands.Hanging.Contains("unshare --mount"):
                return _backup;
            default:
                return null;
        }
    }
}
