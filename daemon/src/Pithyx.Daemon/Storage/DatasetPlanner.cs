using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage;

/// <summary>What a dataset is to Pithyx, by its name relative to the D100 layout.</summary>
public enum DatasetRole
{
    /// <summary>The pool's root dataset, which holds no data (D100).</summary>
    Root,

    /// <summary><c>&lt;pool&gt;/pithyx</c> and every dataset directly below it (<c>shares</c>, <c>homes</c>, <c>apps</c>, <c>system</c>, <c>volumes</c>).</summary>
    Structure,

    /// <summary>Below <c>homes</c>, <c>apps</c>, <c>system</c> or an unknown structure dataset: users and apps find them by name.</summary>
    Managed,

    /// <summary>Below <c>&lt;pool&gt;/pithyx/shares</c>.</summary>
    Share,

    /// <summary>Below <c>&lt;pool&gt;/pithyx/volumes</c>.</summary>
    Volume,

    /// <summary>Outside <c>&lt;pool&gt;/pithyx</c>, for example on a pool Pithyx did not create.</summary>
    Other,
}

/// <summary>
/// Plans renaming, changing and destroying datasets and volumes and creating volumes. It decides which dataset may be
/// changed how; <see cref="ZfsDatasetPlans"/> builds the commands. Nothing here assumes a dataset is a share, so apps
/// (M4) use the same actions for their datasets.
/// </summary>
public sealed partial class DatasetPlanner(
    SystemStateService state,
    PoolInventory pools,
    SnapshotInventory snapshots,
    SnapshotService snapshotService,
    ShareService shares,
    TimeProvider time,
    ILogger<DatasetPlanner> logger)
{
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
    {
        "dataset.rename",
        "dataset.set",
        "dataset.destroy",
        "volume.create",
    };

    private static readonly HashSet<string> _managedParents = ["homes", "apps", "system"];

    public static DatasetRole RoleOf(string pool, string dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (dataset == pool)
        {
            return DatasetRole.Root;
        }
        var layout = $"{pool}/pithyx";
        if (dataset == layout)
        {
            return DatasetRole.Structure;
        }
        if (!dataset.StartsWith(layout + "/", StringComparison.Ordinal))
        {
            return DatasetRole.Other;
        }
        var parts = dataset[(layout.Length + 1)..].Split('/');
        if (parts.Length == 1)
        {
            return DatasetRole.Structure;
        }
        return parts[0] switch
        {
            "shares" => DatasetRole.Share,
            "volumes" => DatasetRole.Volume,
            // homes, apps, system, and structure datasets a later version adds.
            _ => DatasetRole.Managed,
        };
    }

    public async Task<StoragePlanner.Result> PlanAsync(StoragePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Used space and new datasets raise no zpool event, so the checks below read what ZFS has now.
        await state.RefreshPoolsAsync(cancellationToken);
        return request.Action switch
        {
            "dataset.rename" => Rename(request),
            "dataset.set" => Set(request),
            "dataset.destroy" => await DestroyAsync(request, cancellationToken),
            "volume.create" => CreateVolume(request),
            _ => throw new PithyxException(ErrorCodes.StoragePlanActionInvalid, new Dictionary<string, object?> { ["action"] = request.Action }),
        };
    }

    private StoragePlanner.Result Rename(StoragePlanRequest request)
    {
        var dataset = Find(request);
        RequireChangeable(dataset);
        var newName = ZfsDatasetPlans.Child(ZfsDatasetPlans.Parent(dataset.Name), request.NewName ?? "");
        if (pools.Current.Datasets.Any(candidate => candidate.Name == newName))
        {
            throw new PithyxException(ErrorCodes.StorageDatasetExists, new Dictionary<string, object?> { ["name"] = newName });
        }
        var oldName = dataset.Name;
        var oldMountPoint = dataset.MountPoint;
        var renamed = WithChildren(oldName).Select(candidate => candidate.Name).ToList();
        return Result(request, ZfsDatasetPlans.Rename(oldName, request.NewName!), renamed, async cancellationToken =>
        {
            // The runner read the datasets again, so this is where ZFS mounted it now.
            var newMountPoint = pools.Current.Datasets.FirstOrDefault(candidate => candidate.Name == newName)?.MountPoint;
            var moved = 0;
            if (oldMountPoint is not null && newMountPoint is not null)
            {
                moved = await shares.MovePathsAsync(oldMountPoint, newMountPoint, cancellationToken);
            }
            else if (oldMountPoint is not null)
            {
                LogMountPointUnknown(newName);
            }
            await shares.MoveDatasetsAsync(oldName, newName, cancellationToken);
            await snapshotService.MoveSchedulesAsync(oldName, newName, cancellationToken);
            LogRenamed(oldName, newName, moved);
        });
    }

    private StoragePlanner.Result Set(StoragePlanRequest request)
    {
        var dataset = Find(request);
        var role = RoleOf(dataset.Pool, dataset.Name);
        // Volumes take only compression; the pool root and the layout datasets too, since a limit there would hit
        // everything below.
        var compressionOnly = dataset.Type == "volume" || role is DatasetRole.Root or DatasetRole.Structure;
        var changes = new List<KeyValuePair<string, string>>();
        var quota = dataset.QuotaBytes ?? 0;
        if (request.QuotaBytes is { } newQuota)
        {
            RequireLimit(dataset, "quota", newQuota, compressionOnly);
            if (newQuota > 0 && newQuota < dataset.UsedBytes)
            {
                throw new PithyxException(ErrorCodes.StorageDatasetQuotaBelowUsed, new Dictionary<string, object?> { ["usedBytes"] = dataset.UsedBytes });
            }
            Change(changes, "quota", Limit(newQuota), quota != newQuota);
            quota = newQuota;
        }
        if (request.ReservationBytes is { } reservation)
        {
            RequireLimit(dataset, "reservation", reservation, compressionOnly);
            // Available counts the dataset's own unused reservation already; a quota caps it further.
            var most = dataset.UsedBytes + dataset.AvailableBytes;
            if (quota > 0)
            {
                most = Math.Min(most, quota);
            }
            if (reservation > most)
            {
                throw new PithyxException(ErrorCodes.StorageDatasetReservationTooLarge, new Dictionary<string, object?> { ["maxBytes"] = most });
            }
            Change(changes, "reservation", Limit(reservation), (dataset.ReservationBytes ?? 0) != reservation);
        }
        if (request.Compression is { } compression)
        {
            if (!ZfsDatasetPlans.Compressions.Contains(compression, StringComparer.Ordinal))
            {
                throw new PithyxException(ErrorCodes.StorageDatasetCompressionInvalid, new Dictionary<string, object?> { ["compression"] = compression });
            }
            Change(changes, "compression", compression, dataset.Compression != compression);
        }
        if (request.RecordSizeBytes is { } recordSize)
        {
            if (compressionOnly)
            {
                throw Unsupported(dataset, "recordsize");
            }
            if (recordSize is < ZfsDatasetPlans.MinimumRecordSize or > ZfsDatasetPlans.MaximumRecordSize || (recordSize & (recordSize - 1)) != 0)
            {
                throw new PithyxException(
                    ErrorCodes.StorageDatasetRecordSizeInvalid,
                    new Dictionary<string, object?> { ["minBytes"] = ZfsDatasetPlans.MinimumRecordSize, ["maxBytes"] = ZfsDatasetPlans.MaximumRecordSize });
            }
            Change(changes, "recordsize", Number(recordSize), dataset.RecordSizeBytes != recordSize);
        }
        if (changes.Count == 0)
        {
            throw new PithyxException(ErrorCodes.StorageDatasetUnchanged, new Dictionary<string, object?> { ["dataset"] = dataset.Name });
        }
        return Result(request, ZfsDatasetPlans.Set(dataset.Name, changes), [], null);
    }

    private async Task<StoragePlanner.Result> DestroyAsync(StoragePlanRequest request, CancellationToken cancellationToken)
    {
        var dataset = Find(request);
        RequireChangeable(dataset);
        if (!string.Equals(request.ConfirmName, dataset.Name, StringComparison.Ordinal))
        {
            throw new PithyxException(ErrorCodes.StorageDatasetNameMismatch, new Dictionary<string, object?> { ["name"] = dataset.Name });
        }
        var scope = await DestroyScopeAsync(dataset, null, cancellationToken);
        return Result(request, ZfsDatasetPlans.Destroy(dataset.Name, scope.Recursive), scope.Listed, async token =>
        {
            await snapshotService.ForgetSchedulesAsync(scope.Datasets, token);
            LogDestroyed(dataset.Name, scope.Datasets.Count, scope.SnapshotCount);
        });
    }

    /// <summary>What destroying a dataset takes with it.</summary>
    /// <param name="Listed">The dataset, its children and their snapshots, each dataset followed by its snapshots.</param>
    /// <param name="Datasets">The dataset and its children, parents first.</param>
    /// <param name="SnapshotCount">How many snapshots go.</param>
    /// <param name="Recursive">Whether <c>zfs destroy</c> needs <c>-r</c>.</param>
    public sealed record DestroyScope(IReadOnlyList<string> Listed, IReadOnlyList<string> Datasets, int SnapshotCount, bool Recursive);

    /// <summary>
    /// Lists what destroying <paramref name="dataset"/> takes with it, after reading the snapshots again. Refuses while
    /// a share other than <paramref name="ownShare"/> has its folder in it, or a snapshot in it is held.
    /// </summary>
    public async Task<DestroyScope> DestroyScopeAsync(Dataset dataset, long? ownShare, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var gone = WithChildren(dataset.Name).ToList();
        foreach (var share in await shares.ListPathsAsync(cancellationToken))
        {
            if (share.Id != ownShare && gone.Any(candidate => candidate.MountPoint is { } mountPoint && FsPath.IsSameOrInside(share.Path, mountPoint)))
            {
                throw new PithyxException(ErrorCodes.StorageDatasetInUseByShare, new Dictionary<string, object?> { ["dataset"] = dataset.Name, ["share"] = share.Name });
            }
        }
        // The snapshot list may be minutes old; a hold placed since would make ZFS refuse halfway.
        await snapshotService.RefreshAsync(cancellationToken);
        var listed = new List<string>();
        var snapshotCount = 0;
        foreach (var candidate in gone)
        {
            listed.Add(candidate.Name);
            foreach (var snapshot in snapshots.Of(candidate.Name))
            {
                if (snapshot.Holds > 0)
                {
                    throw new PithyxException(ErrorCodes.StorageSnapshotHeld, new Dictionary<string, object?> { ["name"] = snapshot.FullName });
                }
                listed.Add(snapshot.FullName);
                snapshotCount++;
            }
        }
        return new DestroyScope(listed, [.. gone.Select(candidate => candidate.Name)], snapshotCount, gone.Count > 1 || snapshotCount > 0);
    }

    private StoragePlanner.Result CreateVolume(StoragePlanRequest request)
    {
        var pool = pools.Current.Pools.FirstOrDefault(candidate => candidate.Name == request.Pool)
            ?? throw new PithyxException(ErrorCodes.StoragePoolNotFound, new Dictionary<string, object?> { ["name"] = request.Pool });
        var datasets = pools.Current.Datasets;
        var layout = datasets.FirstOrDefault(candidate => candidate.Name == $"{pool.Name}/pithyx")
            ?? throw new PithyxException(ErrorCodes.StoragePoolNotPrepared, new Dictionary<string, object?> { ["pool"] = pool.Name });
        var parent = datasets.FirstOrDefault(candidate => candidate.Name == ZfsDatasetPlans.VolumesDataset(pool.Name));
        var name = ZfsDatasetPlans.Child(ZfsDatasetPlans.VolumesDataset(pool.Name), request.Name ?? "");
        if (datasets.Any(candidate => candidate.Name == name))
        {
            throw new PithyxException(ErrorCodes.StorageDatasetExists, new Dictionary<string, object?> { ["name"] = name });
        }
        var size = ZfsDatasetPlans.RoundVolumeSize(request.SizeBytes ?? 0);
        var sparse = request.Sparse ?? false;
        var available = (parent ?? layout).AvailableBytes;
        if (!sparse && size > available)
        {
            throw new PithyxException(ErrorCodes.StorageVolumeTooLarge, new Dictionary<string, object?> { ["availableBytes"] = available });
        }
        return Result(request, ZfsDatasetPlans.CreateVolume(pool.Name, request.Name!, size, sparse, createParent: parent is null), [], null);
    }

    private Dataset Find(StoragePlanRequest request) =>
        pools.Current.Datasets.FirstOrDefault(candidate => candidate.Name == request.Dataset && candidate.Pool == request.Pool)
        ?? throw new PithyxException(ErrorCodes.StorageDatasetNotFound, new Dictionary<string, object?> { ["dataset"] = request.Dataset });

    /// <summary>The dataset and every dataset below it, parents first.</summary>
    private IEnumerable<Dataset> WithChildren(string name) =>
        pools.Current.Datasets
            .Where(candidate => candidate.Name == name || candidate.Name.StartsWith(name + "/", StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal);

    /// <summary>Renaming and destroying: never the pool root or the layout, never what Pithyx finds by name.</summary>
    private static void RequireChangeable(Dataset dataset)
    {
        switch (RoleOf(dataset.Pool, dataset.Name))
        {
            case DatasetRole.Root or DatasetRole.Structure:
                throw new PithyxException(ErrorCodes.StorageDatasetProtected, new Dictionary<string, object?> { ["dataset"] = dataset.Name });
            case DatasetRole.Managed:
                throw new PithyxException(ErrorCodes.StorageDatasetManaged, new Dictionary<string, object?> { ["dataset"] = dataset.Name });
        }
    }

    private static void RequireLimit(Dataset dataset, string property, long bytes, bool compressionOnly)
    {
        if (compressionOnly)
        {
            throw Unsupported(dataset, property);
        }
        if (bytes < 0)
        {
            throw new PithyxException(ErrorCodes.StorageDatasetLimitInvalid, new Dictionary<string, object?> { ["property"] = property });
        }
    }

    private static PithyxException Unsupported(Dataset dataset, string property) =>
        new(ErrorCodes.StorageDatasetPropertyUnsupported, new Dictionary<string, object?> { ["dataset"] = dataset.Name, ["property"] = property });

    private static void Change(List<KeyValuePair<string, string>> changes, string property, string value, bool changed)
    {
        if (changed)
        {
            changes.Add(new(property, value));
        }
    }

    private static string Limit(long bytes) => bytes == 0 ? "none" : Number(bytes);

    private static string Number(long bytes) => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private StoragePlanner.Result Result(StoragePlanRequest request, IReadOnlyList<PlanStep> steps, IReadOnlyList<string> datasets, Func<CancellationToken, Task>? afterRun) =>
        new(
            [],
            new CommandPlan(
                request.Action,
                steps,
                [],
                new Dictionary<string, string>(),
                time.GetUtcNow() + CommandPlan.Lifetime,
                afterRun,
                // Renaming and destroying unmount the datasets.
                request.Action is "dataset.rename" or "dataset.destroy" ? PlanReleases.MountPointsOf(pools.Current, datasets) : null),
            datasets);

    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed dataset {OldName} to {NewName}; {Shares} share folders moved")]
    private partial void LogRenamed(string oldName, string newName, int shares);

    [LoggerMessage(Level = LogLevel.Error, Message = "Renamed dataset {Dataset} was not read again; its share folders were not moved")]
    private partial void LogMountPointUnknown(string dataset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destroyed dataset {Dataset} with {Datasets} datasets and {Snapshots} snapshots")]
    private partial void LogDestroyed(string dataset, int datasets, int snapshots);
}
