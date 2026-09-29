using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Files.Thumbnails;

namespace Pithyx.Daemon.Tests.Files.Thumbnails;

public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.CreateTempSubdirectory("pithyx-thumbnails-").FullName, "thumbnails");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);

    private static FsStat Stat(long size = 100, int minute = 0, string id = "1.2") =>
        new(FsKind.File, size, new DateTimeOffset(2026, 9, 1, 12, minute, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 12, minute, 0, TimeSpan.Zero), id, 0, 0);

    [Fact]
    public void TheKeyChangesWithEverythingThatChangesTheFile()
    {
        var key = ThumbnailCache.KeyOf("/tank/media", "a/b.jpg", Stat(), ThumbnailSize.Large);

        Assert.Equal(key, ThumbnailCache.KeyOf("/tank/media", "a/b.jpg", Stat(), ThumbnailSize.Large));
        Assert.All(
            new[]
            {
                ThumbnailCache.KeyOf("/tank/media", "a/b.jpg", Stat(size: 101), ThumbnailSize.Large),
                ThumbnailCache.KeyOf("/tank/media", "a/b.jpg", Stat(minute: 1), ThumbnailSize.Large),
                ThumbnailCache.KeyOf("/tank/media", "a/b.jpg", Stat(id: "1.3"), ThumbnailSize.Large),
                ThumbnailCache.KeyOf("/tank/media", "a/c.jpg", Stat(), ThumbnailSize.Large),
                ThumbnailCache.KeyOf("/tank/other", "a/b.jpg", Stat(), ThumbnailSize.Large),
                ThumbnailCache.KeyOf("/tank/media", "a/b.jpg", Stat(), ThumbnailSize.Small),
            },
            other => Assert.NotEqual(key, other));
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void StoresAndFinds()
    {
        var cache = new ThumbnailCache(_directory, _time);

        Assert.Null(cache.Find("aa"));
        cache.Store("aa", [1, 2, 3]);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(cache.Find("aa")!));
        Assert.Equal(3, cache.TotalBytes);
    }

    [Fact]
    public void EvictsTheLeastRecentlyUsedDownToNinetyPercent()
    {
        var cache = new ThumbnailCache(_directory, _time, limitBytes: 1000);
        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            cache.Store(key, new byte[250]);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        cache.Find("a");
        _time.Advance(TimeSpan.FromSeconds(1));

        cache.Store("e", new byte[250]);

        Assert.NotNull(cache.Find("a"));
        Assert.Null(cache.Find("b"));
        Assert.Null(cache.Find("c"));
        Assert.NotNull(cache.Find("d"));
        Assert.NotNull(cache.Find("e"));
        Assert.Equal(750, cache.TotalBytes);
        Assert.Equal(3, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void AfterARestartTheIndexComesFromTheFolderAndLeftoversAreRemoved()
    {
        new ThumbnailCache(_directory, _time).Store("aa", new byte[10]);
        File.WriteAllBytes(Path.Combine(_directory, "bb.webp.123.tmp"), new byte[5]);

        var cache = new ThumbnailCache(_directory, _time);

        Assert.NotNull(cache.Find("aa"));
        Assert.Equal(10, cache.TotalBytes);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void AThumbnailDeletedByHandIsAMiss()
    {
        var cache = new ThumbnailCache(_directory, _time);
        cache.Store("aa", new byte[10]);
        File.Delete(Path.Combine(_directory, "aa.webp"));

        Assert.Null(cache.Find("aa"));
        Assert.Equal(0, cache.TotalBytes);
    }
}
