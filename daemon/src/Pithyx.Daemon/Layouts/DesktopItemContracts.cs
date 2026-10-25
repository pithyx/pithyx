namespace Pithyx.Daemon.Layouts;

public enum DesktopItemKind
{
    Shortcut,
    Note,
    File,
}

/// <summary>What a shortcut to a file or folder points at now, for the user who lists it.</summary>
public enum DesktopFileTarget
{
    File,
    Folder,
    /// <summary>Gone, moved past the daemon, or no longer readable for the user.</summary>
    Missing,
}

/// <summary>An app shortcut, a shortcut to a file or folder, or a sticky note on the desktop, shared across the user's devices.</summary>
/// <param name="Id">Stable id of the item.</param>
/// <param name="Kind">What the item is.</param>
/// <param name="App">Shortcuts: the id of the app it opens.</param>
/// <param name="Name">Shortcuts and files: the label the user gave it, or null for the app's or the target's own name.</param>
/// <param name="Text">Notes: the text.</param>
/// <param name="Column">Grid cell of the item, counted from the top left corner of the desktop.</param>
/// <param name="Row">Grid row of the item.</param>
/// <param name="Version">Increments with every change. Send it as <c>If-Match: "n"</c> with the next change of this item.</param>
/// <param name="Share">Files: the shared folder or home of the target.</param>
/// <param name="Path">Files: the target's path in the share; it follows renames and moves made through the daemon.</param>
/// <param name="Target">Files: whether the target is a file or a folder, or missing for this user.</param>
public sealed record DesktopItem(
    long Id,
    DesktopItemKind Kind,
    string? App,
    string? Name,
    string? Text,
    int Column,
    int Row,
    long Version,
    long? Share = null,
    string? Path = null,
    DesktopFileTarget? Target = null);

/// <param name="Kind">What to create.</param>
/// <param name="App">Shortcuts: the id of the app to open. Required for shortcuts, not allowed otherwise.</param>
/// <param name="Name">Shortcuts and files: an own label, or null for the app's or the target's name.</param>
/// <param name="Text">Notes: the text, at most 4000 characters. Required for notes, not allowed otherwise.</param>
/// <param name="Column">Grid column.</param>
/// <param name="Row">Grid row.</param>
/// <param name="Share">Files: the shared folder or home of the target. Required for files, not allowed otherwise.</param>
/// <param name="Path">Files: the target's path in the share; the user must be able to read it.</param>
public sealed record NewDesktopItem(DesktopItemKind Kind, int Column, int Row, string? App = null, string? Name = null, string? Text = null, long? Share = null, string? Path = null);

/// <summary>A change of one item. Fields left out (or null) stay as they are.</summary>
/// <param name="Name">Shortcuts and files: the new label; an empty string goes back to the app's or the target's name.</param>
/// <param name="Text">Notes: the new text.</param>
/// <param name="Column">New grid column.</param>
/// <param name="Row">New grid row.</param>
public sealed record DesktopItemChange(string? Name = null, string? Text = null, int? Column = null, int? Row = null);
