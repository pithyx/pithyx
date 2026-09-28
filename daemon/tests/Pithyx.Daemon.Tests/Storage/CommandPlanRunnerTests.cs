using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>A plan runs only against the disks the user saw, checked by the guard before every command.</summary>
public sealed class CommandPlanRunnerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _disks = new();
    private readonly PoolInventory _pools = new();
    private readonly StorageFacts _facts;
    private readonly SystemStateService _state;
    private readonly StoragePlanner _planner;
    private readonly CommandPlanRunner _runner;
    private readonly string _proc = Directory.CreateTempSubdirectory("pithyx-proc-").FullName;

    public CommandPlanRunnerTests()
    {
        File.WriteAllText(Path.Combine(_proc, "mountinfo"), Proc("mountinfo"));
        File.WriteAllText(Path.Combine(_proc, "swaps"), Proc("swaps"));
        var environment = new DeploymentEnvironment(DeploymentKind.Dev, new HashSet<string> { "VYXTEST02", "VYXTEST03" });
        _facts = new StorageFacts(_disks, _pools, environment, new SystemFiles(Path.Combine(_proc, "mountinfo"), Path.Combine(_proc, "swaps")));
        var hub = new TopicHub(new TopicRegistry([_disks.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _state = new SystemStateService(_commands, _disks, _pools, new ZfsBackend(_commands), new DiskProbe(_commands), _facts, hub, _time, NullLogger<SystemStateService>.Instance);
        _planner = new StoragePlanner(_state, _facts, _pools, _time);
        _runner = new CommandPlanRunner(_commands, _state, _facts, _time);
        _commands.Results["lsblk"] = Ok(Read("default", "lsblk"));
        _commands.Results["wipefs"] = Ok("");
        _commands.Results["blkid"] = new CommandResult(2, "", "", TimeSpan.Zero);
        _commands.Results["zpool"] = Ok("");
        _commands.Results["zfs"] = Ok("");
    }

    public void Dispose()
    {
        _state.Dispose();
        Directory.Delete(_proc, recursive: true);
    }

    private static CommandResult Ok(string output) => new(0, output, "", TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<CommandPlan> MirrorPlanAsync()
    {
        await _state.RefreshDisksAsync([], Token);
        var (verdicts, plan, _) = await _planner.PlanAsync(
            new StoragePlanRequest("pool.create", "tank", "mirror", ["serial-VYXTEST02", "serial-VYXTEST03"], Encryption: "none"), Token);
        Assert.All(verdicts, verdict => Assert.Equal(GuardDecision.Allowed, verdict.Decision));
        return plan!;
    }

    private List<string> ZpoolCalls() => [.. _commands.Calls.Where(call => call.FileName == "zpool").Select(call => call.Arguments[0])];

    [Fact]
    public async Task RunsThePlanAsShown()
    {
        var plan = await MirrorPlanAsync();
        var progress = new List<int>();

        await _runner.RunAsync(plan, (index, _) => progress.Add(index), Token);

        Assert.Equal(["create", "status", "list"], ZpoolCalls());
        Assert.Equal(["create", "create", "create", "create", "create", "list"], _commands.Calls.Where(call => call.FileName == "zfs").Select(call => call.Arguments[0]));
        Assert.Equal([0, 1, 2, 3, 4, 5], progress);
        // After the last command the state is read again, so the page and the bookkeeping see the new pool.
        Assert.Equal([plan.Steps[^1].Command, ZfsBackend.Status, ZfsBackend.List, ZfsBackend.Datasets], _commands.Calls.TakeLast(4));
    }

    [Fact]
    public async Task BookkeepingRunsOnlyAfterEveryCommandSucceeded()
    {
        var plan = await MirrorPlanAsync();
        var callsBefore = -1;
        await _runner.RunAsync(plan with { AfterRun = _ => { callsBefore = _commands.Calls.Count; return Task.CompletedTask; } }, null, Token);
        Assert.Equal(_commands.Calls.Count, callsBefore);

        var failing = await MirrorPlanAsync();
        var ran = false;
        _commands.Results["zpool create"] = new CommandResult(1, "", "cannot create", TimeSpan.Zero);
        await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(failing with { AfterRun = _ => { ran = true; return Task.CompletedTask; } }, null, Token));
        Assert.False(ran);
    }

    [Fact]
    public async Task ADiskThatGotAPartitionTableMeanwhileStopsThePlan()
    {
        var plan = await MirrorPlanAsync();
        _commands.Results["wipefs"] = Ok("""{"signatures":[{"device":"sdc","offset":"0x200","type":"gpt","uuid":null,"label":null}]}""");

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(plan, null, Token));

        Assert.Equal(ErrorCodes.StoragePlanStale, error.Code);
        Assert.Empty(ZpoolCalls());
    }

    [Fact]
    public async Task ADiskThatWasMountedMeanwhileIsRefused()
    {
        var plan = await MirrorPlanAsync();
        var lsblk = System.Text.Json.Nodes.JsonNode.Parse(Read("default", "lsblk"))!;
        var sdc = lsblk["blockdevices"]!.AsArray().First(device => device!["name"]!.GetValue<string>() == "sdc")!;
        sdc["mountpoints"] = new System.Text.Json.Nodes.JsonArray("/mnt/usb");
        _commands.Results["lsblk"] = Ok(lsblk.ToJsonString());

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(plan, null, Token));

        Assert.Equal((ErrorCodes.StorageDiskRefused, "mounted"), (error.Code, error.Parameters["reason"]));
        Assert.Empty(ZpoolCalls());
    }

    [Fact]
    public async Task APlanShownMoreThanTenMinutesAgoMustBeShownAgain()
    {
        var plan = await MirrorPlanAsync();
        _time.Advance(CommandPlan.Lifetime + TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(plan, null, Token));

        Assert.Equal(ErrorCodes.StoragePlanExpired, error.Code);
        Assert.Empty(ZpoolCalls());
    }

    [Fact]
    public async Task TheSystemDiskAndANonTestDiskNeverGetAPlanEvenWithTheirSerials()
    {
        await _state.RefreshDisksAsync([], Token);
        var (verdicts, plan, _) = await _planner.PlanAsync(
            new StoragePlanRequest("pool.create", "tank", "mirror", ["serial-02609329D99", "serial-VYXTEST06"], Encryption: "none", Confirmations: new Dictionary<string, string> { ["serial-02609329D99"] = "02609329D99", ["serial-VYXTEST06"] = "VYXTEST06" }),
            Token);

        Assert.Null(plan);
        Assert.Equal(["system_disk", "not_test_disk"], verdicts.Select(verdict => verdict.Reason));
    }

    [Fact]
    public async Task AFailedCommandStopsThePlan()
    {
        var plan = await MirrorPlanAsync();
        _commands.Results["zpool create"] = new CommandResult(1, "", "invalid vdev specification", TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(plan, null, Token));

        Assert.Equal((ErrorCodes.StorageCommandFailed, "zpool", 1), (error.Code, error.Parameters["command"], error.Parameters["exitCode"]));
    }
}
