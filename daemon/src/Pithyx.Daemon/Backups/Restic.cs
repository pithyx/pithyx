using System.Globalization;
using System.Text.Json;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.Backups;

/// <summary>A snapshot mounted read-only over a live folder while restic runs, so the stored paths are the live ones.</summary>
/// <param name="Snapshot">The snapshot, <c>&lt;dataset&gt;@&lt;name&gt;</c>.</param>
/// <param name="Mountpoint">The dataset's mountpoint.</param>
public sealed record SnapshotMount(string Snapshot, string Mountpoint);

/// <summary>
/// restic's command lines (D415), pure: every call runs with a cleared environment, the repository over SFTP through
/// the target's hardened ssh (<see cref="BackupSsh.Options"/>), the key from a file, and JSON output.
/// </summary>
public static class ResticCommands
{
    /// <summary>restic's exit codes.</summary>
    public const int Incomplete = 3;
    public const int RepositoryMissing = 10;
    public const int Locked = 11;
    public const int WrongPassword = 12;
    public const int Interrupted = 130;

    /// <summary>The tag of versions the retention always keeps.</summary>
    public const string ProtectedTag = "pithyx-protected";

    /// <summary>The namespace script's exit code when a snapshot could not be mounted.</summary>
    public const int MountFailed = 70;

    /// <summary>Progress lines per second; restic prints none without it when stdout is no terminal.</summary>
    public const string ProgressFps = "0.5";

    /// <summary>
    /// Where the namespace script mounts the snapshots before it binds them over their live folders. A tmpfs is mounted
    /// on it inside the namespace, so the host only ever sees the empty folder.
    /// </summary>
    public const string StageDirectory = "/run/pithyx/backup-stage";

    /// <summary>
    /// Mounts each snapshot read-only on an empty folder of a private tmpfs and binds it over the dataset's mountpoint
    /// (ZFS refuses to mount over a folder that is not empty), then runs the rest. Runs in a private mount namespace, so
    /// nobody else sees the mounts and they go away with the process. Never goes through <c>.zfs/snapshot</c>, whose
    /// automounts would reach every bind of the share on the host and keep the snapshot busy.
    /// </summary>
    public const string MountScript =
        """
        stage=$1
        shift
        mkdir -p "$stage" && mount -t tmpfs -o mode=0700,size=64k pithyx-backup "$stage" || exit 70
        i=0
        while [ "$1" != "--" ]; do
          i=$((i + 1))
          mkdir "$stage/$i" && mount -t zfs -o ro "$1" "$stage/$i" && mount --bind "$stage/$i" "$2" || exit 70
          shift 2
        done
        shift
        exec "$@"
        """;

    /// <summary>The repository URL: the target's alias and the folder on the server, relative to the login folder or absolute.</summary>
    public static string Repository(string remotePath) => "sftp:" + BackupSsh.Alias + ":" + remotePath;

