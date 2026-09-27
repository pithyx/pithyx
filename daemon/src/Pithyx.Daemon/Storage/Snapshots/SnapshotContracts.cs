namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>Response of <c>GET /api/storage/snapshots</c>.</summary>
/// <param name="Dataset">The dataset.</param>
/// <param name="Snapshots">Its snapshots, oldest first.</param>
public sealed record SnapshotList(string Dataset, IReadOnlyList<Snapshot> Snapshots);

/// <param name="Dataset">The dataset to snapshot.</param>
public sealed record SnapshotCreateRequest(string Dataset);

/// <param name="Dataset">The dataset.</param>
/// <param name="Name">The exact snapshot name after <c>@</c>.</param>
public sealed record SnapshotRequest(string Dataset, string Name);

/// <param name="Dataset">The dataset to roll back.</param>
/// <param name="Name">The snapshot to return to.</param>
/// <param name="ConfirmDataset">The dataset name as the user typed it; must match exactly.</param>
/// <param name="DiscardNewer">Every newer snapshot by name, as the user was shown them; required when there are any.</param>
public sealed record SnapshotRollbackRequest(string Dataset, string Name, string ConfirmDataset, IReadOnlyList<string>? DiscardNewer = null);

/// <param name="Kind"><c>hourly</c>, <c>daily</c>, <c>weekly</c> or <c>monthly</c>.</param>
/// <param name="Enabled">Whether the schedule takes snapshots.</param>
/// <param name="Keep">How many of its snapshots it keeps (1 to 1000).</param>
public sealed record SnapshotSchedule(string Kind, bool Enabled, int Keep);

/// <summary>Schedules of a dataset: all four kinds, in the order hourly, daily, weekly, monthly.</summary>
/// <param name="Dataset">The dataset.</param>
/// <param name="Schedules">One entry per kind; kinds left out of a PUT stay as they are.</param>
public sealed record SnapshotScheduleList(string Dataset, IReadOnlyList<SnapshotSchedule> Schedules);
