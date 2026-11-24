using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>Moving what restic restored into its place, on real folders (#395, D419).</summary>
public sealed class BackupRestoreMergeTests : IDisposable
{
    private static readonly DateTimeOffset VersionTime = new(2026, 11, 23, 18, 42, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pithyx-restore-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void AnEntryThatIsNotInPlaceIsMovedThereWhole()
    {
        Write("staged/docs/a.txt", "old a");
        Write("staged/docs/sub/b.txt", "old b");
        Directory.CreateDirectory(Full("live"));

        var tally = Merge("docs", BackupRestoreConflict.KeepBoth);

        Assert.Equal("old b", Read("live/docs/sub/b.txt"));
        Assert.Equal((1L, 0L, 0L, 0L), Counts(tally));
        Assert.False(Directory.Exists(Full("staged/docs")));
    }

    [Fact]
    public void FoldersAreMergedAndAFileInPlaceIsKeptNextToTheRestoredOne()
    {
        Write("staged/docs/a.txt", "old a");
        Write("staged/docs/new.txt", "gone");
        Write("live/docs/a.txt", "new a");
        Write("live/docs/mine.txt", "mine");

        var tally = Merge("docs", BackupRestoreConflict.KeepBoth);

        Assert.Equal("new a", Read("live/docs/a.txt"));
        Assert.Equal("old a", Read("live/docs/a (version 2026-11-23 18.42 UTC).txt"));
        Assert.Equal("gone", Read("live/docs/new.txt"));
        Assert.Equal("mine", Read("live/docs/mine.txt"));
        Assert.Equal((1L, 0L, 1L, 0L), Counts(tally));
    }

    [Fact]
    public void KeepingBothLeavesAFileOfTheSameSizeAndTimeAlone()
    {
        Write("staged/docs/same.txt", "same");
        Write("live/docs/same.txt", "same");
        File.SetLastWriteTimeUtc(Full("live/docs/same.txt"), StagedTime);
        Write("staged/docs/touched.txt", "same");
        Write("live/docs/touched.txt", "same");

        var tally = Merge("docs", BackupRestoreConflict.KeepBoth);

        Assert.Equal(["same.txt", "touched (version 2026-11-23 18.42 UTC).txt", "touched.txt"], Directory.GetFiles(Full("live/docs")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal((0L, 0L, 1L, 1L), Counts(tally));
    }

    [Fact]
    public void KeepingBothTwiceFindsAFreeName()
    {
        Write("staged/docs/a.txt", "old a");
        Write("live/docs/a.txt", "new a");
        Write("live/docs/a (version 2026-11-23 18.42 UTC).txt", "earlier restore");

        Merge("docs", BackupRestoreConflict.KeepBoth);

        Assert.Equal("earlier restore", Read("live/docs/a (version 2026-11-23 18.42 UTC).txt"));
        Assert.Equal("old a", Read("live/docs/a (version 2026-11-23 18.42 UTC) (2).txt"));
    }

    [Fact]
    public void OverwriteReplacesFilesAndSkipLeavesThem()
    {
        Write("staged/docs/a.txt", "old a");
        Write("live/docs/a.txt", "new a");
        Assert.Equal((0L, 1L, 0L, 0L), Counts(Merge("docs", BackupRestoreConflict.Overwrite)));
        Assert.Equal("old a", Read("live/docs/a.txt"));

        Write("staged/docs/a.txt", "older a");
        Assert.Equal((0L, 0L, 0L, 1L), Counts(Merge("docs", BackupRestoreConflict.Skip)));
        Assert.Equal("old a", Read("live/docs/a.txt"));
    }

    [Fact]
    public void AFolderNeverReplacesAFileOrTheOtherWayRoundEvenWithOverwrite()
    {
        Write("staged/docs/a", "file now folder");
        Write("live/docs/a/inside.txt", "kept");

        var tally = Merge("docs", BackupRestoreConflict.Overwrite);

        Assert.Equal("kept", Read("live/docs/a/inside.txt"));
        Assert.Equal("file now folder", Read("live/docs/a (version 2026-11-23 18.42 UTC)"));
        Assert.Equal((0L, 0L, 1L, 0L), Counts(tally));
    }

    [Fact]
    public void ASymlinkInPlaceIsNeverFollowedIntoAnotherFolder()
    {
        Write("staged/docs/sub/a.txt", "old a");
        Directory.CreateDirectory(Full("live/docs"));
        Directory.CreateDirectory(Full("elsewhere"));
        File.CreateSymbolicLink(Full("live/docs/sub"), Full("elsewhere"));

        var tally = Merge("docs", BackupRestoreConflict.Overwrite);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("elsewhere")));
        Assert.Equal("old a", Read("live/docs/sub (version 2026-11-23 18.42 UTC)/a.txt"));
        Assert.Equal((0L, 0L, 1L, 0L), Counts(tally));
    }

    [Fact]
    public void TheStagedCopyOfAStoredPathIsOpenedFolderByFolder()
    {
        Write("staged/tank/shares/media/a.txt", "a");
        using var root = LinuxFs.OpenRoot(Full("staged"));

        using (var found = BackupRestoreMerge.OpenStaged(root, "/tank/shares/media"))
        {
            Assert.NotNull(found);
            Assert.NotNull(FileService.TryStat(found!, "a.txt"));
        }
        Assert.Null(BackupRestoreMerge.OpenStaged(root, "/tank/shares/other"));
        Assert.Null(BackupRestoreMerge.OpenStaged(root, "/tank/shares/media/a.txt"));
    }

    private BackupRestoreTally Merge(string name, BackupRestoreConflict conflict)
    {
        var tally = new BackupRestoreTally();
        using var from = LinuxFs.OpenRoot(Full("staged"));
        using var to = LinuxFs.OpenRoot(Full("live"));
        BackupRestoreMerge.Entry(from, name, to, name, conflict, VersionTime, tally, CancellationToken.None);
        return tally;
    }

    private static (long, long, long, long) Counts(BackupRestoreTally tally) => (tally.Restored, tally.Replaced, tally.Renamed, tally.Skipped);

    private string Full(string path) => Path.Combine(_root.FullName, path);

    /// <summary>restic restores the stored time; files in place keep the time they were written.</summary>
    private static readonly DateTime StagedTime = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
        File.WriteAllText(Full(path), content);
        if (path.StartsWith("staged/", StringComparison.Ordinal))
        {
            File.SetLastWriteTimeUtc(Full(path), StagedTime);
        }
    }

    private string Read(string path) => File.ReadAllText(Full(path));
}
