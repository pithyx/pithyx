using System.Globalization;
using System.Text.Json;

namespace Pithyx.Daemon.Backups;

/// <summary>Why the retention kept a version.</summary>
public enum BackupKeepReason
{
    Last,
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Yearly,
    Protected,
}

/// <summary>How a file changed against the previous version.</summary>
public enum BackupChangeKind
{
    Added,
    Modified,
    Removed,
}

/// <summary>A file that changed against the previous version.</summary>
/// <param name="Path">The shared folder's or home's name and the path below it.</param>
/// <param name="Change">Added, modified or removed.</param>
public sealed record BackupVersionChange(string Path, BackupChangeKind Change);

/// <summary>A version of a job: a restic snapshot in its repository.</summary>
/// <param name="Id">Stable id; it stays when the version is protected or released.</param>
/// <param name="Snapshot">restic's id of the version now, shortened to 8 characters as restic shows it.</param>
/// <param name="Time">When it was made, UTC.</param>
/// <param name="Files">Files in it.</param>
/// <param name="Bytes">Bytes in it.</param>
/// <param name="AddedBytes">Bytes it added to the repository.</param>
/// <param name="FilesAdded">Files new against the version before.</param>
/// <param name="FilesChanged">Files changed against the version before.</param>
/// <param name="FilesRemoved">Files gone against the version before; null when the box did not compare.</param>
/// <param name="Protected">The retention always keeps it.</param>
/// <param name="Keep">Why the last retention kept it; empty when the job keeps every version or no retention ran since.</param>
/// <param name="Changes">The first 100 files that changed against the version before; null when the box did not compare.</param>
public sealed record BackupVersion(
    long Id,
    string Snapshot,
    DateTimeOffset Time,
    long? Files,
    long? Bytes,
    long? AddedBytes,
    long? FilesAdded,
    long? FilesChanged,
    long? FilesRemoved,
    bool Protected,
    IReadOnlyList<BackupKeepReason> Keep,
    IReadOnlyList<BackupVersionChange>? Changes);

/// <summary>A job's versions, newest first.</summary>
public sealed record BackupVersionList(IReadOnlyList<BackupVersion> Versions);

/// <summary>Protects a version from the retention or releases it.</summary>
public sealed record BackupVersionProtection(bool Protected);

/// <summary>A job's repository key, shown again after a step-up.</summary>
public sealed record BackupJobKey(string Key);

/// <summary>A version as <c>restic snapshots</c> lists it.</summary>
/// <param name="Id">Its id now.</param>
/// <param name="Origin">The id it was made with; restic keeps it as <c>original</c> when its tags change.</param>
/// <param name="Time">When it was made, UTC.</param>
/// <param name="Protected">It has the tag <c>pithyx-protected</c>.</param>
/// <param name="Files">Files in it, from its summary.</param>
/// <param name="Bytes">Bytes in it.</param>
/// <param name="AddedBytes">Bytes it added to the repository.</param>
/// <param name="FilesAdded">Files new against the version before.</param>
/// <param name="FilesChanged">Files changed against the version before.</param>
/// <param name="Paths">The folders it holds, as restic stored them.</param>
public sealed record ResticSnapshot(
    string Id,
    string Origin,
    DateTimeOffset Time,
    bool Protected,
    long? Files,
    long? Bytes,
    long? AddedBytes,
    long? FilesAdded,
    long? FilesChanged,
    IReadOnlyList<string> Paths);

/// <summary>What <c>restic diff</c> found between two versions.</summary>
/// <param name="Changes">The first files that changed, with restic's paths.</param>
/// <param name="Added">Files added.</param>
/// <param name="Modified">Files changed.</param>
/// <param name="Removed">Files removed.</param>
public sealed record ResticDiff(IReadOnlyList<BackupVersionChange> Changes, long Added, long Modified, long Removed);

/// <summary>restic's JSON about versions (#404), pure.</summary>
public static class ResticVersions
{
    /// <summary>Changed files kept per version.</summary>
    public const int KeptChanges = 100;

    /// <summary>The versions of <c>snapshots --json</c>, oldest first; null when the output is no list.</summary>
    public static IReadOnlyList<ResticSnapshot>? Snapshots(string standardOutput)
    {
        if (Json(standardOutput, '[') is not { } document)
        {
            return null;
        }
        using (document)
        {
            var result = new List<ResticSnapshot>();
            foreach (var snapshot in document.RootElement.EnumerateArray())
            {
                if (Snapshot(snapshot) is { } parsed)
                {
                    result.Add(parsed);
                }
            }
            return [.. result.OrderBy(snapshot => snapshot.Time)];
        }
    }

