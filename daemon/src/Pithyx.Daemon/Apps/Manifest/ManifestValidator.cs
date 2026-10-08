using System.Text.Json;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>The outcome of <see cref="ManifestValidator.Validate"/>.</summary>
/// <param name="Issues">Empty when the manifest is valid.</param>
/// <param name="Manifest">The parsed manifest when it is valid.</param>
public sealed record ManifestValidationResult(IReadOnlyList<ValidationIssue> Issues, JsonElement? Manifest)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Checks <c>pithyx.json</c> against <c>sdk/schema/manifest.v1.schema.json</c> (embedded at build) and the rules a
/// schema cannot express. <c>@pithyx/cli</c> runs the same checks; the corpus in <c>sdk/schema/fixtures/manifest</c>
/// holds both to the same verdicts and codes.
/// </summary>
public static class ManifestValidator
{
    public const int SupportedVersion = 1;

    /// <summary>Capabilities that reach the host's secrets (D71); never granted on their own.</summary>
    private static readonly HashSet<string> _blockedCapabilities =
        ["ALL", "SYS_ADMIN", "SYS_PTRACE", "SYS_MODULE", "SYS_RAWIO", "DAC_READ_SEARCH", "BPF"];

    private static readonly Lazy<JsonSchemaCheck> _schema = new(() => JsonSchemaCheck.Load("manifest.v1.schema.json"));

