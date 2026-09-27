using System.Text;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Files;

/// <summary>
/// Paths inside a share as clients send them: segments separated by <c>/</c>, relative to the share root, with an
/// optional leading slash. "." and ".." are refused instead of resolved, so a path means the same to every layer; the
/// kernel check in <see cref="LinuxFs.OpenBeneath"/> is the second line against symlinks.
/// </summary>
public static class FsPath
{
    public const int MaxBytes = 4096;
    public const int MaxNameBytes = 255;

    /// <summary>Hidden temporary files of uploads in progress start with this; listings never show them.</summary>
    public const string UploadPrefix = ".pithyx-upload-";

    /// <summary>The recycle bin folder at a share root. Paths never name it; only the bin's own calls reach it.</summary>
    public const string TrashFolder = ".pithyx-trash";

    /// <summary>The normalized form ("" for the root, otherwise "a/b/c"), or <c>fs.path.invalid</c>.</summary>
    public static string Normalize(string? path)
    {
        if (path is null)
        {
            return "";
        }
        if (path.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(path) > MaxBytes)
        {
            throw Invalid(path);
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (!IsName(segment))
            {
                throw Invalid(path);
            }
        }
        if (segments is [TrashFolder, ..])
        {
            throw Invalid(path);
        }
        return string.Join('/', segments);
    }

    /// <summary>Whether a single file or folder name is acceptable (no separator, not "." or "..", at most 255 bytes).</summary>
    public static bool IsName(string name) =>
        name.Length > 0
        && name is not ("." or "..")
        && !name.Contains('/', StringComparison.Ordinal)
        && !name.Contains('\0', StringComparison.Ordinal)
        && Encoding.UTF8.GetByteCount(name) <= MaxNameBytes;

    /// <summary>Checks a new name for a file or folder the user creates or renames to.</summary>
    public static string CheckName(string? name)
    {
        if (name is null || !IsName(name) || name.StartsWith(UploadPrefix, StringComparison.Ordinal) || name == TrashFolder || name.Any(char.IsControl))
        {
            throw new PithyxException(ErrorCodes.FsNameInvalid);
        }
        return name;
    }

    /// <summary>The parent ("" for top-level entries) and the name of a normalized, non-root path.</summary>
    public static (string Parent, string Name) Split(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? ("", normalized) : (normalized[..slash], normalized[(slash + 1)..]);
    }

    public static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}/{name}";

    /// <summary>What <see cref="LinuxFs.OpenBeneath"/> expects: "." for the root.</summary>
    public static string ForOpen(string normalized) => normalized.Length == 0 ? "." : normalized;

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> itself or inside it.</summary>
    public static bool IsSameOrInside(string path, string folder) =>
        folder.Length == 0 || path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static PithyxException Invalid(string path) =>
        new(ErrorCodes.FsPathInvalid, new Dictionary<string, object?> { ["maxBytes"] = MaxBytes });
}
