using Pithyx.Daemon.Backups;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>How versions name their folders and map the names back to the stored paths (#395).</summary>
public sealed class BackupRestorePathsTests
{
    private static readonly BackupShareRoot[] Roots =
    [
        new(1, "Media", "/tank/shares/media"),
        new(2, "Media Archive", "/tank/shares/media/archive"),
        new(3, "Homes/anna", "/tank/homes/anna/"),
    ];

    [Fact]
    public void AStoredPathIsNamedAfterTheInnermostShareThatHoldsItNow()
    {
        Assert.Equal("Media/Movies/a.mkv", BackupRestorePaths.Name("/tank/shares/media/Movies/a.mkv", Roots));
        Assert.Equal("Media Archive/2019", BackupRestorePaths.Name("/tank/shares/media/archive/2019", Roots));
        Assert.Equal("Homes/anna", BackupRestorePaths.Name("/tank/homes/anna", Roots));
        Assert.Equal("Homes/anna/Photos", BackupRestorePaths.Name("/tank/homes/anna/Photos", Roots));
    }

    [Fact]
    public void APathNoShareHoldsKeepsItsStoredNameAndAPrefixIsNoParent()
    {
        Assert.Equal("/tank/shares/mediaX/a", BackupRestorePaths.Name("/tank/shares/mediaX/a", Roots));
        Assert.Null(BackupRestorePaths.Place("/tank/shares/mediaX/a", Roots));
        var place = BackupRestorePaths.Place("/tank/shares/media/archive", Roots)!.Value;
        Assert.Equal((2L, ""), (place.Root.Id, place.Relative));
    }

    [Fact]
    public void ANameMapsBackThroughTheVersionsFoldersAndRefusesDotsAndEmptyParts()
    {
        BackupVersionSource[] sources = [new("Media", "/tank/shares/media"), new("Media/Movies/Old", "/old/movies")];

        Assert.Equal("/tank/shares/media", BackupRestorePaths.Stored("Media", sources));
        Assert.Equal("/tank/shares/media/Series/s1", BackupRestorePaths.Stored("Media/Series/s1", sources));
        Assert.Equal("/old/movies/x", BackupRestorePaths.Stored("Media/Movies/Old/x", sources));
        Assert.Null(BackupRestorePaths.Stored("MediaX/a", sources));
        Assert.Null(BackupRestorePaths.Stored("Media/../etc", sources));
        Assert.Null(BackupRestorePaths.Stored("Media/./a", sources));
        Assert.Null(BackupRestorePaths.Stored("Media//a", sources));
        Assert.Null(BackupRestorePaths.Stored("Media/a\0b", sources));
        Assert.Null(BackupRestorePaths.Stored("Photos", sources));
    }

    [Fact]
    public void NamesAreCleanedAndTheLastPartIsTheBasename()
    {
        Assert.Equal("Media/a", BackupRestorePaths.Clean("/Media/a/"));
        Assert.Equal("", BackupRestorePaths.Clean(null));
        Assert.Equal("a.mkv", BackupRestorePaths.Last("Media/Movies/a.mkv"));
        Assert.Equal("Media", BackupRestorePaths.Last("Media/"));
    }
}
