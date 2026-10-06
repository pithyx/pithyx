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

    public static TheoryData<string> Files(string area, string kind) =>
        [.. System.IO.Directory.GetFiles(Directory(area, kind)).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

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

    public static bool Matches(ValidationIssue issue, JsonElement expected) =>
        issue.Code == expected.GetProperty("code").GetString()
        && (!expected.TryGetProperty("params", out var parameters)
            || parameters.EnumerateObject().All(parameter =>
                issue.Params.TryGetValue(parameter.Name, out var actual) && Text(actual) == Text(parameter.Value)));
}
