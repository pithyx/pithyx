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

/// <summary>
/// Import, export, add vdev and RAID-Z expansion from request to plan and run, on the dev VM's disks: VYXTEST01 (sdb)
/// holds the exported pool <c>oldtank</c>; VYXTEST01 to VYXTEST04 are test disks here.
/// </summary>
public sealed class PoolOperationPlannerTests : IDisposable
{
    private const string OldTank = "5249471309174385122";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _disks = new();
    private readonly PoolInventory _pools = new();
    private readonly StorageFacts _facts;
    private readonly SystemStateService _state;
    private readonly PoolImportScanner _scanner;
    private readonly StoragePlanner _planner;
    private readonly CommandPlanRunner _runner;
    private readonly string _proc = Directory.CreateTempSubdirectory("pithyx-proc-").FullName;

    public PoolOperationPlannerTests()
    {
        File.WriteAllText(Path.Combine(_proc, "mountinfo"), Proc("mountinfo"));
        File.WriteAllText(Path.Combine(_proc, "swaps"), Proc("swaps"));
        var environment = new DeploymentEnvironment(DeploymentKind.Dev, new HashSet<string> { "VYXTEST01", "VYXTEST02", "VYXTEST03", "VYXTEST04" });
        _facts = new StorageFacts(_disks, _pools, environment, new SystemFiles(Path.Combine(_proc, "mountinfo"), Path.Combine(_proc, "swaps")));
        var hub = new TopicHub(new TopicRegistry([_disks.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _state = new SystemStateService(_commands, _disks, _pools, new ZfsBackend(_commands), new DiskProbe(_commands), _facts, hub, _time, NullLogger<SystemStateService>.Instance);
        _scanner = new PoolImportScanner(_commands, _state, _facts, _pools);
        _planner = new StoragePlanner(_state, _facts, _pools, _scanner, _commands, _time);
        _runner = new CommandPlanRunner(_commands, _state, _facts, _time);
        _commands.Results["lsblk"] = Ok(Read("default", "lsblk"));
        _commands.Results["wipefs"] = Ok("");
        _commands.Results["blkid"] = new CommandResult(2, "", "", TimeSpan.Zero);
        _commands.Results["zpool"] = Ok("");
        _commands.Results["zfs"] = Ok("");
        _commands.Results["zpool import"] = Ok(Read("default", "zpool-import-handwritten"));
        Pools("zfs-empty");
    }

    public void Dispose()
    {
        _state.Dispose();
        Directory.Delete(_proc, recursive: true);
    }

    private static CommandResult Ok(string output) => new(0, output, "", TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string[] Line(PlanStep step) => [step.Command.FileName, .. step.Command.Arguments];

    /// <summary>What the pool reads return from now on, from a fixture scenario.</summary>
    private void Pools(string scenario)
    {
        _commands.Results["zpool status"] = Ok(Read(scenario, "zpool-status"));
        _commands.Results["zpool list"] = Ok(Read(scenario, "zpool-list"));
        _commands.Results["zfs list"] = Ok(Read(scenario, "zfs-list"));
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", scenario, "zfs-get.stdout")))
        {
            _commands.Results["zfs get"] = Ok(Read(scenario, "zfs-get"));
        }
    }

    private async Task<StoragePlanner.Result> PlanAsync(StoragePlanRequest request)
    {
        await _state.RefreshPoolsAsync(Token);
        return await _planner.PlanAsync(request, Token);
    }

    private async Task<string> ErrorAsync(StoragePlanRequest request) =>
        (await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(request))).Code;

    /// <summary>The scan of the default scenario, with <c>oldtank</c> last used by another system.</summary>
    private void OldTankOnAnotherSystem() =>
        _commands.Results["zpool import"] = Ok(Read("default", "zpool-import-handwritten").Replace(
            "action: The pool can be imported using its name or numeric identifier.\n",
            "status: The pool was last accessed by another system.\naction: The pool can be imported using its name or numeric identifier and\n\tthe '-f' flag.\n   see: https://openzfs.github.io/openzfs-docs/msg/ZFS-8000-EY\n",
            StringComparison.Ordinal));

    [Fact]
    public async Task AScanMapsEveryDeviceToItsDiskAndOffersOnlyWhatTheGuardAllows()
    {
        var scan = await _scanner.ScanAsync(Token);

        Assert.Equal(["oldtank", "archive"], scan.Pools.Select(pool => pool.Name));
        var oldTank = scan.Pools[0];
        Assert.Equal((OldTank, true, false, false, true), (oldTank.PoolGuid, oldTank.CanImport, oldTank.NeedsForce, oldTank.NameTaken, oldTank.Allowed));
        Assert.Equal("serial-VYXTEST01", Assert.Single(oldTank.Vdevs).DiskId);
        var verdict = Assert.Single(oldTank.Disks);
        Assert.Equal(("serial-VYXTEST01", GuardDecision.Allowed, "importable"), (verdict.DiskId, verdict.Decision, verdict.Reason));
        var archive = scan.Pools[1];
        Assert.Equal((false, false), (archive.CanImport, archive.Allowed));
        Assert.Empty(archive.UnknownDevices);
        Assert.Contains(_commands.Calls, call => Line(new PlanStep(call, [])).SequenceEqual(["zpool", "import", "-d", "/dev/disk/by-id"]));
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments.Contains("-a") || call.Arguments.Contains("-D") || call.Arguments.Contains("-f"));
    }

    [Fact]
    public async Task ADiskThatIsNoTestDiskOrOfUnknownDevicesIsNeverOffered()
    {
        _commands.Results["zpool import"] = Ok(Read("zfs-import-handwritten-other-system", "zpool-import"));

        var scan = await _scanner.ScanAsync(Token);

        Assert.All(scan.Pools, pool => Assert.False(pool.Allowed));
        Assert.Equal(["wwn-0x5000c500a1b2c3d4", "wwn-0x5000c500a1b2c3d5", "nvme-PCIE_SSD_1TB_S1234-part1", "nvme-PCIE_SSD_1TB_S1234-part2", "wwn-0x5000c500a1b2c3d6"], scan.Pools[0].UnknownDevices);
        // The faulted device named by its GUID is missing, not unknown.
        Assert.Equal(["ata-DISK_A-part1", "ata-DISK_B-part1", "ata-DISK_C-part1"], scan.Pools[1].UnknownDevices);
        Assert.Equal(ErrorCodes.StorageImportDeviceUnknown, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "backup", PoolGuid: "9012345678901234567")));
    }

