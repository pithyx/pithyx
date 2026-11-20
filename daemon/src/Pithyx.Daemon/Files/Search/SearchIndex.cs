using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Files.Thumbnails;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Files.Search;

/// <summary>
/// Keeps the name index of every shared folder and home (D413) in step with the disks. One thread at the lowest CPU
/// priority does all the work, one folder per step, so a search, a hold or an event never waits for a whole walk:
/// <list type="bullet">
/// <item>a share new to the index is crawled, every folder read;</item>
/// <item>at start, after a pool came back or a hold ended, the index is walked: a folder whose stamp (its mtime and ctime) is unchanged is not read again, only its known subfolders are visited;</item>
/// <item>fanotify filesystem marks on the share datasets report every created, deleted or moved entry (whoever made it), and the folders they name are read again a second later;</item>
/// <item>without fanotify (not root, or not Linux) every share is walked every 15 minutes.</item>
/// </list>
/// The index is never trusted for access: a search filters by the caller's rights and checks every hit on disk.
/// </summary>
public sealed partial class SearchIndex : IHostedService, IDisposable
{
    /// <summary>The most entries one pool's index holds (about 500 MB beside the thumbnails in the cache dataset's quota).</summary>
    public const long MaxEntries = 2_000_000;

    public static readonly TimeSpan EventDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(15);

    private const int LowestPriority = 19;

    private readonly ShareService _shares;
    private readonly ShareChanges _shareChanges;
    private readonly PoolInventory _pools;
    private readonly TimeProvider _time;
    private readonly string? _fallbackDirectory;
    private readonly ILogger<SearchIndex> _logger;

    // Everything below is the worker's; the gate keeps holds and searches out while a step runs.
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Root> _roots = [];
    private readonly Dictionary<string, Store> _stores = new(StringComparer.Ordinal);
    private readonly Stack<Visit> _pending = new();
    private readonly HashSet<long> _walking = [];
    private readonly HashSet<long> _stale = [];
    private readonly List<IReadOnlyCollection<string>> _held = [];
    private readonly Dictionary<ulong, string> _fileSystems = [];

    private readonly ConcurrentQueue<Action> _control = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _dirtyLock = new();
    private readonly Dictionary<string, (ulong Fsid, byte[] Handle)> _dirty = new(StringComparer.Ordinal);
    private ITimer? _flush;
    private ITimer? _fallback;
    private ITimer? _retry;
    private SafeFileHandle? _fanotify;
    private SafeFileHandle? _wake;
    private Thread? _worker;
    private Thread? _reader;
    private volatile bool _stopping;

    public SearchIndex(ShareService shares, ShareChanges shareChanges, PoolInventory pools, TimeProvider time, string? fallbackDirectory, ILogger<SearchIndex> logger)
    {
        _shares = shares;
        _shareChanges = shareChanges;
        _pools = pools;
        _time = time;
        _fallbackDirectory = fallbackDirectory;
        _logger = logger;
    }

