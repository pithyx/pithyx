namespace Pithyx.Daemon.Files;

/// <summary>
/// Folder snapshots of recent listings, least recently used first out, bounded by the number of names held. Also
/// remembers which folder id belongs to which path, so a folder topic can be watched after its listing.
/// </summary>
public sealed class FolderCache
{
    public const int MaxNames = 2_000_000;
    public const int MaxFolders = 512;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, FolderSnapshot Snapshot)>> _snapshots = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, FolderSnapshot Snapshot)> _order = new();
    private readonly Dictionary<string, ListedFolder> _folders = new(StringComparer.Ordinal);
    private long _names;

    internal FolderSnapshot? Get(long shareId, string path)
    {
        lock (_lock)
        {
            if (!_snapshots.TryGetValue(Key(shareId, path), out var node))
            {
                return null;
            }
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Snapshot;
        }
    }

    internal void Put(long shareId, string path, FolderSnapshot snapshot)
    {
        var key = Key(shareId, path);
        lock (_lock)
        {
            if (_snapshots.Remove(key, out var old))
            {
                _order.Remove(old);
                _names -= old.Value.Snapshot.Count;
            }
            _snapshots[key] = _order.AddFirst((key, snapshot));
            _names += snapshot.Count;
            while (_order.Count > 1 && (_names > MaxNames || _order.Count > MaxFolders))
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _snapshots.Remove(last.Value.Key);
                _names -= last.Value.Snapshot.Count;
            }
        }
    }

    /// <summary>Drops the snapshot of a folder that changed, so the next listing reads it again.</summary>
    public void Invalidate(long shareId, string path)
    {
        lock (_lock)
        {
            if (_snapshots.Remove(Key(shareId, path), out var node))
            {
                _order.Remove(node);
                _names -= node.Value.Snapshot.Count;
            }
        }
    }

    internal void Register(long shareId, string sharePath, string folderId, string path)
    {
        var topic = FileService.TopicOf(shareId, folderId);
        lock (_lock)
        {
            if (_folders.Count >= MaxFolders * 4 && !_folders.ContainsKey(topic))
            {
                _folders.Clear();
            }
            _folders[topic] = new ListedFolder(shareId, sharePath, folderId, path);
        }
    }

    /// <summary>The folder behind a topic, if it was listed recently.</summary>
    public ListedFolder? FolderOf(string topic)
    {
        lock (_lock)
        {
            return _folders.GetValueOrDefault(topic);
        }
    }

    public IReadOnlyList<string> Topics()
    {
        lock (_lock)
        {
            return [.. _folders.Keys];
        }
    }

    private static string Key(long shareId, string path) => $"{shareId}\0{path}";
}

/// <summary>A folder that was listed, for its live-change topic.</summary>
public sealed record ListedFolder(long ShareId, string SharePath, string FolderId, string Path);
