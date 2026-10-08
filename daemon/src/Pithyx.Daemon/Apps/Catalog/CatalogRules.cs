using System.Text;
using System.Text.Unicode;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>A pull request to a catalog repository, as the checks see it.</summary>
/// <param name="Catalog">The <c>id</c> of the repository's <c>catalog.json</c>.</param>
/// <param name="Author">The GitHub login of the pull request's author.</param>
/// <param name="Changed">Every path the pull request adds, changes or deletes, relative to the repository root.</param>
/// <param name="Files">The files of the app's folder after the pull request, by path relative to the repository root.</param>
/// <param name="Published">The versions of the app the catalog has published, empty for a new app.</param>
/// <param name="Maintainers">The app's <c>maintainers.json</c> before the pull request; null for a new app.</param>
public sealed record CatalogPullRequest(
    string Catalog,
    string Author,
    IReadOnlyList<string> Changed,
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlyList<string> Published,
    IReadOnlyList<string>? Maintainers = null);

/// <summary>
/// Checks a pull request to a catalog repository against <c>sdk/schema/catalog.v1.rules.json</c> (embedded at build),
/// with the same verdicts and codes as <c>@pithyx/cli</c> (the corpus in <c>sdk/schema/fixtures/catalog/rules</c> holds
/// both alike): one app folder equal to the app's id, the required files, only text and media files of the allowed
/// kinds, no binaries or minified code, every image of a Dockerfile pinned, the size limits, an OSI license, the required
/// manifest fields, a version above every published one, a valid manifest, icon and <c>compose.dev.yaml</c>, and an
/// author from <c>maintainers.json</c>.
/// </summary>
public static class CatalogRules
{
    private static readonly Lazy<CatalogRuleSet> _rules = new(CatalogRuleSet.Load);

    private static readonly Lazy<JsonSchemaCheck> _maintainersSchema = new(() => JsonSchemaCheck.Load("maintainers.v1.schema.json"));

    internal static CatalogRuleSet Rules => _rules.Value;

    public static IReadOnlyList<ValidationIssue> CheckPullRequest(CatalogPullRequest request)
    {
        var rules = Rules;
        var issues = new List<ValidationIssue>();
        var prefix = $"{rules.AppsFolder}/";
        var folders = new List<string>();
        foreach (var path in request.Changed.Order(StringComparer.Ordinal))
        {
            var folder = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..].Split('/') : [];
            if (folder.Length < 2 || folder[0].Length == 0)
            {
                issues.Add(Violation("one_app", ("path", path)));
            }
            else if (!folders.Contains(folder[0]))
            {
                folders.Add(folder[0]);
            }
        }
        if (folders.Count != 1)
        {
            issues.AddRange(folders.Select(folder => Violation("one_app", ("path", $"{prefix}{folder}"))));
            return issues;
        }
        var appFolder = folders[0];
        var root = $"{prefix}{appFolder}/";

        var tree = request.Files
            .Where(file => file.Key.StartsWith(root, StringComparison.Ordinal))
            .Select(file => (Path: file.Key[root.Length..], Bytes: file.Value))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var files = tree.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
        foreach (var alternatives in rules.RequiredFiles)
        {
            if (!alternatives.Any(files.ContainsKey))
            {
                issues.Add(Violation("required_file", ("path", $"{root}{(alternatives.Count > 0 ? alternatives[0] : "")}")));
            }
        }

        long size = 0;
        var screenshots = 0;
        var screenshotPrefix = $"{rules.ScreenshotFolder}/";
        foreach (var (relative, bytes) in tree)
        {
            size += bytes.Length;
            issues.AddRange(FileIssues(rules, $"{root}{relative}", relative, bytes));
            if (relative.StartsWith(screenshotPrefix, StringComparison.Ordinal))
            {
                screenshots++;
                var name = relative[screenshotPrefix.Length..];
                if (!rules.ScreenshotName.IsMatch(name) || bytes.Length > rules.ScreenshotBytes)
                {
                    issues.Add(Violation("screenshot", ("path", $"{root}{relative}")));
                }
            }
        }
        if (screenshots > rules.Screenshots)
        {
            issues.Add(Violation("screenshots", ("path", $"{root}{rules.ScreenshotFolder}"), ("limit", rules.Screenshots)));
        }
        if (size > rules.AppBytes)
        {
            issues.Add(Violation("app_size", ("path", root[..^1]), ("size", size), ("limit", rules.AppBytes)));
        }

