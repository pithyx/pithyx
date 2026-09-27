using System.Text.Json;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage;

/// <summary>The disks as last read by <see cref="SystemStateService"/>. Reads never run a command.</summary>
public sealed class DiskInventory
{
    public const string Topic = "storage/disks";

    private DiskList _current = new([]);

    public DiskList Current => Volatile.Read(ref _current);

    /// <summary>Replaces the list and returns what changed, compared by id and content.</summary>
    public DisksChanged Replace(IReadOnlyList<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var previous = Current.Disks.ToDictionary(d => d.Id, Fingerprint, StringComparer.Ordinal);
        var upserts = disks.Where(d => !previous.TryGetValue(d.Id, out var before) || before != Fingerprint(d)).ToList();
        var ids = disks.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var removals = previous.Keys.Where(id => !ids.Contains(id)).Order(StringComparer.Ordinal).ToList();
        Volatile.Write(ref _current, new DiskList(disks));
        return new DisksChanged(upserts, removals);
    }

    public TopicDefinition Definition() => new(
        Topic,
        typeof(DiskList),
        typeof(DisksChanged),
        (_, _) => ValueTask.FromResult<object>(Current),
        TopicDefinition.AnyUser);

    // Records compare lists by reference, so content is compared through its JSON form.
    private static string Fingerprint(Disk disk) => JsonSerializer.Serialize(disk);
}
