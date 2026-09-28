using Pithyx.Daemon.Storage.Zfs;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// The dataset list with volumes and a quota, recorded on dev01 by <c>DatasetIntegrationTests</c> on a file pool
/// (<c>PITHYX_RECORD_FIXTURES</c>); the owner's test pool is in it too, read-only.
/// </summary>
public sealed class ZfsVolumeParserTests
{
    [Fact]
    public void VolumesCarryTheirSizeAndBlockSizeAndNoMountPoint()
    {
        var datasets = ZfsDatasetParser.ParseList(Read("zfs-volumes", "zfs-list"));
        var thick = datasets.Single(dataset => dataset.Name.EndsWith("/pithyx/volumes/thick", StringComparison.Ordinal));
        var sparse = datasets.Single(dataset => dataset.Name.EndsWith("/pithyx/volumes/sparse", StringComparison.Ordinal));

        Assert.Equal(("volume", (long?)33570816, (long?)16384), (thick.Type, thick.VolumeSizeBytes, thick.VolumeBlockSizeBytes));
        Assert.Equal(((string?)null, false, (long?)null), (thick.MountPoint, thick.Mounted, thick.RecordSizeBytes));
        // The thick volume's reservation counts as used; the sparse one holds only its metadata.
        Assert.True(thick.UsedBytes > thick.VolumeSizeBytes);
        Assert.Equal((long?)1073741824, sparse.VolumeSizeBytes);
        Assert.True(sparse.UsedBytes < 1024 * 1024);
        // A reservation ZFS sets on its own (refreservation) is not the reservation a user sets.
        Assert.Null(thick.ReservationBytes);
        var parent = datasets.Single(dataset => dataset.Name.EndsWith("/pithyx/volumes", StringComparison.Ordinal));
        Assert.Equal(("filesystem", (string?)null), (parent.Type, parent.MountPoint));
    }

    [Fact]
    public void AQuotaAndCompressionSetByAPlanAreRead()
    {
        var media = ZfsDatasetParser.ParseList(Read("zfs-volumes", "zfs-list")).Single(dataset => dataset.Name.EndsWith("/pithyx/shares/media", StringComparison.Ordinal));

        Assert.Equal(((long?)67108864, (long?)null, "zstd"), (media.QuotaBytes, media.ReservationBytes, media.Compression));
        Assert.EndsWith($"/{media.Pool}/media", media.MountPoint, StringComparison.Ordinal);
    }
}
