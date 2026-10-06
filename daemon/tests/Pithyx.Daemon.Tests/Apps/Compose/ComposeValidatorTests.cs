using System.Text.Json;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Tests.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Compose;

public sealed class ComposeValidatorTests
{
    public static TheoryData<string> Valid => Corpus.Files("compose", "valid");

    public static TheoryData<string> Invalid => Corpus.Files("compose", "invalid");

    public static TheoryData<string> Manifests => Corpus.Files("compose", "manifests");

    internal static JsonElement Manifest(string name)
    {
        var result = ManifestValidator.Validate(File.ReadAllBytes(Corpus.Directory("compose", "manifests", name)));
        Assert.True(result.IsValid, $"compose/manifests/{name} is not a valid manifest");
        return result.Manifest!.Value;
    }

    internal static JsonElement ManifestFor(string kind, string file)
    {
        using var choices = JsonDocument.Parse(File.ReadAllText(Corpus.Directory("compose", "manifests.json")));
        return Manifest(choices.RootElement.TryGetProperty($"{kind}/{file}", out var name) ? name.GetString()! : "base.json");
    }

    private static ComposeValidationResult Validate(string kind, string file) =>
        ComposeValidator.Validate(File.ReadAllBytes(Corpus.Directory("compose", kind, file)), ManifestFor(kind, file));

    private static string Describe(IEnumerable<ValidationIssue> issues) =>
        string.Join("; ", issues.Select(issue => $"{issue.Code} {string.Join(", ", issue.Params)}"));

    [Theory]
    [MemberData(nameof(Valid))]
    public void ValidFilesPass(string file)
    {
        var result = Validate("valid", file);

        Assert.Empty(Describe(result.Issues));
        Assert.NotNull(result.Document);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidFilesFailWithExactlyTheExpectedIssues(string file)
    {
        var expected = Corpus.Expected("compose", file).EnumerateArray().ToList();
        var result = Validate("invalid", file);

        Assert.Null(result.Document);
        Assert.All(expected, issue => Assert.True(
            result.Issues.Any(actual => Corpus.Matches(actual, issue)),
            $"{issue} not in {Describe(result.Issues)}"));
        Assert.All(result.Issues, actual => Assert.True(
            expected.Any(issue => Corpus.Matches(actual, issue)),
            $"{Describe([actual])} is not expected"));
    }

    [Fact]
    public void EveryExpectationAndManifestChoiceHasAFixture()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Corpus.Directory("compose", "expected.json")));
        Assert.Equal(
            Invalid.Select(row => row.Data).Order(StringComparer.Ordinal),
            expected.RootElement.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));
        using var choices = JsonDocument.Parse(File.ReadAllText(Corpus.Directory("compose", "manifests.json")));
        Assert.All(choices.RootElement.EnumerateObject(), choice =>
        {
            Assert.True(File.Exists(Corpus.Directory("compose", choice.Name)), choice.Name);
            Assert.True(File.Exists(Corpus.Directory("compose", "manifests", choice.Value.GetString()!)), choice.Value.GetString());
        });
    }

    [Fact]
    public void TheRulesNeverAllowAKeyTheyListAsRefused()
    {
        var rules = ComposeValidator.Rules;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Directory(), "..", "compose.v1.rules.json")));
        var refused = document.RootElement.GetProperty("refused");
        Assert.DoesNotContain(refused.GetProperty("top").EnumerateObject(), key => rules.Top.ContainsKey(key.Name));
        Assert.DoesNotContain(refused.GetProperty("service").EnumerateObject(), key => rules.Service.ContainsKey(key.Name));
        Assert.DoesNotContain(refused.GetProperty("mount").EnumerateObject(), key => rules.MountKeys.ContainsKey(key.Name));
    }

    [Theory]
    [InlineData("1", ComposeScalarKind.WholeNumber)]
    [InlineData("-12", ComposeScalarKind.WholeNumber)]
    [InlineData("012", ComposeScalarKind.OtherNumber)]
    [InlineData("1_000", ComposeScalarKind.OtherNumber)]
    [InlineData("0o17", ComposeScalarKind.OtherNumber)]
    [InlineData("+1", ComposeScalarKind.OtherNumber)]
    [InlineData(".inf", ComposeScalarKind.OtherNumber)]
    [InlineData("True", ComposeScalarKind.Bool)]
    [InlineData("yes", ComposeScalarKind.Text)]
    [InlineData("~", ComposeScalarKind.Null)]
    [InlineData("", ComposeScalarKind.Null)]
    [InlineData("1.2.3", ComposeScalarKind.Text)]
    public void PlainScalarsResolveLikeTheCli(string text, ComposeScalarKind kind)
    {
        Assert.Equal(kind, new ComposeScalar(text, Plain: true).Kind);
        Assert.Equal(ComposeScalarKind.Text, new ComposeScalar(text, Plain: false).Kind);
    }
}
