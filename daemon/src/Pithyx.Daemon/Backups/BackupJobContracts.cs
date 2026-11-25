using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pithyx.Daemon.Backups;

/// <summary>How a job backs up. Versioned backups and mirrors go from this box (#394, #397); pulls come onto it (#396).</summary>
public enum BackupJobKind
{
    /// <summary>restic over SFTP into a repository of the job's own on the target, with versions and retention.</summary>
    Versioned,

    /// <summary>rsync over SSH from a Linux server into a dataset of the job's own, with a ZFS snapshot per run as its version.</summary>
    Pull,

    /// <summary>rsync over SSH from the run's ZFS snapshots into a folder of the job's own on the target: one current copy as plain files, without versions.</summary>
    Mirror,
}

/// <summary>Whose owners and rights the files of a server backup carry on the box.</summary>
public enum BackupPullOwners
{
    /// <summary>The server's: owners and groups as numbers, modes, ACLs and extended attributes, as needed to put them back there.</summary>
    Server,

    /// <summary>The shared folder's: the files belong to the box and get the rights of the shared folder they are in.</summary>
    Box,
}

/// <summary>What a server backup pulls and where it keeps it.</summary>
/// <param name="ShareId">The shared folder that holds the destination; set when the backup is made.</param>
/// <param name="Folder">The destination's name in the shared folder, made by the box as a dataset of its own; set when the backup is made.</param>
/// <param name="Paths">1 to 32 folders or files on the server, absolute below the folder its key may read (<c>/</c> for all of it).</param>
/// <param name="Delete">Files gone on the server go from the destination too; the versions keep them.</param>
/// <param name="Owners">Whose owners and rights the files carry on the box.</param>
public sealed record BackupPullSettings(long ShareId, string Folder, IReadOnlyList<string> Paths, bool Delete, BackupPullOwners Owners);

/// <summary>A new server backup.</summary>
/// <param name="ServerId">The server it pulls from.</param>
/// <param name="Settings">Its name, schedule and retention; no sources, no check schedule, no upload limit. The excludes are rsync patterns.</param>
/// <param name="Pull">What it pulls and where to.</param>
public sealed record BackupPullCreateRequest(long ServerId, BackupJobSettings Settings, BackupPullSettings Pull);

/// <summary>The changes to a server backup; its server and destination stay.</summary>
public sealed record BackupPullUpdateRequest(BackupJobSettings Settings, BackupPullSettings Pull);

/// <summary>How a mirror treats what is gone on the box.</summary>
/// <param name="Delete">Files and folders deleted on the box are deleted from the copy at the next run; otherwise the copy keeps them.</param>
public sealed record BackupMirrorSettings(bool Delete);

/// <summary>A new mirror.</summary>
/// <param name="TargetId">Where it copies to; the target needs rsync over SSH, not only SFTP.</param>
/// <param name="Folder">Its folder below the target's path, made by the box if missing.</param>
/// <param name="Settings">What it copies and when; no retention (all 0), no check schedule, no download limit. The excludes are rsync patterns.</param>
/// <param name="Mirror">What happens to what is deleted on the box.</param>
public sealed record BackupMirrorCreateRequest(long TargetId, string Folder, BackupJobSettings Settings, BackupMirrorSettings Mirror);

/// <summary>The changes to a mirror; its target and folder stay.</summary>
public sealed record BackupMirrorUpdateRequest(BackupJobSettings Settings, BackupMirrorSettings Mirror);

/// <summary>A folder a job backs up.</summary>
/// <param name="ShareId">The shared folder or home, as in Files.</param>
/// <param name="Path">The folder below it, with <c>/</c> between its parts; empty for the whole shared folder.</param>
public sealed record BackupSource(long ShareId, string Path);

/// <summary>How many versions a job keeps after each successful run (restic's <c>forget</c>). 0 keeps none of that kind; all 0 keeps everything.</summary>
public sealed record BackupRetention(int Last, int Hourly, int Daily, int Weekly, int Monthly, int Yearly)
{
    /// <summary>A new job's: a week of daily, a month of weekly and a year of monthly versions.</summary>
    public static BackupRetention Default { get; } = new(0, 0, 7, 4, 12, 0);

