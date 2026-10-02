using System.Globalization;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Import, export, adding a vdev and RAID-Z expansion with the exact arguments of the plans, against real OpenZFS on
/// file vdevs. Imports search only the test's own folder: the plan's <c>-d /dev/disk/by-id</c> is replaced by it, and
/// nothing else of the arguments changes. With <c>PITHYX_RECORD_DIR</c> set, the outputs are written as fixtures.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class PoolOperationsIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<PoolList> ReadAsync(FileVdevs vdevs) => await new ZfsBackend(vdevs.Commands).ReadAsync(Token);

    private static Pool? Find(PoolList list, FileVdevs vdevs) => list.Pools.SingleOrDefault(pool => pool.Name == vdevs.PoolName);

    /// <summary>The plan's command with the import search folder replaced by the test's folder.</summary>
    private static string[] InFolder(PlanStep step, FileVdevs vdevs)
    {
        var arguments = step.Command.Arguments.ToArray();
        var index = Array.IndexOf(arguments, "-d");
        if (index >= 0)
        {
            Assert.Equal(ImportPlans.SearchDirectory, arguments[index + 1]);
            arguments[index + 1] = vdevs.Directory;
        }
        return arguments;
    }

    private static async Task RunAsync(FileVdevs vdevs, IEnumerable<PlanStep> steps)
    {
        foreach (var step in steps)
        {
            await vdevs.RunAsync(step.Command.FileName, InFolder(step, vdevs));
        }
    }

    private static GuardDisk FileDisk(string file, int index) =>
        new(
            new Disk($"path-file{index}", $"file{index}", file, null, null, null, null, new FileInfo(file).Length, false, null, false, false, []),
            new BlockDevice($"file{index}", file, null, $"0:{index}", "disk", null, [], []),
            new DiskSignatures([]));

    private static async Task<ImportCandidate> ScanAsync(FileVdevs vdevs, string scenario)
    {
        var scan = await vdevs.RecordAsync(scenario, "zpool-import", "pithyxtesti", "zpool", [.. InFolder(new PlanStep(ImportPlans.Scan, []), vdevs)]);
        Assert.Equal(0, scan.ExitCode);
        return Assert.Single(ZpoolImportParser.Parse(scan.StandardOutput), candidate => candidate.Name == vdevs.PoolName);
    }

    [Fact]
    public async Task AnExportedPoolIsFoundThenImportedReadOnlyThenReadWriteWithOnlySafeMounts()
    {
        FileVdevs.RequireEnabled();
        // The temporary folder is below /tmp, where nothing may mount; /mnt is where an imported pool may.
        var safe = $"/mnt/pithyxci-{Guid.NewGuid():N}";
        try
        {
            await ImportReadOnlyThenReadWriteAsync(safe);
        }
        finally
        {
            if (System.IO.Directory.Exists(safe))
            {
                System.IO.Directory.Delete(safe);
            }
        }
    }

    [Fact]
    public async Task ADestroyedPoolLeavesNoFoldersSoItsNameCanBeUsedAgain()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var read = await ReadAsync(vdevs);
        var mountPoints = PlanReleases.MountPointsOf(read, read.Datasets.Where(dataset => dataset.Pool == vdevs.PoolName).Select(dataset => dataset.Name));
        var root = Path.Combine(vdevs.AltRoot, vdevs.PoolName);
        Assert.True(System.IO.Directory.Exists(Path.Combine(root, "homes")));

        await RunAsync(vdevs, ZfsPlans.DestroyPool(vdevs.PoolName));
        // The root is not mounted, so ZFS made the mount points below it as folders, and they stay.
        Assert.True(System.IO.Directory.Exists(Path.Combine(root, "homes")));
        LeftoverMountPoints.Remove(LeftoverMountPoints.Of(mountPoints));

        Assert.False(System.IO.Directory.Exists(root));
        await vdevs.CreateLayoutPoolAsync();
        Assert.NotNull(Find(await ReadAsync(vdevs), vdevs));
    }

    private static async Task ImportReadOnlyThenReadWriteAsync(string safe)
    {
        await using var vdevs = FileVdevs.Create(3);
        await vdevs.CreatePoolAsync("raidz1");
        var unsafePath = $"/usr/{vdevs.PoolName}";
        // -u: created without mounting, so nothing is ever mounted below /usr.
        await vdevs.RunAsync("zfs", ["create", "-u", "-o", $"mountpoint={safe}", $"{vdevs.PoolName}/data"]);
        await vdevs.RunAsync("zfs", ["create", "-u", "-o", $"mountpoint={unsafePath}", $"{vdevs.PoolName}/system"]);
        var guid = Find(await ReadAsync(vdevs), vdevs)!.PoolGuid;

        await RunAsync(vdevs, ImportPlans.Export(vdevs.PoolName));
        Assert.Null(Find(await ReadAsync(vdevs), vdevs));

        var candidate = await ScanAsync(vdevs, "zfs-import");
        Assert.Equal((guid, "ONLINE", true, false), (candidate.PoolGuid, candidate.State, candidate.CanImport, candidate.NeedsForce));
        Assert.Equal(vdevs.Files, Assert.Single(candidate.Vdevs).Children.Select(child => child.Name));

        await RunAsync(vdevs, ImportPlans.ImportReadOnly(guid, null, null, []));
        var readOnly = await ReadAsync(vdevs);
        Assert.True(Find(readOnly, vdevs)!.ReadOnly);
        var datasets = readOnly.Datasets.Where(dataset => dataset.Pool == vdevs.PoolName).ToList();
        Assert.Equal([vdevs.PoolName, $"{vdevs.PoolName}/data", $"{vdevs.PoolName}/system"], datasets.Select(dataset => dataset.Name));
        Assert.All(datasets, dataset => Assert.False(dataset.Mounted));

        var properties = ZfsDatasetParser.ParseProperties(await vdevs.RunAsync("zfs", ImportPlans.MountProperties(vdevs.PoolName).Arguments));
        var mounts = ImportPlans.DecideMounts(properties, MountTable.ParseMountInfo(await File.ReadAllTextAsync("/proc/self/mountinfo", Token)));
        Assert.Equal([$"{vdevs.PoolName}/data"], mounts.Mount);
        Assert.Equal([new UnmountedDataset($"{vdevs.PoolName}/system", unsafePath, "system_path")], mounts.Unmounted);

        await RunAsync(vdevs, ImportPlans.ImportReadWrite(vdevs.PoolName, guid, null, [], mounts));
        var readWrite = await ReadAsync(vdevs);
        Assert.False(Find(readWrite, vdevs)!.ReadOnly);
        var mounted = readWrite.Datasets.Where(dataset => dataset.Pool == vdevs.PoolName).ToDictionary(dataset => dataset.Name, dataset => dataset.Mounted);
        Assert.True(mounted[$"{vdevs.PoolName}/data"]);
        Assert.False(mounted[$"{vdevs.PoolName}/system"]);
        Assert.False(System.IO.Directory.Exists(unsafePath));
        var canmount = ZfsDatasetParser.ParseProperties(await vdevs.RunAsync("zfs", ImportPlans.MountProperties(vdevs.PoolName).Arguments));
        Assert.Equal("noauto", canmount[$"{vdevs.PoolName}/system"]["canmount"]);
    }

    [Fact]
    public async Task AScanShowsMissingDevicesAndPoolsThatCannotBeImported()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(3);
        await vdevs.CreatePoolAsync("raidz1");
        await RunAsync(vdevs, ImportPlans.Export(vdevs.PoolName));
        var aside = System.IO.Directory.CreateTempSubdirectory("pithyxci-aside-").FullName;
        try
        {
            File.Move(vdevs.Files[2], Path.Combine(aside, "vdev2.img"));
            var degraded = await ScanAsync(vdevs, "zfs-import-degraded");
            Assert.Equal(("DEGRADED", true, "ZFS-8000-2Q"), (degraded.State, degraded.CanImport, degraded.MessageId));
            Assert.Equal(["ONLINE", "ONLINE", "UNAVAIL"], degraded.Vdevs[0].Children.Select(child => child.State));
            Assert.Equal("cannot open", degraded.Vdevs[0].Children[2].Detail);

            File.Move(vdevs.Files[1], Path.Combine(aside, "vdev1.img"));
            var unavailable = await ScanAsync(vdevs, "zfs-import-unavail");
            Assert.Equal(("UNAVAIL", false), (unavailable.State, unavailable.CanImport));
        }
        finally
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(aside))
            {
                File.Move(file, Path.Combine(vdevs.Directory, Path.GetFileName(file)));
            }
            System.IO.Directory.Delete(aside);
        }
        // Back in place, so the pool can be imported and destroyed at the end.
        await vdevs.RunAsync("zpool", ["import", "-N", "-d", vdevs.Directory, vdevs.PoolName]);
        var empty = System.IO.Directory.CreateTempSubdirectory("pithyxci-empty-").FullName;
        try
        {
            var none = await vdevs.Commands.RunAsync(new CommandRequest("zpool", ["import", "-d", empty]), Token);
            Assert.Equal(0, none.ExitCode);
            Assert.Empty(ZpoolImportParser.Parse(none.StandardOutput));
        }
        finally
        {
            System.IO.Directory.Delete(empty);
        }
    }

    public static TheoryData<string, int, string, int> AddCombinations => new()
    {
        { "single", 1, "single", 1 },
        { "mirror", 2, "single", 1 },
        { "mirror", 2, "mirror", 2 },
        { "mirror", 2, "mirror", 3 },
        { "mirror", 2, "raidz1", 3 },
        { "mirror", 2, "raidz2", 4 },
        { "mirror", 3, "raidz2", 4 },
        { "raidz1", 3, "mirror", 2 },
        { "raidz1", 3, "mirror", 3 },
        { "raidz1", 3, "raidz1", 3 },
        { "raidz1", 3, "raidz1", 4 },
        { "raidz1", 3, "raidz2", 4 },
        { "raidz1", 3, "single", 1 },
        { "raidz2", 4, "mirror", 2 },
    };

    /// <summary>The prediction of <see cref="VdevRedundancy.Mismatch"/> is what <c>zpool add</c> itself decides.</summary>
    [Theory]
    [MemberData(nameof(AddCombinations))]
    public async Task ZpoolRefusesExactlyTheMismatchesThePlannerPredicts(string poolLayout, int poolDisks, string addLayout, int addDisks)
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(poolDisks);
        await vdevs.CreatePoolAsync(poolLayout == "single" ? [] : [poolLayout]);
        var added = Enumerable.Range(0, addDisks).Select(index => FileDisk(vdevs.AddFile($"add{index}.img"), index)).ToList();
        var pool = Find(await ReadAsync(vdevs), vdevs)!;
        var assessment = VdevRedundancy.AssessAdd(pool, addLayout, addDisks);

        var plain = ZfsPlans.AddVdev(vdevs.PoolName, addLayout, added, null).Single().Command;
        var dryRun = await vdevs.Commands.RunAsync(new CommandRequest("zpool", ["add", "-n", .. plain.Arguments.Skip(1)]), Token);

        // OpenZFS 2.3 (Debian 13) refuses raidz vdevs of another width; 2.4 compares only the parity of raidz and
        // draid vdevs. The planner keeps asking for the override then, which 2.4 accepts without needing it.
        var widthOnly = poolLayout == addLayout && addLayout.StartsWith("raidz", StringComparison.Ordinal) && poolDisks != addDisks;
        Assert.Equal(assessment.ReplicationMismatch, dryRun.ExitCode != 0 || (widthOnly && ZfsVersionAtLeast(2, 4)));
        if (dryRun.ExitCode != 0)
        {
            Assert.Contains("mismatched replication level", dryRun.StandardError, StringComparison.Ordinal);
        }
    }

    /// <summary>The loaded kernel module's version, for example <c>2.3.9-0+deb13u1</c>.</summary>
    private static bool ZfsVersionAtLeast(int major, int minor)
    {
        var parts = File.ReadAllText("/sys/module/zfs/version").Split('-')[0].Split('.');
        var version = new Version(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        return version >= new Version(major, minor);
    }

    [Fact]
    public async Task AddingASingleDiskToAMirrorRunsOnlyWithTheOverrideAndAddsAVdev()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(2);
        await vdevs.CreatePoolAsync("mirror");
        var disk = FileDisk(vdevs.AddFile("add0.img"), 0);

        var refused = ZfsPlans.AddVdev(vdevs.PoolName, "single", [disk], null).Single().Command;
        Assert.NotEqual(0, (await vdevs.Commands.RunAsync(refused, Token)).ExitCode);
        await RunAsync(vdevs, ZfsPlans.AddVdev(vdevs.PoolName, "single", [disk], TypedName.Confirm(vdevs.PoolName, vdevs.PoolName)));

        var pool = Find(await ReadAsync(vdevs), vdevs)!;
        Assert.Equal(["mirror", "file"], pool.Vdevs.Where(vdev => vdev.Role == "data").Select(vdev => vdev.Type));
    }

    [Fact]
    public async Task ARaidzGrowsByOneDiskAndReportsTheExpansion()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(3);
        await vdevs.CreatePoolAsync("raidz1");
        var mountPoint = Path.Combine(vdevs.Directory, "data");
        await vdevs.RunAsync("zfs", ["create", "-o", $"mountpoint={mountPoint}", $"{vdevs.PoolName}/data"]);
        // Some data, so the expansion has something to move and is seen while it runs.
        var block = new byte[16 * 1024 * 1024];
        Random.Shared.NextBytes(block);
        for (var i = 0; i < 20; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(mountPoint, $"blob{i}"), block, Token);
        }
        await vdevs.RunAsync("zpool", ["sync", vdevs.PoolName]);
        var raidz = Assert.Single(Find(await ReadAsync(vdevs), vdevs)!.Vdevs);
        Assert.Null(Find(await ReadAsync(vdevs), vdevs)!.Expansion);

        await RunAsync(vdevs, ZfsPlans.ExpandRaidz(vdevs.PoolName, raidz.VdevGuid, FileDisk(vdevs.AddFile("vdev3.img"), 3)));

        var status = await vdevs.RecordAsync("zfs-raidz-expanding", "zpool-status", "pithyxtestr", "zpool", ZfsBackend.Status.Arguments);
        await vdevs.RecordAsync("zfs-raidz-expanding", "zpool-list", "pithyxtestr", "zpool", ZfsBackend.List.Arguments);
        await vdevs.RecordAsync("zfs-raidz-expanding", "zfs-list", "pithyxtestr", "zfs", ZfsBackend.Datasets.Arguments);
        var running = Assert.Single(ZpoolStatusParser.Parse(status.StandardOutput), pool => pool.Name == vdevs.PoolName).Expansion!;
        Assert.Equal(raidz.VdevGuid, running.VdevGuid);
        Assert.True(running.ToReflowBytes > 0);
        Assert.NotNull(running.StartedAt);

        await vdevs.RunAsync("zpool", ["wait", "-t", "raidz_expand", vdevs.PoolName]);
        var done = await vdevs.RecordAsync("zfs-raidz-expanded", "zpool-status", "pithyxtestr", "zpool", ZfsBackend.Status.Arguments);
        await vdevs.RecordAsync("zfs-raidz-expanded", "zpool-list", "pithyxtestr", "zpool", ZfsBackend.List.Arguments);
        await vdevs.RecordAsync("zfs-raidz-expanded", "zfs-list", "pithyxtestr", "zfs", ZfsBackend.Datasets.Arguments);
        var expanded = Assert.Single(ZpoolStatusParser.Parse(done.StandardOutput), pool => pool.Name == vdevs.PoolName);
        Assert.Equal("finished", expanded.Expansion!.State);
        Assert.NotNull(expanded.Expansion.EndedAt);
        Assert.Equal(4, Assert.Single(expanded.Vdevs).Children.Count);
    }
}
