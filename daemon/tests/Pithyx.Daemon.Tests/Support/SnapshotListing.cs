using System.Text.Json;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>Answers <see cref="SnapshotPlans.List"/> with the snapshots a test put into its inventory, as <c>zfs list -j</c> prints them.</summary>
internal static class SnapshotListing
{
    public static Func<CommandRequest, CommandResult?> Answer(SnapshotInventory inventory) => request =>
        request.FileName == SnapshotPlans.List.FileName && request.Arguments.SequenceEqual(SnapshotPlans.List.Arguments)
            ? new CommandResult(0, Json(inventory.Current), "", TimeSpan.Zero)
            : null;

    public static string Json(IEnumerable<Snapshot> snapshots)
    {
        static object Property(long value) => new { value, source = new { type = "NONE", data = "-" } };
        var datasets = snapshots.ToDictionary(
            snapshot => $"{snapshot.Dataset}@{snapshot.Name}",
            snapshot => (object)new
            {
                name = $"{snapshot.Dataset}@{snapshot.Name}",
                type = "SNAPSHOT",
                pool = snapshot.Dataset.Split('/')[0],
                createtxg = snapshot.CreateTxg,
                dataset = snapshot.Dataset,
                snapshot_name = snapshot.Name,
                properties = new
                {
                    used = Property(snapshot.UsedBytes),
                    referenced = Property(snapshot.ReferencedBytes),
                    creation = Property(snapshot.CreatedAt.ToUnixTimeSeconds()),
                    userrefs = Property(snapshot.Holds),
                },
            });
        return JsonSerializer.Serialize(new { output_version = new { command = "zfs list", vers_major = 0, vers_minor = 1 }, datasets });
    }
}
