using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>
/// App ids (D302): the author's domain reversed plus the app name, 3 to 5 parts of lowercase letters, digits and
/// dashes, at most 100 characters, for example <c>com.mueller.chat</c>. The manifest schema
/// (<c>sdk/schema/manifest.v1.schema.json</c>) carries the same pattern; the shared fixture corpus keeps both alike.
/// </summary>
public static partial class AppId
{
    public const int MaxLength = 100;

    /// <summary>The namespace of the system apps and the official catalog; never accepted from a sideload.</summary>
    public const string ReservedPrefix = "org.pithyx.";

    /// <summary>Whether <paramref name="id"/> is an app id of D302.</summary>
    public static bool IsValid(string? id) => id is { Length: <= MaxLength } && Pattern().IsMatch(id);

    /// <summary>Whether a valid id lies in the reserved namespace <c>org.pithyx.*</c>.</summary>
    public static bool IsReserved(string id) => id.StartsWith(ReservedPrefix, StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*){2,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