    /// <summary>Why <c>forget --json</c> kept each version, by its id; null when the output is no list.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BackupKeepReason>>? KeepReasons(string standardOutput)
    {
        if (Json(standardOutput, '[') is not { } document)
        {
            return null;
        }
        using (document)
        {
            var result = new Dictionary<string, IReadOnlyList<BackupKeepReason>>(StringComparer.Ordinal);
            foreach (var group in document.RootElement.EnumerateArray())
            {
                if (!group.TryGetProperty("reasons", out var reasons) || reasons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var reason in reasons.EnumerateArray())
                {
                    if (reason.TryGetProperty("snapshot", out var snapshot) && String(snapshot, "id") is { } id
                        && reason.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
                    {
                        result[id] = [.. matches.EnumerateArray()
                            .Select(match => Reason(match.GetString() ?? ""))
                            .OfType<BackupKeepReason>()
                            .Distinct()
                            .Order()];
                    }
                }
            }
            return result;
        }
    }

    /// <summary>The changes of <c>diff --json</c>: the first <see cref="KeptChanges"/> files and the counts of restic's statistics.</summary>
    public static ResticDiff? Diff(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        var changes = new List<BackupVersionChange>();
        long? added = null, modified = null, removed = null;
        foreach (var line in standardOutput.Split('\n'))
        {
            if (Json(line, '{') is not { } document)
            {
                continue;
            }
            using (document)
            {
                var root = document.RootElement;
                switch (String(root, "message_type"))
                {
                    case "change" when changes.Count < KeptChanges && String(root, "path") is { } path && !path.EndsWith('/'):
                        if (Change(String(root, "modifier")) is { } change)
                        {
                            changes.Add(new BackupVersionChange(path, change));
                        }
                        break;
                    case "statistics":
                        added = Files(root, "added");
                        removed = Files(root, "removed");
                        modified = Number(root, "changed_files");
                        break;
                }
            }
        }
        return added is null ? null : new ResticDiff(changes, added.Value, modified ?? 0, removed ?? 0);
    }

    /// <summary>The version's new id from <c>tag --json</c>; null when restic changed nothing.</summary>
    public static string? TagChanged(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        foreach (var line in standardOutput.Split('\n'))
        {
            if (Json(line, '{') is not { } document)
            {
                continue;
            }
            using (document)
            {
                if (String(document.RootElement, "message_type") == "changed" && String(document.RootElement, "new_snapshot_id") is { } id)
                {
                    return id;
                }
            }
        }
        return null;
    }

    /// <summary>restic's short form of an id, as its own lists show it.</summary>
    public static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    private static ResticSnapshot? Snapshot(JsonElement snapshot)
    {
        if (String(snapshot, "id") is not { } id || String(snapshot, "time") is not { } text
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
        {
            return null;
        }
        var tags = snapshot.TryGetProperty("tags", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(tag => tag.GetString()).ToList()
            : [];
        var summary = snapshot.TryGetProperty("summary", out var value) && value.ValueKind == JsonValueKind.Object ? value : (JsonElement?)null;
        return new ResticSnapshot(
            id,
            String(snapshot, "original") ?? id,
            time.ToUniversalTime(),
            tags.Contains(ResticCommands.ProtectedTag),
            summary is { } files ? Number(files, "total_files_processed") : null,
            summary is { } bytes ? Number(bytes, "total_bytes_processed") : null,
            summary is { } added ? Number(added, "data_added_packed") : null,
            summary is { } fresh ? Number(fresh, "files_new") : null,
            summary is { } changed ? Number(changed, "files_changed") : null,
            snapshot.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array
                ? [.. paths.EnumerateArray().Select(path => path.GetString()).OfType<string>()]
                : []);
    }

    // restic names each rule that kept a version: "last snapshot", "daily snapshot", "oldest weekly snapshot",
    // "has tags [pithyx-protected]".
    private static BackupKeepReason? Reason(string match) =>
        match.StartsWith("has tags", StringComparison.Ordinal) ? BackupKeepReason.Protected
        : match.Contains("last", StringComparison.Ordinal) ? BackupKeepReason.Last
        : match.Contains("hourly", StringComparison.Ordinal) ? BackupKeepReason.Hourly
        : match.Contains("daily", StringComparison.Ordinal) ? BackupKeepReason.Daily
        : match.Contains("weekly", StringComparison.Ordinal) ? BackupKeepReason.Weekly
        : match.Contains("monthly", StringComparison.Ordinal) ? BackupKeepReason.Monthly
        : match.Contains("yearly", StringComparison.Ordinal) ? BackupKeepReason.Yearly
        : null;

    // "+" added, "-" removed, "M" content, "T" type changed; "U" (only metadata) and "?" are left out.
    private static BackupChangeKind? Change(string? modifier) => modifier switch
    {
        "+" => BackupChangeKind.Added,
        "-" => BackupChangeKind.Removed,
        "M" or "T" => BackupChangeKind.Modified,
        _ => null,
    };

    private static JsonDocument? Json(string text, char start)
    {
        ArgumentNullException.ThrowIfNull(text);
        // forget --prune prints its JSON on the first line and then the prune's progress as text.
        foreach (var line in text.Split('\n'))
        {
            if (!line.TrimStart().StartsWith(start))
            {
                continue;
            }
            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private static long? Files(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? Number(value, "files") : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
