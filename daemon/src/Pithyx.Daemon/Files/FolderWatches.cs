using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Pithyx.Daemon.Files;

/// <summary>A batch of changes of one watched folder, ready to publish.</summary>
public sealed record FolderEvent(string Topic, FolderChanges Changes);

/// <summary>
/// Live changes of the folders open in a file manager, from one inotify instance. A folder is watched only while its
/// topic has subscribers, through a handle opened below the share root, so the watch follows that folder and never a
/// path someone swaps in. The mask has no access or open events: reading a file never produces an event, and a watch
/// on its own never touches the disk. Changes are collected for a moment and sent together; only then are the changed
/// entries looked up, and a change means the disk is awake already.
/// </summary>
/// <remarks>Dependency-free on purpose: the folder topic's snapshot delegate starts watches through it.</remarks>
public sealed class FolderWatches : IDisposable
{
    public const int MaxWatches = 1024;
    public static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(150);

    private const uint Mask = LinuxFs.InCreate | LinuxFs.InDelete | LinuxFs.InMovedFrom | LinuxFs.InMovedTo
        | LinuxFs.InCloseWrite | LinuxFs.InAttrib | LinuxFs.InDeleteSelf | LinuxFs.InMoveSelf;

    private readonly FolderCache _cache;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Watch> _byTopic = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Watch> _byDescriptor = [];
    private readonly Channel<FolderEvent> _events = Channel.CreateUnbounded<FolderEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SafeFileHandle _inotify;
    private readonly SafeFileHandle _wake;
    private readonly Thread _reader;
    private volatile bool _stopping;

    public FolderWatches(FolderCache cache, TimeProvider time)
    {
        _cache = cache;
        _time = time;
        _inotify = LinuxFs.InotifyInit();
        _wake = LinuxFs.EventCreate();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pithyx-inotify" };
        _reader.Start();
    }

    public ChannelReader<FolderEvent> Events => _events.Reader;

    /// <summary>Topics of folders listed recently, which clients may subscribe to.</summary>
    public IEnumerable<string> Topics => _cache.Topics();

    public IReadOnlyCollection<string> Watched
    {
        get
        {
            lock (_lock)
            {
                return [.. _byTopic.Keys];
            }
        }
    }

    /// <summary>Starts watching the folder behind a topic, if it was listed recently. Returns whether it is watched.</summary>
    public bool Ensure(string topic)
    {
        lock (_lock)
        {
            if (_byTopic.ContainsKey(topic))
            {
                return true;
            }
            if (_byTopic.Count >= MaxWatches || _cache.FolderOf(topic) is not { } folder)
            {
                return false;
            }
            SafeFileHandle? handle = null;
            try
            {
                using var root = LinuxFs.OpenRoot(folder.SharePath);
                handle = LinuxFs.OpenBeneath(root, FsPath.ForOpen(folder.Path), LinuxFs.Path | LinuxFs.Directory);
                // The path may point at another folder by now; the topic belongs to the one that was listed.
                if (LinuxFs.Stat(handle, "").Id != folder.FolderId)
                {
                    handle.Dispose();
                    return false;
                }
                var descriptor = LinuxFs.InotifyWatch(_inotify, handle, Mask);
                var watch = new Watch(topic, folder.ShareId, folder.SharePath, folder.Path, handle, descriptor);
                _byTopic[topic] = watch;
                _byDescriptor[descriptor] = watch;
                return true;
            }
            catch (FsError)
            {
                // Gone, or the kernel's watch limit is reached: the client simply gets no live changes.
                handle?.Dispose();
                return false;
            }
        }
    }

    /// <summary>Stops watching folders whose topic nobody subscribes to any more.</summary>
    public void Release(Func<string, bool> hasSubscribers)
    {
        ArgumentNullException.ThrowIfNull(hasSubscribers);
        lock (_lock)
        {
            foreach (var watch in _byTopic.Values.Where(watch => !hasSubscribers(watch.Topic)).ToList())
            {
                Remove(watch, unwatch: true);
            }
        }
    }

    /// <summary>
    /// Stops watching every folder at or below the given paths and tells its subscribers it is gone, so a dataset can
    /// be unmounted: the watch holds a handle on the folder. Returns how many watches ended.
    /// </summary>
    public int ReleaseBelow(IReadOnlyCollection<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        List<Watch> ended;
        lock (_lock)
        {
            ended = [.. _byTopic.Values.Where(watch => roots.Any(root => FsPath.IsSameOrInside(watch.FullPath, root)))];
            foreach (var watch in ended)
            {
                Remove(watch, unwatch: true);
            }
        }
        foreach (var watch in ended)
        {
            _cache.Invalidate(watch.ShareId, watch.Path);
            _events.Writer.TryWrite(new FolderEvent(watch.Topic, new FolderChanges([], [], false, true)));
        }
        return ended.Count;
    }

