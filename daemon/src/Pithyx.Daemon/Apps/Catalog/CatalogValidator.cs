using System.Text.Json;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>The outcome of reading <c>catalog.json</c> or <c>index.json</c> of a catalog.</summary>
/// <param name="Issues">Empty when the file is valid.</param>
/// <param name="Document">The parsed file when it is valid.</param>
public sealed record CatalogValidationResult(IReadOnlyList<ValidationIssue> Issues, JsonElement? Document)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>What a box expects of an index before it reads it.</summary>
/// <param name="Catalog">The id of the catalog the index was fetched for.</param>
/// <param name="Channel"><c>stable</c> (branch <c>catalog</c>) or <c>beta</c> (branch <c>catalog-next</c>).</param>
/// <param name="KnownSequence">The highest sequence the box verified for this catalog and channel, if any.</param>
public sealed record IndexExpectation(string Catalog, string Channel, long? KnownSequence = null);

/// <summary>
/// Checks <c>catalog.json</c> and <c>index.json</c> of a catalog repository (D328) against
/// <c>sdk/schema/catalog.v1.schema.json</c> and <c>index.v1.schema.json</c> (embedded at build) and the rules a schema
/// cannot express. <c>@pithyx/cli</c> runs the same checks; the corpus in <c>sdk/schema/fixtures/catalog</c> holds both to
/// the same verdicts and codes. Signed indexes go through <see cref="IndexSignature.CheckSignedIndex"/>.
/// </summary>
public static class CatalogValidator
{
    public const int SupportedCatalogVersion = 1;

    public const int SupportedIndexVersion = 1;

    private static readonly Lazy<JsonSchemaCheck> _catalogSchema = new(() => JsonSchemaCheck.Load("catalog.v1.schema.json"));

    private static readonly Lazy<JsonSchemaCheck> _indexSchema = new(() => JsonSchemaCheck.Load("index.v1.schema.json"));

    /// <summary>Whether a catalog may list apps in the reserved namespace <c>org.pithyx.*</c>.</summary>
    public static bool MayUseReservedIds(string catalog) => CatalogRules.Rules.ReservedCatalogs.Contains(catalog);

    private static bool IsReserved(string id) => id.StartsWith(CatalogRules.Rules.ReservedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Validates the bytes of <c>catalog.json</c>. An id in <c>org.pithyx.*</c> is refused unless
    /// <paramref name="allowReservedId"/> is set, which only the project's own catalogs may do.
    /// </summary>
    public static CatalogValidationResult ValidateCatalog(ReadOnlyMemory<byte> utf8, bool allowReservedId = false) =>
        ReadFile(utf8, "catalog.json", "catalogVersion", SupportedCatalogVersion, _catalogSchema.Value, catalog =>
        {
            var id = catalog.GetProperty("id").GetString()!;
            return !allowReservedId && IsReserved(id)
                ? [new ValidationIssue(ErrorCodes.StoreCatalogIdReserved, ("path", "/id"), ("id", id))]
                : [];
        });

    /// <summary>
    /// Validates the bytes of <c>index.json</c>: its schema, the catalog and channel it was fetched for, a sequence not
    /// below the known one, ids in <c>org.pithyx.*</c> only in the official catalog, the source folder of each app, every
    /// architecture in every image and a <c>dataVersion</c> that never goes down from one version to the next.
    /// </summary>
    public static CatalogValidationResult ValidateIndex(ReadOnlyMemory<byte> utf8, IndexExpectation expected) =>
        ReadFile(utf8, "index.json", "indexVersion", SupportedIndexVersion, _indexSchema.Value, index => IndexIssues(index, expected));

    /// <summary>
    /// Reads a file up to its schema: refuses bytes that are not strict JSON, an unknown format version and every schema
    /// violation; only then the rules of <paramref name="semantics"/>.
    /// </summary>
    private static CatalogValidationResult ReadFile(
        ReadOnlyMemory<byte> utf8,
        string file,
        string versionField,
        int supported,
        JsonSchemaCheck schema,
        Func<JsonElement, List<ValidationIssue>> semantics)
    {
        if (JsonSchemaCheck.ParseStrict(utf8) is not { } document)
        {
            return new([new ValidationIssue(ErrorCodes.StoreCatalogInvalidJson, ("file", file))], null);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(versionField, out var version)
                && !(version.ValueKind == JsonValueKind.Number && version.GetDouble() == supported))
            {
                return new([new ValidationIssue(
                    ErrorCodes.StoreCatalogVersionUnsupported,
                    ("file", file),
                    ("version", JsonSchemaCheck.Display(version)),
                    ("supported", supported))], null);
            }
            var issues = schema.Violations(root)
                .Select(violation => new ValidationIssue(ErrorCodes.StoreCatalogSchemaViolation, ("file", file), ("path", violation.Path), ("keyword", violation.Keyword)))
                .ToList();
            if (issues.Count > 0)
            {
                return new(issues, null);
            }
            issues = semantics(root);
            return new(issues, issues.Count == 0 ? root.Clone() : null);
        }
    }

