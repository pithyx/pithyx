using System.Collections.Concurrent;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// Files in shared folders that the user handed to one window of an app (D318): picked in the desktop's file dialog,
/// chosen in the save dialog or opened with the app. Such a file can be read, or written when it was handed over to be
/// written, with <c>files.read</c> or <c>files.write</c> alone, without <c>files.shares</c>: the user's choice is the
/// grant. Only the desktop registers handles, with the window's host token, after a dialog the user answered. A handle
/// names exactly one path, never a folder, and the user's own rights are still checked on every use. In memory only:
/// a restart ends them, and they are bound to the session of the token that registered them.
/// </summary>
public sealed class AppHandles(TimeProvider time)
{
    /// <summary>Handles one window may hold; the oldest make room for new ones.</summary>
    public const int MaxPerWindow = 256;

    private static readonly TimeSpan _lifetime = TimeSpan.FromHours(12);
    private const int MaxWindows = 4096;

    private sealed record Handle(long ShareId, string Path, bool Write, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, List<Handle>> _windows = new(StringComparer.Ordinal);

    private static string KeyOf(AppTokenGrant grant) => $"{grant.SessionId}/{grant.AppId}/{grant.Instance}";

    /// <summary>Remembers that the window may read, or also write, the file <paramref name="path"/> (normalized) of a share.</summary>
    public void Add(AppTokenGrant grant, long shareId, string path, bool write)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var now = time.GetUtcNow();
        if (_windows.Count >= MaxWindows)
        {
            foreach (var (key, handles) in _windows)
            {
                lock (handles)
                {
                    handles.RemoveAll(handle => handle.ExpiresAt <= now);
                    if (handles.Count == 0)
                    {
                        _windows.TryRemove(key, out _);
                    }
                }
            }
        }
        var list = _windows.GetOrAdd(KeyOf(grant), _ => []);
        lock (list)
        {
            list.RemoveAll(handle => handle.ShareId == shareId && handle.Path == path);
            list.Add(new Handle(shareId, path, write, now + _lifetime));
            if (list.Count > MaxPerWindow)
            {
                list.RemoveAt(0);
            }
        }
    }

    /// <summary>Whether the window was handed this exact file, for writing when <paramref name="write"/>.</summary>
    public bool Allows(AppTokenGrant grant, long shareId, string path, bool write)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!_windows.TryGetValue(KeyOf(grant), out var list))
        {
            return false;
        }
        var now = time.GetUtcNow();
        lock (list)
        {
            return list.Any(handle => handle.ShareId == shareId && handle.Path == path && handle.ExpiresAt > now && (!write || handle.Write));
        }
    }
}