    private void Remove(Watch watch, bool unwatch)
    {
        _byTopic.Remove(watch.Topic);
        _byDescriptor.Remove(watch.Descriptor);
        watch.Timer?.Dispose();
        if (unwatch)
        {
            LinuxFs.InotifyUnwatch(_inotify, watch.Descriptor);
        }
        watch.Handle.Dispose();
    }

    private void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        while (!_stopping)
        {
            try
            {
                if (LinuxFs.WaitReadable(_inotify, _wake) == 2)
                {
                    continue;
                }
                int read;
                while ((read = LinuxFs.ReadEvents(_inotify, buffer)) > 0)
                {
                    Parse(buffer.AsSpan(0, read));
                }
            }
            catch (Exception exception) when (exception is FsError or ObjectDisposedException)
            {
                if (_stopping)
                {
                    return;
                }
            }
        }
    }

    // struct inotify_event: int wd, uint32 mask, uint32 cookie, uint32 len, char name[len].
    private void Parse(ReadOnlySpan<byte> events)
    {
        lock (_lock)
        {
            while (events.Length >= 16)
            {
                var descriptor = BinaryPrimitives.ReadInt32LittleEndian(events);
                var mask = BinaryPrimitives.ReadUInt32LittleEndian(events[4..]);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(events[12..]);
                var nameBytes = events.Slice(16, length);
                var name = Encoding.UTF8.GetString(nameBytes[..(nameBytes.IndexOf((byte)0) is var end and >= 0 ? end : nameBytes.Length)]);
                events = events[(16 + length)..];

                if ((mask & LinuxFs.InQueueOverflow) != 0)
                {
                    foreach (var watch in _byTopic.Values)
                    {
                        watch.Resync = true;
                        Schedule(watch);
                    }
                    continue;
                }
                if (!_byDescriptor.TryGetValue(descriptor, out var target))
                {
                    continue;
                }
                if ((mask & (LinuxFs.InDeleteSelf | LinuxFs.InMoveSelf | LinuxFs.InIgnored)) != 0)
                {
                    target.Gone = true;
                    Schedule(target);
                    continue;
                }
                if (name.Length == 0 || name.StartsWith(FsPath.UploadPrefix, StringComparison.Ordinal) || (target.Path.Length == 0 && name == FsPath.TrashFolder))
                {
                    continue;
                }
                target.Names.Add(name);
                Schedule(target);
            }
        }
    }

    private void Schedule(Watch watch)
    {
        if (watch.Timer is not null)
        {
            return;
        }
        // The timer is stored before it is armed: its callback can run before CreateTimer or Change returns (a fake
        // clock runs a due callback inline), and Flush must find the timer to clear it, or later changes wait behind a
        // timer that already fired (#243).
        var timer = _time.CreateTimer(_ => Flush(watch), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        watch.Timer = timer;
        timer.Change(BatchDelay, Timeout.InfiniteTimeSpan);
    }

    private void Flush(Watch watch)
    {
        List<FileEntry> changed = [];
        List<string> removed = [];
        bool resync, gone;
        lock (_lock)
        {
            watch.Timer?.Dispose();
            watch.Timer = null;
            if (!_byTopic.ContainsKey(watch.Topic))
            {
                return;
            }
            resync = watch.Resync;
            gone = watch.Gone;
            watch.Resync = false;
            foreach (var name in watch.Names.Order(StringComparer.Ordinal))
            {
                if (FileService.TryStat(watch.Handle, name) is { } stat)
                {
                    changed.Add(FileService.ToEntry(name, stat));
                }
                else
                {
                    removed.Add(name);
                }
            }
            watch.Names.Clear();
            if (gone)
            {
                // The kernel dropped the watch (IN_IGNORED follows a deleted folder); nothing is left to remove.
                Remove(watch, unwatch: false);
            }
        }
        _cache.Invalidate(watch.ShareId, watch.Path);
        _events.Writer.TryWrite(new FolderEvent(watch.Topic, new FolderChanges(changed, removed, resync, gone)));
    }

    public void Dispose()
    {
        _stopping = true;
        LinuxFs.EventSignal(_wake);
        _reader.Join(TimeSpan.FromSeconds(2));
        lock (_lock)
        {
            foreach (var watch in _byTopic.Values.ToList())
            {
                Remove(watch, unwatch: false);
            }
        }
        _inotify.Dispose();
        _wake.Dispose();
        _events.Writer.TryComplete();
    }

    private sealed class Watch(string topic, long shareId, string sharePath, string path, SafeFileHandle handle, int descriptor)
    {
        public string Topic { get; } = topic;
        public long ShareId { get; } = shareId;
        public string Path { get; } = path;

        /// <summary>The folder on the host: the share's root joined with the path inside it.</summary>
        public string FullPath { get; } = path.Length == 0 ? sharePath : System.IO.Path.Join(sharePath, path);
        public SafeFileHandle Handle { get; } = handle;
        public int Descriptor { get; } = descriptor;
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public ITimer? Timer { get; set; }
        public bool Resync { get; set; }
        public bool Gone { get; set; }
    }
}
