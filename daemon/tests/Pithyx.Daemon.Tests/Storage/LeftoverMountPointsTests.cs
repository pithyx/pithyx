using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>After a destroy or export only the pool's own empty mount point folders go.</summary>
public sealed class LeftoverMountPointsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-leftovers-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheMountPointsAndTheFoldersBetweenThemComeDeepestFirst()
    {
        var folders = LeftoverMountPoints.Of(["/tank", "/tank/homes", "/tank/.pithyx/cache", "/tank/.pithyx/system", "/"]);

        Assert.Equal(["/tank/.pithyx/cache", "/tank/.pithyx/system", "/tank/.pithyx", "/tank/homes", "/tank"], folders);
    }

    [Fact]
    public void FoldersAboveTheMountPointsAreNeverTouched()
    {
        Assert.Equal(["/mnt/data/media"], LeftoverMountPoints.Of(["/mnt/data/media"]));
    }

    [Fact]
    public void OnlyEmptyFoldersAreRemoved()
    {
        var pool = Path.Combine(_root, "tank");
        Directory.CreateDirectory(Path.Combine(pool, "homes"));
        Directory.CreateDirectory(Path.Combine(pool, ".pithyx", "cache"));
        Directory.CreateDirectory(Path.Combine(pool, "media"));
        File.WriteAllText(Path.Combine(pool, "media", "kept.txt"), "data");

        LeftoverMountPoints.Remove(LeftoverMountPoints.Of([pool, Path.Combine(pool, "homes"), Path.Combine(pool, ".pithyx", "cache"), Path.Combine(pool, "media")]));

        Assert.False(Directory.Exists(Path.Combine(pool, "homes")));
        Assert.False(Directory.Exists(Path.Combine(pool, ".pithyx")));
        Assert.True(File.Exists(Path.Combine(pool, "media", "kept.txt")));
        Assert.True(Directory.Exists(pool));
    }
}
