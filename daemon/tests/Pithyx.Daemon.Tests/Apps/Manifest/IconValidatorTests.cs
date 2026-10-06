using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Apps.Manifest;

public sealed class IconValidatorTests
{
    public static TheoryData<string> Valid => Corpus.Files("icon", "valid");

    public static TheoryData<string> Invalid => Corpus.Files("icon", "invalid");

    private static IReadOnlyList<ValidationIssue> Validate(string kind, string file) =>
        IconValidator.Validate(File.ReadAllBytes(Corpus.Directory("icon", kind, file)));

    [Theory]
    [MemberData(nameof(Valid))]
    public void ValidIconsPass(string file) => Assert.Empty(Validate("valid", file));

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidIconsFailWithTheExpectedReasons(string file)
    {
        var expected = Corpus.Expected("icon", file).EnumerateArray()
            .Select(issue => string.Join(';', issue.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={Corpus.Text(p.Value)}")))
            .ToHashSet();
        var issues = Validate("invalid", file);

        Assert.All(issues, issue => Assert.Equal(ErrorCodes.AppIconInvalid, issue.Code));
        Assert.Equal(
            expected,
            issues.Select(issue => string.Join(';', issue.Params.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={Corpus.Text(p.Value)}"))).ToHashSet());
    }
}