    private static ValidationIssue Mismatch(string path, string value, string expected) =>
        new(ErrorCodes.StoreCatalogMismatch, ("path", path), ("value", value), ("expected", expected));

    private static List<ValidationIssue> IndexIssues(JsonElement index, IndexExpectation expected)
    {
        var issues = new List<ValidationIssue>();
        var catalog = index.GetProperty("catalog").GetString()!;
        if (catalog != expected.Catalog)
        {
            issues.Add(Mismatch("/catalog", catalog, expected.Catalog));
        }
        var channel = index.GetProperty("channel").GetString()!;
        if (channel != expected.Channel)
        {
            issues.Add(Mismatch("/channel", channel, expected.Channel));
        }
        var sequence = (long)index.GetProperty("sequence").GetDouble();
        if (expected.KnownSequence is { } known && sequence < known)
        {
            issues.Add(new ValidationIssue(ErrorCodes.StoreCatalogSequenceOld, ("sequence", sequence), ("known", known)));
        }

        var rules = CatalogRules.Rules;
        var reservedAllowed = MayUseReservedIds(expected.Catalog);
        foreach (var app in index.GetProperty("apps").EnumerateObject())
        {
            var id = app.Name;
            var appPath = $"/apps/{JsonSchemaCheck.EscapePointer(id)}";
            if (!reservedAllowed && IsReserved(id))
            {
                issues.Add(new ValidationIssue(ErrorCodes.StoreCatalogIdReserved, ("path", appPath), ("id", id)));
            }
            long? previous = null;
            var versions = app.Value.GetProperty("versions").EnumerateObject()
                .OrderBy(version => version.Name, Comparer<string>.Create(AppService.CompareVersions));
            foreach (var version in versions)
            {
                var entry = version.Value;
                var path = $"{appPath}/versions/{JsonSchemaCheck.EscapePointer(version.Name)}";
                var sourcePath = entry.GetProperty("source").GetProperty("path").GetString()!;
                var folder = $"{rules.AppsFolder}/{id}";
                if (sourcePath != folder)
                {
                    issues.Add(Mismatch($"{path}/source/path", sourcePath, folder));
                }
                if (entry.TryGetProperty("images", out var images))
                {
                    var architectures = entry.GetProperty("architectures").EnumerateArray().Select(architecture => architecture.GetString()!).ToList();
                    foreach (var image in images.EnumerateObject())
                    {
                        foreach (var architecture in architectures.Where(architecture => !image.Value.TryGetProperty(architecture, out _)))
                        {
                            issues.Add(new ValidationIssue(
                                ErrorCodes.StoreCatalogArchitectureMissing,
                                ("path", $"{path}/images/{JsonSchemaCheck.EscapePointer(image.Name)}"),
                                ("architecture", architecture)));
                        }
                    }
                }
                if (entry.TryGetProperty("dataVersion", out var dataVersionElement))
                {
                    var dataVersion = (long)dataVersionElement.GetDouble();
                    if (previous is { } before && dataVersion < before)
                    {
                        issues.Add(new ValidationIssue(
                            ErrorCodes.StoreCatalogDataVersionDecreased,
                            ("path", $"{path}/dataVersion"),
                            ("dataVersion", dataVersion),
                            ("previous", before)));
                    }
                    previous = Math.Max(previous ?? dataVersion, dataVersion);
                }
            }
        }
        return issues;
    }
}
