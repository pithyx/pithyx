using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>One image for one architecture: the platform manifest's digest and the config digest, which is the image id.</summary>
public sealed record LockedImage(string Manifest, string Config);

/// <summary>
/// <c>backend/images.lock.json</c> of a package (D315, schema <c>sdk/schema/images-lock.v1.schema.json</c>): every image
/// reference of the Compose file, as written, with its image per architecture. The box loads or pulls an image only by
/// these digests and checks the id it gets against <see cref="LockedImage.Config"/>.
/// </summary>
public sealed partial class ImagesLock
{
    public const string FileName = "backend/images.lock.json";

    private ImagesLock(IReadOnlyDictionary<string, IReadOnlyDictionary<string, LockedImage>> images) => Images = images;

    /// <summary>The images by reference, then by architecture (<c>amd64</c>, <c>arm64</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, LockedImage>> Images { get; }

    /// <summary>The architecture of this box as the lock and Docker name it, or null on an architecture Pithyx does not run on.</summary>
    public static string? HostArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    /// <summary>The lock, or null when the bytes are not a lock of version 1 with exactly the schema's shape.</summary>
    public static ImagesLock? Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !OnlyKeys(root, "lockVersion", "images")
                || !root.TryGetProperty("lockVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1"
                || !root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var parsed = new SortedDictionary<string, IReadOnlyDictionary<string, LockedImage>>(StringComparer.Ordinal);
            foreach (var image in images.EnumerateObject())
            {
                if (image.Name.Length > 512 || !Reference().IsMatch(image.Name) || image.Value.ValueKind != JsonValueKind.Object || !OnlyKeys(image.Value, "amd64", "arm64"))
                {
                    return null;
                }
                var platforms = new SortedDictionary<string, LockedImage>(StringComparer.Ordinal);
                foreach (var platform in image.Value.EnumerateObject())
                {
                    if (platform.Value.ValueKind != JsonValueKind.Object || !OnlyKeys(platform.Value, "manifest", "config")
                        || Digest(platform.Value, "manifest") is not { } manifest || Digest(platform.Value, "config") is not { } config)
                    {
                        return null;
                    }
                    platforms[platform.Name] = new LockedImage(manifest, config);
                }
                if (platforms.Count == 0)
                {
                    return null;
                }
                parsed[image.Name] = platforms;
            }
            return parsed.Count is >= 1 and <= 16 ? new ImagesLock(parsed) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where pulling a reference by digest goes: its name without the tag and the platform manifest's digest (a tag is
    /// only a label once the reference is pinned).
    /// </summary>
    public static string PullReference(string reference, LockedImage image)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(image);
        var name = reference[..reference.LastIndexOf('@')];
        var tag = name.LastIndexOf(':');
        if (tag > name.LastIndexOf('/'))
        {
            name = name[..tag];
        }
        return $"{name}@{image.Manifest}";
    }

    /// <summary>The problem of a lock that does not fit: <c>missing</c>, <c>invalid</c>, <c>stale</c> or <c>platform</c>.</summary>
    public static Errors.PithyxException Invalid(string reason) =>
        new(Errors.ErrorCodes.AppPackageLockInvalid, new Dictionary<string, object?> { ["reason"] = reason, ["architecture"] = HostArchitecture ?? "" });

    /// <summary>The image ids (config digests) a lock names for this box's architecture; empty for bytes that are no lock.</summary>
    public static IReadOnlySet<string> HostImages(byte[]? utf8) =>
        utf8 is not null && Parse(utf8) is { } parsed && HostArchitecture is { } architecture
            ? parsed.Images.Values.Where(platforms => platforms.ContainsKey(architecture)).Select(platforms => platforms[architecture].Config).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static bool OnlyKeys(JsonElement element, params string[] allowed) =>
        element.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));

    private static string? Digest(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && DigestPattern().IsMatch(value.GetString()!) ? value.GetString() : null;

    [GeneratedRegex(@"^\S+@sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    [GeneratedRegex(@"^sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