        if (files.TryGetValue("pithyx.json", out var manifest))
        {
            issues.AddRange(ManifestIssues(rules, request, root, appFolder, manifest, files));
        }
        if (files.TryGetValue("icon.svg", out var icon))
        {
            issues.AddRange(IconValidator.Validate(icon));
        }
        issues.AddRange(MaintainerIssues(request, root, files.GetValueOrDefault("maintainers.json")));
        return issues;
    }

    private static ValidationIssue Violation(string rule, params (string Name, object? Value)[] parameters) =>
        new(ErrorCodes.StoreCatalogRuleViolation, [("rule", rule), .. parameters]);

    private static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    private static List<ValidationIssue> FileIssues(CatalogRuleSet rules, string path, string relative, byte[] bytes)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        var segments = relative.Split('/');
        var hidden = segments.Where((segment, index) =>
            segment.StartsWith('.') && !(index == segments.Length - 1 && rules.TextNames.Contains(name))).Any();
        if (hidden)
        {
            return [Violation("file_type", ("path", path))];
        }
        var extension = Extension(name);
        if (rules.Dockerfile.IsMatch(name) || rules.TextNames.Contains(name) || rules.TextExtensions.Contains(extension))
        {
            return TextIssues(rules, path, name, extension, bytes);
        }
        if (rules.Media.TryGetValue(extension, out var magic))
        {
            var matches = magic.All(part => bytes.Length >= part.Offset + part.Bytes.Length && bytes.AsSpan(part.Offset, part.Bytes.Length).SequenceEqual(part.Bytes));
            return matches ? [] : [Violation("media", ("path", path))];
        }
        return [Violation("file_type", ("path", path))];
    }

    private static List<ValidationIssue> TextIssues(CatalogRuleSet rules, string path, string name, string extension, byte[] bytes)
    {
        if (!Utf8.IsValid(bytes))
        {
            return [Violation("binary", ("path", path))];
        }
        // A decoder drops the byte order mark, as the CLI's TextDecoder does.
        var text = Encoding.UTF8.GetString(bytes.AsSpan().StartsWith("\uFEFF"u8) ? bytes.AsSpan(3) : bytes);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return [Violation("binary", ("path", path))];
        }
        if (rules.Minified.IsMatch(name))
        {
            return [Violation("minified", ("path", path))];
        }
        var issues = new List<ValidationIssue>();
        if (!rules.LongLines.Contains(extension))
        {
            // Lengths in UTF-16 code units, as JavaScript counts them.
            var longLine = Array.FindIndex(text.Split('\n'), line => line.Length > rules.LineChars);
            if (longLine >= 0)
            {
                issues.Add(Violation("minified", ("path", path), ("line", longLine + 1)));
            }
        }
        if (rules.Dockerfile.IsMatch(name))
        {
            issues.AddRange(DockerfileImages.Unpinned(text).Select(image => Violation("unpinned", ("path", path), ("line", image.Line), ("image", image.Image))));
        }
        return issues;
    }

    private static bool LicenseAccepted(CatalogRuleSet rules, string expression) =>
        SpdxExpression.Parse(expression) is { } parsed
        && parsed.Licenses.All(license => rules.OsiLicenses.Contains((license.EndsWith('+') ? license[..^1] : license).ToLowerInvariant()))
        && parsed.Exceptions.All(exception => rules.LicenseExceptions.Contains(exception.ToLowerInvariant()));

    private static IReadOnlyList<ValidationIssue> ManifestIssues(
        CatalogRuleSet rules,
        CatalogPullRequest request,
        string root,
        string folder,
        byte[] bytes,
        Dictionary<string, byte[]> files)
    {
        var path = $"{root}pithyx.json";
        var result = ManifestValidator.Validate(bytes, CatalogValidator.MayUseReservedIds(request.Catalog));
        if (result.Manifest is not { } manifest)
        {
            return result.Issues;
        }
        var issues = new List<ValidationIssue>();
        var id = manifest.GetProperty("id").GetString()!;
        if (id != folder)
        {
            issues.Add(Violation("folder_id", ("path", path), ("id", id)));
        }
        foreach (var field in rules.ManifestFields.Where(field => !manifest.TryGetProperty(field, out _)))
        {
            issues.Add(Violation("manifest_field", ("path", path), ("field", field)));
        }
        if (manifest.TryGetProperty("license", out var license) && !LicenseAccepted(rules, license.GetString()!))
        {
            issues.Add(Violation("license", ("path", path), ("license", license.GetString())));
        }
        var version = manifest.GetProperty("version").GetString()!;
        var newest = request.Published.Order(Comparer<string>.Create(AppService.CompareVersions)).LastOrDefault();
        if (newest is not null && AppService.CompareVersions(version, newest) <= 0)
        {
            issues.Add(Violation("version_not_higher", ("path", path), ("version", version), ("published", newest)));
        }
        if (manifest.TryGetProperty("backend", out var backend))
        {
            var compose = ComposeValidator.DevComposePath(backend.GetProperty("compose").GetString()!);
            if (files.TryGetValue(compose, out var source))
            {
                issues.AddRange(ComposeValidator.Validate(source, manifest, dev: true).Issues);
            }
            else
            {
                issues.Add(Violation("required_file", ("path", $"{root}{compose}")));
            }
        }
        return issues;
    }

    private static List<ValidationIssue> MaintainerIssues(CatalogPullRequest request, string root, byte[]? bytes)
    {
        bool Listed(IEnumerable<string> logins) => logins.Any(login => login.Equals(request.Author, StringComparison.OrdinalIgnoreCase));

        var issues = new List<ValidationIssue>();
        if (request.Maintainers is not null && !Listed(request.Maintainers))
        {
            issues.Add(Violation("maintainer", ("author", request.Author)));
        }
        if (bytes is null)
        {
            return issues;
        }
        using var document = JsonSchemaCheck.ParseStrict(bytes);
        if (document is null || !_maintainersSchema.Value.IsValid(document.RootElement))
        {
            issues.Add(Violation("maintainers_invalid", ("path", $"{root}maintainers.json")));
        }
        else if (request.Maintainers is null
            && !Listed(document.RootElement.GetProperty("maintainers").EnumerateArray().Select(login => login.GetString()!)))
        {
            // A new app's own file must name its author, who becomes its first maintainer.
            issues.Add(Violation("maintainer", ("author", request.Author)));
        }
        return issues;
    }
}
