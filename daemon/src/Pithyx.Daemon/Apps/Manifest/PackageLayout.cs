using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>
/// The file layout of an app package (D301): <c>pithyx.json</c> and <c>icon.svg</c> at the root (next to README,
/// LICENSE and checksums), the entry under <c>frontend/</c>, and files under <c>backend/</c> only with a backend, whose
/// Compose file must be there. Paths are relative, with <c>/</c> as the separator, and use ASCII letters, digits and
/// <c>._~@+-</c> only, so no two names can mean the same file on any file system; the list holds the regular files of
/// the package only. <c>frontend/api</c> and <c>frontend/api-*</c> are reserved: their URLs belong to the backend (D303).
/// </summary>
public static partial class PackageLayout
{
    public const string ManifestFile = "pithyx.json";
    public const string IconFile = "icon.svg";
    public const string FrontendFolder = "frontend";
    public const string BackendFolder = "backend";

    public const int MaxPathLength = 512;
    public const int MaxSegmentLength = 255;
    public const int MaxDepth = 32;

    /// <summary>Checks the file paths of a package against the manifest's <paramref name="entry"/> and <paramref name="compose"/> file.</summary>
    public static IReadOnlyList<ValidationIssue> Check(IEnumerable<string> paths, string entry, string? compose)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Two names that differ only in case are one file on a case-insensitive client, and a file is in the way of a
        // folder of the same name.
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (!IsSafe(path))
            {
                issues.Add(Issue(path, "unsafe"));
            }
            else if (!seen.Add(path) || !folded.Add(path))
            {
                issues.Add(Issue(path, "duplicate"));
            }
            else
            {
                files.Add(path);
                if (PlacementProblem(path, compose is not null) is { } reason)
                {
                    issues.Add(Issue(path, reason));
                }
            }
        }
        var inTheWay = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                if (folded.TryGetValue(path[..slash], out var file) && inTheWay.Add(file))
                {
                    issues.Add(Issue(file, "duplicate"));
                }
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

    /// <summary>
    /// Whether <paramref name="path"/> stays inside the package wherever it is unpacked and names exactly one file: at
    /// most <see cref="MaxDepth"/> segments of ASCII letters, digits and <c>._~@+-</c>, none empty or starting with a dot.
    /// </summary>
    public static bool IsSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
        {
            return false;
        }
        var segments = path.Split('/');
        return segments.Length <= MaxDepth && segments.All(IsSafeSegment);
    }

    /// <summary>Whether one path segment is allowed (see <see cref="IsSafe"/>).</summary>
    public static bool IsSafeSegment(string segment) => segment.Length <= MaxSegmentLength && Segment().IsMatch(segment);

    /// <summary>Whether a path below <c>frontend/</c> lies in the URLs of the backend (<c>api</c>, <c>api-&lt;endpoint&gt;</c>).</summary>
    public static bool IsReservedFrontendPath(string relative)
    {
        var first = relative.Split('/')[0];
        return first == "api" || first.StartsWith("api-", StringComparison.Ordinal);
    }

    private static string? PlacementProblem(string path, bool hasBackend)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }
        return path[..slash] switch
        {
            FrontendFolder => IsReservedFrontendPath(path[(slash + 1)..]) ? "reserved" : null,
            BackendFolder => hasBackend ? null : "unexpected",
            _ => "unexpected",
        };
    }

    private static ValidationIssue Issue(string path, string reason) =>
        new(ErrorCodes.AppPackageLayoutInvalid, ("path", path), ("reason", reason));

    [GeneratedRegex("^[A-Za-z0-9_~@+-][A-Za-z0-9._~@+-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();
}
