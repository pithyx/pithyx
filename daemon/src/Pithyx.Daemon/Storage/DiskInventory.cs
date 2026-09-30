using System.Text.Json;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage;

/// <summary>The disks as last read by <see cref="SystemStateService"/>. Reads never run a command.</summary>
public sealed class DiskInventory
{
    public const string Topic = "storage/disks";

    private DiskList _current = new([]);
    private IReadOnlyDictionary<string, BlockDevice> _devices = new Dictionary<string, BlockDevice>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DiskSignatures> _signatures = new(StringComparer.Ordinal);

    public DiskList Current => Volatile.Read(ref _current);

    /// <summary>False until the disks were read once; before that a missing disk means nothing.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Device trees by disk id, for the disk guard.</summary>
    public IReadOnlyDictionary<string, BlockDevice> Devices => Volatile.Read(ref _devices);

    /// <summary>What the last probe found on a disk; missing until it was probed.</summary>
    public DiskSignatures? SignaturesOf(string diskId) => _signatures.GetValueOrDefault(diskId);

    public void SetSignatures(string diskId, DiskSignatures? signatures)
    {
        if (signatures is null)
        {
            _signatures.TryRemove(diskId, out _);
        }
        else
        {
            _signatures[diskId] = signatures;
        }
    }

    /// <summary>Raised after <see cref="Replace"/> found disks that appeared, changed or disappeared.</summary>
    public event Action<DisksChanged>? Changed;

    /// <summary>Replaces the list and returns what changed, compared by id and content.</summary>
    public DisksChanged Replace(IReadOnlyList<Disk> disks, IReadOnlyDictionary<string, BlockDevice>? devices = null)
    {
        ArgumentNullException.ThrowIfNull(disks);
        Loaded = true;
        Volatile.Write(ref _devices, devices ?? new Dictionary<string, BlockDevice>());
        var previous = Current.Disks.ToDictionary(d => d.Id, Fingerprint, StringComparer.Ordinal);
        var upserts = disks.Where(d => !previous.TryGetValue(d.Id, out var before) || before != Fingerprint(d)).ToList();
        var ids = disks.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var removals = previous.Keys.Where(id => !ids.Contains(id)).Order(StringComparer.Ordinal).ToList();
        removals.ForEach(id => _signatures.TryRemove(id, out _));
        Volatile.Write(ref _current, new DiskList(disks));
        var changes = new DisksChanged(upserts, removals);
        if (upserts.Count > 0 || removals.Count > 0)
        {
            Changed?.Invoke(changes);
        }
        return changes;
    }

    public TopicDefinition Definition() => new(
        Topic,
        typeof(DiskList),
        typeof(DisksChanged),
        (_, _) => ValueTask.FromResult<object>(Current),
        // Administrators only, like the Storage app that shows them: disks name serials and models.
        PoolInventory.IsAllowed);

    // Records compare lists by reference, so content is compared through its JSON form.
    private static string Fingerprint(Disk disk) => JsonSerializer.Serialize(disk);
}
