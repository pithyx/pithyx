using System.Security.Claims;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Files.Search;

/// <summary>A file or folder whose name matched a search.</summary>
/// <param name="Share">The share it is in.</param>
/// <param name="Path">Relative to the share, with its name as the last part.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">File or folder.</param>
/// <param name="Size">Bytes for files, 0 for folders, as it is now.</param>
/// <param name="Modified">Last change of the content as it is now, UTC.</param>
public sealed record SearchHit(long Share, string Path, string Name, FileKind Kind, long Size, DateTimeOffset Modified);

/// <param name="Hits">The best matches first: the whole name, then names that start with the search, then a word in the name, then the rest.</param>
/// <param name="Complete">False while a searched place has not been read completely once since it was added, so hits may be missing.</param>
/// <param name="More">Whether there are more matches than were returned; a longer search narrows them.</param>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, bool Complete, bool More);

/// <summary>Searches the names of the files and folders the user can reach in shared folders and their own home (D413).</summary>
public sealed class FileSearch(SearchIndex index, ShareService shares)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxQueryLength = 200;
    public const int MaxTerms = 8;

    // Matches checked on disk before the best ones are returned; a search with more asks for a narrower one.
    private const int CandidateCap = 400;

    public async Task<SearchResults> SearchAsync(ClaimsPrincipal user, string? query, int? limit, CancellationToken cancellationToken)
    {
        var terms = SearchRanking.Terms(query);
        if (terms.Count == 0)
        {
            return new SearchResults([], true, false);
        }
        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        // Shared folders and the caller's own home only: other users' homes and Apps stay out, also for administrators.
        var places = (await shares.ListAsync(user, cancellationToken))
            .Where(share => share.Kind is ShareKind.Share or ShareKind.Home && share.Unavailable is null)
            .Select(share => share.Id)
            .ToList();
        var candidates = index.Find(places, terms, CandidateCap, out var complete);
        var roots = new Dictionary<long, SafeFileHandle?>();
        try
        {
            var hits = new List<SearchHit>(size);
            var more = false;
            foreach (var (shareId, path) in candidates)
            {
                if (hits.Count == size)
                {
                    more = true;
                    break;
                }
                if (!roots.TryGetValue(shareId, out var root))
                {
                    roots[shareId] = root = await TryOpenRootAsync(user, shareId, cancellationToken);
                }
                if (root is not null && TryStat(root, path) is { Kind: FsKind.File or FsKind.Folder } stat)
                {
                    var entry = FileService.ToEntry(FsPath.Split(path).Name, stat);
                    hits.Add(new SearchHit(shareId, path, entry.Name, entry.Kind, entry.Size, entry.Modified));
                }
            }
            return new SearchResults(hits, complete, more || candidates.Count >= CandidateCap);
        }
        finally
        {
            foreach (var root in roots.Values)
            {
                root?.Dispose();
            }
        }
    }

    private async Task<SafeFileHandle?> TryOpenRootAsync(ClaimsPrincipal user, long shareId, CancellationToken cancellationToken)
    {
        try
        {
            return FileService.OpenRoot(await shares.ResolveAsync(user, shareId, write: false, cancellationToken));
        }
        catch (PithyxException)
        {
            return null;
        }
    }

    private static FsStat? TryStat(SafeFileHandle root, string path)
    {
        var (parent, name) = FsPath.Split(path);
        try
        {
            using var folder = FileService.Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
            return FileService.TryStat(folder, name);
        }
        catch (Exception error) when (error is PithyxException or FsError)
        {
            return null;
        }
    }
}

/// <summary>How a search is split into terms and how well a name matches them. Pure.</summary>
public static class SearchRanking
{
    /// <summary>The search's words as keys (composed, lower case), at most <see cref="FileSearch.MaxTerms"/>, in order and without repeats.</summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        var text = query.Trim();
        text = text.Length > FileSearch.MaxQueryLength ? text[..FileSearch.MaxQueryLength] : text;
        return [.. SearchStore.KeyOf(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Take(FileSearch.MaxTerms)];
    }

    /// <summary>
    /// 0 when the name (or the name without its extension) is the search, 1 when it starts with the first term, 2 when a
    /// word in it does, 3 for any other match.
    /// </summary>
    public static int Score(string name, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var key = SearchStore.KeyOf(name);
        var whole = string.Join(' ', terms);
        var dot = key.LastIndexOf('.');
        if (key == whole || (dot > 0 && key[..dot] == whole))
        {
            return 0;
        }
        var first = terms[0];
        if (key.StartsWith(first, StringComparison.Ordinal))
        {
            return 1;
        }
        for (var at = key.IndexOf(first, StringComparison.Ordinal); at > 0; at = key.IndexOf(first, at + 1, StringComparison.Ordinal))
        {
            if (!char.IsLetterOrDigit(key[at - 1]))
            {
                return 2;
            }
        }
        return 3;
    }
}