    [JsonIgnore]
    public bool KeepsAll => Last == 0 && Hourly == 0 && Daily == 0 && Weekly == 0 && Monthly == 0 && Yearly == 0;
}

/// <summary>What an administrator sets on a job.</summary>
/// <param name="Name">1 to 64 characters, unique on the box.</param>
/// <param name="Sources">1 to 32 folders of shared folders or homes on a pool.</param>
/// <param name="Schedule">When it runs: five cron fields in the box's time zone, as app schedules.</param>
/// <param name="Enabled">Whether the schedule runs it; a disabled job still runs by hand.</param>
/// <param name="Retention">How many versions it keeps.</param>
/// <param name="Excludes">restic exclude patterns, at most 100; left out, none.</param>
/// <param name="CheckSchedule">When the repository's integrity is checked (cron); null for never.</param>
/// <param name="CheckReadPercent">The share of the data a check reads back, 0 to 100; 0 checks only the structure.</param>
/// <param name="UploadLimitKib">Upload limit in KiB/s; 0 or left out for none.</param>
/// <param name="DownloadLimitKib">Download limit in KiB/s; 0 or left out for none.</param>
/// <param name="OverdueHours">Hours without a successful run before administrators are told; null for three times the interval, at least two days.</param>
public sealed record BackupJobSettings(
    string Name,
    IReadOnlyList<BackupSource> Sources,
    string Schedule,
    bool Enabled,
    BackupRetention Retention,
    IReadOnlyList<string>? Excludes = null,
    string? CheckSchedule = null,
    int CheckReadPercent = 0,
    int UploadLimitKib = 0,
    int DownloadLimitKib = 0,
    int? OverdueHours = null);

/// <summary>A new job.</summary>
/// <param name="TargetId">Where it backs up to.</param>
/// <param name="Repository">The repository's folder below the target's path; made by the box unless <paramref name="ExistingKey"/> is given.</param>
/// <param name="Settings">What it backs up and when.</param>
/// <param name="ExistingKey">The key of a repository that exists already, for example of a reinstalled box; null makes a new repository with a new key.</param>
public sealed record BackupJobCreateRequest(long TargetId, string Repository, BackupJobSettings Settings, string? ExistingKey = null);

/// <summary>A new job and the key of its new repository, which the box shows only now.</summary>
/// <param name="Job">The job.</param>
/// <param name="Key">The new repository's key; null when the job connected to an existing repository.</param>
public sealed record BackupJobCreated(BackupJob Job, string? Key);

/// <summary>How a run or check ended, or that it runs.</summary>
public enum BackupRunState
{
    Running,
    Succeeded,

    /// <summary>It finished, but some files could not be read; they are missing from this version.</summary>
    Warning,

    Failed,
    Cancelled,

    /// <summary>The daemon stopped while it ran.</summary>
    Interrupted,
}

/// <summary>What a run did.</summary>
public enum BackupRunKind
{
    Backup,
    Check,
}

/// <summary>Why a run started.</summary>
public enum BackupRunTrigger
{
    Schedule,
    Manual,
}

/// <summary>A file or folder a backup run could not read, so it is missing from that version.</summary>
/// <param name="Path">The shared folder's or home's name and the path below it, with <c>/</c> between the parts.</param>
/// <param name="Reason"><c>permission</c> (the box may not read it), <c>missing</c> (it went away while the run read it) or <c>other</c>.</param>
public sealed record BackupUnreadable(string Path, string Reason);

/// <summary>One run or check of a job.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="JobId">The job it belongs to.</param>
/// <param name="Kind">A backup or an integrity check.</param>
/// <param name="Trigger">The schedule or an administrator.</param>
/// <param name="BoardJobId">Its job on the job board, for progress and cancel while it runs.</param>
/// <param name="State">How it ended, or that it runs.</param>
/// <param name="StartedAt">UTC.</param>
/// <param name="FinishedAt">UTC; null while it runs.</param>
/// <param name="Files">Files in the version.</param>
/// <param name="Bytes">Bytes in the version.</param>
/// <param name="AddedBytes">Bytes the run added to the repository.</param>
/// <param name="RepositoryBytes">What the repository took on the target after the run (restic stats, raw data); null when unknown.</param>
/// <param name="Snapshot">The id of the version restic made.</param>
/// <param name="Warnings">Files or folders that could not be read.</param>
/// <param name="ErrorCode">Why it failed.</param>
/// <param name="ErrorParams">The error's params.</param>
/// <param name="Unreadable">The first 100 files and folders it could not read; empty when it read everything.</param>
public sealed record BackupRun(
    long Id,
    long JobId,
    BackupRunKind Kind,
    BackupRunTrigger Trigger,
    long? BoardJobId,
    BackupRunState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? Files,
    long? Bytes,
    long? AddedBytes,
    long? RepositoryBytes,
    string? Snapshot,
    int Warnings,
    string? ErrorCode,
    JsonElement? ErrorParams,
    IReadOnlyList<BackupUnreadable> Unreadable);

