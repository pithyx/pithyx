namespace Pithyx.Daemon.Files.Thumbnails;

/// <summary>Body of <c>POST /api/files/{shareId}/thumbnails</c>.</summary>
/// <param name="Folder">The folder shown, relative to the share.</param>
/// <param name="Names">Entries of that folder on screen, at most 200.</param>
/// <param name="Size"><c>small</c> (64 px, list rows) or <c>large</c> (256 px, grid tiles).</param>
public sealed record ThumbnailRequest(string? Folder, IReadOnlyList<string> Names, ThumbnailSize Size);

/// <summary>Whether a thumbnail can be shown now.</summary>
public enum ThumbnailStatus
{
    /// <summary>Cached: load it with <c>GET /api/files/{shareId}/thumbnail</c> and the version.</summary>
    Ready,

    /// <summary>Queued: a <c>thumbnails/&lt;share&gt;/&lt;folder&gt;</c> event says when it is ready or refused.</summary>
    Pending,

    /// <summary>No thumbnail: not an image the daemon renders, too large or broken. Show the file's icon.</summary>
    None,
}

/// <param name="Name">The entry.</param>
/// <param name="Status">Whether it has a thumbnail.</param>
/// <param name="Version">For <c>ready</c>: changes whenever the file changes; pass it as <c>v</c> so a new version is loaded.</param>
public sealed record ThumbnailState(string Name, ThumbnailStatus Status, string? Version);

/// <summary>Response of <c>POST /api/files/{shareId}/thumbnails</c>. Nothing was rendered while answering.</summary>
/// <param name="Topic">Subscribe to it for the thumbnails that are still pending.</param>
/// <param name="Items">One state per distinct name asked for.</param>
public sealed record ThumbnailStates(string Topic, IReadOnlyList<ThumbnailState> Items);

/// <summary>Event of <c>thumbnails/&lt;share&gt;/&lt;folder&gt;</c>: thumbnails that were rendered or refused.</summary>
/// <param name="Items">Each with <c>ready</c> and its version, or <c>none</c>.</param>
/// <param name="Size">Which size they are.</param>
public sealed record ThumbnailsReady(IReadOnlyList<ThumbnailState> Items, ThumbnailSize Size);

/// <summary>Snapshot of a thumbnail topic; the events carry everything.</summary>
/// <param name="Watching">Always true.</param>
public sealed record ThumbnailWatch(bool Watching);
