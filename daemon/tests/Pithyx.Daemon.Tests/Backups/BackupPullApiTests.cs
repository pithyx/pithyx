using System.Globalization;
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

/// <summary>Server backups through the API (#396, D420): servers, the dataset of each backup, pulls, versions and retention.</summary>
public sealed class BackupPullApiTests : IAsyncLifetime
{
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
    // The simulation's pool pithyxtestm is mounted, so a shared folder at its root is available.
    private const string ShareDataset = "pithyxtestm";
    private const string Dataset = ShareDataset + "/web01";
    private static readonly string[] Paths = ["x"];
    private static readonly string[] WebRoot = ["/var/www"];
    private static readonly CommandResult Ok = new(0, "", "", TimeSpan.Zero);

    private readonly DaemonFactory _factory = new();
    private readonly ScriptedCommandRunner _commands = new();
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-pull-").FullName;
    private WebApplicationFactory<Program> _daemon = null!;
    private HttpClient _admin = null!;
    private long _serverId;
    private long _shareId;

    // The pull dataset as ZFS would answer: its owner property and its snapshots with their holds.
    private string? _owner;
    private readonly List<(string Name, DateTimeOffset Created, int Holds)> _snapshots = [];
    private CommandResult _rsync = Ok;

    private string SharePath => Path.Combine(_root, "servers");

