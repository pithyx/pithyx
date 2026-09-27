namespace Vyxis.Daemon.Shares;

public enum ShareAccess
{
    None,
    Read,
    ReadWrite,
}

/// <summary>A share the user can use.</summary>
/// <param name="Id">Stable id; file paths are relative to the share.</param>
/// <param name="Name">Name shown to users.</param>
/// <param name="Access">The user's access: read or read and write.</param>
/// <param name="Path">The share root on the host. Only administrators get it.</param>
public sealed record ShareInfo(long Id, string Name, ShareAccess Access, string? Path);

/// <param name="Name">Letters, digits, spaces, dots, dashes and underscores, 1 to 64 characters, not starting with a dot.</param>
/// <param name="Path">An existing folder on the host: absolute, without symlinks, outside the system directories and not overlapping another share.</param>
public sealed record NewShare(string Name, string Path);

/// <param name="Access"><c>none</c> removes the user's access.</param>
public sealed record ShareAccessRequest(ShareAccess Access);

/// <summary>A share resolved for one request, with the caller's access.</summary>
public sealed record ResolvedShare(long Id, string Name, string Path, ShareAccess Access);
