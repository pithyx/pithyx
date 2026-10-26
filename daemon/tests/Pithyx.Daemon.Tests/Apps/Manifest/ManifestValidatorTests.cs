using System.Text;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Apps.Manifest;

public sealed class ManifestValidatorTests
{
    public static TheoryData<string> Valid => Corpus.Files("manifest", "valid");

    public static TheoryData<string> Invalid => Corpus.Files("manifest", "invalid");

    private static ManifestValidationResult Validate(string kind, string file, bool allowReservedId = false) =>
        ManifestValidator.Validate(File.ReadAllBytes(Corpus.Directory("manifest", kind, file)), allowReservedId);

    [Theory]
    [MemberData(nameof(Valid))]
    public void ValidManifestsPass(string file)
    {
        var result = Validate("valid", file);

        Assert.Empty(result.Issues.Select(issue => $"{issue.Code} {string.Join(", ", issue.Params)}"));
        Assert.NotNull(result.Manifest);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidManifestsFailWithTheExpectedCodes(string file)
    {
        var expected = Corpus.Expected("manifest", file).EnumerateArray().ToList();
        var result = Validate("invalid", file);

        Assert.Null(result.Manifest);
        Assert.Equal(
            expected.Select(issue => issue.GetProperty("code").GetString()!).ToHashSet(),
            result.Issues.Select(issue => issue.Code).ToHashSet());
        Assert.All(expected, issue => Assert.True(
            result.Issues.Any(actual => Corpus.Matches(actual, issue)),
            $"{issue} not in {string.Join("; ", result.Issues.Select(actual => $"{actual.Code} {string.Join(", ", actual.Params)}"))}"));
    }

    [Fact]
    public void EveryExpectationHasAFixture()
    {
        using var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Corpus.Directory("manifest", "expected.json")));
        var files = Invalid.Select(row => row.Data).ToHashSet();
        Assert.All(expected.RootElement.EnumerateObject(), entry => Assert.Contains(entry.Name, files));
    }

    [Fact]
    public void SystemAppsMayUseTheReservedNamespace()
    {
        Assert.Empty(Validate("invalid", "id-reserved.json", allowReservedId: true).Issues);
    }

    [Fact]
    public void IssuesBecomeProblemsWithTheirParams()
    {
        var issue = ManifestValidator.Validate(Encoding.UTF8.GetBytes("{\"manifestVersion\":3}")).Issues.Single();

        var exception = issue.ToException();

        Assert.Equal(ErrorCodes.AppManifestVersionUnsupported, exception.Code);
        Assert.Equal("3", exception.Parameters["version"]);
    }

    [Theory]
    [InlineData("docker.socket")]
    [InlineData("host.full")]
    [InlineData("capability:BPF")]
    [InlineData("capability:SYS_ADMIN")]
    [InlineData("device:/dev/sda")]
    [InlineData("device:/dev/mem")]
    [InlineData("device:/dev/tty1")]
    [InlineData("device:/dev/zfs")]
    public void RightsThatReachTheBoxAreRefused(string right) =>
        Assert.False(ManifestValidator.IsSupportedElevatedRight(right));

    [Theory]
    [InlineData("capability:NET_ADMIN")]
    [InlineData("device:/dev/dri/renderD128")]
    [InlineData("device:/dev/ttyUSB0")]
    [InlineData("device:/dev/net/tun")]
    [InlineData("host.network")]
    [InlineData("host.pid")]
    public void RightsOfD311AreGranted(string right) =>
        Assert.True(ManifestValidator.IsSupportedElevatedRight(right));
}