    /// <summary>
    /// restic's <c>sftp.command</c>: ssh with the target's options. restic splits it at spaces, so no option may hold a
    /// space or a quote; the options are checked values and paths of the key folder.
    /// </summary>
    public static string SftpCommand(IReadOnlyList<string> sshOptions)
    {
        ArgumentNullException.ThrowIfNull(sshOptions);
        if (sshOptions.Any(option => option.Length == 0 || option.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '\\')))
        {
            throw new ArgumentException("An ssh option holds a space or a quote.", nameof(sshOptions));
        }
        return "ssh " + string.Join(' ', sshOptions) + " " + BackupSsh.Alias + " -s sftp";
    }

    /// <summary>The flags every call has: repository, key, ssh, limits and the cache.</summary>
    public static IReadOnlyList<string> Global(string remotePath, string passwordFile, IReadOnlyList<string> sshOptions, int uploadLimitKib, int downloadLimitKib, string? cacheDir)
    {
        var arguments = new List<string>
        {
            "-r", Repository(remotePath),
            "--password-file", passwordFile,
            "-o", "sftp.command=" + SftpCommand(sshOptions),
            "--json",
        };
        if (uploadLimitKib > 0)
        {
            arguments.AddRange(["--limit-upload", Text(uploadLimitKib)]);
        }
        if (downloadLimitKib > 0)
        {
            arguments.AddRange(["--limit-download", Text(downloadLimitKib)]);
        }
        arguments.AddRange(cacheDir is null ? ["--no-cache"] : ["--cache-dir", cacheDir]);
        return arguments;
    }

    /// <summary><c>env</c>'s arguments that run restic with a cleared environment and <paramref name="arguments"/>.</summary>
    public static IReadOnlyList<string> Env(IReadOnlyList<string> arguments) =>
        [.. BackupSsh.CleanEnvironment(), "RESTIC_PROGRESS_FPS=" + ProgressFps, "restic", .. arguments];

    public static IReadOnlyList<string> Init(IReadOnlyList<string> global) => [.. global, "init"];

    /// <summary>Reads the repository's config: proves the repository is there and the key opens it.</summary>
    public static IReadOnlyList<string> Verify(IReadOnlyList<string> global) => [.. global, "cat", "config"];

    public static IReadOnlyList<string> Backup(IReadOnlyList<string> global, IReadOnlyList<string> paths, IReadOnlyList<string> excludes, string host)
    {
        var arguments = new List<string>(global) { "backup", "--group-by", "", "--host", host };
        foreach (var pattern in excludes)
        {
            arguments.AddRange(["--exclude", pattern]);
        }
        arguments.Add("--");
        arguments.AddRange(paths);
        return arguments;
    }

    /// <summary>
    /// <c>unshare</c>'s arguments that mount the snapshots in a private namespace and then run <paramref name="command"/>.
    /// Parents are mounted before their children; a child's mountpoint is then a folder in the parent's snapshot.
    /// </summary>
    public static IReadOnlyList<string> InNamespace(IReadOnlyList<SnapshotMount> mounts, IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        var arguments = new List<string> { "--mount", "--propagation", "private", "--", "sh", "-c", MountScript, "sh", StageDirectory };
        foreach (var mount in mounts.OrderBy(mount => mount.Mountpoint.Length).ThenBy(mount => mount.Mountpoint, StringComparer.Ordinal))
        {
            arguments.AddRange([mount.Snapshot, mount.Mountpoint]);
        }
        arguments.Add("--");
        arguments.AddRange(command);
        return arguments;
    }

    /// <summary>
    /// <c>forget --prune</c> with the retention, over every version of the repository; null when it keeps all. Versions
    /// with the tag <see cref="ProtectedTag"/> are always kept.
    /// </summary>
    public static IReadOnlyList<string>? Forget(IReadOnlyList<string> global, BackupRetention retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        if (retention.KeepsAll)
        {
            return null;
        }
        var arguments = new List<string>(global) { "forget", "--prune", "--group-by", "", "--keep-tag", ProtectedTag };
        void Keep(string flag, int count)
        {
            if (count > 0)
            {
                arguments.AddRange([flag, Text(count)]);
            }
        }
        Keep("--keep-last", retention.Last);
        Keep("--keep-hourly", retention.Hourly);
        Keep("--keep-daily", retention.Daily);
        Keep("--keep-weekly", retention.Weekly);
        Keep("--keep-monthly", retention.Monthly);
        Keep("--keep-yearly", retention.Yearly);
        return arguments;
    }

    /// <summary>Every version in the repository.</summary>
    public static IReadOnlyList<string> Snapshots(IReadOnlyList<string> global) => [.. global, "snapshots"];

    /// <summary>What changed from version <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyList<string> global, string from, string to) => [.. global, "diff", "--", from, to];

    /// <summary>Adds or removes <see cref="ProtectedTag"/> on a version; restic gives the version a new id.</summary>
    public static IReadOnlyList<string> Protect(IReadOnlyList<string> global, string snapshot, bool protect) =>
        [.. global, "tag", protect ? "--add" : "--remove", ProtectedTag, "--", snapshot];

    /// <summary><c>stats</c> in raw-data mode: what the repository takes on the target after compression and deduplication.</summary>
    public static IReadOnlyList<string> Stats(IReadOnlyList<string> global) => [.. global, "stats", "--mode", "raw-data"];

    /// <summary><c>check</c>, reading back <paramref name="readPercent"/> percent of the data, with the cache on the pool.</summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<string> global, int readPercent)
    {
        var arguments = new List<string>(global) { "check", "--with-cache" };
        if (readPercent > 0)
        {
            arguments.Add("--read-data-subset=" + Text(readPercent) + "%");
        }
        return arguments;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What a run of restic reported, collected from its JSON lines.</summary>
public sealed class ResticReport
{
    public JobProgress? Progress { get; set; }
    public long? Files { get; set; }
    public long? Bytes { get; set; }
    public long? AddedBytes { get; set; }
    public string? Snapshot { get; set; }

    /// <summary>What the repository takes on the target after the run, from <c>stats</c>.</summary>
    public long? RepositoryBytes { get; set; }

    /// <summary>Files and folders restic could not read.</summary>
    public int Warnings { get; set; }

    /// <summary>The first of them with restic's message, for the log and the run's details.</summary>
    public List<ResticUnreadable> WarningItems { get; } = [];

    /// <summary>Errors a check found.</summary>
    public int CheckErrors { get; set; }
}

/// <summary>A file or folder restic could not read: its path as restic saw it and restic's message.</summary>
public sealed record ResticUnreadable(string Item, string Message)
{
    /// <summary>Why, as the run's details name it: <c>permission</c>, <c>missing</c> (gone while the run read it) or <c>other</c>.</summary>
    public string Reason =>
        Message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "permission"
        : Message.Contains("no such file", StringComparison.OrdinalIgnoreCase) ? "missing"
        : "other";
}

/// <summary>restic's JSON lines and exit codes (D415), pure.</summary>
public static class ResticOutput
{
    /// <summary>Unreadable files kept per run.</summary>
    public const int KeptWarningItems = 100;

    /// <summary>Reads one line of stdout or stderr into <paramref name="report"/>. Returns whether it was a progress line.</summary>
    public static bool Read(string line, ResticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            switch (String(root, "message_type"))
            {
                case "status":
                    report.Progress = new JobProgress(
                        Number(root, "files_done"),
                        Number(root, "total_files"),
                        Number(root, "bytes_done"),
                        Number(root, "total_bytes"),
                        root.TryGetProperty("current_files", out var current) && current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 0
                            ? current[0].GetString()
                            : null);
                    return true;
                case "summary" when root.TryGetProperty("snapshot_id", out _) || root.TryGetProperty("total_files_processed", out _):
                    report.Files = Number(root, "total_files_processed");
                    report.Bytes = Number(root, "total_bytes_processed");
                    report.AddedBytes = Number(root, "data_added_packed");
                    report.Snapshot = String(root, "snapshot_id");
                    return false;
                case "summary" when root.TryGetProperty("num_errors", out _):
                    report.CheckErrors = (int)Number(root, "num_errors");
                    return false;
                case "error":
                    report.Warnings++;
                    if (report.WarningItems.Count < KeptWarningItems)
                    {
                        var message = root.TryGetProperty("error", out var error) ? String(error, "message") ?? "" : "";
                        report.WarningItems.Add(new ResticUnreadable(String(root, "item") ?? "", message));
                    }
                    return false;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The repository's stored size from the JSON of <c>stats --mode raw-data</c>; null without it.</summary>
    public static long? RepositoryBytes(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        foreach (var line in standardOutput.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{'))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("total_size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) && bytes >= 0)
                {
                    return bytes;
                }
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// The error of a failed call, or null for exit 0 (and 3 of a backup, which is a warning). Connection failures are
    /// read from ssh's messages in stderr, as for a target's test.
    /// </summary>
    public static PithyxException? Failure(int exitCode, string standardError, string remotePath)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (exitCode is 0 or ResticCommands.Incomplete)
        {
            return null;
        }
        if (BackupSsh.ConnectionFailure(standardError) is { } connection)
        {
            return connection;
        }
        return exitCode switch
        {
            ResticCommands.RepositoryMissing => new PithyxException(ErrorCodes.BackupRepositoryNotFound, new Dictionary<string, object?> { ["path"] = remotePath }),
            ResticCommands.Locked => new PithyxException(ErrorCodes.BackupRepositoryLocked),
            ResticCommands.WrongPassword => new PithyxException(ErrorCodes.BackupRepositoryWrongKey),
            ResticCommands.MountFailed => new PithyxException(ErrorCodes.BackupSourceUnavailable, new Dictionary<string, object?> { ["reason"] = "snapshot", ["share"] = "" }),
            _ when standardError.Contains("config file already exists", StringComparison.Ordinal) =>
                new PithyxException(ErrorCodes.BackupRepositoryExists, new Dictionary<string, object?> { ["path"] = remotePath }),
            _ => new PithyxException(ErrorCodes.BackupRunFailed, new Dictionary<string, object?> { ["reason"] = "exit", ["exitCode"] = exitCode }),
        };
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
        : element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number ? (long)value.GetDouble()
        : 0;
}
