using System.Runtime.CompilerServices;
using System.Text.Json;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage.Snapshots;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>
/// Plays the snapshot commands of ZFS: <c>zfs list -t snapshot</c> in the OpenZFS 2.3 JSON shape, and snapshot,
/// destroy, hold, release and rollback, refusing what ZFS refuses (held snapshots, rollback past newer ones without
/// <c>-r</c>). Every call is recorded.
/// </summary>
public sealed class FakeZfsSnapshots(TimeProvider time) : ICommandRunner
{
    private long _txg = 100;

    public List<Snapshot> Snapshots { get; } = [];

    public List<CommandRequest> Calls { get; } = [];

    /// <summary>The calls that change something, as one line each.</summary>
    public IEnumerable<string> Changes => Calls.Where(call => call.Arguments[0] != "list").Select(call => string.Join(' ', call.Arguments.Prepend(call.FileName)));

    public Snapshot Add(string dataset, string name, DateTimeOffset createdAt, int holds = 0)
    {
        var snapshot = new Snapshot(dataset, name, createdAt, 0, 1024, holds, ++_txg);
        Snapshots.Add(snapshot);
        return snapshot;
    }

    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (Calls)
        {
            Calls.Add(request);
            return Task.FromResult(Run(request));
        }
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    private CommandResult Run(CommandRequest request)
    {
        if (request.FileName != "zfs")
        {
            return Fail("not zfs");
        }
        var arguments = request.Arguments;
        switch (arguments[0])
        {
            case "list":
                return Ok(ListJson());
            case "snapshot" when arguments.Count == 2 && Split(arguments[1]) is (string dataset, string name):
                if (Find(dataset, name) is not null)
                {
                    return Fail("dataset already exists");
                }
                Add(dataset, name, DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds()));
                return Ok("");
            case "destroy" when arguments.Count == 2 && Split(arguments[1]) is (string dataset, string name):
                var target = Find(dataset, name);
                if (target is null || target.Holds > 0)
                {
                    return Fail(target is null ? "could not find any snapshots to destroy" : "dataset is busy");
                }
                Snapshots.Remove(target);
                return Ok("");
            case "hold" when arguments.Count == 3 && Split(arguments[2]) is (string dataset, string name):
                return Change(dataset, name, snapshot => snapshot with { Holds = snapshot.Holds + 1 });
            case "release" when arguments.Count == 3 && Split(arguments[2]) is (string dataset, string name):
                return Find(dataset, name) is { Holds: 0 } ? Fail("no such tag on this dataset") : Change(dataset, name, snapshot => snapshot with { Holds = snapshot.Holds - 1 });
            case "rollback":
                var recursive = arguments.Count == 3 && arguments[1] == "-r";
                if (arguments.Count != (recursive ? 3 : 2) || Split(arguments[^1]) is not (string rollbackDataset, string rollbackName) || Find(rollbackDataset, rollbackName) is not { } to)
                {
                    return Fail("usage");
                }
                var newer = Snapshots.Where(snapshot => snapshot.Dataset == rollbackDataset && snapshot.CreateTxg > to.CreateTxg).ToList();
                if (newer.Count > 0 && !recursive)
                {
                    return Fail("more recent snapshots or bookmarks exist; use '-r' to force deletion");
                }
                if (newer.Any(snapshot => snapshot.Holds > 0))
                {
                    return Fail("dataset is busy");
                }
                Snapshots.RemoveAll(newer.Contains);
                return Ok("");
            default:
                return Fail("unexpected");
        }
    }

    private Snapshot? Find(string dataset, string name) => Snapshots.FirstOrDefault(snapshot => snapshot.Dataset == dataset && snapshot.Name == name);

    private CommandResult Change(string dataset, string name, Func<Snapshot, Snapshot> change)
    {
        var index = Snapshots.FindIndex(snapshot => snapshot.Dataset == dataset && snapshot.Name == name);
        if (index < 0)
        {
            return Fail("dataset does not exist");
        }
        Snapshots[index] = change(Snapshots[index]);
        return Ok("");
    }

    private static (string Dataset, string Name)? Split(string fullName)
    {
        var at = fullName.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && fullName.IndexOf('@', at + 1) < 0 ? (fullName[..at], fullName[(at + 1)..]) : null;
    }

    private string ListJson()
    {
        var datasets = Snapshots.ToDictionary(
            snapshot => snapshot.FullName,
            snapshot => (object)new Dictionary<string, object>
            {
                ["name"] = snapshot.FullName,
                ["type"] = "SNAPSHOT",
                ["pool"] = snapshot.Dataset.Split('/')[0],
                ["createtxg"] = snapshot.CreateTxg,
                ["dataset"] = snapshot.Dataset,
                ["snapshot_name"] = snapshot.Name,
                ["properties"] = new Dictionary<string, object>
                {
                    ["used"] = Value(snapshot.UsedBytes),
                    ["referenced"] = Value(snapshot.ReferencedBytes),
                    ["creation"] = Value(snapshot.CreatedAt.ToUnixTimeSeconds()),
                    ["userrefs"] = Value(snapshot.Holds),
                },
            });
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["output_version"] = new { command = "zfs list", vers_major = 0, vers_minor = 1 },
            ["datasets"] = datasets,
        });
    }

    private static object Value(long value) => new { value, source = new { type = "NONE", data = "-" } };

    private static CommandResult Ok(string output) => new(0, output, "", TimeSpan.Zero);

    private static CommandResult Fail(string error) => new(1, "", $"cannot do it: {error}", TimeSpan.Zero);
}
