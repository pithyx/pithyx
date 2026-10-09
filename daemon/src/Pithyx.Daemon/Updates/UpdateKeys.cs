using Pithyx.Daemon.Apps.Catalog;

namespace Pithyx.Daemon.Updates;

/// <summary>
/// The keys whose signatures on update files and channels a box accepts (D338), the same as
/// <c>tools/release/keys.json</c>: <c>pithyx-release-1</c> (the owner's) signs stable and beta, <c>pithyx-beta-1</c>
/// (on dev01) only beta.
/// </summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<TrustedKey> Project =
    [
        new("pithyx-beta-1", "GUjkHR+XBQRaeqK47HMYVuhXwbXEb64gUPs7pGYL/9k=", [UpdateDocuments.Beta]),
    ];
}