    [Fact]
    public async Task AReadOnlyImportIsPlannedFromANewScanByGuid()
    {
        var result = await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank));

        var step = Assert.Single(result.Plan!.Steps);
        Assert.Equal(["zpool", "import", "-o", "readonly=on", "-o", "cachefile=none", "-N", "-d", "/dev/disk/by-id", OldTank], Line(step));
        Assert.Equal((GuardRule.Import, (string?)null), (step.Rule, step.ImportingPool));
        Assert.Equal(["serial-VYXTEST01"], step.DiskIds);
        Assert.Equal(["serial-VYXTEST01"], result.Plan.Disks.Select(disk => disk.DiskId));
        Assert.Empty(result.Warnings);
        Assert.False(result.RequiresConfirmName);

        Assert.Equal(ErrorCodes.StorageImportNotFound, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: "1")));
        Assert.Equal(ErrorCodes.StorageImportNotImportable, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "archive", PoolGuid: "7733120945871106301")));
        Assert.Equal(ErrorCodes.StoragePoolNameInvalid, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, NewName: "Old Tank")));
        var renamed = await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, NewName: "oldtank2"));
        Assert.Equal([OldTank, "oldtank2"], renamed.Plan!.Steps[0].Command.Arguments.TakeLast(2));
    }

    [Fact]
    public async Task ATakenNameNeedsANewNameAndTheDiskOfAnImportedPoolIsRefused()
    {
        Pools("zfs-import-handwritten-readonly");

        Assert.Equal(ErrorCodes.StoragePoolNameTaken, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank)));
        var result = await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, NewName: "oldtank2"));
        Assert.Null(result.Plan);
        Assert.Equal((GuardDecision.Refused, "pool_member"), (result.Verdicts[0].Decision, result.Verdicts[0].Reason));
    }

    [Fact]
    public async Task AReadOnlyImportOnADiskThatIsNoTestDiskHasNoPlan()
    {
        var environment = new DeploymentEnvironment(DeploymentKind.Dev, new HashSet<string> { "VYXTEST02" });
        var facts = new StorageFacts(_disks, _pools, environment, new SystemFiles(Path.Combine(_proc, "mountinfo"), Path.Combine(_proc, "swaps")));
        var planner = new StoragePlanner(_state, facts, _pools, new PoolImportScanner(_commands, _state, facts, _pools), _commands, _time);

        var result = await planner.PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank), Token);

        Assert.Null(result.Plan);
        Assert.Equal("not_test_disk", Assert.Single(result.Verdicts).Reason);
    }

    [Fact]
    public async Task APoolOfAnotherSystemIsForcedOnlyWithForceAndItsTypedName()
    {
        OldTankOnAnotherSystem();

        Assert.Equal(ErrorCodes.StorageImportForceRequired, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank)));
        Assert.Equal(ErrorCodes.StorageImportForceRequired, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, Force: true)));
        Assert.Equal(ErrorCodes.StorageImportForceRequired, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, Force: true, ConfirmName: "oldtan")));
        Assert.Equal(ErrorCodes.StorageImportForceRequired, await ErrorAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, ConfirmName: "oldtank")));

        var forced = await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, Force: true, ConfirmName: "oldtank"));

        Assert.Equal(["zpool", "import", "-o", "readonly=on", "-o", "cachefile=none", "-N", "-d", "/dev/disk/by-id", "-f", OldTank], Line(forced.Plan!.Steps[0]));
        Assert.Equal(["other_system"], forced.Warnings);
        Assert.True(forced.RequiresConfirmName);

        // The read-write import of the same pool needs the force again.
        Pools("zfs-import-handwritten-readonly");
        Assert.Equal(ErrorCodes.StorageImportForceRequired, await ErrorAsync(new StoragePlanRequest("pool.import", "oldtank")));
        var readWrite = await PlanAsync(new StoragePlanRequest("pool.import", "oldtank", Force: true, ConfirmName: "oldtank"));
        Assert.Equal(["zpool", "import", "-N", "-d", "/dev/disk/by-id", "-f", OldTank, "oldtank"], Line(readWrite.Plan!.Steps[1]));
    }

    [Fact]
    public async Task APoolOfThisSystemIsNeverForcedEvenWhenAskedTo()
    {
        var result = await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank, Force: true, ConfirmName: "oldtank"));

        Assert.DoesNotContain("-f", result.Plan!.Steps[0].Command.Arguments);
    }

    [Fact]
    public async Task AReadWriteImportComesOnlyAfterTheReadOnlyOne()
    {
        Pools("zfs-mirror-single");

        Assert.Equal(ErrorCodes.StorageImportPreviewRequired, await ErrorAsync(new StoragePlanRequest("pool.import", "pithyxtestm")));
        Assert.Equal(ErrorCodes.StoragePoolNotFound, await ErrorAsync(new StoragePlanRequest("pool.import", "oldtank")));
    }

    [Fact]
    public async Task AReadWriteImportMountsOnlySafeDatasetsAfterTheTypedName()
    {
        Pools("zfs-import-handwritten-readonly");

        var awaiting = await PlanAsync(new StoragePlanRequest("pool.import", "oldtank"));
        Assert.Equal((true, true, (CommandPlan?)null), (awaiting.RequiresConfirmName, awaiting.AwaitsConfirmName, awaiting.Plan));
        Assert.Equal(ErrorCodes.StoragePoolNameMismatch, await ErrorAsync(new StoragePlanRequest("pool.import", "oldtank", ConfirmName: "tank")));

        var result = await PlanAsync(new StoragePlanRequest("pool.import", "oldtank", ConfirmName: "oldtank"));

        Assert.Equal(
            [
                ["zpool", "export", "oldtank"],
                ["zpool", "import", "-N", "-d", "/dev/disk/by-id", OldTank, "oldtank"],
                ["zfs", "set", "canmount=noauto", "oldtank/etc"],
                ["zfs", "mount", "oldtank"],
                ["zfs", "mount", "oldtank/media"],
            ],
            result.Plan!.Steps.Select(Line));
        Assert.All(result.Plan.Steps, step => Assert.Equal((GuardRule.Import, "oldtank", "serial-VYXTEST01"), (step.Rule, step.ImportingPool, Assert.Single(step.DiskIds))));
        Assert.Equal(["oldtank", "oldtank/media"], result.Datasets);
        Assert.Equal([("oldtank/etc", "system_path"), ("oldtank/vault", "key_unavailable")], result.Unmounted.Select(dataset => (dataset.Name, dataset.Reason)));
        Assert.Equal(["unmounted_datasets"], result.Warnings);
        Assert.Equal(("importable", GuardDecision.Allowed), (Assert.Single(result.Verdicts).Reason, result.Verdicts[0].Decision));
        Assert.Contains(_commands.Calls, call => Line(new PlanStep(call, [])).SequenceEqual(["zfs", "get", "-j", "--json-int", "-r", "-t", "filesystem", "canmount,mountpoint,encryption,keystatus", "oldtank"]));
    }

    [Fact]
    public async Task TheRunnerChecksImportStepsWithTheImportRuleAndReadsThePoolsAfterwards()
    {
        var plan = (await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank))).Plan!;
        _commands.Calls.Clear();

        await _runner.RunAsync(plan, null, Token);

        var calls = _commands.Calls.Select(call => $"{call.FileName} {call.Arguments[0]}").ToList();
        var import = calls.IndexOf("zpool import");
        Assert.True(import > 0 && calls.Take(import).Contains("zpool status"), "the pools are read before the import step");
        Assert.Equal("zpool status", calls.Skip(import + 1).First(call => call.StartsWith("zpool", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ADiskThatBelongsToAnotherPoolMeanwhileStopsTheImport()
    {
        Pools("zfs-import-handwritten-readonly");
        var plan = (await PlanAsync(new StoragePlanRequest("pool.import", "oldtank", ConfirmName: "oldtank"))).Plan!;
        // sdb is now a member of pithyxtestr: the import rule allows only the disks of the pool it imports.
        Pools("zfs-raidz1");
        _commands.Calls.Clear();

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(plan, null, Token));

        Assert.Equal((ErrorCodes.StorageDiskRefused, "pool_member"), (error.Code, error.Parameters["reason"]));
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["export", ..] or ["import", ..]);
    }

    [Fact]
    public async Task AReadWriteImportRunsExportImportAndMountsWithTheImportRule()
    {
        Pools("zfs-import-handwritten-readonly");
        var plan = (await PlanAsync(new StoragePlanRequest("pool.import", "oldtank", ConfirmName: "oldtank"))).Plan!;
        _commands.Calls.Clear();

        await _runner.RunAsync(plan, null, Token);

        Assert.Equal(
            ["zpool export", "zpool import", "zfs set", "zfs mount", "zfs mount"],
            _commands.Calls.Where(call => call.Arguments[0] is "export" or "import" or "set" or "mount").Select(call => $"{call.FileName} {call.Arguments[0]}"));
    }

    [Fact]
    public async Task AnImportThatZfsWantsForcedFailsWithTheForceCode()
    {
        var plan = (await PlanAsync(new StoragePlanRequest("pool.import.readonly", "oldtank", PoolGuid: OldTank))).Plan!;
        _commands.Handler = call => call.Arguments is ["import", "-o", ..]
            ? new CommandResult(1, "", "cannot import 'oldtank': pool was previously in use from another system.\nThe pool can be imported, use 'zpool import -f' to import the pool.\n", TimeSpan.Zero)
            : null;

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(plan, null, Token));

        Assert.Equal(ErrorCodes.StorageImportForceRequired, error.Code);
    }

    [Fact]
    public async Task ExportingListsTheDatasetsThatBecomeUnavailable()
    {
        Pools("zfs-raidz1");

        var result = await PlanAsync(new StoragePlanRequest("pool.export", "pithyxtestr"));

        Assert.Equal([["zpool", "export", "pithyxtestr"]], result.Plan!.Steps.Select(Line));
        Assert.Equal(["pithyxtestr"], result.Datasets);
        Assert.Equal(["datasets_unavailable"], result.Warnings);
        Assert.False(result.RequiresConfirmName);
    }

    [Fact]
    public async Task AddingASingleDiskToAMirrorNeedsTheTypedNameAndOnlyThenTheOverride()
    {
        Pools("zfs-mirror-single");
        var request = new StoragePlanRequest("pool.vdev.add", "pithyxtestm", "single", ["serial-VYXTEST02"]);

        var awaiting = await PlanAsync(request);
        Assert.Null(awaiting.Plan);
        Assert.Equal(["lowers_redundancy"], awaiting.Warnings);
        Assert.Equal((true, true), (awaiting.RequiresConfirmName, awaiting.AwaitsConfirmName));
        Assert.Equal(GuardDecision.Allowed, Assert.Single(awaiting.Verdicts).Decision);
        Assert.Equal(ErrorCodes.StoragePoolNameMismatch, await ErrorAsync(request with { ConfirmName = "pithyxtests" }));

        var confirmed = await PlanAsync(request with { ConfirmName = "pithyxtestm" });

        Assert.Equal(
            [["zpool", "add", "--allow-replication-mismatch", "pithyxtestm", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2"]],
            confirmed.Plan!.Steps.Select(Line));
    }

    [Fact]
    public async Task AddingAMatchingMirrorNeedsNoNameAndNoOverride()
    {
        Pools("zfs-mirror-single");

        var result = await PlanAsync(new StoragePlanRequest("pool.vdev.add", "pithyxtestm", "mirror", ["serial-VYXTEST02", "serial-VYXTEST03"]));

        Assert.Equal(
            [["zpool", "add", "pithyxtestm", "mirror", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi3"]],
            result.Plan!.Steps.Select(Line));
        Assert.Empty(result.Warnings);
        Assert.False(result.RequiresConfirmName);
        Assert.Equal(ErrorCodes.StoragePoolLayoutInvalid, await ErrorAsync(new StoragePlanRequest("pool.vdev.add", "pithyxtestm", "stripe", ["serial-VYXTEST02", "serial-VYXTEST03"])));
        Assert.Equal(ErrorCodes.StoragePoolDiskCount, await ErrorAsync(new StoragePlanRequest("pool.vdev.add", "pithyxtestm", "raidz1", ["serial-VYXTEST02", "serial-VYXTEST03"])));
        // A disk with data still needs its serial, as for a new pool.
        var withData = await PlanAsync(new StoragePlanRequest("pool.vdev.add", "pithyxtests", "single", ["serial-VYXTEST01"]));
        Assert.Equal(GuardDecision.NeedsSerialConfirmation, Assert.Single(withData.Verdicts).Decision);
    }

    [Fact]
    public async Task AReadOnlyPoolIsNeitherGrownNorExpanded()
    {
        Pools("zfs-import-handwritten-readonly");

        Assert.Equal(ErrorCodes.StoragePoolReadOnly, await ErrorAsync(new StoragePlanRequest("pool.vdev.add", "oldtank", "single", ["serial-VYXTEST02"])));
        Assert.Equal(ErrorCodes.StoragePoolReadOnly, await ErrorAsync(new StoragePlanRequest("pool.raidz.expand", "oldtank", DiskIds: ["serial-VYXTEST02"], Vdev: "1")));
    }

    [Fact]
    public async Task ARaidzIsExpandedByOneDiskAfterTheTypedName()
    {
        Pools("zfs-raidz1");
        var request = new StoragePlanRequest("pool.raidz.expand", "pithyxtestr", DiskIds: ["serial-VYXTEST04"], Vdev: "8912351914708349237");

        var awaiting = await PlanAsync(request);
        Assert.Equal(["irreversible"], awaiting.Warnings);
        Assert.Equal((true, true, (CommandPlan?)null), (awaiting.RequiresConfirmName, awaiting.AwaitsConfirmName, awaiting.Plan));

        var result = await PlanAsync(request with { ConfirmName = "pithyxtestr" });

        Assert.Equal(
            [["zpool", "attach", "pithyxtestr", "8912351914708349237", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi4"]],
            result.Plan!.Steps.Select(Line));
        Assert.Equal(ErrorCodes.StorageVdevNotFound, await ErrorAsync(request with { Vdev = "1" }));
        Assert.Equal(ErrorCodes.StoragePoolDiskCount, await ErrorAsync(request with { DiskIds = [] }));
        Pools("zfs-mirror-single");
        Assert.Equal(ErrorCodes.StorageVdevNotRaidz, await ErrorAsync(request with { Pool = "pithyxtestm", Vdev = "11762469217994242773", ConfirmName = "pithyxtestm" }));
        Pools("zfs-raidz-expanding");
        Assert.Equal(ErrorCodes.StoragePoolExpanding, await ErrorAsync(request with { Pool = "pithyxtestr", Vdev = await RaidzGuidAsync() }));
    }

    private async Task<string> RaidzGuidAsync()
    {
        await _state.RefreshPoolsAsync(Token);
        return _pools.Current.Pools.Single().Vdevs[0].VdevGuid;
    }
}
