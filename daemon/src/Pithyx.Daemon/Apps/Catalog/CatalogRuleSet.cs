using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary><c>sdk/schema/catalog.v1.rules.json</c> as read by the daemon.</summary>
internal sealed class CatalogRuleSet
{
    private CatalogRuleSet(JsonElement root)
    {
        AppsFolder = root.GetProperty("appsFolder").GetString()!;
        var reserved = root.GetProperty("reservedIds");
        ReservedPrefix = reserved.GetProperty("prefix").GetString()!;
        ReservedCatalogs = Strings(reserved.GetProperty("catalogs"));
        var limits = root.GetProperty("limits");
        AppBytes = limits.GetProperty("appBytes").GetInt64();
        LineChars = limits.GetProperty("lineChars").GetInt32();
        Screenshots = limits.GetProperty("screenshots").GetInt32();
        ScreenshotBytes = limits.GetProperty("screenshotBytes").GetInt64();
        RequiredFiles = [.. root.GetProperty("requiredFiles").EnumerateArray().Select(Strings)];
        ManifestFields = Strings(root.GetProperty("manifestFields"));
        var text = root.GetProperty("text");
        TextNames = new HashSet<string>(Strings(text.GetProperty("names")), StringComparer.Ordinal);
        TextExtensions = new HashSet<string>(Strings(text.GetProperty("extensions")), StringComparer.Ordinal);
        LongLines = new HashSet<string>(Strings(text.GetProperty("longLines")), StringComparer.Ordinal);
        Dockerfile = Pattern(root.GetProperty("dockerfile"));
        Minified = Pattern(root.GetProperty("minified"));
        Media = root.GetProperty("media").EnumerateObject().ToDictionary(
            kind => kind.Name,
            kind => (IReadOnlyList<(int Offset, byte[] Bytes)>)[.. kind.Value.GetProperty("magic").EnumerateArray().Select(magic =>
                (magic.GetProperty("offset").GetInt32(), Convert.FromHexString(magic.GetProperty("hex").GetString()!)))],
            StringComparer.Ordinal);
        var screenshots = root.GetProperty("screenshots");
        ScreenshotFolder = screenshots.GetProperty("folder").GetString()!;
        ScreenshotName = Pattern(screenshots.GetProperty("name"));
        var licenses = root.GetProperty("licenses");
        OsiLicenses = new HashSet<string>(Strings(licenses.GetProperty("osi")).Select(id => id.ToLowerInvariant()), StringComparer.Ordinal);
        LicenseExceptions = new HashSet<string>(Strings(licenses.GetProperty("exceptions")).Select(id => id.ToLowerInvariant()), StringComparer.Ordinal);
    }

    public string AppsFolder { get; }

    public string ReservedPrefix { get; }

    public IReadOnlyList<string> ReservedCatalogs { get; }

    public long AppBytes { get; }

    public int LineChars { get; }

    public int Screenshots { get; }

    public long ScreenshotBytes { get; }

    /// <summary>Each entry lists the names of one required file; any of them will do, the first is reported.</summary>
    public IReadOnlyList<IReadOnlyList<string>> RequiredFiles { get; }

    public IReadOnlyList<string> ManifestFields { get; }

    public IReadOnlySet<string> TextNames { get; }

    public IReadOnlySet<string> TextExtensions { get; }

    public IReadOnlySet<string> LongLines { get; }

    public Regex Dockerfile { get; }

    public Regex Minified { get; }

    /// <summary>The magic bytes a media file starts with, by extension.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(int Offset, byte[] Bytes)>> Media { get; }

    public string ScreenshotFolder { get; }

    public Regex ScreenshotName { get; }

    /// <summary>OSI approved SPDX ids in lower case.</summary>
    public IReadOnlySet<string> OsiLicenses { get; }

    /// <summary>SPDX exception ids in lower case.</summary>
    public IReadOnlySet<string> LicenseExceptions { get; }

    public static CatalogRuleSet Load()
    {
        using var stream = typeof(CatalogRuleSet).Assembly.GetManifestResourceStream("schema/catalog.v1.rules.json")
            ?? throw new InvalidOperationException("The catalog rules are not embedded.");
        using var document = JsonDocument.Parse(stream);
        return new CatalogRuleSet(document.RootElement);
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    // .NET's $ also matches before a final line break, ECMAScript's only at the end; (?!\n) keeps both engines alike.
    private static Regex Pattern(JsonElement pattern) => new(pattern.GetString()! + "(?!\\n)", RegexOptions.CultureInvariant);
}
