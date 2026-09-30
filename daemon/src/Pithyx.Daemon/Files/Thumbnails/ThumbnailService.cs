using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Channels;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Files.Thumbnails;

/// <summary>
/// Thumbnails for the entries a file manager shows. A request never renders: it answers at once which thumbnails are
/// cached and queues the rest; a background worker renders them one at a time and announces each on
/// <c>thumbnails/&lt;share&gt;/&lt;folder&gt;</c>. Nothing is scanned ahead: only the names a client asks for, in
/// the folder it shows, are ever read. Access is checked like a download, on every request and every served image,
/// so a thumbnail never shows more than the file would.
/// </summary>
public sealed partial class ThumbnailService(ShareService shares, ThumbnailCaches caches, TopicHub hub, ILogger<ThumbnailService> logger) : BackgroundService
{
    public const string Pattern = "thumbnails/+/+";

    /// <summary>At most this many names per request, about a screen of tiles.</summary>
    public const int MaxNames = 200;

    /// <summary>Queued renders beyond this are dropped; the client asks again for what it still shows.</summary>
    public const int QueueCapacity = 1000;

    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".png", ".webp", ".gif" };

    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, bool> _queued = new(StringComparer.Ordinal);

    /// <summary>Keys that could not be rendered, so a broken file is not tried again on every request; bounded.</summary>
    private readonly ConcurrentDictionary<string, ThumbnailRefusal> _refused = new(StringComparer.Ordinal);

    /// <summary>One render: the file as it was when it was requested.</summary>
    private sealed record Work(ResolvedShare Share, ThumbnailCache Cache, string Folder, string Name, string Topic, string Key, ThumbnailSize Size);

    public static TopicDefinition Definition(ShareService shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return new TopicDefinition(
            Pattern,
            typeof(ThumbnailWatch),
            typeof(ThumbnailsReady),
            (_, _) => ValueTask.FromResult<object>(new ThumbnailWatch(true)),
            (user, topic) => ShareOf(topic) is { } share && shares.CanRead(user, share));
    }

    public static bool IsImageName(string name) => _extensions.Contains(Path.GetExtension(name));

    /// <summary>Renders and announcements that are still to come; for tests.</summary>
    internal int Queued => _queued.Count;

    /// <summary>Which of the requested names in the requested folder have a thumbnail, and queues the missing ones.</summary>
    public async Task<ThumbnailStates> RequestAsync(ClaimsPrincipal user, long shareId, ThumbnailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Names.Count > MaxNames)
        {
            throw new PithyxException(ErrorCodes.FsThumbnailTooMany, new Dictionary<string, object?> { ["max"] = MaxNames });
        }
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var folder = FsPath.Normalize(request.Folder);
        using var root = FileService.OpenRoot(share);
        using var handle = FileService.Open(root, folder, LinuxFs.ReadOnly | LinuxFs.Directory);
        var topic = TopicOf(share.Id, FileService.Stat(handle, "").Id);
        var cache = caches.For(share.Path);
        var items = new List<ThumbnailState>(request.Names.Count);
        foreach (var name in request.Names.Distinct(StringComparer.Ordinal))
        {
            if (cache is null || !FsPath.IsName(name) || !IsImageName(name) || FileService.TryStat(handle, name) is not { Kind: FsKind.File } stat)
            {
                items.Add(new ThumbnailState(name, ThumbnailStatus.None, null));
                continue;
            }
            if (stat.Size > ThumbnailRenderer.MaxFileBytes)
            {
                items.Add(new ThumbnailState(name, ThumbnailStatus.None, null));
                continue;
            }
            var key = ThumbnailCache.KeyOf(share.Path, FsPath.Join(folder, name), stat, request.Size);
            if (cache.Contains(key))
            {
                items.Add(new ThumbnailState(name, ThumbnailStatus.Ready, Version(key)));
            }
            else if (_refused.ContainsKey(key))
            {
                items.Add(new ThumbnailState(name, ThumbnailStatus.None, null));
            }
            else
            {
                if (_queued.TryAdd(key, true) && !_queue.Writer.TryWrite(new Work(share, cache, folder, name, topic, key, request.Size)))
                {
                    _queued.TryRemove(key, out _);
                }
                items.Add(new ThumbnailState(name, ThumbnailStatus.Pending, null));
            }
        }
        return new ThumbnailStates(topic, items);
    }

    /// <summary>The cached thumbnail of a file as it is now, after the same access check as a download; null when there is none.</summary>
    public async Task<(string File, string Version)?> FindAsync(ClaimsPrincipal user, long shareId, string? path, ThumbnailSize size, CancellationToken cancellationToken)
    {
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var normalized = FsPath.Normalize(path);
        var (parent, name) = FsPath.Split(normalized);
        using var root = FileService.OpenRoot(share);
        using var folder = FileService.Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
        var stat = FileService.Stat(folder, name);
        if (stat.Kind != FsKind.File)
        {
            return null;
        }
        var key = ThumbnailCache.KeyOf(share.Path, normalized, stat, size);
        return caches.For(share.Path)?.Find(key) is { } file ? (file, Version(key)) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    Render(work);
                }
                finally
                {
                    _queued.TryRemove(work.Key, out _);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; what is still queued is asked for again by the next listing.
        }
    }

    /// <summary>Renders one thumbnail; never throws, a failure is announced like a refusal.</summary>
    private void Render(Work work)
    {
        ThumbnailRefusal? refusal = null;
        try
        {
            using var root = FileService.OpenRoot(work.Share);
            var path = FsPath.Join(work.Folder, work.Name);
            using var handle = FileService.Open(root, path, LinuxFs.ReadOnly);
            var stat = FileService.Stat(handle, "");
            // Changed since it was requested: its new version gets a new key when it is shown again.
            if (stat.Kind != FsKind.File || ThumbnailCache.KeyOf(work.Share.Path, path, stat, work.Size) != work.Key)
            {
                return;
            }
            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: false);
            work.Cache.Store(work.Key, ThumbnailRenderer.Render(stream, stat.Size, ThumbnailCache.EdgeOf(work.Size)));
        }
        catch (ThumbnailRefusedException exception)
        {
            refusal = exception.Refusal;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PithyxException)
        {
            // Gone or unreadable since it was requested.
            refusal = ThumbnailRefusal.Broken;
            LogRenderFailed(work.Name, exception);
        }
        if (refusal is { } reason)
        {
            if (_refused.Count >= QueueCapacity * 10)
            {
                _refused.Clear();
            }
            _refused[work.Key] = reason;
        }
        hub.Publish(work.Topic, new ThumbnailsReady([new ThumbnailState(work.Name, refusal is null ? ThumbnailStatus.Ready : ThumbnailStatus.None, refusal is null ? Version(work.Key) : null)], work.Size));
    }

    /// <summary>Only a prefix of the key goes to clients, to make the image URL change with the file.</summary>
    private static string Version(string key) => key[..16];

    public static string TopicOf(long shareId, string folderId) => $"thumbnails/{shareId.ToString(CultureInfo.InvariantCulture)}/{folderId}";

    private static long? ShareOf(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length == 3 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var share) ? share : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No thumbnail for {Name}")]
    private partial void LogRenderFailed(string name, Exception exception);
}
