using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

/// <summary>
/// The apply and the health check of a box against a scripted system: the order of the steps, what is left for the
/// next start, and the rollback when a step after the snapshot fails.
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-apply-").FullName;
    private readonly ScriptedCommandRunner _commands = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 21, 40, 0, TimeSpan.Zero));
    private readonly UpdatePaths _paths;
    private readonly UpdateSystem _system;
    private readonly string _update;

    public UpdateApplierTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "cache"), Path.Combine(_root, "run"), Path.Combine(_root, "top"),
            Path.Combine(_root, "release"), Path.Combine(_root, "daemon"))
        {
            SystemArray = Path.Combine(_root, "pithyx-system"),
            Mounts = Path.Combine(_root, "mounts"),
        };
        File.CreateSymbolicLink(_paths.SystemArray, "/dev/loop0");
        File.WriteAllText(_paths.Release, "PITHYX_VERSION=0.1.0\nPITHYX_COMMIT=abc\n");
        File.WriteAllText(_paths.Mounts, "/dev/sda1 /boot/efi vfat rw 0 0\n/dev/sdb1 /boot/efi2 vfat rw 0 0\n/dev/md127 / btrfs rw 0 0\n");
        _update = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_update);
        File.WriteAllBytes(Path.Combine(_update, UpdateFile.ManifestFile), TestUpdate.Manifest());
        _system = new UpdateSystem(_commands, _paths, _time, NullLogger<UpdateSystem>.Instance);
        _commands.Handler = Handle;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>What the scripted system answers; <c>apt-get install</c> fails when <see cref="_failInstall"/>.</summary>
    private CommandResult? Handle(CommandRequest request)
    {
        var ok = new CommandResult(0, "", "", TimeSpan.Zero);
        switch (request.FileName)
        {
            case "findmnt":
                return new CommandResult(0, File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "findmnt", "btrfs-subvol.json")), "", TimeSpan.Zero);
            case "zpool":
                return new CommandResult(0, "tank\nbackup\n", "", TimeSpan.Zero);
            case "btrfs" when request.Arguments[1] == "snapshot":
                Directory.CreateDirectory(request.Arguments[^1]);
                return ok;
            case "mv":
                Directory.Move(request.Arguments[^2], request.Arguments[^1]);
                return ok;
            case "env" when _failInstall && request.Arguments.Contains("install"):
                return new CommandResult(100, "", "E: broken", TimeSpan.Zero);
            default:
                return ok;
        }
    }

    private bool _failInstall;

    private UpdateApplyRequest Request() => new(7, _update, "0.1.0", "0.2.0-beta.3", false, [
        new UpdateHistoryEntry(7, "update", "file", "0.1.0", "0.2.0-beta.3", null, "running", null, null, _time.GetUtcNow(), null),
    ]);

    private UpdateApplier Applier() => new(_system, _time, NullLogger<UpdateApplier>.Instance);

    private List<string> Calls() => [.. _commands.Calls.Select(call => $"{call.FileName} {string.Join(' ', call.Arguments)}")];

    [Fact]
    public async Task AnUpdateTakesASnapshotInstallsSyncsGrubAndRestarts()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Top, "@"));

        var progress = await Applier().RunAsync(Request(), CancellationToken.None);

        Assert.Equal(("restart", "restarting"), (progress.Step, progress.State));
        var calls = Calls();
        int Index(string start) => calls.FindIndex(call => call.StartsWith(start, StringComparison.Ordinal));
        Assert.True(Index("btrfs subvolume snapshot -r") < Index("env DEBIAN_FRONTEND=noninteractive apt-get"));
        Assert.Contains(calls, call => call.EndsWith("install --yes --no-install-recommends pithyx=0.2.0~beta.3 pithyx-kernel=0.2.0~beta.3", StringComparison.Ordinal));
        Assert.Contains("grub-install --target=x86_64-efi --efi-directory=/boot/efi2 --bootloader-id=pithyx --removable --no-nvram", calls);
        Assert.True(Index("update-grub") < Index("systemctl reboot"));
        Assert.Equal("systemctl reboot", calls[^1]);
        Assert.DoesNotContain(calls, call => call.StartsWith("apt-get", StringComparison.Ordinal) || call.Contains("zpool upgrade", StringComparison.Ordinal));

        var trial = UpdateSystem.ReadJson<UpdateTrial>(_paths.TrialFile)!;
        Assert.Equal(("20261009T214000Z", 7L), (trial.Snapshot, trial.HistoryId));
        Assert.Equal(["backup", "tank"], trial.Pools);
        var menu = File.ReadAllText(Path.Combine(_paths.Top, "@snapshots", "menu.cfg"));
        Assert.Contains("pithyx.rollback=auto:20261009T214000Z", menu, StringComparison.Ordinal);
        Assert.Equal("0.1.0", UpdateSystem.ReadSnapshots(_paths.Top).Single().Version);
    }

    [Fact]
    public async Task AFailedInstallRollsBackToTheSnapshotAndRestarts()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Top, "@"));
        _failInstall = true;

        var progress = await Applier().RunAsync(Request(), CancellationToken.None);

        Assert.Equal(("install", "rolled_back", ErrorCodes.UpdateApplyFailed), (progress.Step, progress.State, progress.ErrorCode));
        Assert.True(Directory.Exists(Path.Combine(_paths.Top, "@rollback-20261009T214000Z")));
        var outcome = UpdateSystem.ReadJson<UpdateOutcome>(Path.Combine(_paths.Top, "@", _paths.ResultFile.TrimStart('/')))!;
        Assert.Equal(("rolled_back", 7L), (outcome.State, outcome.HistoryId));
        Assert.Single(outcome.History);
        Assert.False(File.Exists(_paths.TrialFile));
        Assert.Equal("systemctl reboot", Calls()[^1]);
    }

    [Fact]
    public async Task NothingHappensOnAMachineTheInstallerDidNotLayOut()
    {
        File.Delete(_paths.SystemArray);

        var progress = await Applier().RunAsync(Request(), CancellationToken.None);

        Assert.Equal(("snapshot", "failed", ErrorCodes.UpdateSystemUnsupported), (progress.Step, progress.State, progress.ErrorCode));
        Assert.DoesNotContain(Calls(), call => call.StartsWith("btrfs", StringComparison.Ordinal) || call.StartsWith("systemctl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGoodHealthCheckClearsTheTrialAndReportsSuccess()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Top, "@"));
        await Applier().RunAsync(Request(), CancellationToken.None);
        var trial = UpdateSystem.ReadJson<UpdateTrial>(_paths.TrialFile)!;
        using var http = new HttpClient(new AnswerHandler(healthy: true));
        var health = new UpdateHealth(_system, _commands, http, _time, NullLogger<UpdateHealth>.Instance);

        var failed = await health.CheckAsync(trial, _ => Task.FromResult(true), CancellationToken.None);
        await health.FinishAsync(trial, failed, Request().History, CancellationToken.None);

        Assert.Null(failed);
        Assert.False(File.Exists(_paths.TrialFile));
        Assert.Equal("succeeded", UpdateSystem.ReadJson<UpdateOutcome>(_paths.ResultFile)!.State);
        Assert.DoesNotContain("pithyx-rollback-auto", File.ReadAllText(Path.Combine(_paths.Top, "@snapshots", "menu.cfg")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingPoolFailsTheHealthCheckAfterFiveMinutesAndRollsBack()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Top, "@"));
        await Applier().RunAsync(Request(), CancellationToken.None);
        var trial = UpdateSystem.ReadJson<UpdateTrial>(_paths.TrialFile)! with { Pools = ["tank", "gone"] };
        using var http = new HttpClient(new AnswerHandler(healthy: true));
        var health = new UpdateHealth(_system, _commands, http, _time, NullLogger<UpdateHealth>.Instance);

        var checking = health.CheckAsync(trial, _ => Task.FromResult(true), CancellationToken.None);
        while (!checking.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(1, CancellationToken.None);
        }
        var failed = await checking;
        await health.FinishAsync(trial, failed, Request().History, CancellationToken.None);

        Assert.Equal("pools", failed);
        var outcome = UpdateSystem.ReadJson<UpdateOutcome>(Path.Combine(_paths.Top, "@", _paths.ResultFile.TrimStart('/')))!;
        Assert.Equal("rolled_back", outcome.State);
        Assert.Equal("pools", outcome.ErrorParams!["command"]!.ToString());
        Assert.Equal("systemctl reboot", Calls()[^1]);
    }

    [Fact]
    public async Task ADaemonWithoutItsMigrationsIsNotHealthy()
    {
        using var http = new HttpClient(new AnswerHandler(healthy: true));
        var health = new UpdateHealth(_system, _commands, http, _time, NullLogger<UpdateHealth>.Instance);
        var trial = new UpdateTrial(7, "0.1.0", "0.2.0", "20261009T214000Z", [], _time.GetUtcNow());

        var checking = health.CheckAsync(trial, _ => Task.FromResult(false), CancellationToken.None);
        while (!checking.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(1, CancellationToken.None);
        }

        Assert.Equal("migrations", await checking);
    }

    /// <summary>The daemon's API and desktop on 127.0.0.1:7484.</summary>
    private sealed class AnswerHandler(bool healthy) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(healthy ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/health" ? "{\"status\":\"ok\"}" : "<!doctype html><html></html>"),
            });
    }
}
