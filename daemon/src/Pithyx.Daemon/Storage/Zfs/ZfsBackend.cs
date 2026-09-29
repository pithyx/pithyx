using System.Runtime.CompilerServices;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>Reads pools and datasets from OpenZFS 2.3 and follows <c>zpool events</c>. Never changes anything.</summary>
public sealed class ZfsBackend(ICommandRunner commands) : IStorageBackend
{
    public static readonly CommandRequest Status = new("zpool", ["status", "-j", "--json-int"]);
    public static readonly CommandRequest List = new("zpool", ["list", "-j", "--json-int", "-o", "name,size,allocated,free,capacity,fragmentation,health,readonly"]);
    public static readonly CommandRequest Datasets = new(
        "zfs",
        ["list", "-j", "--json-int", "-t", "filesystem,volume", "-o", "name,type,used,available,referenced,mountpoint,mounted,encryption,keystatus,encryptionroot,quota,reservation,compression,recordsize,volsize,volblocksize,usedbysnapshots"]);

    /// <summary>Prints every past event, then follows new ones; one line per event.</summary>
    public static readonly CommandRequest Events = new("zpool", ["events", "-f", "-H"]);

    public async Task<PoolList> ReadAsync(CancellationToken cancellationToken)
    {
        var status = await RunAsync(Status, cancellationToken);
        var usage = await RunAsync(List, cancellationToken);
        var datasets = await RunAsync(Datasets, cancellationToken);
        var sizes = ZpoolListParser.Parse(usage).ToDictionary(pool => pool.Name, StringComparer.Ordinal);
        var pools = ZpoolStatusParser.Parse(status).Select(pool =>
        {
            var size = sizes.GetValueOrDefault(pool.Name);
            return new Pool(
                pool.Name,
                pool.PoolGuid,
                pool.State,
                size?.SizeBytes ?? 0,
                size?.AllocatedBytes ?? 0,
                size?.FreeBytes ?? 0,
                size?.CapacityPercent ?? 0,
                size?.FragmentationPercent,
                size?.ReadOnly ?? false,
                pool.DataErrors,
                pool.Scan,
                pool.Vdevs,
                pool.Expansion);
        });
        return new PoolList([.. pools], ZfsDatasetParser.ParseList(datasets));
    }

    public async IAsyncEnumerable<string> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in commands.StreamLinesAsync(Events, cancellationToken))
        {
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private async Task<string> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(request, cancellationToken);
        // With no pools, zpool list exits 0 with no output; a failure means ZFS is missing or broken.
        return result.ExitCode == 0
            ? result.StandardOutput
            : throw new InvalidOperationException($"{request.FileName} {request.Arguments[0]} failed with {result.ExitCode}: {result.StandardError}");
    }
}
