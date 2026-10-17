using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// Brotli and gzip copies of an app's frontend files, written once per installed version into
/// <c>versions/&lt;version&gt;/.encoded/</c> (a dot folder, so no package path can name it) and served in place of the
/// file when the browser accepts them (N14). The box compresses what the package holds; it still never builds. A version
/// installed before this existed gets its copies in the background at its first request.
/// </summary>
public static partial class AppFrontendEncodings
{
    public const string Folder = ".encoded";

    /// <summary>The encodings in the order the daemon prefers them, with the extension of their copies.</summary>
    public static readonly (string Token, string Extension)[] Encodings = [("br", ".br"), ("gzip", ".gz")];

    private const int MinimumSize = 1024;

    private static readonly HashSet<string> _compressible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".html", ".htm", ".json", ".map", ".svg", ".txt", ".xml", ".wasm", ".webmanifest",
    };

    private static readonly ConcurrentDictionary<string, Task> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Writes the copies for every compressible file of <paramref name="versionFolder"/>'s frontend. A copy is kept only
    /// when it saves at least a tenth; the folder appears complete or not at all.
    /// </summary>
    public static void Write(string versionFolder, CancellationToken cancellationToken)
    {
        var frontend = Path.Combine(versionFolder, Manifest.PackageLayout.FrontendFolder);
        var target = Path.Combine(versionFolder, Folder);
        if (!Directory.Exists(frontend) || Directory.Exists(target))
        {
            return;
        }
        var incoming = Path.Combine(versionFolder, Folder + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(incoming);
            foreach (var file in new DirectoryInfo(frontend).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.LinkTarget is not null || file.Length < MinimumSize || !_compressible.Contains(file.Extension))
                {
                    continue;
                }
                var original = File.ReadAllBytes(file.FullName);
                var relative = Path.GetRelativePath(frontend, file.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(incoming, relative))!);
                foreach (var (_, extension) in Encodings)
                {
                    var compressed = Compress(original, extension);
                    if (compressed.Length <= original.Length * 9L / 10)
                    {
                        File.WriteAllBytes(Path.Combine(incoming, relative + extension), compressed);
                    }
                }
            }
            Directory.Move(incoming, target);
        }
        finally
        {
            if (Directory.Exists(incoming))
            {
                Directory.Delete(incoming, recursive: true);
            }
        }
    }

    /// <summary>
    /// The copy of <paramref name="path"/> (below the frontend) in the best encoding <paramref name="acceptEncoding"/>
    /// allows, or null. A version without copies yet gets them in the background, so this answer stays uncompressed.
    /// </summary>
    public static (FileInfo File, string Encoding)? Find(string frontend, string path, string acceptEncoding, Action<Exception>? failed = null)
    {
        var versionFolder = Path.GetDirectoryName(frontend)!;
        var folder = Path.Combine(versionFolder, Folder);
        if (!Directory.Exists(folder))
        {
            _pending.GetOrAdd(versionFolder, key => Task.Run(() =>
            {
                try
                {
                    Write(key, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The version may go while it is compressed (an update or an uninstall); the next request tries again.
                    failed?.Invoke(exception);
                }
                finally
                {
                    _pending.TryRemove(key, out _);
                }
            }));
            return null;
        }
        foreach (var (token, extension) in Encodings)
        {
            if (Accepts(acceptEncoding, token) && new FileInfo(Path.Combine(folder, path + extension)) is { Exists: true, LinkTarget: null } copy)
            {
                return (copy, token);
            }
        }
        return null;
    }

    /// <summary>Whether an <c>Accept-Encoding</c> value allows <paramref name="token"/> (named, or <c>*</c>, without <c>q=0</c>).</summary>
    public static bool Accepts(string acceptEncoding, string token)
    {
        ArgumentNullException.ThrowIfNull(acceptEncoding);
        bool? named = null;
        var any = false;
        foreach (var item in acceptEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(';', StringSplitOptions.TrimEntries);
            var allowed = !parts.Skip(1).Any(parameter => ZeroQuality().IsMatch(parameter));
            if (parts[0].Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                named = allowed;
            }
            else if (parts[0] == "*")
            {
                any = allowed;
            }
        }
        return named ?? any;
    }

    /// <summary>
    /// Whether a frontend file never changes under its name, so it may be cached for a year: a file under
    /// <c>assets/</c> whose name ends in a build hash as Vite writes it (<c>index-B4kz_x9Q.js</c>, eight characters of
    /// base64url with at least one that is not a lowercase letter, so a plain word is never taken for a hash).
    /// </summary>
    public static bool IsHashed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith("assets/", StringComparison.Ordinal))
        {
            return false;
        }
        var match = HashedName().Match(path[(path.LastIndexOf('/') + 1)..]);
        return match.Success && match.Groups["hash"].Value.Any(character => character is not (>= 'a' and <= 'z'));
    }

    private static byte[] Compress(byte[] original, string extension)
    {
        using var output = new MemoryStream();
        using (Stream encoder = extension == ".br"
            ? new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            encoder.Write(original);
        }
        return output.ToArray();
    }

    [GeneratedRegex(@"^q\s*=\s*0(\.0{0,3})?$", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroQuality();

    [GeneratedRegex(@"^.+-(?<hash>[A-Za-z0-9_-]{8})\.[A-Za-z0-9]+$")]
    private static partial Regex HashedName();
}
