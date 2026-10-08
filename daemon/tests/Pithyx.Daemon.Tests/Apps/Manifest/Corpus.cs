using System.Globalization;
using System.Text.Json;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps.Manifest;

/// <summary>The fixture corpus in <c>sdk/schema/fixtures</c>, shared with the Vitest suite of <c>@pithyx/cli</c>.</summary>
internal static class Corpus
{
    public static string Directory(params string[] parts) =>
        Path.Combine([RepositoryFiles.Root, "sdk", "schema", "fixtures", .. parts]);

    public static TheoryData<string> Files(params string[] parts) =>
        [.. System.IO.Directory.GetFiles(Directory(parts)).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    public static byte[] Read(params string[] parts) => File.ReadAllBytes(Directory(parts));

    public static JsonElement Json(params string[] parts)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Directory(parts)));
        return document.RootElement.Clone();
    }

    public static JsonElement Expected(string area, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Directory(area, "expected.json")));
        Assert.True(document.RootElement.TryGetProperty(file, out var expected), $"{area}/expected.json lacks {file}");
        return expected.Clone();
    }

    /// <summary>A param value as text, so JSON numbers and C# numbers compare alike.</summary>
    public static string Text(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    public static string Describe(IEnumerable<ValidationIssue> issues) =>
        string.Join("; ", issues.Select(issue => $"{issue.Code} {string.Join(", ", issue.Params)}"));

    /// <summary>Every expected issue and nothing else, in any order: both validators report exactly the same list.</summary>
    public static void AssertExactly(IReadOnlyList<ValidationIssue> actual, IEnumerable<JsonElement> expected)
    {
        var issues = expected.ToList();
        Assert.True(actual.Count == issues.Count, $"expected {issues.Count} issues, got {Describe(actual)}");
        Assert.All(issues, issue => Assert.True(actual.Any(candidate => Matches(candidate, issue)), $"{issue} not in {Describe(actual)}"));
        Assert.All(actual, candidate => Assert.True(issues.Any(issue => Matches(candidate, issue)), $"{Describe([candidate])} is not expected"));
    }

    public static bool Matches(ValidationIssue issue, JsonElement expected) =>
        issue.Code == expected.GetProperty("code").GetString()
        && (!expected.TryGetProperty("params", out var parameters)
            || parameters.EnumerateObject().All(parameter =>
                issue.Params.TryGetValue(parameter.Name, out var actual) && Text(actual) == Text(parameter.Value)));
}
