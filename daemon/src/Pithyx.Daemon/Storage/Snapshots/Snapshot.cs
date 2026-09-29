using System.Globalization;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Storage.Snapshots;

/// <param name="Dataset">The dataset the snapshot belongs to, for example <c>tank/pithyx/shares/media</c>.</param>
/// <param name="Name">The part after <c>@</c>, for example <c>pithyx-daily-20260927T000000Z</c>.</param>
/// <param name="CreatedAt">When ZFS took it (UTC), by the clock the box had then.</param>
/// <param name="UsedBytes">Space only this snapshot holds.</param>
/// <param name="ReferencedBytes">Data the snapshot references.</param>
/// <param name="Holds">Number of holds (<c>userrefs</c>); a held snapshot cannot be destroyed.</param>
/// <param name="CreateTxg">The transaction group it was taken in: the order of snapshots, whatever the clock said.</param>
public sealed record Snapshot(string Dataset, string Name, DateTimeOffset CreatedAt, long UsedBytes, long ReferencedBytes, int Holds, long CreateTxg)
{
    public string FullName => $"{Dataset}@{Name}";

    /// <summary>The schedule kind of a snapshot Pithyx created, or null for every other snapshot.</summary>
    public string? PithyxKind => SnapshotNames.KindOf(Name);
}

/// <summary>Schedule kinds, oldest first by period length.</summary>
public static class SnapshotKinds
{
    public const string Hourly = "hourly";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";

    /// <summary>Taken by a user; never pruned by a schedule.</summary>
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> Scheduled = [Hourly, Daily, Weekly, Monthly];

    /// <summary>What a new schedule keeps: a day of hourly, a week of daily, a month of weekly and half a year of monthly snapshots.</summary>
    public static int DefaultKeep(string kind) => kind switch
    {
        Hourly => 24,
        Daily => 7,
        Weekly => 4,
        Monthly => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>Names of the snapshots Pithyx creates: <c>pithyx-&lt;kind&gt;-&lt;yyyyMMddTHHmmssZ&gt;</c>.</summary>
public static partial class SnapshotNames
{
    public const string Prefix = "pithyx-";
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static string For(string kind, DateTimeOffset utc) =>
        $"{Prefix}{kind}-{utc.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}";

    /// <summary>The kind of a name Pithyx created, or null when the name does not follow the pattern exactly.</summary>
    public static string? KindOf(string name) =>
        name is not null && PithyxName().Match(name) is { Success: true } match ? match.Groups["kind"].Value : null;

    [GeneratedRegex("^pithyx-(?<kind>hourly|daily|weekly|monthly|manual)-[0-9]{8}T[0-9]{6}Z$")]
    private static partial Regex PithyxName();
}
