using Vyxis.Daemon.Realtime;

namespace Vyxis.Daemon.Tests.Realtime;

public sealed class TopicPatternTests
{
    [Theory]
    [InlineData("storage/disks", "storage/disks", true)]
    [InlineData("storage/disks", "storage/pools", false)]
    [InlineData("storage/+", "storage/disks", true)]
    [InlineData("storage/+", "storage/pools/tank", false)]
    [InlineData("storage/+/tank", "storage/pools/tank", true)]
    [InlineData("storage/#", "storage/pools/tank", true)]
    [InlineData("storage/#", "storage", true)]
    [InlineData("#", "system/info", true)]
    [InlineData("+", "system/info", false)]
    [InlineData("system/info/#", "system/info", true)]
    [InlineData("system/info", "system/info/cpu", false)]
    public void MatchesLikeMqtt(string pattern, string topic, bool expected) =>
        Assert.Equal(expected, TopicPattern.Matches(pattern, topic));

    [Theory]
    [InlineData("storage/disks", false, true)]
    [InlineData("storage/+", true, true)]
    [InlineData("storage/+", false, false)]
    [InlineData("storage/#", true, true)]
    [InlineData("storage/#/x", true, false)]
    [InlineData("storage/di+sks", true, false)]
    [InlineData("storage//disks", true, false)]
    [InlineData("/storage", true, false)]
    [InlineData("", true, false)]
    public void ValidatesNamesAndPatterns(string value, bool allowWildcards, bool expected) =>
        Assert.Equal(expected, TopicPattern.IsValid(value, allowWildcards));
}
