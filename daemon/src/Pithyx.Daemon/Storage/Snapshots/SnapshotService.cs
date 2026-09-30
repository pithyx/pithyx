using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>
/// Takes, holds and releases snapshots, checks and runs the plans that destroy one or roll back to one, and stores
/// the schedules. Changes run one at a time; after each the snapshot list is read again. The list is also read after
/// every pool refresh (a <c>zpool events</c> line), so snapshots taken outside Pithyx show up without polling.
/// </summary>
public sealed partial class SnapshotService : IDisposable
{
    public const int MaxKeep = 1000;

    private readonly ICommandRunner _commands;
    private readonly SnapshotInventory _inventory;
    private readonly PoolInventory _pools;
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly ILogger<SnapshotService> _logger;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Channel<bool> _refreshes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public SnapshotService(ICommandRunner commands, SnapshotInventory inventory, PoolInventory pools, SqliteConnectionFactory connections, TimeProvider time, ILogger<SnapshotService> logger)
    {
        ArgumentNullException.ThrowIfNull(pools);
        _commands = commands;
        _inventory = inventory;
        _pools = pools;
        _connections = connections;
        _time = time;
        _logger = logger;
        pools.Refreshed += () => _refreshes.Writer.TryWrite(true);
    }

    public void Dispose() => _changes.Dispose();

    /// <summary>Raised after an administrator saved schedules, so the scheduler wakes up.</summary>
    public event Action? SchedulesChanged;

    /// <summary>Pool refreshes that asked for the snapshot list to be read again; consumed by <see cref="SnapshotScheduler"/>.</summary>
    internal ChannelReader<bool> Refreshes => _refreshes.Reader;

