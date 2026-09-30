using Pithyx.Daemon.Files.Thumbnails;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Files.Thumbnails;

public sealed class ThumbnailCachesTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const string Fallback = "/dev-cache/thumbnails";

    private static Pool Pool(string name, string state = "ONLINE", bool readOnly = false, string guid = "1") =>
        new(name, guid, state, 200 * GiB, 10 * GiB, 190 * GiB, 5, 0, readOnly, 0, null, []);

    private static Dataset Filesystem(string name, string? mountPoint, bool mounted = true, string? keyStatus = null) =>
        new(name, name.Split('/')[0], "filesystem", GiB, 100 * GiB, GiB, mountPoint, mounted && mountPoint is not null, keyStatus is null ? "off" : "aes-256-gcm", keyStatus, null, null, null, "lz4", 128 * 1024, null, null, 0);

    private static List<Dataset> Layout(string pool, bool cache = true, bool cacheMounted = true)
    {
        List<Dataset> datasets =
        [
            Filesystem(pool, null),
            Filesystem($"{pool}/pithyx", null),
            Filesystem($"{pool}/pithyx/shares", $"/{pool}", mounted: false),
            Filesystem($"{pool}/pithyx/shares/media", $"/{pool}/media"),
            Filesystem($"{pool}/pithyx/shares/media/photos", $"/{pool}/media/photos"),
        ];
        if (cache)
        {
            datasets.Add(Filesystem($"{pool}/pithyx/cache", $"/{pool}/.pithyx/cache", mounted: cacheMounted));
        }
        return datasets;
    }

    [Theory]
    [InlineData("/tank/media", "/tank/.pithyx/cache/thumbnails")]
    [InlineData("/tank/media/photos/2026", "/tank/.pithyx/cache/thumbnails")]
    [InlineData("/other/media", "/other/.pithyx/cache/thumbnails")]
    [InlineData("/home/dev/shares/docs", Fallback)]
    [InlineData("/tankmedia", Fallback)]
    public void AShareUsesTheCacheOfThePoolThatHoldsIt(string sharePath, string expected)
    {
        var pools = new PoolList([Pool("tank"), Pool("other", guid: "2")], [.. Layout("tank"), .. Layout("other")]);

        Assert.Equal(expected, ThumbnailCaches.DirectoryFor(pools, sharePath, Fallback));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void APoolWithoutAMountedCacheDatasetGetsNoThumbnails(bool cache, bool mounted)
    {
        var pools = new PoolList([Pool("tank")], Layout("tank", cache, mounted));

        Assert.Null(ThumbnailCaches.DirectoryFor(pools, "/tank/media", Fallback));
    }

    [Fact]
    public void WithoutAFallbackAShareOutsideAPoolGetsNoThumbnails()
    {
        var pools = new PoolList([Pool("tank")], Layout("tank"));

        Assert.Null(ThumbnailCaches.DirectoryFor(pools, "/srv/share", null));
    }

    [Fact]
    public void AnExportedPoolTakesItsCacheAlong()
    {
        var inventory = new PoolInventory();
        inventory.Replace(new PoolList([Pool("tank")], Layout("tank")));
        using var caches = new ThumbnailCaches(inventory, TimeProvider.System, null);
        var before = caches.For("/tank/media");
        Assert.NotNull(before);

        inventory.Replace(new PoolList([], []));
        Assert.Null(caches.For("/tank/media"));
        inventory.Replace(new PoolList([Pool("tank")], Layout("tank")));

        Assert.NotSame(before, caches.For("/tank/media"));
    }

    [Fact]
    public void OnlyWritableUnlockedPoolsWithThePithyxLayoutGetACacheDataset()
    {
        var pools = new PoolList(
            [
                Pool("ready"),
                Pool("done", guid: "2"),
                Pool("readonly", readOnly: true, guid: "3"),
                Pool("faulted", state: "FAULTED", guid: "4"),
                Pool("degraded", state: "DEGRADED", guid: "5"),
                Pool("foreign", guid: "6"),
                Pool("locked", guid: "7"),
            ],
            [
                .. Layout("ready", cache: false),
                .. Layout("done"),
                .. Layout("readonly", cache: false),
                .. Layout("faulted", cache: false),
                .. Layout("degraded", cache: false),
                Filesystem("foreign", "/foreign"),
                Filesystem("locked", null),
                Filesystem("locked/pithyx", null, keyStatus: "unavailable"),
            ]);

        Assert.Equal(["ready", "degraded"], ThumbnailCacheProvisioner.Missing(pools).Select(pool => pool.Name));
    }
}
