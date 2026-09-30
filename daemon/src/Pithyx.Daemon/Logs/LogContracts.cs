namespace Pithyx.Daemon.Logs;

public enum LogLevelName
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>One log entry of the daemon, a system service or an app.</summary>
/// <param name="Id">Stable within the current logs or the archive it comes from.</param>
/// <param name="Time">When it happened, UTC.</param>
/// <param name="Level">debug, info, warn or error.</param>
/// <param name="Source"><c>daemon</c>, <c>system/&lt;service&gt;</c> (for example <c>system/kernel</c>, <c>system/smbd</c>) or <c>app/&lt;id&gt;</c>.</param>
/// <param name="Message">The rendered message; secrets are masked before it is stored.</param>
/// <param name="RequestId">The request id (W3C trace id) of the request that caused it, if any.</param>
/// <param name="User">The user an app entry was written for, if any.</param>
/// <param name="Data">Further fields (event name, error code, path, the app's own data), as text.</param>
/// <param name="Archived">True when the entry comes from the archive on a pool.</param>
public sealed record LogEntry(
    long Id,
    DateTimeOffset Time,
    LogLevelName Level,
    string Source,
    string Message,
    string? RequestId,
    string? User,
    IReadOnlyDictionary<string, string>? Data,
    bool Archived);

/// <summary>A page of entries, newest first.</summary>
/// <param name="Entries">At most the requested limit.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next, older page; null when there is none.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, string? NextCursor);

/// <summary>Retention of the current logs on the system disk and of the optional archive on a pool.</summary>
/// <param name="RetentionDays">Current logs older than this are dropped (1 to 365).</param>
/// <param name="MaxBytes">The current logs of the system and all apps together stay below this (64 MB to 4 GB); the oldest go first.</param>
/// <param name="ArchiveEnabled">Whether entries are also kept in an archive on a pool.</param>
/// <param name="ArchivePool">The pool of the archive; null for the first pool.</param>
/// <param name="ArchiveRetentionDays">Archived entries older than this are dropped (1 to 3650).</param>
/// <param name="ArchiveMaxBytes">The archive stays below this (256 MB to 1 TB).</param>
public sealed record LogSettings(
    int RetentionDays,
    long MaxBytes,
    bool ArchiveEnabled,
    string? ArchivePool,
    int ArchiveRetentionDays,
    long ArchiveMaxBytes)
{
    public const long MinMaxBytes = 64L * 1024 * 1024;
    public const long MaxMaxBytes = 4L * 1024 * 1024 * 1024;
    public const long MinArchiveBytes = 256L * 1024 * 1024;
    public const long MaxArchiveBytes = 1024L * 1024 * 1024 * 1024;

    public static LogSettings Default { get; } = new(30, MaxMaxBytes, false, null, 365, 10L * 1024 * 1024 * 1024);
}

public enum LogArchiveState
{
    /// <summary>No archive is configured.</summary>
    Off,

    /// <summary>The archive is written.</summary>
    Ready,

    /// <summary>The archive's pool is missing, locked, read-only or without the Pithyx layout; entries wait on the system disk.</summary>
    Unavailable,
}

/// <summary>The settings with what they hold now.</summary>
/// <param name="Settings">The retention as set.</param>
/// <param name="CurrentBytes">Space the current logs take on the system disk.</param>
/// <param name="CurrentOldest">The oldest current entry, or null when there is none.</param>
/// <param name="ArchiveState">Whether the archive is off, written, or waiting for its pool.</param>
/// <param name="ArchivePool">The pool the archive is on or would be on, or null when there is no pool.</param>
/// <param name="ArchiveBytes">Space the archive takes, or null without one.</param>
/// <param name="ArchiveOldest">The oldest archived entry, or null.</param>
/// <param name="ArchivedAt">When entries were last copied to the archive, or null.</param>
/// <param name="Pools">Pools that can hold the archive.</param>
public sealed record LogStatus(
    LogSettings Settings,
    long CurrentBytes,
    DateTimeOffset? CurrentOldest,
    LogArchiveState ArchiveState,
    string? ArchivePool,
    long? ArchiveBytes,
    DateTimeOffset? ArchiveOldest,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<string> Pools);

/// <param name="Level">debug, info, warn or error.</param>
/// <param name="Message">At most 4000 characters; longer ones are cut.</param>
/// <param name="Data">At most 20 fields of at most 1000 characters each.</param>
public sealed record AppLogEntry(LogLevelName Level, string Message, IReadOnlyDictionary<string, string>? Data = null);

/// <param name="Entries">At most 100 entries per request.</param>
public sealed record AppLogBatch(IReadOnlyList<AppLogEntry> Entries);

/// <param name="Accepted">Entries stored.</param>
/// <param name="Dropped">Entries over the app's rate limit, not stored.</param>
public sealed record AppLogResult(int Accepted, int Dropped);

/// <summary>An entry on its way into the store.</summary>
public sealed record PendingLogEntry(
    DateTimeOffset Time,
    LogLevelName Level,
    string Source,
    string Message,
    string? RequestId = null,
    string? User = null,
    IReadOnlyDictionary<string, string>? Data = null,
    string? JournalCursor = null);

/// <summary>What a query asks for; every filter is optional.</summary>
public sealed record LogQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlySet<LogLevelName>? Levels,
    IReadOnlySet<string>? Sources,
    string? Search,
    LogCursor? Before,
    int Limit);

/// <summary>Where a page ended: the last entry's time and id.</summary>
public sealed record LogCursor(long TimeMicros, long Id)
{
    public override string ToString() => FormattableString.Invariant($"{TimeMicros}_{Id}");

    public static LogCursor? Parse(string? text)
    {
        var parts = text?.Split('_');
        return parts is [var time, var id]
            && long.TryParse(time, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var micros)
            && long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new LogCursor(micros, number)
            : null;
    }
}
