using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>restic's JSON about versions (#404), on recorded output of restic 0.18.</summary>
public sealed class ResticVersionsTests
{
    [Fact]
    public void SnapshotsAreListedOldestFirstInUtcWithTheirOriginAndProtection()
    {
        var snapshots = ResticVersions.Snapshots(Fixture("restic-snapshots.stdout"))!;

        Assert.Equal(2, snapshots.Count);
        var (protectedOne, plain) = (snapshots[0], snapshots[1]);
        Assert.StartsWith("9f9e9f2c", protectedOne.Id, StringComparison.Ordinal);
        Assert.StartsWith("87a0dfd8", protectedOne.Origin, StringComparison.Ordinal);
        Assert.True(protectedOne.Protected);
        Assert.Equal(TimeSpan.Zero, protectedOne.Time.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 18, 42, 1, TimeSpan.Zero), protectedOne.Time.AddTicks(-(protectedOne.Time.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal((2L, 4L, 1711L, 2L, 0L), (protectedOne.Files, protectedOne.Bytes, protectedOne.AddedBytes, protectedOne.FilesAdded, protectedOne.FilesChanged));
        Assert.Equal(["/tank/documents"], protectedOne.Paths);
        Assert.Equal(plain.Id, plain.Origin);
        Assert.False(plain.Protected);
        Assert.Equal((1L, 1L), (plain.FilesAdded, plain.FilesChanged));
    }

    [Fact]
    public void OutputThatIsNoListGivesNoSnapshots()
    {
        Assert.Null(ResticVersions.Snapshots("Fatal: repository does not exist\n"));
        Assert.Empty(ResticVersions.Snapshots("[]\n")!);
    }

    [Fact]
    public void ForgetNamesWhyEachVersionWasKeptBeforeThePrunesText()
    {
        var keep = ResticVersions.KeepReasons(Fixture("restic-forget.stdout"))!;

        var (id, reasons) = Assert.Single(keep);
        Assert.StartsWith("00cd32bc", id, StringComparison.Ordinal);
        Assert.Equal([BackupKeepReason.Last], reasons);
    }

    [Fact]
    public void AProtectedVersionIsKeptByItsTagAndTheOldestWeeklyCountsAsWeekly()
    {
        var keep = ResticVersions.KeepReasons(Fixture("restic-forget-protected.stdout"))!;

        Assert.Equal([BackupKeepReason.Last, BackupKeepReason.Weekly, BackupKeepReason.Monthly], keep.Single(pair => pair.Key.StartsWith("cf33a011", StringComparison.Ordinal)).Value);
        Assert.Equal([BackupKeepReason.Weekly, BackupKeepReason.Protected], keep.Single(pair => pair.Key.StartsWith("d22c342d", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public void ADiffListsFilesWithTheirChangeAndResticsCounts()
    {
        var diff = ResticVersions.Diff(Fixture("restic-diff.stdout"))!;

        Assert.Equal(
            [
                new BackupVersionChange("/tank/documents/a/x.txt", BackupChangeKind.Removed),
                new BackupVersionChange("/tank/documents/a/z.txt", BackupChangeKind.Added),
                new BackupVersionChange("/tank/documents/y.txt", BackupChangeKind.Modified),
            ],
            diff.Changes);
        Assert.Equal((1L, 1L, 1L), (diff.Added, diff.Modified, diff.Removed));
    }

    [Fact]
    public void ADiffKeepsOnlyTheFirstFilesAndLeavesFoldersAndMetadataOut()
    {
        var lines = Enumerable.Range(0, ResticVersions.KeptChanges + 20)
            .Select(index => $$"""{"message_type":"change","path":"/tank/f{{index}}","modifier":"+"}""")
            .Prepend("""{"message_type":"change","path":"/tank/a/","modifier":"+"}""")
            .Prepend("""{"message_type":"change","path":"/tank/meta","modifier":"U"}""")
            .Append("""{"message_type":"statistics","changed_files":0,"added":{"files":120},"removed":{"files":0}}""");

        var diff = ResticVersions.Diff(string.Join('\n', lines))!;

        Assert.Equal(ResticVersions.KeptChanges, diff.Changes.Count);
        Assert.Equal("/tank/f0", diff.Changes[0].Path);
        Assert.Equal(120, diff.Added);
        Assert.Null(ResticVersions.Diff("Fatal: no matching ID found\n"));
    }

    [Fact]
    public void TaggingGivesTheVersionANewId()
    {
        Assert.StartsWith("d22c342d", ResticVersions.TagChanged(Fixture("restic-tag.stdout")), StringComparison.Ordinal);
        Assert.Null(ResticVersions.TagChanged("""{"message_type":"summary","changed_snapshots":0}"""));
        Assert.Equal("d22c342d", ResticVersions.ShortId("d22c342de47a8e02"));
    }

    [Fact]
    public void ProtectingTagsOneVersionAndForgetKeepsTaggedOnes()
    {
        Assert.Equal(["--json", "tag", "--add", "pithyx-protected", "--", "abc"], ResticCommands.Protect(["--json"], "abc", protect: true));
        Assert.Equal(["--json", "tag", "--remove", "pithyx-protected", "--", "abc"], ResticCommands.Protect(["--json"], "abc", protect: false));
        Assert.Equal(["--json", "diff", "--", "a", "b"], ResticCommands.Diff(["--json"], "a", "b"));
    }

    [Fact]
    public void OnlyMountedBackupCachesOfAPoolAreSwept()
    {
        var listing = "tank\tyes\t/tank\ntank/pithyx/backup-cache\tyes\t/tank/.pithyx/backup-cache\n"
            + "other/pithyx/backup-cache\tno\t/other/.pithyx/backup-cache\ntank/x/pithyx/backup-cache\tyes\t/tank/x/.pithyx/backup-cache\n";

        Assert.Equal(["/tank/.pithyx/backup-cache"], BackupJobRules.CacheRoots(listing));
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "backups", name));
}