    private string DatasetPath => Path.Combine(SharePath, "web01");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(SharePath);
        _rsync = new CommandResult(BackupRsync.Partial, Fixture("rsync-pull.stdout"), Fixture("rsync-pull.stderr"), TimeSpan.Zero);
        _commands.Handler = Answer;
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
        using (var server = await _admin.PostAsync("/api/backups/targets", new { name = "Web", host = "web01.lan", user = "backup", path = "/", hostKey = HostKey, purpose = "server" }))
        {
            Assert.Equal(HttpStatusCode.Created, server.StatusCode);
            _serverId = (await server.JsonAsync()).GetProperty("id").GetInt64();
        }
        _shareId = await _factory.ScalarAsync<long>(
            "INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES ('Servers', @path, @dataset, 'x', 'x') RETURNING id",
            new { path = SharePath, dataset = ShareDataset });
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _daemon.DisposeAsync();
        await _factory.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AServerShowsTheLineForItsAuthorizedKeysAndIsTestedWithRsync()
    {
        var server = await TargetAsync(_serverId);
        Assert.Equal("server", server.GetProperty("purpose").GetString());
        Assert.Equal("restrict,command=\"rrsync -ro /\" ssh-ed25519 PUBLIC pithyx-backup@test", server.GetProperty("authorizedKeys").GetString());

        using var tested = await _admin.PostAsync($"/api/backups/targets/{_serverId}/test", new { });

        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var probe = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("--list-only"));
        Assert.Equal("pithyx-target:/", probe.Arguments[^1]);
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments.Contains("sftp"));
        using var versioned = await _admin.PostAsync("/api/backups/jobs", new
        {
            targetId = _serverId,
            repository = "r",
            settings = new { name = "Media", sources = new[] { new { shareId = _shareId, path = "" } }, schedule = "0 3 * * *", enabled = true, retention = new { last = 1, hourly = 0, daily = 0, weekly = 0, monthly = 0, yearly = 0 } },
        });
        Assert.Equal(ErrorCodes.BackupTargetNotFound, await versioned.ErrorCodeAsync());
    }

    [Fact]
    public async Task ABackupMakesItsOwnDatasetInTheSharedFolder()
    {
        var job = await CreateAsync("Web");

        Assert.Equal("pull", job.GetProperty("kind").GetString());
        Assert.Equal(Dataset, job.GetProperty("dataset").GetString());
        Assert.Equal(["/srv/www", "/etc"], job.GetProperty("pull").GetProperty("paths").EnumerateArray().Select(path => path.GetString()));
        var id = job.GetProperty("id").GetInt64();
        var create = Assert.Single(_commands.Calls, call => call.FileName == "zfs" && call.Arguments is ["create", ..]);
        Assert.Equal(["create", "-o", "setuid=off", "-o", "devices=off", "-o", $"org.pithyx:backup-job={id}", Dataset], create.Arguments);
        Assert.Contains(_commands.Calls, call => call.FileName == "getfacl" && call.PipeTo?.FileName == "setfacl" && call.PipeTo.Arguments[^1] == DatasetPath);
        Assert.Contains(("backup.job.created", "Web"), await AuditAsync());

        using var again = await _admin.PostAsync("/api/backups/pulls", Request("Other"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ErrorCodes.BackupDestinationInvalid, await again.ErrorCodeAsync());
        using var noShare = await _admin.PostAsync("/api/backups/pulls", Request("Other", folder: "db01", shareId: 9999));
        Assert.Equal("destination", await FieldAsync(noShare));
        using var target = await _admin.PostAsync("/api/backups/pulls", Request("Other", folder: "db01", serverId: await PlainTargetAsync()));
        Assert.Equal(ErrorCodes.BackupTargetNotFound, await target.ErrorCodeAsync());
        using var badPath = await _admin.PostAsync("/api/backups/pulls", Request("Other", folder: "db01", paths: ["srv"]));
        Assert.Equal("paths", await FieldAsync(badPath));
    }

    [Fact]
    public async Task ARunPullsIntoTheDatasetAndKeepsASnapshotAsItsVersion()
    {
        var id = (await CreateAsync("Web")).GetProperty("id").GetInt64();

        var run = await RunAsync(id);

        Assert.Equal("warning", run.GetProperty("state").GetString());
        Assert.Equal(11, run.GetProperty("files").GetInt64());
        Assert.Equal(300_020, run.GetProperty("bytes").GetInt64());
        Assert.Equal(4096, run.GetProperty("addedBytes").GetInt64());
        Assert.Equal(123_456, run.GetProperty("repositoryBytes").GetInt64());
        Assert.Equal("etc/app/secret", run.GetProperty("unreadable")[0].GetProperty("path").GetString());
        var pull = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("--itemize-changes"));
        Assert.Matches("^/proc/[0-9]+/fd/[0-9]+/$", pull.Arguments[^1]);
        Assert.Equal(["pithyx-target:/srv/www", "pithyx-target:/etc"], pull.Arguments.SkipWhile(argument => argument != "--").Skip(1).SkipLast(1));
        Assert.Contains("-pgoAX", pull.Arguments);
        var version = Assert.Single(await VersionsAsync(id));
        Assert.StartsWith("pithyx-pull-", version.GetProperty("snapshot").GetString(), StringComparison.Ordinal);
        Assert.Equal((1, 1, 1), (version.GetProperty("filesAdded").GetInt64(), version.GetProperty("filesChanged").GetInt64(), version.GetProperty("filesRemoved").GetInt64()));
        Assert.Equal(3, version.GetProperty("changes").GetArrayLength());
        Assert.Equal(id, (await JobAsync(id)).GetProperty("lastRun").GetProperty("jobId").GetInt64());
        // The copy is on this box, so the days count the run and its data but nothing on a target.
        var today = JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/days", Token)).RootElement.GetProperty("days").EnumerateArray().Last();
        Assert.Equal(1, today.GetProperty("warnings").GetInt32());
        Assert.Equal(4096, today.GetProperty("addedBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, today.GetProperty("repositoryBytes").ValueKind);
    }

    [Fact]
    public async Task TheRetentionDestroysTheVersionsItDoesNotKeepUnlessTheyAreProtected()
    {
        _rsync = Ok;
        var id = (await CreateAsync("Web", last: 2)).GetProperty("id").GetInt64();
        await RunAsync(id);
        _factory.Time.Advance(TimeSpan.FromHours(1));
        await RunAsync(id);
        var oldest = (await VersionsAsync(id))[^1];
        using (var protect = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}/versions/{oldest.GetProperty("id").GetInt64()}/protection", new { @protected = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, protect.StatusCode);
        }
        Assert.Contains(_commands.Calls, call => call.FileName == "zfs" && call.Arguments is ["hold", "pithyx", ..]);

        _factory.Time.Advance(TimeSpan.FromHours(1));
        await RunAsync(id);
        _factory.Time.Advance(TimeSpan.FromHours(1));
        await RunAsync(id);

        var versions = await VersionsAsync(id);
        Assert.Equal(3, versions.Count);
        Assert.True(versions[^1].GetProperty("protected").GetBoolean());
        Assert.Equal(oldest.GetProperty("snapshot").GetString(), versions[^1].GetProperty("snapshot").GetString());
        Assert.Single(_commands.Calls, call => call.FileName == "zfs" && call.Arguments is ["destroy", _]);
        Assert.Equal(3, _snapshots.Count);
    }

    [Fact]
    public async Task WhatOnlyVersionedBackupsHaveIsRefused()
    {
        var id = (await CreateAsync("Web")).GetProperty("id").GetInt64();

        foreach (var (path, action) in new[] { ("check", "check"), ("browse", "browse"), ("restore", "restore") })
        {
            using var refused = await _admin.PostAsJsonAsync($"/api/backups/jobs/{id}/{path}", new { versionId = 1, paths = Paths, conflict = "skip" }, Token);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = await refused.JsonAsync();
            Assert.Equal(ErrorCodes.BackupJobUnsupported, problem.GetProperty("code").GetString());
            Assert.Equal(action, problem.GetProperty("params").GetProperty("action").GetString());
        }
        using var edit = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}", Settings("Web", 1), Token);
        Assert.Equal(ErrorCodes.BackupJobUnsupported, await edit.ErrorCodeAsync());

        using var changed = await _admin.PutAsJsonAsync($"/api/backups/pulls/{id}", new
        {
            settings = Settings("Web server", 3),
            pull = new { shareId = 0, folder = "ignored", paths = WebRoot, delete = false, owners = "box" },
        }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var job = await changed.JsonAsync();
        Assert.Equal("Web server", job.GetProperty("settings").GetProperty("name").GetString());
        Assert.Equal("web01", job.GetProperty("pull").GetProperty("folder").GetString());
        Assert.Equal("box", job.GetProperty("pull").GetProperty("owners").GetString());
        await RunAsync(id);
        var pull = _commands.Calls.Last(call => call.FileName == "env" && call.Arguments.Contains("--itemize-changes"));
        Assert.DoesNotContain("-pgoAX", pull.Arguments);
        Assert.DoesNotContain("--delete", pull.Arguments);
    }

    [Fact]
    public async Task DeletingKeepsTheDatasetUnlessItsDataGoesToo()
    {
        var kept = (await CreateAsync("Web")).GetProperty("id").GetInt64();
        await RunAsync(kept);
        using (var deleted = await _admin.DeleteAsync($"/api/backups/jobs/{kept}", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        Assert.DoesNotContain(_commands.Calls, call => call.FileName == "zfs" && call.Arguments is ["destroy", "-r", ..]);

        Directory.Delete(DatasetPath, recursive: true);
        _owner = null;
        _snapshots.Clear();
        var id = (await CreateAsync("Web")).GetProperty("id").GetInt64();
        await RunAsync(id);
        var version = (await VersionsAsync(id))[0].GetProperty("id").GetInt64();
        using (var protect = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}/versions/{version}/protection", new { @protected = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, protect.StatusCode);
        }
        using (var deleted = await _admin.DeleteAsync($"/api/backups/jobs/{id}?data=true", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        Assert.Contains(_commands.Calls, call => call.FileName == "zfs" && call.Arguments is ["release", "pithyx", ..]);
        Assert.Contains(_commands.Calls, call => call.FileName == "zfs" && call.Arguments.SequenceEqual(["destroy", "-r", Dataset]));
    }

    [Fact]
    public async Task ADatasetThatIsNotTheBackupsOwnIsNeverWrittenTo()
    {
        var id = (await CreateAsync("Web")).GetProperty("id").GetInt64();
        _owner = "999";

        var run = await RunAsync(id, JobState.Failed);

        Assert.Equal(ErrorCodes.BackupDestinationUnavailable, run.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("--itemize-changes"));
    }

    private object Request(string name, string folder = "web01", long? shareId = null, long? serverId = null, string[]? paths = null, int last = 0) => new
    {
        serverId = serverId ?? _serverId,
        settings = Settings(name, last),
        pull = new { shareId = shareId ?? _shareId, folder, paths = paths ?? ["/srv/www", "/etc/"], delete = true, owners = "server" },
    };

    private static object Settings(string name, int last) => new
    {
        name,
        sources = Array.Empty<object>(),
        schedule = "0 2 * * *",
        // Off, so the scheduler never starts a run of its own while a test advances the clock past 02:00.
        enabled = false,
        retention = new { last, hourly = 0, daily = last == 0 ? 7 : 0, weekly = 0, monthly = 0, yearly = 0 },
    };

    private async Task<JsonElement> CreateAsync(string name, int last = 0)
    {
        using var response = await _admin.PostAsync("/api/backups/pulls", Request(name, last: last));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        var created = await response.JsonAsync();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("key").ValueKind);
        return created.GetProperty("job");
    }

    private async Task<long> PlainTargetAsync()
    {
        using var target = await _admin.PostAsync("/api/backups/targets", new { name = "Box", host = "u1.example.de", user = "u1", path = "backups", hostKey = HostKey });
        return (await target.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> RunAsync(long id, JobState state = JobState.Succeeded)
    {
        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/run", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, state);
        return JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/runs", Token)).RootElement.GetProperty("runs")[0];
    }

    private async Task<List<JsonElement>> VersionsAsync(long id) =>
        [.. JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/versions", Token)).RootElement.GetProperty("versions").EnumerateArray()];

    private async Task<JsonElement> JobAsync(long id) => JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}", Token)).RootElement;

    private async Task<JsonElement> TargetAsync(long id) =>
        JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/targets", Token)).RootElement.GetProperty("targets").EnumerateArray().Single(target => target.GetProperty("id").GetInt64() == id);

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
            case "zfs" when arguments is ["get", "-H", "-p", "-o", "value", "mounted,mountpoint", var dataset]:
                return dataset switch
                {
                    ShareDataset => new CommandResult(0, $"yes\n{SharePath}\n", "", TimeSpan.Zero),
                    Dataset when Directory.Exists(DatasetPath) => new CommandResult(0, $"yes\n{DatasetPath}\n", "", TimeSpan.Zero),
                    _ => new CommandResult(1, "", "dataset does not exist", TimeSpan.Zero),
                };
            case "zfs" when arguments is ["get", "-H", "-o", "value", "org.pithyx:backup-job", Dataset]:
                return _owner is null ? new CommandResult(1, "", "dataset does not exist", TimeSpan.Zero) : new CommandResult(0, _owner + "\n", "", TimeSpan.Zero);
            case "zfs" when arguments is ["get", "-H", "-p", "-o", "value", "used", Dataset]:
                return new CommandResult(0, "123456\n", "", TimeSpan.Zero);
            case "zfs" when arguments is ["list", "-H", "-o", "name", Dataset]:
                return _owner is null ? new CommandResult(1, "", "dataset does not exist", TimeSpan.Zero) : new CommandResult(0, Dataset + "\n", "", TimeSpan.Zero);
            case "zfs" when arguments is ["list", "-H", "-p", "-o", "name,creation,written,userrefs", "-t", "snapshot", "-d", "1", Dataset]:
                return new CommandResult(0, string.Concat(_snapshots.Select(snapshot =>
                    string.Create(CultureInfo.InvariantCulture, $"{Dataset}@{snapshot.Name}\t{snapshot.Created.ToUnixTimeSeconds()}\t4096\t{snapshot.Holds}\n"))), "", TimeSpan.Zero);
            case "zfs" when arguments is ["create", .., var dataset] && dataset == Dataset:
                _owner = arguments.Single(argument => argument.StartsWith("org.pithyx:backup-job=", StringComparison.Ordinal)).Split('=')[1];
                Directory.CreateDirectory(DatasetPath);
                return Ok;
            case "zfs" when arguments is ["snapshot", var full]:
                _snapshots.Add((full.Split('@')[1], _factory.Time.GetUtcNow(), 0));
                return Ok;
            case "zfs" when arguments is ["destroy", var full] && full.Contains('@', StringComparison.Ordinal):
                _snapshots.RemoveAll(snapshot => snapshot.Name == full.Split('@')[1]);
                return Ok;
            case "zfs" when arguments is ["hold" or "release", _, var full]:
                var index = _snapshots.FindIndex(snapshot => snapshot.Name == full.Split('@')[1]);
                _snapshots[index] = _snapshots[index] with { Holds = arguments[0] == "hold" ? 1 : 0 };
                return Ok;
            case "zfs" when arguments is ["destroy", "-r", Dataset]:
                _owner = null;
                return Ok;
            case "zfs":
                return Ok;
            case "chown" or "chmod" or "getfacl":
                return Ok;
            case "env" when arguments.Contains("--list-only"):
                return new CommandResult(0, "drwxr-xr-x          4,096 2026/11/24 10:00:00 .\n", "", TimeSpan.Zero);
            case "env" when arguments.Contains("--itemize-changes"):
                return _rsync;
            default:
                return null;
        }
    }
}
