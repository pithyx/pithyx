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

/// <summary>Mirrors through the API (#397, D421): the folder on the target, the rsync check and runs from snapshots.</summary>
public sealed class BackupMirrorApiTests : IAsyncLifetime
{
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
    private static readonly CommandResult Ok = new(0, "", "", TimeSpan.Zero);

    private const string RsyncOutput =
        """
        >f+++++++++ Media/Photos/a.jpg
        >f.st...... Media/Photos/b.jpg

        Number of files: 5 (reg: 3, dir: 2)
        Total file size: 3,000 bytes
        Total transferred file size: 1,000 bytes
        """;

    private readonly DaemonFactory _factory = new();
    private readonly ScriptedCommandRunner _commands = new();
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-mirror-").FullName;
    private WebApplicationFactory<Program> _daemon = null!;
    private HttpClient _admin = null!;
    private long _targetId;
    private long _shareId;

    private CommandResult _probe = Ok;
    private CommandResult _rsync = new(0, RsyncOutput, "", TimeSpan.Zero);

    private string SharePath => Path.Combine(_root, "media");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(SharePath, "Photos"));
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
    public async Task AMirrorMakesItsFolderOverSftpAndChecksThatRsyncRunsThere()
    {
        var job = await CreateAsync("Copy");

        Assert.Equal("mirror", job.GetProperty("kind").GetString());
        Assert.Equal("mirror", job.GetProperty("repository").GetString());
        Assert.False(job.GetProperty("mirror").GetProperty("delete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("pull").ValueKind);
        var sftp = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("sftp"));
        Assert.Contains("-mkdir \"backups/mirror\"", sftp.StandardInput, StringComparison.Ordinal);
        var probe = Assert.Single(_commands.Calls, call => call.FileName == "env" && call.Arguments.Contains("--list-only"));
        Assert.Equal("pithyx-target:backups/mirror/", probe.Arguments[^1]);
        Assert.Contains(("backup.job.created", "Copy"), await AuditAsync());

        using var again = await _admin.PostAsync("/api/backups/mirrors", Request("Other"));
        Assert.Equal(ErrorCodes.BackupRepositoryExists, await again.ErrorCodeAsync());
        using var retention = await _admin.PostAsync("/api/backups/mirrors", Request("Other", folder: "other", last: 3));
        Assert.Equal("retention", await FieldAsync(retention));
        using var noSources = await _admin.PostAsync("/api/backups/mirrors", Request("Other", folder: "other", sources: []));
        Assert.Equal("sources", await FieldAsync(noSources));
    }

    [Fact]
    public async Task ATargetThatOffersOnlySftpIsRefused()
    {
        _probe = new CommandResult(2, "", "protocol version mismatch -- is your shell clean?\nrsync error: protocol incompatibility (code 2)\n", TimeSpan.Zero);

        using var response = await _admin.PostAsync("/api/backups/mirrors", Request("Copy"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ErrorCodes.BackupTargetRsyncMissing, await response.ErrorCodeAsync());
        Assert.Empty(JsonDocument.Parse(await _admin.GetStringAsync("/api/backups/jobs", Token)).RootElement.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task ARunCopiesTheSnapshotBelowTheSharedFoldersNameWithoutVersions()
    {
        var id = (await CreateAsync("Copy", delete: true)).GetProperty("id").GetInt64();

        var run = await RunAsync(id);

        Assert.Equal("succeeded", run.GetProperty("state").GetString());
        Assert.Equal(5, run.GetProperty("files").GetInt64());
        Assert.Equal(3_000, run.GetProperty("bytes").GetInt64());
        Assert.Equal(1_000, run.GetProperty("addedBytes").GetInt64());
        Assert.Equal(3_000, run.GetProperty("repositoryBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("snapshot").ValueKind);
        var snapshot = Assert.Single(_commands.Calls, call => call is { FileName: "zfs", Arguments: ["snapshot", ..] }).Arguments[1];
        var mirror = Assert.Single(_commands.Calls, call => call.FileName == "unshare");
        var arguments = mirror.Arguments.ToList();
        Assert.Equal([snapshot, SharePath, "--", "sh", "-c", BackupRsync.ViewScript, "sh", BackupRsync.MirrorView, SharePath, "Media", "--", "env"], arguments.Skip(9).Take(12));
        Assert.Contains("--delete", arguments);
        Assert.Contains("--chmod=Du+rwx,Fu+rw", arguments);
        Assert.Equal([BackupRsync.MirrorView + "/./Media/Photos", "pithyx-target:backups/mirror/"], arguments.TakeLast(2));
        Assert.Contains(_commands.Calls, call => call is { FileName: "zfs", Arguments: ["destroy", var name] } && name == snapshot);
        Assert.Empty(JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/versions", Token)).RootElement.GetProperty("versions").EnumerateArray());
        Assert.Equal(3_000, (await JobAsync(id)).GetProperty("repositoryBytes").GetInt64());
    }

    [Fact]
    public async Task FilesThatVanishOnTheBoxAreWarningsNamedByTheirSharedFolder()
    {
        var id = (await CreateAsync("Copy")).GetProperty("id").GetInt64();
        _rsync = new CommandResult(BackupRsync.Vanished, RsyncOutput, $"file has vanished: \"{BackupRsync.MirrorView}/./Media/Photos/c.jpg\"\n", TimeSpan.Zero);

        var run = await RunAsync(id);

        Assert.Equal("warning", run.GetProperty("state").GetString());
        Assert.Equal("Media/Photos/c.jpg", run.GetProperty("unreadable")[0].GetProperty("path").GetString());
        Assert.DoesNotContain("--delete", Assert.Single(_commands.Calls, call => call.FileName == "unshare").Arguments);
    }

    [Theory]
    [InlineData(23, "rsync: [receiver] mkstemp \"/backups/mirror/Media/.a.jpg.Xy12\" failed: Permission denied (13)\n", "write")]
    [InlineData(11, "rsync: [receiver] write failed on \"/backups/mirror/Media/a.jpg\": No space left on device (28)\n", "full")]
    public async Task ATargetThatCannotTakeTheFilesFailsTheRun(int exitCode, string standardError, string reason)
    {
        var id = (await CreateAsync("Copy")).GetProperty("id").GetInt64();
        _rsync = new CommandResult(exitCode, RsyncOutput, standardError, TimeSpan.Zero);

        var run = await RunAsync(id, JobState.Failed);

        Assert.Equal(ErrorCodes.BackupTargetNotWritable, run.GetProperty("errorCode").GetString());
        Assert.Equal(reason, run.GetProperty("errorParams").GetProperty("reason").GetString());
        Assert.Equal("backups/mirror", run.GetProperty("errorParams").GetProperty("path").GetString());
    }

    [Fact]
    public async Task AMirrorIsChangedInItsOwnDialogAndHasNoCheckKeyOrRestore()
    {
        var id = (await CreateAsync("Copy")).GetProperty("id").GetInt64();

        using (var updated = await _admin.PutAsJsonAsync($"/api/backups/mirrors/{id}", new { settings = Settings("Copy 2", 0, null), mirror = new { delete = true } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var job = await updated.JsonAsync();
            Assert.Equal("Copy 2", job.GetProperty("settings").GetProperty("name").GetString());
            Assert.True(job.GetProperty("mirror").GetProperty("delete").GetBoolean());
            Assert.Equal("mirror", job.GetProperty("repository").GetString());
        }
        using (var generic = await _admin.PutAsJsonAsync($"/api/backups/jobs/{id}", Settings("Copy", 0, null), Token))
        {
            Assert.Equal(ErrorCodes.BackupJobUnsupported, await generic.ErrorCodeAsync());
        }
        foreach (var path in new[] { "check", "browse" })
        {
            using var refused = await _admin.PostAsync($"/api/backups/jobs/{id}/{path}", new { });
            var problem = await refused.JsonAsync();
            Assert.Equal(ErrorCodes.BackupJobUnsupported, problem.GetProperty("code").GetString());
            Assert.Equal("mirror", problem.GetProperty("params").GetProperty("action").GetString());
        }
    }

    private object Request(string name, string folder = "mirror", int last = 0, object[]? sources = null, bool delete = false) => new
    {
        targetId = _targetId,
        folder,
        settings = Settings(name, last, sources),
        mirror = new { delete },
    };

    private object Settings(string name, int last, object[]? sources) => new
    {
        name,
        sources = sources ?? [new { shareId = _shareId, path = "Photos" }],
        schedule = "0 2 * * *",
        // Off, so the scheduler never starts a run of its own while a test advances the clock past 02:00.
        enabled = false,
        retention = new { last, hourly = 0, daily = 0, weekly = 0, monthly = 0, yearly = 0 },
    };

    private async Task<JsonElement> CreateAsync(string name, bool delete = false)
    {
        using var response = await _admin.PostAsync("/api/backups/mirrors", Request(name, delete: delete));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        var created = await response.JsonAsync();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("key").ValueKind);
        return created.GetProperty("job");
    }

    private async Task<JsonElement> RunAsync(long id, JobState state = JobState.Succeeded)
    {
        using var started = await _admin.PostAsync($"/api/backups/jobs/{id}/run", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var boardId = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await _daemon.Services.GetRequiredService<JobService>().WaitForStateAsync(boardId, state);
        return JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}/runs", Token)).RootElement.GetProperty("runs")[0];
    }

    private async Task<JsonElement> JobAsync(long id) => JsonDocument.Parse(await _admin.GetStringAsync($"/api/backups/jobs/{id}", Token)).RootElement;

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
            case "zfs" when arguments is ["get", "-H", "-p", "-o", "value", "mounted,mountpoint", _]:
                return new CommandResult(0, $"yes\n{SharePath}\n", "", TimeSpan.Zero);
            case "zfs" when arguments is ["list", ..]:
                return Ok;
            case "zfs":
                return Ok;
            case "env" when arguments.Contains("--list-only"):
                return _probe;
            case "env" when arguments.Contains("sftp"):
                return new CommandResult(0, Fixture("sftp-ok.stdout"), "", TimeSpan.Zero);
            case "unshare":
                return _rsync;
            default:
                return null;
        }
    }
}
