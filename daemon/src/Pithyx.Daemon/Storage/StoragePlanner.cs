using System.Collections.Concurrent;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage;

/// <summary>Turns a request into a <see cref="CommandPlan"/> after probing the chosen disks and asking the guard.</summary>
public sealed class StoragePlanner(
    SystemStateService state,
    StorageFacts facts,
    PoolInventory pools,
    PoolImportScanner scanner,
    DatasetPlanner datasetPlanner,
    ICommandRunner commands,
    TimeProvider time)
{
    /// <param name="Verdicts">The guard's verdict for every disk the plan touches.</param>
    /// <param name="Plan">The plan, or null while a disk is not allowed.</param>
    /// <param name="Datasets">Datasets the preview lists, see <see cref="StoragePlanPreview.Datasets"/>.</param>
    public sealed record Result(IReadOnlyList<GuardVerdict> Verdicts, CommandPlan? Plan, IReadOnlyList<string> Datasets)
    {
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public bool RequiresConfirmName { get; init; }

        /// <summary>True when the name must be typed and was not: the plan is shown but may not run.</summary>
        public bool AwaitsConfirmName { get; init; }

        public IReadOnlyList<UnmountedDataset> Unmounted { get; init; } = [];
    }

    /// <summary>
    /// GUIDs of pools that were imported read-only with <c>-f</c>. Their labels still name the other system, so the
    /// read-write import needs <c>-f</c> again. In memory: after a restart ZFS asks for it, and the user confirms again.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _forcedImports = new(StringComparer.Ordinal);

    public async Task<Result> PlanAsync(StoragePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diskIds = (request.DiskIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        var confirmations = request.Confirmations ?? new Dictionary<string, string>();
        switch (request.Action)
        {
            case "pool.create":
                ZfsPlans.ValidateName(request.Pool, pools.Current.Pools.Select(pool => pool.Name));
                ZfsPlans.ValidateLayout(request.Layout ?? "", diskIds.Count);
                RequireEncryptionAnswer(request.Encryption);
                return await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.CreatePool(request.Pool, request.Layout!, disks), cancellationToken);
            case "pool.replace":
                {
                    var leaf = Leaf(Pool(request.Pool), request.Vdev);
                    RequireOneDisk(diskIds);
                    // A disk is never used as a replacement on its own: the user always types its serial, even when it is empty.
                    return await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.Replace(request.Pool, leaf.VdevGuid, disks[0]), cancellationToken, requireSerial: true);
                }
            case "pool.spare.add":
                Pool(request.Pool);
                RequireOneDisk(diskIds);
                return await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.AddSpare(request.Pool, disks[0]), cancellationToken);
            case "pool.spare.remove":
                {
                    var spare = Leaf(Pool(request.Pool), request.Vdev);
                    if (spare.Role != "spare")
                    {
                        throw new PithyxException(ErrorCodes.StorageVdevNotFound, new Dictionary<string, object?> { ["vdev"] = request.Vdev });
                    }
                    return Plan(request, ZfsPlans.RemoveSpare(request.Pool, spare.VdevGuid));
                }
            case "pool.offline":
                return Plan(request, ZfsPlans.Offline(request.Pool, Leaf(Pool(request.Pool), request.Vdev).VdevGuid));
            case "pool.online":
                return Plan(request, ZfsPlans.Online(request.Pool, Leaf(Pool(request.Pool), request.Vdev).VdevGuid));
            case "pool.destroy":
                {
                    var pool = Reachable(Pool(request.Pool));
                    if (!string.Equals(request.ConfirmName, pool.Name, StringComparison.Ordinal))
                    {
                        throw new PithyxException(ErrorCodes.StoragePoolNameMismatch, new Dictionary<string, object?> { ["name"] = pool.Name });
                    }
                    var datasets = pools.Current.Datasets.Where(dataset => dataset.Pool == pool.Name).Select(dataset => dataset.Name).ToList();
                    return Releasing(Plan(request, ZfsPlans.DestroyPool(pool.Name)) with { Datasets = datasets, RequiresConfirmName = true });
                }
            case "pool.export":
                {
                    var pool = Reachable(Pool(request.Pool));
                    var datasets = pools.Current.Datasets.Where(dataset => dataset.Pool == pool.Name).ToList();
                    return Releasing(Plan(request, ImportPlans.Export(pool.Name)) with
                    {
                        Datasets = [.. datasets.Select(dataset => dataset.Name)],
                        Warnings = datasets.Any(dataset => dataset.Mounted || dataset.Type == "volume") ? ["datasets_unavailable"] : [],
                    });
                }
            case "pool.import.readonly":
                return await PlanReadOnlyImportAsync(request, cancellationToken);
            case "pool.import":
                return await PlanImportAsync(request, cancellationToken);
            case "pool.vdev.add":
                {
                    var pool = WritablePool(request.Pool);
                    var layout = request.Layout ?? "";
                    if (!ZfsPlans.AddLayouts.Contains(layout))
                    {
                        throw new PithyxException(ErrorCodes.StoragePoolLayoutInvalid, new Dictionary<string, object?> { ["layout"] = layout });
                    }
                    ZfsPlans.ValidateLayout(layout, diskIds.Count);
                    var assessment = VdevRedundancy.AssessAdd(pool, layout, diskIds.Count);
                    var typed = assessment.RequiresConfirmName ? TypedNameOrNull(pool, request.ConfirmName) : null;
                    var result = await WithDisksAsync(
                        request,
                        diskIds,
                        confirmations,
                        disks => ZfsPlans.AddVdev(pool.Name, layout, disks, assessment.ReplicationMismatch ? typed : null),
                        cancellationToken);
                    return AwaitingName(result, assessment.RequiresConfirmName && typed is null) with
                    {
                        Warnings = assessment.Warnings,
                        RequiresConfirmName = assessment.RequiresConfirmName,
                    };
                }
            case "pool.raidz.expand":
                {
                    var pool = WritablePool(request.Pool);
                    var raidz = pool.Vdevs.FirstOrDefault(vdev => vdev.Role == "data" && vdev.VdevGuid == request.Vdev)
                        ?? throw new PithyxException(ErrorCodes.StorageVdevNotFound, new Dictionary<string, object?> { ["vdev"] = request.Vdev });
                    if (raidz.Type != "raidz")
                    {
                        throw new PithyxException(ErrorCodes.StorageVdevNotRaidz, new Dictionary<string, object?> { ["vdev"] = raidz.Name });
                    }
                    if (pool.Expansion is { State: "scanning" })
                    {
                        throw new PithyxException(ErrorCodes.StoragePoolExpanding, new Dictionary<string, object?> { ["name"] = pool.Name });
                    }
                    RequireOneDisk(diskIds);
                    var typed = TypedNameOrNull(pool, request.ConfirmName);
                    var result = await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.ExpandRaidz(pool.Name, raidz.VdevGuid, disks[0]), cancellationToken);
                    return AwaitingName(result, typed is null) with { Warnings = ["irreversible"], RequiresConfirmName = true };
                }
            case var action when DatasetPlanner.Actions.Contains(action):
                return await datasetPlanner.PlanAsync(request, cancellationToken);
            default:
                throw new PithyxException(ErrorCodes.StoragePlanActionInvalid, new Dictionary<string, object?> { ["action"] = request.Action });
        }
    }

    private async Task<Result> PlanReadOnlyImportAsync(StoragePlanRequest request, CancellationToken cancellationToken)
    {
        // The pool is looked up in a new scan, never taken from the request: the scan decides force and disks.
        var scan = await scanner.ScanAsync(cancellationToken);
        var candidate = scan.Pools.FirstOrDefault(pool => pool.PoolGuid == request.PoolGuid)
            ?? throw new PithyxException(ErrorCodes.StorageImportNotFound, new Dictionary<string, object?> { ["pool"] = request.PoolGuid });
        if (!candidate.CanImport)
        {
            throw new PithyxException(ErrorCodes.StorageImportNotImportable, new Dictionary<string, object?> { ["name"] = candidate.Name, ["state"] = candidate.State });
        }
        if (candidate.UnknownDevices.Count > 0 || candidate.Disks.Count == 0)
        {
            // Every present device must be on a disk the guard checks; a pool on no known disk is never imported.
            throw new PithyxException(
                ErrorCodes.StorageImportDeviceUnknown,
                new Dictionary<string, object?> { ["device"] = candidate.UnknownDevices.Count > 0 ? candidate.UnknownDevices[0] : candidate.Name });
        }
        var existing = pools.Current.Pools.Select(pool => pool.Name).ToList();
        var newName = string.IsNullOrEmpty(request.NewName) ? null : request.NewName;
        if (newName is not null)
        {
            ZfsPlans.ValidateName(newName, existing);
        }
        else if (existing.Contains(candidate.Name, StringComparer.Ordinal))
        {
            throw new PithyxException(ErrorCodes.StoragePoolNameTaken, new Dictionary<string, object?> { ["name"] = candidate.Name });
        }
        TypedName? force = null;
        if (candidate.NeedsForce)
        {
            force = request.Force == true ? TypedName.Confirm(candidate.Name, request.ConfirmName) : null;
            if (force is null)
            {
                throw new PithyxException(ErrorCodes.StorageImportForceRequired, new Dictionary<string, object?> { ["name"] = candidate.Name });
            }
        }
        var diskIds = candidate.Disks.Select(verdict => verdict.DiskId).ToList();
        if (candidate.Disks.Any(verdict => verdict.Decision != GuardDecision.Allowed))
        {
            return new Result(candidate.Disks, null, []);
        }
        // Probed now, so the plan compares the disks as they are, not as they were when they appeared.
        await state.RefreshDisksAsync(diskIds, cancellationToken);
        if (force is not null)
        {
            _forcedImports[candidate.PoolGuid] = true;
        }
        var warnings = new List<string>();
        if (force is not null)
        {
            warnings.Add("other_system");
        }
        if (candidate.State != "ONLINE")
        {
            warnings.Add("degraded");
        }
        return new Result(candidate.Disks, ImportPlan(request, ImportPlans.ImportReadOnly(candidate.PoolGuid, newName, force, diskIds), diskIds), [])
        {
            Warnings = warnings,
            RequiresConfirmName = force is not null,
        };
    }

    private async Task<Result> PlanImportAsync(StoragePlanRequest request, CancellationToken cancellationToken)
    {
        var pool = Pool(request.Pool);
        if (!pool.ReadOnly)
        {
            throw new PithyxException(ErrorCodes.StorageImportPreviewRequired, new Dictionary<string, object?> { ["name"] = pool.Name });
        }
        await state.RefreshDisksAsync([], cancellationToken);
        var current = facts.Current();
        var (diskIds, unknown) = PoolImportScanner.DisksOf(current, pool);
        if (unknown.Count > 0 || diskIds.Count == 0)
        {
            throw new PithyxException(ErrorCodes.StorageImportDeviceUnknown, new Dictionary<string, object?> { ["device"] = unknown.Count > 0 ? unknown[0] : pool.Name });
        }
        var verdicts = DiskGuard.EvaluateImport(current, pool.Name).Where(verdict => diskIds.Contains(verdict.DiskId)).ToList();
        if (verdicts.Any(verdict => verdict.Decision != GuardDecision.Allowed))
        {
            return new Result(verdicts, null, []);
        }
        TypedName? force = null;
        if (request.Force == true)
        {
            force = TypedName.Confirm(pool.Name, request.ConfirmName)
                ?? throw new PithyxException(ErrorCodes.StorageImportForceRequired, new Dictionary<string, object?> { ["name"] = pool.Name });
        }
        else if (_forcedImports.ContainsKey(pool.PoolGuid))
        {
            throw new PithyxException(ErrorCodes.StorageImportForceRequired, new Dictionary<string, object?> { ["name"] = pool.Name });
        }
        var properties = await commands.RunAsync(ImportPlans.MountProperties(pool.Name), cancellationToken);
        if (properties.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = "zfs", ["exitCode"] = properties.ExitCode });
        }
        var mounts = ImportPlans.DecideMounts(ZfsDatasetParser.ParseProperties(properties.StandardOutput), current.Mounts);
        // Datasets that get canmount=noauto change the pool, so the user types its name for them as for a forced import.
        var requiresName = force is not null || mounts.Unmounted.Any(dataset => dataset.Reason != "key_unavailable");
        var typed = requiresName ? TypedNameOrNull(pool, request.ConfirmName) : null;
        var warnings = new List<string>();
        if (force is not null)
        {
            warnings.Add("other_system");
        }
        if (mounts.Unmounted.Count > 0)
        {
            warnings.Add("unmounted_datasets");
        }
        var result = new Result(verdicts, ImportPlan(request, ImportPlans.ImportReadWrite(pool.Name, pool.PoolGuid, force, diskIds, mounts), diskIds), mounts.Mount);
        return AwaitingName(result, requiresName && typed is null) with { Warnings = warnings, RequiresConfirmName = requiresName, Unmounted = mounts.Unmounted };
    }

    /// <summary>An import plan: the disks as they are now, with no serial confirmations (the import rule asks for none).</summary>
    private CommandPlan ImportPlan(StoragePlanRequest request, IReadOnlyList<PlanStep> steps, IReadOnlyList<string> diskIds)
    {
        var current = facts.Current();
        return new CommandPlan(
            request.Action,
            steps,
            [.. current.Disks.Where(disk => diskIds.Contains(disk.Disk.Id)).Select(disk => new PlannedDisk(disk.Disk.Id, disk.Disk.Serial, disk.Disk.SizeBytes, DiskGuard.Fingerprint(disk, GuardRule.Import)))],
            new Dictionary<string, string>(),
            time.GetUtcNow() + CommandPlan.Lifetime);
    }

    /// <summary>
    /// Without the typed name the plan is dropped: the preview shows the warnings and the disks, and the commands only
    /// once the name is typed, because some commands (an override flag) depend on it.
    /// </summary>
    private static Result AwaitingName(Result result, bool awaiting) =>
        awaiting && result.Plan is not null ? result with { Plan = null, AwaitsConfirmName = true } : result;

    /// <summary>The typed name, null when none was typed yet, and <c>storage.pool.name_mismatch</c> when a wrong one was.</summary>
    private static TypedName? TypedNameOrNull(Pool pool, string? typed) =>
        string.IsNullOrEmpty(typed)
            ? null
            : TypedName.Confirm(pool.Name, typed) ?? throw new PithyxException(ErrorCodes.StoragePoolNameMismatch, new Dictionary<string, object?> { ["name"] = pool.Name });

    private Pool WritablePool(string name)
    {
        var pool = Pool(name);
        return pool.ReadOnly ? throw new PithyxException(ErrorCodes.StoragePoolReadOnly, new Dictionary<string, object?> { ["name"] = pool.Name }) : pool;
    }

    // Exporting or destroying a pool unmounts every dataset it lists.
    private Result Releasing(Result result) =>
        result.Plan is null ? result : result with { Plan = result.Plan with { Releases = PlanReleases.MountPointsOf(pools.Current, result.Datasets) } };

    private static void RequireEncryptionAnswer(string? encryption)
    {
        switch (encryption)
        {
            case "none":
                return;
            case "key":
                // Step 9 creates <pool>/pithyx as the encryption root with the key handling; until then only the answer exists.
                throw new PithyxException(ErrorCodes.StorageEncryptionUnavailable);
            default:
                throw new PithyxException(ErrorCodes.StoragePoolEncryptionRequired);
        }
    }

    private static void RequireOneDisk(List<string> diskIds)
    {
        if (diskIds.Count != 1)
        {
            throw new PithyxException(ErrorCodes.StoragePoolDiskCount, new Dictionary<string, object?> { ["layout"] = "single", ["min"] = 1, ["count"] = diskIds.Count });
        }
    }

    private Pool Pool(string name) =>
        pools.Current.Pools.FirstOrDefault(pool => pool.Name == name)
        ?? throw new PithyxException(ErrorCodes.StoragePoolNotFound, new Dictionary<string, object?> { ["name"] = name });

    /// <summary>Refuses a pool that a command on all its disks would hang on (suspended, or a vdev lost).</summary>
    private static Pool Reachable(Pool pool) =>
        PoolHealth.Unreachable(pool) is { } code
            ? throw new PithyxException(code, new Dictionary<string, object?> { ["name"] = pool.Name })
            : pool;

    private static Vdev Leaf(Pool pool, string? guid) =>
        pool.Vdevs.SelectMany(vdev => vdev.Leaves()).FirstOrDefault(leaf => leaf.VdevGuid == guid)
        ?? throw new PithyxException(ErrorCodes.StorageVdevNotFound, new Dictionary<string, object?> { ["vdev"] = guid });

    private Result Plan(StoragePlanRequest request, IReadOnlyList<PlanStep> steps) =>
        new([], new CommandPlan(request.Action, steps, [], new Dictionary<string, string>(), time.GetUtcNow() + CommandPlan.Lifetime), []);

    private async Task<Result> WithDisksAsync(
        StoragePlanRequest request,
        List<string> diskIds,
        IReadOnlyDictionary<string, string> confirmations,
        Func<IReadOnlyList<GuardDisk>, IReadOnlyList<PlanStep>> build,
        CancellationToken cancellationToken,
        bool requireSerial = false)
    {
        // Right before a plan the chosen disks are probed again: what the page showed may be minutes old.
        await state.RefreshDisksAsync(diskIds, cancellationToken);
        var current = facts.Current();
        var chosen = diskIds.Select(id => current.Disks.FirstOrDefault(disk => disk.Disk.Id == id)
            ?? throw new PithyxException(ErrorCodes.StorageDiskNotFound, new Dictionary<string, object?> { ["disk"] = id })).ToList();
        var verdicts = DiskGuard.Evaluate(current, confirmations)
            .Where(verdict => diskIds.Contains(verdict.DiskId))
            .Select(verdict => requireSerial && verdict.Decision == GuardDecision.Allowed && !Confirmed(chosen, confirmations, verdict.DiskId)
                ? verdict with { Decision = GuardDecision.NeedsSerialConfirmation, Reason = "confirm_replacement" }
                : verdict)
            .ToList();
        if (verdicts.Any(verdict => verdict.Decision != GuardDecision.Allowed))
        {
            return new Result(verdicts, null, []);
        }
        var plan = new CommandPlan(
            request.Action,
            build(chosen),
            [.. chosen.Select(disk => new PlannedDisk(disk.Disk.Id, disk.Disk.Serial, disk.Disk.SizeBytes, DiskGuard.Fingerprint(disk)))],
            confirmations.Where(entry => diskIds.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal),
            time.GetUtcNow() + CommandPlan.Lifetime);
        return new Result(verdicts, plan, []);
    }

    private static bool Confirmed(List<GuardDisk> disks, IReadOnlyDictionary<string, string> confirmations, string diskId) =>
        disks.First(disk => disk.Disk.Id == diskId).Disk.Serial is { } serial
        && confirmations.TryGetValue(diskId, out var typed)
        && string.Equals(typed.Trim(), serial, StringComparison.Ordinal);
}
