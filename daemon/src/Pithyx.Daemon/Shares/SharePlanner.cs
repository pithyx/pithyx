using System.Text;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Shares;

/// <summary>
/// Plans creating a shared folder as its own dataset (D101) and deleting one, as command plans that run through the
/// storage plan runner. The plan's bookkeeping registers or removes the share once the commands succeeded.
/// </summary>
public sealed partial class SharePlanner(
    SystemStateService state,
    PoolInventory pools,
    ShareService shares,
    ShareChanges changes,
    DatasetPlanner datasetPlanner,
    SnapshotService snapshotService,
    TimeProvider time,
    ILogger<SharePlanner> logger)
{
    public const int MaxDatasetNameLength = 64;

    /// <summary>Dataset names below <c>shares</c> that would mount where Pithyx keeps something else (D100).</summary>
    public static readonly IReadOnlySet<string> ReservedDatasetNames = new HashSet<string>(StringComparer.Ordinal) { "homes" };

    /// <summary>
    /// The dataset name for a shared folder's name: lower case, Latin letters with accents as their base letters (the
    /// daemon runs with invariant globalization, so this is a table, not Unicode normalization), every run of other
    /// characters one <c>-</c>, starting with a letter or digit, at most 64 characters, no <c>-</c> or <c>.</c> at the
    /// end. Null when nothing is left, for example for a name without any Latin letter or digit.
    /// </summary>
    public static string? DatasetNameFor(string shareName)
    {
        ArgumentNullException.ThrowIfNull(shareName);
        var result = new StringBuilder();
        foreach (var original in shareName.Trim().ToLowerInvariant())
        {
            var replacement = _latin.TryGetValue(original, out var letters) ? letters : original.ToString();
            foreach (var character in replacement)
            {
                var kept = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-';
                if (kept && !(character == '-' && result.Length > 0 && result[^1] == '-'))
                {
                    result.Append(character);
                }
                else if (!kept && (result.Length == 0 || result[^1] != '-'))
                {
                    result.Append('-');
                }
            }
        }
        var name = result.ToString().TrimStart('-', '.', '_');
        if (name.Length > MaxDatasetNameLength)
        {
            name = name[..MaxDatasetNameLength];
        }
        name = name.TrimEnd('-', '.');
        return name.Length > 0 && ZfsPlans.IsDatasetName(name) ? name : null;
    }

    // Lower-case letters of Latin-1 Supplement and Latin Extended-A with their base letters.
    private static readonly Dictionary<char, string> _latin = new[]
    {
        ("àáâãäåāăą", "a"), ("çćĉċč", "c"), ("ďđð", "d"), ("èéêëēĕėęě", "e"), ("ĝğġģ", "g"), ("ĥħ", "h"),
        ("ìíîïĩīĭįı", "i"), ("ĵ", "j"), ("ķ", "k"), ("ĺļľŀł", "l"), ("ñńņňŉ", "n"), ("òóôõöøōŏő", "o"),
        ("ŕŗř", "r"), ("śŝşš", "s"), ("ţťŧ", "t"), ("ùúûüũūŭůűų", "u"), ("ŵ", "w"), ("ýÿŷ", "y"), ("źżž", "z"),
        ("ß", "ss"), ("æ", "ae"), ("œ", "oe"), ("þ", "th"),
    }.SelectMany(entry => entry.Item1.Select(letter => KeyValuePair.Create(letter, entry.Item2))).ToDictionary();

    public async Task<StoragePlanner.Result> PlanAsync(SharePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // New datasets and used space raise no zpool event, so the checks below read what ZFS has now.
        await state.RefreshPoolsAsync(cancellationToken);
        return request.Action switch
        {
            "share.create" => await CreateAsync(request, cancellationToken),
            "share.delete" => await DeleteAsync(request, cancellationToken),
            _ => throw new PithyxException(ErrorCodes.StoragePlanActionInvalid, new Dictionary<string, object?> { ["action"] = request.Action }),
        };
    }

    private async Task<StoragePlanner.Result> CreateAsync(SharePlanRequest request, CancellationToken cancellationToken)
    {
        var name = ShareService.CheckName(request.Name);
        var description = ShareService.CheckDescription(request.Description);
        var datasets = pools.Current.Datasets;
        var pool = pools.Current.Pools.FirstOrDefault(candidate => candidate.Name == request.Pool)
            ?? throw new PithyxException(ErrorCodes.StoragePoolNotFound, new Dictionary<string, object?> { ["name"] = request.Pool });
        var parent = datasets.FirstOrDefault(candidate => candidate.Name == $"{pool.Name}/pithyx/shares")
            ?? throw new PithyxException(ErrorCodes.FsSharePoolNotPrepared, new Dictionary<string, object?> { ["pool"] = pool.Name });
        var datasetName = DatasetNameFor(name)
            ?? throw new PithyxException(ErrorCodes.FsShareDatasetNameInvalid, new Dictionary<string, object?> { ["name"] = name });
        if (ReservedDatasetNames.Contains(datasetName))
        {
            throw new PithyxException(ErrorCodes.FsShareNameReserved, new Dictionary<string, object?> { ["name"] = datasetName });
        }
        var full = ZfsDatasetPlans.Child(parent.Name, datasetName);
        if (datasets.Any(candidate => candidate.Name == full))
        {
            throw new PithyxException(ErrorCodes.StorageDatasetExists, new Dictionary<string, object?> { ["name"] = full });
        }
        if (await shares.NameTakenAsync(name, cancellationToken))
        {
            throw new PithyxException(ErrorCodes.FsShareExists);
        }
        // The shares dataset does not mount itself but hands its mount point down (D100).
        if (parent.MountPoint is { } root)
        {
            var mountPoint = $"{root.TrimEnd('/')}/{datasetName}";
            var taken = datasets.Any(candidate => candidate.MountPoint == mountPoint)
                || (await shares.ListPathsAsync(cancellationToken)).Any(share => ShareService.Overlaps(share.Path, mountPoint));
            if (taken)
            {
                throw new PithyxException(ErrorCodes.FsSharePathTaken, new Dictionary<string, object?> { ["path"] = mountPoint });
            }
        }
        return Result(request, ZfsPlans.CreateShareDataset(pool.Name, datasetName), [full], async token =>
        {
            // The runner read the datasets again, so this is where ZFS mounted the new one.
            var created = pools.Current.Datasets.FirstOrDefault(candidate => candidate.Name == full)
                ?? throw new PithyxException(ErrorCodes.StorageDatasetNotFound, new Dictionary<string, object?> { ["dataset"] = full });
            var path = created.MountPoint
                ?? throw new PithyxException(ErrorCodes.StorageDatasetNotFound, new Dictionary<string, object?> { ["dataset"] = full });
            var id = await shares.RegisterDatasetAsync(name, description, full, path, token);
            changes.Announce();
            LogCreated(name, id, full);
        });
    }

    private async Task<StoragePlanner.Result> DeleteAsync(SharePlanRequest request, CancellationToken cancellationToken)
    {
        var share = (request.ShareId is { } id ? await shares.FindAsync(id, cancellationToken) : null)
            ?? throw new PithyxException(ErrorCodes.FsShareNotFound);
        if (!string.Equals(request.ConfirmName?.Trim(), share.Name, StringComparison.Ordinal))
        {
            throw new PithyxException(ErrorCodes.FsShareNameMismatch, new Dictionary<string, object?> { ["name"] = share.Name });
        }
        switch (request.Data)
        {
            case ShareDataChoice.Keep:
                // Nothing runs; the plan only unregisters, so the choice is shown and confirmed like a destroy.
                return Result(request, [], [], async token =>
                {
                    await shares.UnregisterAsync(share.Id, token);
                    changes.Announce();
                    LogUnregistered(share.Name, share.Id, share.Dataset ?? share.Path);
                });
            case ShareDataChoice.Destroy:
                {
                    var name = share.Dataset ?? throw new PithyxException(ErrorCodes.FsShareNoDataset);
                    var dataset = pools.Current.Datasets.FirstOrDefault(candidate => candidate.Name == name)
                        ?? throw new PithyxException(ErrorCodes.StorageDatasetNotFound, new Dictionary<string, object?> { ["dataset"] = name });
                    // Only a dataset below <pool>/pithyx/shares is destroyed as a shared folder.
                    if (DatasetPlanner.RoleOf(dataset.Pool, dataset.Name) != DatasetRole.Share)
                    {
                        throw new PithyxException(ErrorCodes.StorageDatasetProtected, new Dictionary<string, object?> { ["dataset"] = dataset.Name });
                    }
                    var scope = await datasetPlanner.DestroyScopeAsync(dataset, share.Id, cancellationToken);
                    return Result(request, ZfsDatasetPlans.Destroy(dataset.Name, scope.Recursive), scope.Listed, async token =>
                    {
                        await snapshotService.ForgetSchedulesAsync(scope.Datasets, token);
                        await shares.UnregisterAsync(share.Id, token);
                        changes.Announce();
                        LogDestroyed(share.Name, share.Id, dataset.Name, scope.SnapshotCount);
                    });
                }
            default:
                throw new PithyxException(ErrorCodes.FsShareDataChoiceRequired);
        }
    }

    private StoragePlanner.Result Result(SharePlanRequest request, IReadOnlyList<PlanStep> steps, IReadOnlyList<string> datasets, Func<CancellationToken, Task> afterRun) =>
        new([], new CommandPlan(request.Action, steps, [], new Dictionary<string, string>(), time.GetUtcNow() + CommandPlan.Lifetime, afterRun), datasets);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created shared folder {Share} ({ShareId}) as dataset {Dataset}")]
    private partial void LogCreated(string share, long shareId, string dataset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed shared folder {Share} ({ShareId}); {Location} and its files stay")]
    private partial void LogUnregistered(string share, long shareId, string location);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed shared folder {Share} ({ShareId}) and destroyed dataset {Dataset} with {Snapshots} snapshots")]
    private partial void LogDestroyed(string share, long shareId, string dataset, int snapshots);
}