    /// <summary>Reads every snapshot. Never throws: without ZFS the list stays as it was.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        var parent = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity("storage.snapshots.refresh");
        try
        {
            await ReadAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefreshFailed(exception);
            return false;
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    public SnapshotList List(string dataset)
    {
        RequireDataset(dataset);
        return new SnapshotList(dataset, _inventory.Of(dataset));
    }

    /// <summary>Takes <c>pithyx-&lt;kind&gt;-&lt;utc&gt;</c> of the dataset now and returns it.</summary>
    public async Task<Snapshot> TakeAsync(string dataset, string kind, CancellationToken cancellationToken)
    {
        RequireDataset(dataset);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            var name = SnapshotNames.For(kind, _time.GetUtcNow());
            if (_inventory.Find(dataset, name) is not null)
            {
                throw new PithyxException(ErrorCodes.StorageSnapshotExists, new Dictionary<string, object?> { ["name"] = name });
            }
            await RunAsync(SnapshotPlans.Take(dataset, name), cancellationToken);
            LogTaken(dataset, name);
            await ReadAsync(cancellationToken);
            return _inventory.Find(dataset, name) ?? new Snapshot(dataset, name, _time.GetUtcNow(), 0, 0, 0, 0);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>
    /// The snapshot as ZFS has it now, when it may be destroyed: only snapshots Pithyx created, and never held ones
    /// (D104). The plan <c>snapshot.destroy</c> asks this before it shows the command.
    /// </summary>
    public async Task<Snapshot> DestroyableAsync(string dataset, string name, CancellationToken cancellationToken)
    {
        var snapshot = await CurrentAsync(dataset, name, cancellationToken);
        if (snapshot.PithyxKind is null)
        {
            throw new PithyxException(ErrorCodes.StorageSnapshotNotPithyx, new Dictionary<string, object?> { ["name"] = name });
        }
        if (snapshot.Holds > 0)
        {
            throw new PithyxException(ErrorCodes.StorageSnapshotHeld, new Dictionary<string, object?> { ["name"] = name });
        }
        return snapshot;
    }

    public Task HoldAsync(string dataset, string name, CancellationToken cancellationToken) =>
        ChangeAsync(dataset, name, _ => SnapshotPlans.Hold(dataset, name), cancellationToken);

    public Task ReleaseAsync(string dataset, string name, CancellationToken cancellationToken) =>
        ChangeAsync(dataset, name, _ => SnapshotPlans.Release(dataset, name), cancellationToken);

    /// <summary>
    /// The snapshots a rollback to <paramref name="name"/> destroys as ZFS has them now: every newer one, oldest
    /// first. A held one refuses the rollback, since ZFS would stop halfway.
    /// </summary>
    public async Task<IReadOnlyList<Snapshot>> NewerThanAsync(string dataset, string name, CancellationToken cancellationToken)
    {
        var target = await CurrentAsync(dataset, name, cancellationToken);
        var newer = NewerThan(target);
        if (newer.FirstOrDefault(snapshot => snapshot.Holds > 0) is { } held)
        {
            throw new PithyxException(ErrorCodes.StorageSnapshotHeld, new Dictionary<string, object?> { ["name"] = held.Name });
        }
        return newer;
    }

    /// <summary>
    /// Runs a plan's commands for one snapshot while no other snapshot change runs, so no schedule takes or prunes a
    /// snapshot in between. The list is read first: the snapshot must still be there, and when
    /// <paramref name="newer"/> is given, its newer snapshots must be exactly those names, none of them held;
    /// otherwise nothing runs. The list is read again afterwards.
    /// </summary>
    public async Task RunCheckedAsync(string dataset, string name, IReadOnlyCollection<string>? newer, Func<CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            await ReadAsync(cancellationToken);
            var target = _inventory.Find(dataset, name)
                ?? throw new PithyxException(ErrorCodes.StorageSnapshotNotFound, new Dictionary<string, object?> { ["name"] = name });
            if (newer is not null)
            {
                var current = NewerThan(target);
                if (!current.Select(snapshot => snapshot.Name).ToHashSet(StringComparer.Ordinal).SetEquals(newer))
                {
                    throw new PithyxException(ErrorCodes.StorageSnapshotNewerExist, new Dictionary<string, object?> { ["count"] = current.Count });
                }
                if (current.FirstOrDefault(snapshot => snapshot.Holds > 0) is { } held)
                {
                    throw new PithyxException(ErrorCodes.StorageSnapshotHeld, new Dictionary<string, object?> { ["name"] = held.Name });
                }
            }
            try
            {
                await run(cancellationToken);
            }
            finally
            {
                await RefreshAsync(CancellationToken.None);
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    internal void LogUserDestroyed(string dataset, string name) => LogDestroyed(dataset, name, "user");

    internal void LogRollback(string dataset, string name, int discarded) => LogRolledBack(dataset, name, discarded);

    /// <summary>
    /// Destroys what retention picked for one schedule, one command per snapshot. Each name is checked against the
    /// Pithyx name pattern again when its command is built.
    /// </summary>
    internal async Task<int> PruneAsync(string dataset, string kind, int keep, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            var destroyed = 0;
            foreach (var snapshot in SnapshotRetention.Prune(_inventory.Of(dataset), kind, keep, _time.GetUtcNow()))
            {
                try
                {
                    await RunAsync(SnapshotPlans.Destroy(dataset, snapshot.Name), cancellationToken);
                    LogDestroyed(dataset, snapshot.Name, kind);
                    destroyed++;
                }
                catch (PithyxException exception)
                {
                    // A hold placed since the last read, or a snapshot already gone: ZFS refused, the next pass sees it.
                    LogPruneFailed(dataset, snapshot.Name, exception);
                }
            }
            if (destroyed > 0)
            {
                await ReadAsync(cancellationToken);
            }
            return destroyed;
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<SnapshotScheduleList> GetSchedulesAsync(string dataset, CancellationToken cancellationToken)
    {
        RequireDataset(dataset);
        await using var connection = await _connections.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<(string Kind, long Enabled, long Keep)>(
            "SELECT kind, enabled, keep FROM snapshot_schedules WHERE dataset = @dataset",
            new { dataset })).ToDictionary(row => row.Kind, StringComparer.Ordinal);
        return new SnapshotScheduleList(dataset, [.. SnapshotKinds.Scheduled.Select(kind => rows.TryGetValue(kind, out var row)
            ? new SnapshotSchedule(kind, row.Enabled == 1, (int)row.Keep)
            : new SnapshotSchedule(kind, false, SnapshotKinds.DefaultKeep(kind)))]);
    }

    public async Task<SnapshotScheduleList> SetSchedulesAsync(SnapshotScheduleList request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDataset(request.Dataset);
        var kinds = request.Schedules.Select(schedule => schedule.Kind).ToList();
        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count
            || request.Schedules.Any(schedule => !SnapshotKinds.Scheduled.Contains(schedule.Kind) || schedule.Keep is < 1 or > MaxKeep))
        {
            throw new PithyxException(ErrorCodes.StorageSnapshotScheduleInvalid, new Dictionary<string, object?> { ["max"] = MaxKeep });
        }
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var now = SessionService.Format(_time.GetUtcNow());
            foreach (var schedule in request.Schedules)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO snapshot_schedules (dataset, kind, enabled, keep, updated_at) VALUES (@dataset, @kind, @enabled, @keep, @now)
                    ON CONFLICT (dataset, kind) DO UPDATE SET enabled = excluded.enabled, keep = excluded.keep, updated_at = excluded.updated_at
                    """,
                    new { dataset = request.Dataset, kind = schedule.Kind, enabled = schedule.Enabled ? 1 : 0, keep = schedule.Keep, now },
                    transaction);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        SchedulesChanged?.Invoke();
        return await GetSchedulesAsync(request.Dataset, cancellationToken);
    }

    /// <summary>Every enabled schedule, also of datasets that are gone (the scheduler skips those).</summary>
    internal async Task<IReadOnlyList<(string Dataset, string Kind, int Keep)>> EnabledSchedulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Dataset, string Kind, long Keep)>(
            "SELECT dataset, kind, keep FROM snapshot_schedules WHERE enabled = 1 ORDER BY dataset, kind");
        return [.. rows.Select(row => (row.Dataset, row.Kind, (int)row.Keep))];
    }

    /// <summary>After a rename: the schedules of the dataset and its children follow their new names.</summary>
    public async Task MoveSchedulesAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Rows left by a dataset of the new name that was destroyed outside Pithyx would collide.
            await connection.ExecuteAsync(
                "DELETE FROM snapshot_schedules WHERE dataset = @newName OR substr(dataset, 1, length(@newName) + 1) = @newName || '/'",
                new { newName },
                transaction);
            await connection.ExecuteAsync(
                """
                UPDATE snapshot_schedules SET dataset = @newName || substr(dataset, length(@oldName) + 1)
                WHERE dataset = @oldName OR substr(dataset, 1, length(@oldName) + 1) = @oldName || '/'
                """,
                new { oldName, newName },
                transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        SchedulesChanged?.Invoke();
    }

    /// <summary>After a destroy: a new dataset of the same name starts without the old schedules.</summary>
    public async Task ForgetSchedulesAsync(IReadOnlyCollection<string> datasets, CancellationToken cancellationToken)
    {
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM snapshot_schedules WHERE dataset IN @datasets", new { datasets });
        }
        SchedulesChanged?.Invoke();
    }

    public bool HasDataset(string dataset) => _pools.Current.Datasets.Any(candidate => candidate.Name == dataset);

    private void RequireDataset(string dataset)
    {
        if (string.IsNullOrEmpty(dataset) || !HasDataset(dataset))
        {
            throw new PithyxException(ErrorCodes.StorageSnapshotDatasetNotFound, new Dictionary<string, object?> { ["dataset"] = dataset });
        }
    }

    /// <summary>Reads the list again, since it may be seconds old, and finds the snapshot.</summary>
    private async Task<Snapshot> CurrentAsync(string dataset, string name, CancellationToken cancellationToken)
    {
        RequireDataset(dataset);
        await ReadAsync(cancellationToken);
        return _inventory.Find(dataset, name)
            ?? throw new PithyxException(ErrorCodes.StorageSnapshotNotFound, new Dictionary<string, object?> { ["name"] = name });
    }

    private List<Snapshot> NewerThan(Snapshot target) =>
        [.. _inventory.Of(target.Dataset).Where(snapshot => snapshot.CreateTxg > target.CreateTxg)];

    /// <summary>Reads the list again, finds the snapshot and runs the command <paramref name="plan"/> builds for it.</summary>
    private async Task ChangeAsync(string dataset, string name, Func<Snapshot, CommandRequest> plan, CancellationToken cancellationToken)
    {
        RequireDataset(dataset);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            // The list may be seconds old; what is decided here must match what ZFS has now.
            await ReadAsync(cancellationToken);
            var snapshot = _inventory.Find(dataset, name)
                ?? throw new PithyxException(ErrorCodes.StorageSnapshotNotFound, new Dictionary<string, object?> { ["name"] = name });
            await RunAsync(plan(snapshot), cancellationToken);
            await ReadAsync(cancellationToken);
        }
        finally
        {
            _changes.Release();
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _commands.RunAsync(SnapshotPlans.List, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(
                ErrorCodes.StorageCommandFailed,
                new Dictionary<string, object?> { ["command"] = SnapshotPlans.List.FileName, ["exitCode"] = result.ExitCode });
        }
        _inventory.Replace(ZfsSnapshotParser.Parse(result.StandardOutput));
    }

    private async Task RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var result = await _commands.RunAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            LogCommandFailed(command.FileName, command.Arguments, result.ExitCode, result.StandardError);
            throw new PithyxException(
                ErrorCodes.StorageCommandFailed,
                new Dictionary<string, object?> { ["command"] = command.FileName, ["exitCode"] = result.ExitCode });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Took snapshot {Dataset}@{Name}")]
    private partial void LogTaken(string dataset, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Destroyed snapshot {Dataset}@{Name} ({Reason})")]
    private partial void LogDestroyed(string dataset, string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rolled {Dataset} back to {Name}, discarding {Discarded} newer snapshots")]
    private partial void LogRolledBack(string dataset, string name, int discarded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning {Dataset}@{Name} failed")]
    private partial void LogPruneFailed(string dataset, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Command} {Arguments} failed with {ExitCode}: {StandardError}")]
    private partial void LogCommandFailed(string command, IReadOnlyList<string> arguments, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the snapshots failed")]
    private partial void LogRefreshFailed(Exception exception);
}