    /// <summary>
    /// Validates the bytes of <c>pithyx.json</c>. Ids in <c>org.pithyx.*</c> are refused unless
    /// <paramref name="allowReservedId"/> is set, which only the system apps and the official catalog may do.
    /// </summary>
    public static ManifestValidationResult Validate(ReadOnlyMemory<byte> utf8, bool allowReservedId = false)
    {
        if (JsonSchemaCheck.ParseStrict(utf8) is not { } document)
        {
            return Fail(new ValidationIssue(ErrorCodes.AppManifestInvalidJson));
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("manifestVersion", out var version)
                && !(version.ValueKind == JsonValueKind.Number && version.GetDouble() == SupportedVersion))
            {
                return Fail(new ValidationIssue(ErrorCodes.AppManifestVersionUnsupported, ("version", JsonSchemaCheck.Display(version)), ("supported", SupportedVersion)));
            }

            var issues = SchemaIssues(root);
            if (!issues.Exists(issue => issue.Code == ErrorCodes.AppManifestSchemaViolation))
            {
                issues.AddRange(SemanticIssues(root, allowReservedId, idValid: !issues.Exists(issue => issue.Code == ErrorCodes.AppManifestIdInvalid)));
            }
            return new ManifestValidationResult(issues, issues.Count == 0 ? root.Clone() : null);
        }
    }

    private static ManifestValidationResult Fail(ValidationIssue issue) => new([issue], null);

    private static List<ValidationIssue> SchemaIssues(JsonElement root)
    {
        var issues = new List<ValidationIssue>();
        foreach (var (path, keyword) in _schema.Value.Violations(root))
        {
            // A wrong id, sdk range or pithyx range has a code of its own; a missing one is a plain schema violation.
            if (path == "/id" && keyword != "required")
            {
                AddOnce(issues, new ValidationIssue(ErrorCodes.AppManifestIdInvalid, ("id", JsonSchemaCheck.Display(root.GetProperty("id")))));
            }
            else if (path == "/sdk" && keyword != "required")
            {
                AddOnce(issues, new ValidationIssue(ErrorCodes.AppManifestSdkInvalid, ("sdk", JsonSchemaCheck.Display(root.GetProperty("sdk")))));
            }
            else if (path == "/pithyx" && keyword != "required")
            {
                AddOnce(issues, new ValidationIssue(ErrorCodes.AppManifestPithyxInvalid, ("pithyx", JsonSchemaCheck.Display(root.GetProperty("pithyx")))));
            }
            else
            {
                issues.Add(new ValidationIssue(ErrorCodes.AppManifestSchemaViolation, ("path", path), ("keyword", keyword)));
            }
        }
        return issues;
    }

    private static List<ValidationIssue> SemanticIssues(JsonElement root, bool allowReservedId, bool idValid)
    {
        var issues = new List<ValidationIssue>();
        var id = root.GetProperty("id");
        if (idValid && !allowReservedId && AppId.IsReserved(id.GetString()!))
        {
            issues.Add(new ValidationIssue(ErrorCodes.AppManifestIdReserved, ("id", id.GetString())));
        }

        var window = root.GetProperty("window");
        var defaultSize = window.GetProperty("defaultSize");
        var minSize = window.GetProperty("minSize");
        if (minSize.GetProperty("width").GetInt32() > defaultSize.GetProperty("width").GetInt32()
            || minSize.GetProperty("height").GetInt32() > defaultSize.GetProperty("height").GetInt32())
        {
            issues.Add(new ValidationIssue(ErrorCodes.AppManifestWindowSizeInvalid));
        }

        var hasBackend = root.TryGetProperty("backend", out var backend);
        if (root.TryGetProperty("storage", out var storage))
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var request in storage.EnumerateArray())
            {
                var storageId = request.GetProperty("id").GetString()!;
                if (!ids.Add(storageId))
                {
                    issues.Add(Duplicate($"/storage/{index}/id", storageId));
                }
                if (!hasBackend && request.TryGetProperty("services", out var services))
                {
                    var serviceIndex = 0;
                    foreach (var service in services.EnumerateArray())
                    {
                        issues.Add(UnknownReference($"/storage/{index}/services/{serviceIndex++}", service.GetString()!));
                    }
                }
                index++;
            }
        }

        if (root.TryGetProperty("fileHandlers", out var handlers))
        {
            var extensions = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var handler in handlers.EnumerateArray())
            {
                var extensionIndex = 0;
                foreach (var extension in handler.GetProperty("extensions").EnumerateArray())
                {
                    if (!extensions.Add(extension.GetString()!))
                    {
                        issues.Add(Duplicate($"/fileHandlers/{index}/extensions/{extensionIndex}", extension.GetString()!));
                    }
                    extensionIndex++;
                }
                index++;
            }
        }

        if (hasBackend)
        {
            issues.AddRange(BackendIssues(backend));
        }
        return issues;
    }

    private static IEnumerable<ValidationIssue> BackendIssues(JsonElement backend)
    {
        var endpoints = backend.GetProperty("endpoints").EnumerateObject().Select(endpoint => endpoint.Name).ToHashSet(StringComparer.Ordinal);
        if (backend.TryGetProperty("health", out var health) && !endpoints.Contains(health.GetProperty("endpoint").GetString()!))
        {
            yield return UnknownReference("/backend/health/endpoint", health.GetProperty("endpoint").GetString()!);
        }

        if (backend.TryGetProperty("schedules", out var schedules))
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var schedule in schedules.EnumerateArray())
            {
                var scheduleId = schedule.GetProperty("id").GetString()!;
                if (!ids.Add(scheduleId))
                {
                    yield return Duplicate($"/backend/schedules/{index}/id", scheduleId);
                }
                var cron = schedule.GetProperty("cron").GetString()!;
                if (!CronSyntax.IsValid(cron))
                {
                    yield return new ValidationIssue(ErrorCodes.AppManifestCronInvalid, ("path", $"/backend/schedules/{index}/cron"), ("cron", cron));
                }
                var endpoint = schedule.GetProperty("endpoint").GetString()!;
                if (!endpoints.Contains(endpoint))
                {
                    yield return UnknownReference($"/backend/schedules/{index}/endpoint", endpoint);
                }
                index++;
            }
        }

        if (backend.TryGetProperty("elevated", out var elevated))
        {
            foreach (var right in elevated.EnumerateArray().Select(element => element.GetString()!))
            {
                if (!IsSupportedElevatedRight(right))
                {
                    yield return new ValidationIssue(ErrorCodes.AppElevatedUnsupported, ("right", right));
                }
            }
        }
    }

    /// <summary>Whether this version of Pithyx can grant an elevated right the schema accepts (D71, D304).</summary>
    public static bool IsSupportedElevatedRight(string right) =>
        right is not ("docker.socket" or "host.full")
        && !(right.StartsWith("capability:", StringComparison.Ordinal) && _blockedCapabilities.Contains(right["capability:".Length..]));

    private static ValidationIssue Duplicate(string path, string value) =>
        new(ErrorCodes.AppManifestDuplicate, ("path", path), ("value", value));

    private static ValidationIssue UnknownReference(string path, string name) =>
        new(ErrorCodes.AppManifestReferenceUnknown, ("path", path), ("name", name));

    private static void AddOnce(List<ValidationIssue> issues, ValidationIssue issue)
    {
        if (!issues.Exists(existing => existing.Code == issue.Code))
        {
            issues.Add(issue);
        }
    }
}
