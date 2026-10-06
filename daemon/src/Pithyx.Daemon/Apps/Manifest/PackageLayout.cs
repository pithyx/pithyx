using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>
/// The file layout of an app package (D301): <c>pithyx.json</c> and <c>icon.svg</c> at the root, the entry under
/// <c>frontend/</c>, and files under <c>backend/</c> only with a backend, whose Compose file must be there. Paths are
/// relative, with <c>/</c> as the separator; the list holds the regular files of the package only.
/// </summary>
public static class PackageLayout
{
    public const string ManifestFile = "pithyx.json";
    public const string IconFile = "icon.svg";

    /// <summary>Checks the file paths of a package against the manifest's <paramref name="entry"/> and <paramref name="compose"/> file.</summary>
    public static IReadOnlyList<ValidationIssue> Check(IEnumerable<string> paths, string entry, string? compose)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!IsSafe(path))
            {
                issues.Add(Issue(path, "unsafe"));
            }
            else if (!seen.Add(path))
            {
                issues.Add(Issue(path, "duplicate"));
            }
            else if (compose is null && path.StartsWith("backend/", StringComparison.Ordinal))
            {
                issues.Add(Issue(path, "unexpected"));
            }
        }
        foreach (var required in new[] { ManifestFile, IconFile, entry, compose })
        {
            if (required is not null && !seen.Contains(required))
            {
                issues.Add(Issue(required, "missing"));
            }
        }
        return issues;
    }

    /// <summary>Whether <paramref name="path"/> stays inside the package wherever it is unpacked.</summary>
    public static bool IsSafe(string path) =>
        path.Length > 0
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Any(character => character < ' ' || character == '\u007f')
        && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static ValidationIssue Issue(string path, string reason) =>
        new(ErrorCodes.AppPackageLayoutInvalid, ("path", path), ("reason", reason));
}
