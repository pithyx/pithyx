using System.Globalization;
using Vyxis.Daemon.Localization;

namespace Vyxis.Daemon.Tests.Localization;

public sealed class IcuMessageFormatterTests
{
    private static string Format(string pattern, params (string Name, object? Value)[] arguments) =>
        IcuMessageFormatter.Format(pattern, arguments.ToDictionary(a => a.Name, a => a.Value), CultureInfo.InvariantCulture);

    [Fact]
    public void ReplacesSimpleArguments() => Assert.Equal("Pool tank is full", Format("Pool {pool} is full", ("pool", "tank")));

    [Fact]
    public void KeepsMissingArgumentsVisible() => Assert.Equal("Pool {pool} is full", Format("Pool {pool} is full"));

    [Theory]
    [InlineData(0, "no attempts left")]
    [InlineData(1, "1 attempt left")]
    [InlineData(5, "5 attempts left")]
    public void ChoosesPluralCase(int count, string expected) =>
        Assert.Equal(expected, Format("{count, plural, =0 {no attempts} one {# attempt} other {# attempts}} left", ("count", count)));

    [Fact]
    public void ChoosesSelectCase() =>
        Assert.Equal("Connected (simulated)", Format("{mode, select, simulated {Connected (simulated)} other {Connected}}", ("mode", "simulated")));

    [Fact]
    public void FallsBackToOtherInSelect() =>
        Assert.Equal("Connected", Format("{mode, select, simulated {Connected (simulated)} other {Connected}}", ("mode", "real")));

    [Fact]
    public void SupportsNestedArguments() =>
        Assert.Equal("Retry in 3 seconds on tank", Format("{n, plural, one {Retry in # second on {pool}} other {Retry in # seconds on {pool}}}", ("n", 3), ("pool", "tank")));

    [Fact]
    public void HonorsApostropheQuoting() => Assert.Equal("Use {braces} and it's fine", Format("Use '{braces}' and it''s fine"));

    [Fact]
    public void RejectsUnbalancedBraces() => Assert.Throws<FormatException>(() => Format("Pool {pool"));
}
