using System.Globalization;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>What one uid uses on a dataset and its quota there.</summary>
/// <param name="UsedBytes">Space of the files the uid owns; null when ZFS did not say (not run as root).</param>
/// <param name="QuotaBytes">The uid's quota, or null for none.</param>
public sealed record ZfsUserSpace(long? UsedBytes, long? QuotaBytes);

/// <summary>
/// The users' space on datasets (D411): <c>userused@&lt;uid&gt;</c> and <c>userquota@&lt;uid&gt;</c>. ZFS counts a
/// file for its owner and refuses a write of a uid over its quota with <c>EDQUOT</c>, a few seconds late (it checks
/// per transaction group).
/// </summary>
public static class ZfsUserSpaces
{
    /// <summary>Reads the space and quota of every uid on every dataset with one command.</summary>
    public static CommandRequest Read(IEnumerable<string> datasets, IEnumerable<long> uids)
    {
        var properties = uids.Distinct().Order().SelectMany(uid => new[] { Used(uid), Quota(uid) }).ToList();
        var names = datasets.Distinct(StringComparer.Ordinal).Select(Dataset).ToList();
        if (properties.Count == 0 || names.Count == 0)
        {
            throw new ArgumentException("Nothing to read.");
        }
        return new("zfs", ["get", "-j", "--json-int", "-t", "filesystem", string.Join(',', properties), .. names]);
    }

    /// <summary>Sets the quota of a uid on a dataset in bytes, or removes it with null.</summary>
    public static CommandRequest Set(string dataset, long uid, long? bytes) =>
        new("zfs", ["set", $"{Quota(uid)}={(bytes is { } value ? value.ToString(CultureInfo.InvariantCulture) : "none")}", Dataset(dataset)]);

    /// <summary>The space of each uid by dataset, from the output of <see cref="Read"/>; 0 as a quota is none.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>> Parse(string json)
    {
        using var document = ZfsJson.Parse(json);
        var result = new Dictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>>(StringComparer.Ordinal);
        if (document is null)
        {
            return result;
        }
        foreach (var dataset in ZfsJson.Members(document.RootElement, "datasets"))
        {
            if (ZfsJson.Text(dataset, "name") is not { } name || !dataset.TryGetProperty("properties", out var properties))
            {
                continue;
            }
            var used = new Dictionary<long, long?>();
            var quotas = new Dictionary<long, long?>();
            foreach (var property in properties.EnumerateObject())
            {
                var value = property.Value.TryGetProperty("value", out var raw) ? ZfsJson.AsNumber(raw) : null;
                if (Uid(property.Name, "userused@") is { } usedUid)
                {
                    used[usedUid] = value;
                }
                else if (Uid(property.Name, "userquota@") is { } quotaUid)
                {
                    quotas[quotaUid] = value is 0 ? null : value;
                }
            }
            result[name] = used.Keys.Union(quotas.Keys).ToDictionary(uid => uid, uid => new ZfsUserSpace(used.GetValueOrDefault(uid), quotas.GetValueOrDefault(uid)));
        }
        return result;
    }

    /// <summary>
    /// The quota commands that make the datasets match <paramref name="wanted"/>: every uid in <paramref name="uids"/>
    /// on every dataset in <paramref name="actual"/> gets its wanted quota, or none. Uids and datasets left out are not
    /// touched, so a quota set outside the box on another uid stays.
    /// </summary>
    public static IReadOnlyList<CommandRequest> Changes(
        IReadOnlyDictionary<(string Dataset, long Uid), long> wanted,
        IReadOnlyDictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>> actual,
        IReadOnlyCollection<long> uids)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(uids);
        var changes = new List<CommandRequest>();
        foreach (var (dataset, spaces) in actual.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (var uid in uids.Distinct().Order())
            {
                long? want = wanted.TryGetValue((dataset, uid), out var bytes) ? bytes : null;
                if (spaces.GetValueOrDefault(uid)?.QuotaBytes != want)
                {
                    changes.Add(Set(dataset, uid, want));
                }
            }
        }
        return changes;
    }

    private static string Used(long uid) => $"userused@{uid.ToString(CultureInfo.InvariantCulture)}";

    private static string Quota(long uid) => $"userquota@{uid.ToString(CultureInfo.InvariantCulture)}";

    private static long? Uid(string property, string prefix) =>
        property.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(property.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;

    // A dataset name never starts with a dash, so it is never read as an option.
    private static string Dataset(string name) =>
        name is { Length: > 0 } && name[0] != '-' && name.Contains('/', StringComparison.Ordinal) ? name : throw new ArgumentException($"Not a dataset: {name}.", nameof(name));
}
