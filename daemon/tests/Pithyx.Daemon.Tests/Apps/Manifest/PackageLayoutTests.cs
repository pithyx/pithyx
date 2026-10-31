using System.Text.Json;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Apps.Manifest;

public sealed class PackageLayoutTests
{
    public static TheoryData<string> Cases()
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Corpus.Directory("layout", "cases.json")));
        return [.. cases.RootElement.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LayoutCasesMatch(string name)
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Corpus.Directory("layout", "cases.json")));
        var entry = cases.RootElement.EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var compose = entry.TryGetProperty("compose", out var value) ? value.GetString() : null;
        var screenshots = entry.TryGetProperty("screenshots", out var shots) ? shots.EnumerateArray().Select(shot => shot.GetString()!).ToList() : null;
        var sizes = entry.TryGetProperty("sizes", out var given) ? given.EnumerateObject().ToDictionary(size => size.Name, size => size.Value.GetInt64()) : [];

        var issues = PackageLayout.Check(
            entry.GetProperty("files").EnumerateArray().Select(file => file.GetString()!),
            entry.GetProperty("entry").GetString()!,
            compose,
            screenshots,
            path => sizes.GetValueOrDefault(path));

        Assert.All(issues, issue => Assert.Equal(ErrorCodes.AppPackageLayoutInvalid, issue.Code));
        Assert.Equal(
            entry.GetProperty("expected").EnumerateArray().Select(issue => (issue.GetProperty("path").GetString(), issue.GetProperty("reason").GetString())).ToHashSet(),
            issues.Select(issue => ((string?)issue.Params["path"], (string?)issue.Params["reason"])).ToHashSet());
    }
}
