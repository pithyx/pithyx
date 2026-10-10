namespace Pithyx.Daemon.Updates;

/// <summary>One update or rollback of the system.
/// <c>Kind</c>: <c>update</c> or <c>rollback</c>.
/// <c>Source</c>: <c>online</c> (from the channel), <c>file</c> (uploaded), <c>recovery</c> (the console menu) or <c>manual</c> (a rollback from Settings).
/// <c>Snapshot</c>: The snapshot taken before an update, or the one a rollback restored.
/// <c>State</c>: <c>running</c>, <c>restarting</c>, <c>succeeded</c>, <c>failed</c> (nothing changed) or <c>rolled_back</c>.
/// </summary>
public sealed record UpdateHistoryEntry(
    long Id,
    string Kind,
    string Source,
    string FromVersion,
    string ToVersion,
    string? Snapshot,
    string State,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?>? ErrorParams,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>The newest version of the box's channel when it is newer than the installed one.
/// <c>Installable</c>: False when the box is older than <paramref name="MinimumVersion"/> and needs a version in between first.
/// </summary>
public sealed record AvailableUpdate(string Version, string MinimumVersion, string Changelog, long Size, DateTimeOffset PublishedAt, bool Installable);

/// <summary>The Debian updates apt offers; the kernel and ZFS never come from Debian (D338).</summary>
public sealed record DebianUpdates(int Count, int Security, IReadOnlyList<DebianUpdate> Packages);

/// <summary>Why the last check could not read the channel, as an error code with its params.</summary>
public sealed record UpdateProblem(string Code, IReadOnlyDictionary<string, object?> Params);

/// <summary>An uploaded update file that passed every check and waits for "Install".
/// <c>TrustedSignature</c>: False only for a development file on a development box.
/// </summary>
public sealed record StagedUpdate(
    string Version,
    string Channel,
    string MinimumVersion,
    string Changelog,
    long Size,
    bool TrustedSignature,
    int Packages,
    string Kernel,
    DateTimeOffset PublishedAt);

/// <summary>Settings &gt; Updates for administrators (topic <c>system/updates</c>).
/// <c>Supported</c>: Whether updates and rollbacks apply here; false on a machine not laid out by the installer.
/// <c>UnsupportedReason</c>: <c>not_btrfs</c>, <c>not_subvolume</c>, <c>not_array</c> or <c>findmnt</c>.
/// <c>RunningJobId</c>: The job of an update or rollback that runs now.
/// <c>Snapshots</c>: The kept snapshots, newest first; each can be restored.
/// <c>Source</c>: The update source the box follows (D339); only the recovery menu switches it.
/// <c>SourceCandidate</c>: A source file saved in Settings for the recovery menu, or null.
/// </summary>
public sealed record UpdateStatus(
    string InstalledVersion,
    string Channel,
    bool CheckEnabled,
    DateTimeOffset? LastCheckAt,
    DateTimeOffset? NextCheckAt,
    bool Checking,
    AvailableUpdate? Available,
    DebianUpdates? Debian,
    UpdateProblem? Problem,
    bool Supported,
    string? UnsupportedReason,
    StagedUpdate? Staged,
    long? RunningJobId,
    IReadOnlyList<UpdateHistoryEntry> History,
    IReadOnlyList<SnapshotInfo> Snapshots,
    UpdateSourceInfo Source,
    UpdateSourceCandidate? SourceCandidate);

/// <param name="CheckEnabled">The daily check (on by default; it only notifies).</param>
/// <param name="Channel"><c>stable</c> or <c>beta</c>.</param>
public sealed record UpdateSettingsRequest(bool CheckEnabled, string Channel);

/// <param name="Source"><c>online</c> installs the channel's newest file, <c>file</c> the uploaded one.</param>
/// <param name="IncludeDebian">Also install the Debian updates, in the same snapshot.</param>
public sealed record InstallUpdateRequest(string Source, bool IncludeDebian);

/// <param name="Snapshot">The snapshot to restore.</param>
/// <param name="Version">Its version, typed by the administrator as the confirmation.</param>
public sealed record RollbackRequest(string Snapshot, string Version);
