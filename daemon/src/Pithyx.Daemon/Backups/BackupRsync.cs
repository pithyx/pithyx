using System.Globalization;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.Backups;

/// <summary>What a pull did, read from rsync's itemized lines and its statistics.</summary>
public sealed class RsyncReport
{
    /// <summary>Files and folders in the destination after the run (rsync's "Number of files").</summary>
    public long? Files { get; set; }

    /// <summary>Bytes of the files (rsync's "Total file size").</summary>
    public long? Bytes { get; set; }

    public long Added { get; set; }

    public long Changed { get; set; }

    public long Removed { get; set; }

    /// <summary>The first <see cref="BackupRsync.MaxChanges"/> changes, as server paths without the leading slash.</summary>
    public List<BackupVersionChange> Changes { get; } = [];

    /// <summary>Files rsync could not read or that vanished while it read them.</summary>
    public List<(string Item, string Reason)> Unreadable { get; } = [];

    public int UnreadableCount { get; set; }

    public JobProgress? Progress { get; set; }

    /// <summary>The file rsync wrote last.</summary>
    public string? Current { get; set; }
}

/// <summary>
/// rsync over SSH for server backups (#396, D420), with the same hardened ssh as the targets (<see cref="BackupSsh"/>):
/// rsync gets the ssh command line in <c>-e</c>, the server is named only by the alias, and every path a request sends
/// starts with <c>/</c>, which rrsync reads as the top of the folder the key may read.
/// </summary>
public static partial class BackupRsync
{
    /// <summary>Files vanished while rsync read them: a warning.</summary>
    public const int Vanished = 24;

    /// <summary>Some files were not transferred (most often not readable): a warning.</summary>
    public const int Partial = 23;

    public const int MaxChanges = 100;

    public const int MaxUnreadable = 100;

    /// <summary>The ssh command rsync runs, one string as <c>-e</c> takes it; the options hold no spaces.</summary>
    public static string Shell(KeyFolder folder, SshEndpoint endpoint) => "ssh " + string.Join(' ', BackupSsh.Options(folder, endpoint));

    /// <summary>Lists the top of the folder the server's key may read, as a pull starts.</summary>
    public static CommandRequest Probe(KeyFolder folder, SshEndpoint endpoint) => new(
        "env",
        [.. BackupSsh.CleanEnvironment(), "rsync", "-e", Shell(folder, endpoint), "--list-only", "--no-motd", "--", BackupSsh.Alias + ":/"],
        BackupSsh.ProbeTimeout);

