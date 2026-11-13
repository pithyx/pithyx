using System.Security.Cryptography;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// User quotas on a shared folder's dataset on real OpenZFS (D411): the box's commands set and read them, ZFS counts a
/// file for its owner and refuses a write over the quota with EDQUOT, which the box reports as <c>fs.quota.exceeded</c>.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ShareQuotaIntegrationTests
{
    private const long Uid = 29997;
    private const long Quota = 2 * 1024 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AWriteOverAUsersQuotaFailsAsQuotaExceeded()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var dataset = $"{vdevs.PoolName}/pithyx/shares/quota";
        await vdevs.RunAsync("zfs", ["create", dataset]);
        var mountPoint = (await vdevs.RunAsync("zfs", ["get", "-H", "-o", "value", "mountpoint", dataset])).Trim();

        var set = ZfsUserSpaces.Set(dataset, Uid, Quota);
        await vdevs.RunAsync(set.FileName, set.Arguments);
        var read = ZfsUserSpaces.Read([dataset], [Uid]);
        var before = ZfsUserSpaces.Parse(await vdevs.RunAsync(read.FileName, read.Arguments))[dataset][Uid];
        Assert.Equal(new ZfsUserSpace(0, Quota), before);

        var path = Path.Combine(mountPoint, "big.bin");
        await File.WriteAllBytesAsync(path, [], Token);
        await vdevs.RunAsync("chown", [$"{Uid}:100", path]);
        // ZFS checks quotas per transaction group, so the refusal comes a few writes late; flushing to disk forces it.
        // Random bytes, because the pool compresses zeros to nothing. Root is held to the owner's quota as well.
        var chunk = RandomNumberGenerator.GetBytes(1024 * 1024);
        PithyxException? refused = null;
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 0))
        {
            for (var written = 0; written < 64 && refused is null; written++)
            {
                try
                {
                    await stream.WriteAsync(chunk, Token);
                    stream.Flush(flushToDisk: true);
                }
                catch (IOException exception)
                {
                    refused = FsErrors.NoSpace(exception) ?? throw new InvalidOperationException("Not a space error.", exception);
                }
            }
        }

        Assert.Equal(ErrorCodes.FsQuotaExceeded, refused?.Code);
        var after = ZfsUserSpaces.Parse(await vdevs.RunAsync(read.FileName, read.Arguments))[dataset][Uid];
        Assert.True(after.UsedBytes >= Quota, $"used {after.UsedBytes}");

        var remove = ZfsUserSpaces.Set(dataset, Uid, null);
        await vdevs.RunAsync(remove.FileName, remove.Arguments);
        Assert.Null(ZfsUserSpaces.Parse(await vdevs.RunAsync(read.FileName, read.Arguments))[dataset][Uid].QuotaBytes);
    }
}