/// <summary>Runs and checks, newest first.</summary>
public sealed record BackupRunList(IReadOnlyList<BackupRun> Runs);

/// <summary>A backup job.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Kind">How it backs up.</param>
/// <param name="TargetId">Where it backs up to; for a server backup, the server.</param>
/// <param name="Repository">The repository's folder below the target's path, or a mirror's folder there; empty for a server backup.</param>
/// <param name="Settings">What it backs up and when.</param>
/// <param name="CreatedAt">UTC.</param>
/// <param name="NextRunAt">When the schedule runs it next; null when it is disabled.</param>
/// <param name="NextCheckAt">When it is checked next; null without a check schedule or when it is disabled.</param>
/// <param name="LastRun">Its newest backup run, finished or running.</param>
/// <param name="LastCheck">Its newest check.</param>
/// <param name="LastSuccessAt">When its last backup run succeeded (or ended with warnings).</param>
/// <param name="Overdue">It is enabled and had no successful run for longer than its limit.</param>
/// <param name="ProtectedBytes">The size of its newest version: the bytes its last successful run backed up.</param>
/// <param name="RepositoryBytes">
/// What its repository takes on the target, as the newest run that measured it found; for a server backup, what its
/// dataset takes on the pool with every version; for a mirror, the size of the files it copied.
/// </param>
/// <param name="Pull">What a server backup pulls and where to; null for the other kinds.</param>
/// <param name="Dataset">The dataset that holds a server backup; null for the other kinds.</param>
/// <param name="Mirror">How a mirror treats what is deleted on the box; null for the other kinds.</param>
public sealed record BackupJob(
    long Id,
    BackupJobKind Kind,
    long TargetId,
    string Repository,
    BackupJobSettings Settings,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? NextCheckAt,
    BackupRun? LastRun,
    BackupRun? LastCheck,
    DateTimeOffset? LastSuccessAt,
    bool Overdue,
    long? ProtectedBytes,
    long? RepositoryBytes,
    BackupPullSettings? Pull,
    string? Dataset,
    BackupMirrorSettings? Mirror);

/// <summary>Every backup job, by name.</summary>
public sealed record BackupJobList(IReadOnlyList<BackupJob> Jobs);

/// <summary>A shared folder or home a job can back up.</summary>
/// <param name="ShareId">Its id.</param>
/// <param name="Name">The shared folder's name, or <c>homes/&lt;user&gt;</c> for a home.</param>
/// <param name="Home">Whether it is a home.</param>
/// <param name="Supported">It is a dataset of a pool, so the box can back it up from a snapshot.</param>
/// <param name="Available">Its pool is there and unlocked.</param>
public sealed record BackupSourceOption(long ShareId, string Name, bool Home, bool Supported, bool Available);

/// <summary>Every shared folder and home, by name.</summary>
public sealed record BackupSourceList(IReadOnlyList<BackupSourceOption> Sources);

/// <summary>Which runs the history returns.</summary>
/// <param name="JobId">Only this job's; null for every job.</param>
/// <param name="Kind">Only backups or only checks; null for both.</param>
/// <param name="State">Only runs that ended so, or run now; null for all.</param>
/// <param name="Since">Only runs that started at or after this time.</param>
/// <param name="Until">Only runs that started before this time.</param>
/// <param name="Before">Only runs with a smaller id, for the next page.</param>
/// <param name="Limit">At most this many, 1 to 2000.</param>
public sealed record BackupRunQuery(long? JobId, BackupRunKind? Kind, BackupRunState? State, DateTimeOffset? Since, DateTimeOffset? Until, long? Before, int Limit);
