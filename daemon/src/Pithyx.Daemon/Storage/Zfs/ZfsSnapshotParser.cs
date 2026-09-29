using Pithyx.Daemon.Storage.Snapshots;
using static Pithyx.Daemon.Storage.Zfs.ZfsJson;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>Parses <c>zfs list -j --json-int -t snapshot -o name,used,referenced,creation,userrefs</c>.</summary>
public static class ZfsSnapshotParser
{
    public static IReadOnlyList<Snapshot> Parse(string json)
    {
        using var document = ZfsJson.Parse(json);
        if (document is null)
        {
            return [];
        }
        return [.. Members(document.RootElement, "datasets")
            .Where(item => Text(item, "type") == "SNAPSHOT")
            .Select(item =>
            {
                var fullName = Text(item, "name") ?? "";
                var at = fullName.IndexOf('@', StringComparison.Ordinal);
                var dataset = Text(item, "dataset") ?? (at > 0 ? fullName[..at] : "");
                var name = Text(item, "snapshot_name") ?? (at > 0 ? fullName[(at + 1)..] : fullName);
                return new Snapshot(
                    dataset,
                    name,
                    DateTimeOffset.FromUnixTimeSeconds(PropertyNumber(item, "creation")),
                    PropertyNumber(item, "used"),
                    PropertyNumber(item, "referenced"),
                    (int)PropertyNumber(item, "userrefs"),
                    Number(item, "createtxg"));
            })
            .OrderBy(snapshot => snapshot.Dataset, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.CreateTxg)
            .ThenBy(snapshot => snapshot.Name, StringComparer.Ordinal)];
    }
}
