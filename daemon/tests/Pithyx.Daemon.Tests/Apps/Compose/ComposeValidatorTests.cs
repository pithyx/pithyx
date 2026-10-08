using System.Text.Json;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Compose;

public sealed class ComposeValidatorTests
{
    public static TheoryData<string> Valid => Corpus.Files("compose", "valid");

    public static TheoryData<string> Invalid => Corpus.Files("compose", "invalid");

    public static TheoryData<string> Manifests => Corpus.Files("compose", "manifests");

    public static TheoryData<string> DevValid => Corpus.Files("compose", "dev", "valid");

    public static TheoryData<string> DevInvalid => Corpus.Files("compose", "dev", "invalid");

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

    [Theory]
    [MemberData(nameof(DevValid))]
    public void ValidSourceFormsPass(string file)
    {
        var result = ComposeValidator.Validate(Corpus.Read("compose", "dev", "valid", file), ManifestFor("valid", "minimal.yaml"), dev: true);

        Assert.Empty(Describe(result.Issues));
    }

    [Theory]
    [MemberData(nameof(DevInvalid))]
    public void InvalidSourceFormsFailWithExactlyTheExpectedIssues(string file)
    {
        var expected = Corpus.Json("compose", "dev", "expected.json");
        Assert.True(expected.TryGetProperty(file, out var issues), $"compose/dev/expected.json lacks {file}");

        var result = ComposeValidator.Validate(Corpus.Read("compose", "dev", "invalid", file), ManifestFor("valid", "minimal.yaml"), dev: true);

        Corpus.AssertExactly(result.Issues, issues.EnumerateArray());
    }

    [Fact]
    public void EverySourceFormExpectationHasAFixture() =>
        Assert.Equal(
            DevInvalid.Select(row => row.Data),
            Corpus.Json("compose", "dev", "expected.json").EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal));

    [Fact]
    public void TheFormTheBoxReadsRefusesBuild()
    {
        var result = ComposeValidator.Validate(Corpus.Read("compose", "dev", "valid", "build-short.yaml"), ManifestFor("valid", "minimal.yaml"));

        Assert.Contains(result.Issues, issue => issue.Code == ErrorCodes.AppComposeKeyUnsupported);
    }

    [Theory]
    [InlineData("backend/compose.yaml", "backend/compose.dev.yaml")]
    [InlineData("backend/app.yml", "backend/app.dev.yml")]
    [InlineData("backend/v1.2/stack.yaml", "backend/v1.2/stack.dev.yaml")]
    public void TheSourceFormSitsNextToTheComposeFile(string compose, string dev) =>
        Assert.Equal(dev, ComposeValidator.DevComposePath(compose));

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
