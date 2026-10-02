using Pithyx.Daemon.SystemSettings;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.SystemSettings;

public sealed class SystemRegionTests
{
    [Fact]
    public void TheSystemLanguagesAreTheTranslatedOnes() =>
        Assert.Equal(
            Directory.GetDirectories(Path.Combine(RepositoryFiles.Root, "i18n")).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            SystemRegion.Languages.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("en", null, true)]
    [InlineData("de", "CH", true)]
    [InlineData("fr", null, false)]
    [InlineData("en", "ch", false)]
    [InlineData("en", "CHE", false)]
    [InlineData("en", "", false)]
    public void OnlyShippedLanguagesAndTwoLetterRegionsAreValid(string language, string? region, bool valid) =>
        Assert.Equal(valid, new SystemRegion(language, region).IsValid());
}