    /// <summary>
    /// The arguments after <c>env</c> of a pull of <paramref name="paths"/> into <paramref name="destination"/> (a folder
    /// path ending in <c>/</c>). <c>-R</c> keeps each path below the destination as it is on the server;
    /// <c>--inplace --no-whole-file</c> rewrites only changed blocks, so a snapshot keeps only what changed.
    /// </summary>
    public static IReadOnlyList<string> PullArguments(
        string shell,
        IReadOnlyList<string> paths,
        string destination,
        bool delete,
        BackupPullOwners owners,
        IReadOnlyList<string> excludes,
        int limitKib)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(excludes);
        var arguments = new List<string>(BackupSsh.CleanEnvironment())
        {
            "rsync", "-e", shell,
            "-rltHR", "--numeric-ids", "--inplace", "--no-whole-file", "--no-motd",
            "--itemize-changes", "--stats", "--info=progress2",
        };
        if (owners == BackupPullOwners.Server)
        {
            arguments.AddRange(["-pgoAX"]);
        }
        if (delete)
        {
            arguments.Add("--delete");
        }
        if (limitKib > 0)
        {
            arguments.Add("--bwlimit=" + limitKib.ToString(CultureInfo.InvariantCulture));
        }
        arguments.AddRange(excludes.Select(pattern => "--exclude=" + pattern));
        arguments.Add("--");
        arguments.AddRange(paths.Select(path => BackupSsh.Alias + ":" + path));
        arguments.Add(destination);
        return arguments;
    }

    /// <summary>What a probe's rsync run means: null for no error.</summary>
    public static PithyxException? ProbeOutcome(CommandResult result) => Failure(result.ExitCode, result.StandardError, "/");

    /// <summary>
    /// The error of an rsync run that did not succeed, or null for success and for the warnings (exit 23 and 24), which
    /// rsync gives only after it reached the server. ssh's own failures come first, then a missing rsync or rrsync on
    /// the server.
    /// </summary>
    public static PithyxException? Failure(int exitCode, string standardError, string path)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        // Both warnings mean rsync reached the server and read from it; their messages name unreadable files, which an
        // ssh check would take for a refused key.
        if (exitCode is 0 or Vanished or Partial)
        {
            return null;
        }
        if (BackupSsh.ConnectionFailure(standardError) is { } connection)
        {
            return connection;
        }
        if (Missing().IsMatch(standardError))
        {
            return new PithyxException(ErrorCodes.BackupServerRsyncMissing);
        }
        if (exitCode == 255)
        {
            return new PithyxException(ErrorCodes.BackupTargetUnreachable, new Dictionary<string, object?> { ["reason"] = "failed" });
        }
        return new PithyxException(ErrorCodes.BackupServerRefused, new Dictionary<string, object?> { ["path"] = path });
    }

    /// <summary>
    /// The server backup versions of <paramref name="dataset"/> in <c>zfs list -H -p -o name,creation,written,userrefs
    /// -t snapshot -d 1</c>: snapshots of the kind <c>pull</c>, oldest first.
    /// </summary>
    public static IReadOnlyList<(string Name, DateTimeOffset Created, long? Written, int Holds)> ParseSnapshots(string standardOutput, string dataset)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        var result = new List<(string, DateTimeOffset, long?, int)>();
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length != 4 || !fields[0].StartsWith(dataset + "@", StringComparison.Ordinal))
            {
                continue;
            }
            var name = fields[0][(dataset.Length + 1)..];
            if (Storage.Snapshots.SnapshotNames.KindOf(name) != Storage.Snapshots.SnapshotKinds.Pull
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var created))
            {
                continue;
            }
            result.Add((
                name,
                DateTimeOffset.FromUnixTimeSeconds(created),
                long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var written) ? written : null,
                int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var holds) ? holds : 0));
        }
        return result;
    }

    /// <summary>Reads one line of rsync's stdout: an itemized change, a progress line or a statistic.</summary>
    public static void ReadLine(string line, RsyncReport report)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(report);
        if (Progress().Match(line) is { Success: true } progress)
        {
            var done = Number(progress.Groups["bytes"].Value);
            var percent = int.Parse(progress.Groups["percent"].Value, CultureInfo.InvariantCulture);
            var total = Number(progress.Groups["total"].Value);
            var left = Number(progress.Groups["left"].Value);
            // rsync's percent is of the bytes it knows of so far; the total grows while it still walks the folders.
            report.Progress = new JobProgress(
                Math.Max(total - left, 0),
                total,
                done,
                percent > 0 ? Math.Max(done * 100 / percent, done) : 0,
                report.Current);
            // rsync ends a progress line with a carriage return only, so the next itemized line follows it directly.
            if (progress.Length < line.Length)
            {
                ReadLine(line[progress.Length..], report);
            }
            return;
        }
        if (line.StartsWith("*deleting ", StringComparison.Ordinal))
        {
            report.Removed++;
            Note(report, line["*deleting ".Length..].TrimStart(), BackupChangeKind.Removed);
            return;
        }
        if (Item().Match(line) is { Success: true } item)
        {
            var flags = item.Groups["flags"].Value;
            // Only files count; folders and links change with every file inside or carry no data.
            if (flags[1] != 'f' || flags[0] is not ('>' or 'c'))
            {
                return;
            }
            var added = flags[2] == '+';
            report.Current = item.Groups["name"].Value;
            if (added)
            {
                report.Added++;
            }
            else
            {
                report.Changed++;
            }
            Note(report, item.Groups["name"].Value, added ? BackupChangeKind.Added : BackupChangeKind.Modified);
            return;
        }
        if (Statistic().Match(line) is { Success: true } statistic)
        {
            var value = Number(statistic.Groups["value"].Value);
            switch (statistic.Groups["name"].Value)
            {
                case "Number of files":
                    report.Files = value;
                    break;
                case "Total file size":
                    report.Bytes = value;
                    break;
            }
        }
    }

    /// <summary>The files rsync's messages name as unreadable or vanished, in <paramref name="report"/>.</summary>
    public static void ReadErrors(string standardError, RsyncReport report)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(report);
        foreach (var line in standardError.Split('\n'))
        {
            var (path, reason) = Vanish().Match(line) is { Success: true } vanished
                ? (vanished.Groups["path"].Value, "missing")
                : Unreadable().Match(line) is { Success: true } unreadable
                    ? (unreadable.Groups["path"].Value, unreadable.Groups["reason"].Value.StartsWith("Permission denied", StringComparison.Ordinal) ? "permission" : "other")
                    : (null, null);
            if (path is null || reason is null)
            {
                continue;
            }
            report.UnreadableCount++;
            if (report.Unreadable.Count < MaxUnreadable)
            {
                report.Unreadable.Add((path.TrimStart('/'), reason));
            }
        }
    }

    private static void Note(RsyncReport report, string name, BackupChangeKind change)
    {
        if (report.Changes.Count < MaxChanges)
        {
            report.Changes.Add(new BackupVersionChange(name.TrimEnd('/'), change));
        }
    }

    private static long Number(string text) => long.Parse(text.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\s*(?<bytes>[0-9][0-9,.]*)\s+(?<percent>[0-9]{1,3})%\s[^)]*(ir|to)-chk=(?<left>[0-9]+)/(?<total>[0-9]+)\)")]
    private static partial Regex Progress();

    [GeneratedRegex(@"^(?<flags>[<>ch.*][fdLDS][^ ]{9}) (?<name>.+)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"^(?<name>Number of files|Total file size): (?<value>[0-9][0-9,.]*)")]
    private static partial Regex Statistic();

    [GeneratedRegex(@"(rrsync|rsync): (command )?not found|exec: rrsync|No such file or directory.*rrsync|rrsync.*No such file or directory")]
    private static partial Regex Missing();

    [GeneratedRegex(@"^file has vanished: ""(?<path>.+)""")]
    private static partial Regex Vanish();

    [GeneratedRegex(@"^rsync: (\[sender\] )?(send_files failed to open|opendir|readlink_stat|link_stat) ""(?<path>[^""]+)""( failed)?: (?<reason>.+?) \(\d+\)")]
    private static partial Regex Unreadable();
}
