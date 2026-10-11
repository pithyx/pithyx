using Pithyx.Daemon.Apps.Catalog;

namespace Pithyx.Daemon.Updates;

/// <summary>
/// The keys whose signatures on update files and channels a box accepts (D338), the same as
/// <c>tools/release/keys.json</c>: <c>pithyx-release-1</c> (the owner's) signs stable and beta, <c>pithyx-release-2</c> is
/// its offline reserve for the same channels, <c>pithyx-beta-1</c> (on the maintainer's dev machine) signs only beta.
/// </summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<TrustedKey> Project =
    [
        new("pithyx-release-1", "X86BspTUgM8UdRY2ac31eIq2l7bRduf0leSj5x3+pZI=", [UpdateDocuments.Stable, UpdateDocuments.Beta]),
        new("pithyx-release-2", "FIvJOrvmVyraaU7ZbULv5RPD/auz4V4V+k4Rgg4W6pY=", [UpdateDocuments.Stable, UpdateDocuments.Beta]),
        new("pithyx-beta-1", "GUjkHR+XBQRaeqK47HMYVuhXwbXEb64gUPs7pGYL/9k=", [UpdateDocuments.Beta]),
    ];
}
