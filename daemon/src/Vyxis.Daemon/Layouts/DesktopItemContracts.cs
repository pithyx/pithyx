namespace Vyxis.Daemon.Layouts;

public enum DesktopItemKind
{
    Shortcut,
    Note,
}

/// <summary>An app shortcut or a sticky note on the desktop, shared across the user's devices.</summary>
/// <param name="Id">Stable id of the item.</param>
/// <param name="Kind">What the item is.</param>
/// <param name="App">Shortcuts: the id of the app it opens.</param>
/// <param name="Name">Shortcuts: the label the user gave it, or null for the app's name in the user's language.</param>
/// <param name="Text">Notes: the text.</param>
/// <param name="Column">Grid cell of the item, counted from the top left corner of the desktop.</param>
/// <param name="Row">Grid row of the item.</param>
/// <param name="Version">Increments with every change. Send it with the next change of this item.</param>
public sealed record DesktopItem(long Id, DesktopItemKind Kind, string? App, string? Name, string? Text, int Column, int Row, long Version);

/// <param name="Kind">What to create.</param>
/// <param name="App">Shortcuts: the id of the app to open. Required for shortcuts, not allowed for notes.</param>
/// <param name="Name">Shortcuts: an own label, or null for the app's name.</param>
/// <param name="Text">Notes: the text, at most 4000 characters. Required for notes, not allowed for shortcuts.</param>
/// <param name="Column">Grid column.</param>
/// <param name="Row">Grid row.</param>
public sealed record NewDesktopItem(DesktopItemKind Kind, int Column, int Row, string? App = null, string? Name = null, string? Text = null);

/// <summary>A change of one item. Fields left out (or null) stay as they are.</summary>
/// <param name="Id">The item to change.</param>
/// <param name="Version">The version the change builds on. A different current version fails the whole request with 412.</param>
/// <param name="Name">Shortcuts: the new label; an empty string goes back to the app's name.</param>
/// <param name="Text">Notes: the new text.</param>
/// <param name="Column">New grid column.</param>
/// <param name="Row">New grid row.</param>
public sealed record DesktopItemChange(long Id, long Version, string? Name = null, string? Text = null, int? Column = null, int? Row = null);

/// <param name="Ids">Items to delete. Ids that do not exist (any more) are ignored.</param>
public sealed record DeleteDesktopItemsRequest(IReadOnlyList<long> Ids);
