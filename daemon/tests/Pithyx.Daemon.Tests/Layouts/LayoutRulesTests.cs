using System.Text.Json;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Layouts;

namespace Pithyx.Daemon.Tests.Layouts;

public sealed class LayoutRulesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string Layout(int windows, string args = "null") =>
        $$"""{"version":1,"taskbar":"bottom","windows":[{{string.Join(",", Enumerable.Range(1, windows).Select(i => $$"""{"id":"w{{i}}","app":"disks","args":{{args}}}"""))}}],"order":[],"focused":null}""";

    [Fact]
    public void AcceptsAValidLayoutAndCountsItsWindows()
    {
        var result = LayoutRules.Check(Json(Layout(3)));

        Assert.Equal(3, result.WindowCount);
        Assert.StartsWith("{\"version\":1", result.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"windows\":[]}")]
    [InlineData("{\"version\":\"1\",\"windows\":[]}")]
    [InlineData("{\"version\":1,\"windows\":{}}")]
    [InlineData("{\"version\":1,\"windows\":[{\"id\":\"w1\"}]}")]
    [InlineData("{\"version\":1,\"windows\":[{\"id\":1,\"app\":\"disks\"}]}")]
    [InlineData("{\"version\":1,\"windows\":[null]}")]
    public void RejectsUnknownShapes(string text)
    {
        var error = Assert.Throws<PithyxException>(() => LayoutRules.Check(Json(text)));
        Assert.Equal(ErrorCodes.DesktopLayoutInvalid, error.Code);
    }

    [Fact]
    public void RejectsMoreThanFiftyWindows()
    {
        LayoutRules.Check(Json(Layout(LayoutRules.MaxWindows)));
        var error = Assert.Throws<PithyxException>(() => LayoutRules.Check(Json(Layout(LayoutRules.MaxWindows + 1))));
        Assert.Equal(ErrorCodes.DesktopLayoutTooLarge, error.Code);
    }

    [Fact]
    public void RejectsMoreThanSixteenKilobytesOfAppData()
    {
        var eightKilobytes = JsonSerializer.Serialize(new string('x', 8 * 1024 - 2));
        LayoutRules.Check(Json(Layout(2, eightKilobytes)));
        var error = Assert.Throws<PithyxException>(() => LayoutRules.Check(Json(Layout(3, eightKilobytes))));
        Assert.Equal(ErrorCodes.DesktopLayoutTooLarge, error.Code);
    }
}
