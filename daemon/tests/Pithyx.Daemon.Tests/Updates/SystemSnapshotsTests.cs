using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

public sealed class SystemSnapshotsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "findmnt", name));

    private static SnapshotInfo Snapshot(string name, string version = "0.1.0") =>
        new(name, version, DateTimeOffset.ParseExact(name, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal), "6.12.111+deb13-amd64", "update");

    [Fact]
    public void NamesAreTheUtcTime()
    {
        var name = SystemSnapshots.Name(new DateTimeOffset(2026, 10, 9, 23, 40, 5, TimeSpan.FromHours(2)));

        Assert.Equal("20261009T214005Z", name);
        Assert.True(SystemSnapshots.IsName(name));
        Assert.False(SystemSnapshots.IsName("../etc"));
    }

    [Fact]
    public void TheNewestThreeStay()
    {
        List<SnapshotInfo> snapshots = [Snapshot("20261001T000000Z"), Snapshot("20261005T000000Z"), Snapshot("20261003T000000Z"), Snapshot("20261007T000000Z"), Snapshot("20261009T000000Z")];

        Assert.Equal(["20261001T000000Z", "20261003T000000Z"], SystemSnapshots.Prune(snapshots, 3, protect: null));
    }

    [Fact]
    public void TheSnapshotOfAWaitingUpdateNeverGoes()
    {
        List<SnapshotInfo> snapshots = [Snapshot("20261001T000000Z"), Snapshot("20261003T000000Z"), Snapshot("20261005T000000Z"), Snapshot("20261007T000000Z")];

        Assert.Empty(SystemSnapshots.Prune(snapshots, 3, protect: "20261001T000000Z"));
    }

    [Fact]
    public void TheMenuHasAnEntryPerSnapshotAndTheAutomaticRollback()
    {
        var menu = SystemSnapshots.RenderMenu([Snapshot("20261003T000000Z", "0.1.0"), Snapshot("20261009T000000Z", "0.2.0")], "253e75c2", trialSnapshot: "20261009T000000Z");

        Assert.Contains("menuentry 'Pithyx 0.2.0 from 2026-10-09 00:00 UTC (roll back)' --id pithyx-snapshot-20261009T000000Z --hotkey=1", menu, StringComparison.Ordinal);
        Assert.Contains("linux /@snapshots/20261003T000000Z/boot/vmlinuz-6.12.111+deb13-amd64 root=UUID=253e75c2 ro rootflags=subvol=@snapshots/20261003T000000Z systemd.unit=pithyx-recovery.target", menu, StringComparison.Ordinal);
        Assert.Contains("--id pithyx-rollback-auto", menu, StringComparison.Ordinal);
        Assert.Contains("pithyx.rollback=auto:20261009T000000Z", menu, StringComparison.Ordinal);
        Assert.True(menu.IndexOf("20261009T000000Z", StringComparison.Ordinal) < menu.IndexOf("20261003T000000Z", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAWaitingUpdateThereIsNoAutomaticRollback() =>
        Assert.DoesNotContain(SystemSnapshots.AutoRollbackEntry, SystemSnapshots.RenderMenu([Snapshot("20261003T000000Z")], "u", trialSnapshot: null), StringComparison.Ordinal);

    [Fact]
    public void TheInstallersLayoutIsSupported()
    {
        var layout = SystemSnapshots.CheckLayout(Fixture("btrfs-subvol.json"), "/dev/loop0");

        Assert.True(layout.Supported);
        Assert.Equal("253e75c2-1a7c-43ba-9196-8bddc98c8e66", layout.FileSystemUuid);
    }

    [Fact]
    public void ADevelopmentMachineIsNot() =>
        Assert.Equal("not_btrfs", SystemSnapshots.CheckLayout(Fixture("devbox-root.json"), null).Reason);

    [Fact]
    public void AnotherDeviceIsNotTheSystemArray() =>
        Assert.Equal("not_array", SystemSnapshots.CheckLayout(Fixture("btrfs-subvol.json"), "/dev/md127").Reason);

    [Fact]
    public void TheRollbackMovesTheRootAsideAndSnapshotsTheChosenOne()
    {
        var plan = SystemSnapshots.Plans.Rollback("/run/pithyx/system", "20261003T000000Z", new DateTimeOffset(2026, 10, 9, 22, 0, 0, TimeSpan.Zero));

        Assert.Equal(["mv --no-target-directory /run/pithyx/system/@ /run/pithyx/system/@rollback-20261009T220000Z",
            "btrfs subvolume snapshot /run/pithyx/system/@snapshots/20261003T000000Z /run/pithyx/system/@"],
            plan.Select(command => $"{command.FileName} {string.Join(' ', command.Arguments)}"));
    }
}