    /// <summary>Whether entries reach the index as they change (fanotify); otherwise only walks bring it up to date.</summary>
    public bool Live => _fanotify is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() && LinuxFs.IsRoot)
        {
            _fanotify = Fanotify.TryInit(out var errno);
            if (_fanotify is null)
            {
                LogNoFanotify(errno);
            }
        }
        if (_fanotify is not null)
        {
            _wake = LinuxFs.EventCreate();
            _reader = new Thread(ReadEvents) { IsBackground = true, Name = "pithyx-search-events" };
            _reader.Start();
        }
        else
        {
            _fallback = _time.CreateTimer(_ => Enqueue(WalkAll), null, FallbackInterval, FallbackInterval);
        }
        _worker = new Thread(Work) { IsBackground = true, Name = "pithyx-search" };
        _worker.Start();
        _pools.Changed += Reconcile;
        _shareChanges.Changed += OnShareChange;
        Reconcile();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>Reads the shares again and brings the index in line with them. Done when the returned task completes.</summary>
    public Task RefreshAsync() => Run(() =>
    {
        ReconcileNow();
        foreach (var root in _roots.Values)
        {
            Push(root, root.Entry, "", VisitMode.Walk);
        }
    });

    /// <summary>Completes once every pending folder was read, for tests and the first search after a change.</summary>
    public Task IdleAsync() => Run(() => { }, afterPending: true);

    /// <summary>
    /// Lets go of everything below <paramref name="paths"/> until the result is disposed, so their datasets can be
    /// unmounted: no folder below them is read, and an index database below them is closed. Waits for a running step.
    /// </summary>
    public IDisposable Hold(IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            _held.Add(paths);
            foreach (var store in _stores.Values.Where(store => IsHeld(store.Data.Directory)))
            {
                store.Data.Close();
            }
            if (_fanotify is not null)
            {
                foreach (var root in _roots.Values.Where(root => root.Dataset is not null && IsHeld(root.Path)))
                {
                    Fanotify.Mark(_fanotify, root.Path, add: false);
                }
            }
        }
        return new Release(this, paths);
    }

    /// <summary>
    /// The names among <paramref name="shareIds"/> that contain every term, with their share and path, ranked by how well the
    /// name matches. Whether a hit may be shown is for the caller to decide. <paramref name="complete"/> is false while
    /// one of the shares has not been read completely once.
    /// </summary>
    internal List<(long ShareId, string Path)> Find(IReadOnlyCollection<long> shareIds, IReadOnlyList<string> terms, int cap, out bool complete)
    {
        var found = new List<(SearchCandidate Candidate, Store Store)>();
        complete = true;
        lock (_gate)
        {
            foreach (var group in shareIds.Where(_roots.ContainsKey).GroupBy(id => _roots[id].Directory))
            {
                if (!_stores.TryGetValue(group.Key, out var store) || IsHeld(store.Data.Directory))
                {
                    complete = false;
                    continue;
                }
                var ids = group.ToList();
                try
                {
                    var completeShares = SearchStore.CompleteShares(store.Data.Writer);
                    complete &= ids.All(completeShares.Contains);
                    found.AddRange(SearchStore.Candidates(store.Data.Writer, ids, terms, cap).Select(candidate => (candidate, store)));
                }
                catch (SqliteException error)
                {
                    LogStoreFailed(error, store.Data.Directory);
                    complete = false;
                }
            }
            complete &= shareIds.All(_roots.ContainsKey);
            var ranked = found
                .OrderBy(item => SearchRanking.Score(item.Candidate.Name, terms))
                .ThenBy(item => item.Candidate.Name.Length)
                .ThenBy(item => item.Candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Take(cap)
                .ToList();
            return [.. ranked.Select(item => (item.Candidate.ShareId, SearchStore.PathOf(item.Store.Data.Writer, item.Candidate.Id)))];
        }
    }

    private void OnShareChange(long revision) => Reconcile();

    private void Reconcile() => Enqueue(ReconcileNow);

    private void Retry()
    {
        _retry?.Dispose();
        _retry = null;
        ReconcileNow();
    }

    private void WalkAll()
    {
        foreach (var root in _roots.Values)
        {
            Push(root, root.Entry, "", VisitMode.Walk);
        }
    }

    private void Enqueue(Action action)
    {
        _control.Enqueue(action);
        _signal.Set();
    }

    private Task Run(Action action, bool afterPending = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete()
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception error)
            {
                done.TrySetException(error);
            }
        }
        Enqueue(afterPending ? () => _afterPending.Add(Complete) : Complete);
        return done.Task;
    }

    private readonly List<Action> _afterPending = [];

    private void Work()
    {
        LowerPriority();
        while (!_stopping)
        {
            while (_control.TryDequeue(out var action))
            {
                lock (_gate)
                {
                    Guard(action);
                }
            }
            if (_pending.TryPop(out var visit))
            {
                lock (_gate)
                {
                    Guard(() => Step(visit));
                    if (_pending.Count == 0)
                    {
                        Settle();
                    }
                }
                continue;
            }
            if (_afterPending.Count > 0)
            {
                var waiting = _afterPending.ToList();
                _afterPending.Clear();
                lock (_gate)
                {
                    Settle();
                }
                waiting.ForEach(complete => complete());
                continue;
            }
            _signal.WaitOne();
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogStepFailed(error);
        }
    }

    /// <summary>Every share whose walk ran to the end has been read completely once.</summary>
    private void Settle()
    {
        foreach (var id in _walking)
        {
            if (_roots.TryGetValue(id, out var root) && !_stale.Contains(id) && UsableStore(root) is { Failed: false, Full: false } store)
            {
                Try(store, () => store.Data.SetComplete(id));
            }
        }
        _walking.Clear();
    }

    private void ReconcileNow()
    {
        var all = _shares.ListIndexRootsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var pools = _pools.Current;
        var known = all.Select(root => root.Id).ToHashSet();
        var wanted = new Dictionary<long, (IndexRoot Root, string Directory)>();
        foreach (var root in all.Where(root => root.Available && !IsHeld(root.Path)))
        {
            if (ThumbnailCaches.DirectoryFor(pools, root.Path, _fallbackDirectory, SearchStore.Folder) is { } directory && !IsHeld(directory))
            {
                wanted[root.Id] = (root, directory);
            }
        }
        foreach (var root in _roots.Values.ToList())
        {
            if (!wanted.TryGetValue(root.Id, out var next) || next.Root.Path != root.Path || next.Directory != root.Directory)
            {
                _roots.Remove(root.Id);
            }
        }
        foreach (var (root, directory) in wanted.Values)
        {
            var store = StoreAt(directory, known);
            if (store is null)
            {
                continue;
            }
            if (_roots.TryGetValue(root.Id, out var existing))
            {
                if (_stale.Remove(root.Id))
                {
                    Push(existing, existing.Entry, "", VisitMode.Walk);
                }
                continue;
            }
            var entry = 0L;
            var fresh = false;
            if (!Try(store, () => entry = store.Data.EnsureRoot(root.Id, root.Path, out fresh)))
            {
                continue;
            }
            var added = new Root(root.Id, root.Path, root.Dataset, directory, entry);
            _roots[root.Id] = added;
            _stale.Remove(root.Id);
            Push(added, entry, "", fresh ? VisitMode.Crawl : VisitMode.Walk);
        }
        foreach (var store in _stores.Values.ToList())
        {
            if (store.Data.IsOpen)
            {
                Try(store, () => Forget(store, known));
            }
            if (!_roots.Values.Any(root => root.Directory == store.Data.Directory))
            {
                store.Data.Dispose();
                _stores.Remove(store.Data.Directory);
            }
        }
        Mark();
    }

    /// <summary>The index database in a cache directory, opened and cleared of shares that no longer exist.</summary>
    private Store? StoreAt(string directory, HashSet<long> known)
    {
        if (!_stores.TryGetValue(directory, out var store))
        {
            store = new Store(new SearchStore(directory));
            _stores[directory] = store;
        }
        if (!store.Data.IsOpen || !store.Cleaned)
        {
            var ok = Try(store, () =>
            {
                Forget(store, known);
                store.Count = store.Data.Count();
                store.Full = store.Count >= MaxEntries;
            });
            if (!ok)
            {
                return null;
            }
            store.Cleaned = true;
        }
        return store;
    }

    /// <summary>Removes the entries of shares that no longer exist.</summary>
    private static void Forget(Store store, HashSet<long> known)
    {
        foreach (var gone in store.Data.Shares().Where(id => !known.Contains(id)))
        {
            store.Data.RemoveRoot(gone);
        }
    }

    private void Mark()
    {
        if (_fanotify is null)
        {
            return;
        }
        _fileSystems.Clear();
        foreach (var root in _roots.Values.Where(root => root.Dataset is not null))
        {
            var errno = Fanotify.Mark(_fanotify, root.Path, add: true);
            if (errno != 0)
            {
                LogMarkFailed(root.Path, errno);
                _stale.Add(root.Id);
                continue;
            }
            if (Fanotify.FsidOf(root.Path) is { } fsid)
            {
                _fileSystems[fsid] = root.Path;
            }
        }
    }

    private void Push(Root root, long entry, string path, VisitMode mode)
    {
        _pending.Push(new Visit(root.Id, entry, path, mode));
        _walking.Add(root.Id);
    }

    /// <summary>Reads one folder (or, on a walk, only looks at its stamp) and writes what changed.</summary>
    private void Step(Visit visit)
    {
        if (!_roots.TryGetValue(visit.Root, out var root))
        {
            return;
        }
        if (IsHeld(FsPath.Join(root.Path, visit.Path)) || UsableStore(root) is not { } store)
        {
            _stale.Add(root.Id);
            return;
        }
        if (store.Failed)
        {
            return;
        }
        using var rootHandle = TryOpen(() => LinuxFs.OpenRoot(root.Path));
        if (rootHandle is null)
        {
            _stale.Add(root.Id);
            return;
        }
        using var folder = TryOpen(() => LinuxFs.OpenBeneath(rootHandle, FsPath.ForOpen(visit.Path), LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow));
        if (folder is null)
        {
            // Gone or no longer a folder: its parent's entry goes with the parent's next read; until then hits are checked on disk.
            return;
        }
        var stat = LinuxFs.Stat(folder, "");
        var stamp = Math.Max(stat.Modified.UtcTicks, stat.Changed.UtcTicks);
        (long? Stamp, string? Node) state = default;
        if (!Try(store, () => state = store.Data.FolderState(visit.Entry)))
        {
            return;
        }
        if (visit.Mode == VisitMode.Walk && state.Stamp == stamp && state.Node == stat.Id)
        {
            IReadOnlyList<(long Id, string Name)> folders = [];
            if (Try(store, () => folders = store.Data.ChildFolders(visit.Entry)))
            {
                foreach (var (id, name) in folders)
                {
                    Push(root, id, FsPath.Join(visit.Path, name), VisitMode.Walk);
                }
            }
            return;
        }
        var present = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, kind) in LinuxFs.ReadNames(folder))
        {
            if (name.StartsWith(FsPath.UploadPrefix, StringComparison.Ordinal) || (visit.Path.Length == 0 && name == FsPath.TrashFolder))
            {
                continue;
            }
            var actual = kind == FsKind.Unknown ? StatKind(folder, name) : kind;
            if (actual is FsKind.File or FsKind.Folder)
            {
                present[name] = actual == FsKind.Folder;
            }
        }
        IReadOnlyList<IndexedChild> children = [];
        if (!Try(store, () => children = store.Data.Children(visit.Entry)))
        {
            return;
        }
        var existing = children.ToDictionary(child => child.Name, StringComparer.Ordinal);
        var removed = children.Where(child => !present.TryGetValue(child.Name, out var isFolder) || isFolder != child.Folder).ToList();
        var addedNames = present.Where(item => !existing.TryGetValue(item.Key, out var child) || child.Folder != item.Value).ToList();
        var added = new List<(string Name, bool Folder, string? Node)>();
        foreach (var (name, isFolder) in addedNames)
        {
            added.Add((name, isFolder, isFolder ? TryStatId(folder, name) : null));
        }
        // A folder renamed within this folder keeps its inode: keep its entries instead of reading them again.
        var renamed = new List<(long Id, string Name)>();
        var removedFolders = removed.Where(child => child.Folder && child.Node is not null).GroupBy(child => child.Node!).ToDictionary(group => group.Key, group => group.First());
        foreach (var item in added.Where(item => item.Folder && item.Node is not null).ToList())
        {
            if (removedFolders.Remove(item.Node!, out var old))
            {
                renamed.Add((old.Id, item.Name));
                removed.Remove(old);
                added.Remove(item);
            }
        }
        if (store.Count + added.Count > MaxEntries)
        {
            store.Count = 0;
            Try(store, () => store.Count = store.Data.Count());
            if (store.Count + added.Count > MaxEntries)
            {
                if (!store.Full)
                {
                    LogFull(store.Data.Directory, MaxEntries);
                }
                store.Full = true;
                added.Clear();
            }
        }
        Dictionary<string, long> newFolders = [];
        if (!Try(store, () => newFolders = store.Data.Apply(root.Id, visit.Entry, stamp, stat.Id, [.. removed.Select(child => child.Id)], renamed, added)))
        {
            return;
        }
        store.Count += added.Count;
        foreach (var (name, id) in newFolders)
        {
            Push(root, id, FsPath.Join(visit.Path, name), VisitMode.Crawl);
        }
        if (visit.Mode == VisitMode.Read)
        {
            return;
        }
        var next = visit.Mode == VisitMode.Crawl ? VisitMode.Crawl : VisitMode.Walk;
        foreach (var child in children.Where(child => child.Folder && present.TryGetValue(child.Name, out var isFolder) && isFolder))
        {
            Push(root, child.Id, FsPath.Join(visit.Path, child.Name), next);
        }
        foreach (var (id, name) in renamed)
        {
            Push(root, id, FsPath.Join(visit.Path, name), VisitMode.Walk);
        }
    }

    private Store? UsableStore(Root root) =>
        _stores.TryGetValue(root.Directory, out var store) && !IsHeld(store.Data.Directory) ? store : null;

    private bool IsHeld(string path) => _held.Any(paths => paths.Any(held => FsPath.IsSameOrInside(path, held)));

    /// <summary>Runs a database step; a failing database (a full quota, a broken file) stops that index until the next reconcile.</summary>
    private bool Try(Store store, Action action)
    {
        try
        {
            action();
            store.Failed = false;
            return true;
        }
        catch (SqliteException error)
        {
            if (!store.Failed)
            {
                LogStoreFailed(error, store.Data.Directory);
            }
            store.Failed = true;
            store.Data.Close();
            store.Cleaned = false;
            foreach (var root in _roots.Values.Where(root => root.Directory == store.Data.Directory))
            {
                _stale.Add(root.Id);
            }
            // A full quota or a broken file does not mend at once; try again later, or on the next change of pools or shares.
            _retry ??= _time.CreateTimer(_ => Enqueue(Retry), null, FallbackInterval, Timeout.InfiniteTimeSpan);
            return false;
        }
    }

    private static SafeFileHandle? TryOpen(Func<SafeFileHandle> open)
    {
        try
        {
            return open();
        }
        catch (FsError)
        {
            return null;
        }
    }

    private static FsKind StatKind(SafeFileHandle folder, string name)
    {
        try
        {
            return LinuxFs.Stat(folder, name).Kind;
        }
        catch (FsError)
        {
            return FsKind.Other;
        }
    }

    private static string? TryStatId(SafeFileHandle folder, string name)
    {
        try
        {
            return LinuxFs.Stat(folder, name).Id;
        }
        catch (FsError)
        {
            return null;
        }
    }

    private void ReadEvents()
    {
        var buffer = new byte[64 * 1024];
        while (!_stopping)
        {
            try
            {
                if (LinuxFs.WaitReadable(_fanotify!, _wake!) == 2)
                {
                    continue;
                }
                int read;
                while ((read = LinuxFs.ReadEvents(_fanotify!, buffer)) > 0)
                {
                    foreach (var change in Fanotify.Parse(buffer.AsSpan(0, read)))
                    {
                        Note(change);
                    }
                }
            }
            catch (Exception error) when (error is FsError or ObjectDisposedException)
            {
                if (!_stopping)
                {
                    // A group that cannot be read stays broken: walk every 15 minutes instead of spinning here.
                    LogReadFailed(error);
                    Enqueue(() => _fallback ??= _time.CreateTimer(_ => Enqueue(WalkAll), null, FallbackInterval, FallbackInterval));
                }
                return;
            }
        }
    }

    private void Note(FanotifyEvent change)
    {
        if (change.Overflow)
        {
            LogOverflow();
            Enqueue(WalkAll);
            return;
        }
        lock (_dirtyLock)
        {
            _dirty[$"{change.Fsid:x}:{Convert.ToHexString(change.Directory)}"] = (change.Fsid, change.Directory);
            if (_flush is null)
            {
                // Stored before it is armed: a fake clock may run a due callback inside Change.
                _flush = _time.CreateTimer(_ => Enqueue(FlushDirty), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _flush.Change(EventDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static string? PathOf(string mount, byte[] handle)
    {
        try
        {
            return Fanotify.PathOf(mount, handle);
        }
        catch (FsError)
        {
            // The dataset went away since the event; a reconcile follows.
            return null;
        }
    }

    /// <summary>Reads again every folder an event named since the last flush.</summary>
    private void FlushDirty()
    {
        List<(ulong Fsid, byte[] Handle)> dirty;
        lock (_dirtyLock)
        {
            dirty = [.. _dirty.Values];
            _dirty.Clear();
            _flush?.Dispose();
            _flush = null;
        }
        foreach (var (fsid, handle) in dirty)
        {
            if (!_fileSystems.TryGetValue(fsid, out var mount) || IsHeld(mount) || PathOf(mount, handle) is not { } path)
            {
                continue;
            }
            var root = _roots.Values.Where(root => FsPath.IsSameOrInside(path, root.Path)).MaxBy(root => root.Path.Length);
            if (root is null || UsableStore(root) is not { } store)
            {
                continue;
            }
            var relative = path.Length == root.Path.Length ? "" : path[(root.Path.Length + 1)..];
            if (FsPath.IsSameOrInside(relative, FsPath.TrashFolder))
            {
                continue;
            }
            var deepest = (Id: root.Entry, Path: "");
            if (Try(store, () => deepest = store.Data.Deepest(root.Entry, relative)))
            {
                Push(root, deepest.Id, deepest.Path, VisitMode.Read);
            }
        }
    }

    private void Stop()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;
        _pools.Changed -= Reconcile;
        _shareChanges.Changed -= OnShareChange;
        _signal.Set();
        if (_wake is not null)
        {
            LinuxFs.EventSignal(_wake);
        }
        _worker?.Join(TimeSpan.FromSeconds(5));
        _reader?.Join(TimeSpan.FromSeconds(2));
        _fallback?.Dispose();
        _retry?.Dispose();
        lock (_dirtyLock)
        {
            _flush?.Dispose();
        }
        lock (_gate)
        {
            foreach (var store in _stores.Values)
            {
                store.Data.Dispose();
            }
            _stores.Clear();
        }
        _fanotify?.Dispose();
        _wake?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    private void LowerPriority()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        // On Linux a nice value set for a thread id applies to that thread only.
        if (SetPriority(0, GetThreadId(), LowestPriority) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            LogNiceFailed(errno);
        }
    }

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriority(int which, int who, int priority);

    [LibraryImport("libc", EntryPoint = "gettid")]
    private static partial int GetThreadId();

    private sealed class Release(SearchIndex index, IReadOnlyCollection<string> paths) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
            {
                return;
            }
            lock (index._gate)
            {
                index._held.Remove(paths);
                foreach (var root in index._roots.Values.Where(root => paths.Any(path => FsPath.IsSameOrInside(root.Path, path) || FsPath.IsSameOrInside(root.Directory, path))))
                {
                    index._stale.Add(root.Id);
                }
                foreach (var store in index._stores.Values.Where(store => paths.Any(path => FsPath.IsSameOrInside(store.Data.Directory, path))))
                {
                    store.Cleaned = false;
                }
            }
            index.Reconcile();
        }
    }

    private sealed record Root(long Id, string Path, string? Dataset, string Directory, long Entry);

    private sealed class Store(SearchStore data)
    {
        public SearchStore Data { get; } = data;
        public long Count { get; set; }
        public bool Cleaned { get; set; }
        public bool Failed { get; set; }
        public bool Full { get; set; }
    }

    private enum VisitMode
    {
        /// <summary>Read the folder only if its stamp changed; visit the known subfolders either way.</summary>
        Walk,

        /// <summary>Read the folder (an event named it); read new subfolders completely.</summary>
        Read,

        /// <summary>Read the folder and every subfolder.</summary>
        Crawl,
    }

    private sealed record Visit(long Root, long Entry, string Path, VisitMode Mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "File search has no fanotify (errno {Errno}); the index is brought up to date by walks every 15 minutes")]
    private partial void LogNoFanotify(int errno);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File search could not mark {Path} for changes (errno {Errno}); it is walked instead")]
    private partial void LogMarkFailed(string path, int errno);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File search could not read change events; the index is brought up to date by walks every 15 minutes")]
    private partial void LogReadFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File search lost change events (queue overflow); every share is walked again")]
    private partial void LogOverflow();

    [LoggerMessage(Level = LogLevel.Warning, Message = "File search index in {Directory} failed; it is tried again on the next change of pools or shares")]
    private partial void LogStoreFailed(Exception error, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File search index in {Directory} is full ({MaxEntries} entries); further names are not indexed")]
    private partial void LogFull(string directory, long maxEntries);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File search step failed")]
    private partial void LogStepFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "File search could not lower its thread priority (errno {Errno})")]
    private partial void LogNiceFailed(int errno);
}
