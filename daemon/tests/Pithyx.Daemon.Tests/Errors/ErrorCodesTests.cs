using System.Text.Json;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Errors;

public sealed partial class ErrorCodesTests
{
    public static TheoryData<string> Codes => [.. ErrorCodes.All.Keys];

    [Theory]
    [MemberData(nameof(Codes))]
    public void CodeFollowsNamingScheme(string code) => Assert.Matches(CodePattern(), code);

    [Theory]
    [MemberData(nameof(Codes))]
    public void CodeHasEnglishText(string code) => Assert.True(ErrorMessages.English.ContainsKey(code), $"i18n/en/errors.json lacks {code}");

    [Theory]
    [MemberData(nameof(Codes))]
    public void CodeHasGermanText(string code)
    {
        using var german = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root, "i18n", "de", "errors.json")));
        var element = german.RootElement;
        foreach (var segment in code.Split('.'))
        {
            Assert.True(element.TryGetProperty(segment, out element), $"i18n/de/errors.json lacks {code}");
        }
        Assert.Equal(JsonValueKind.String, element.ValueKind);
    }

    [Fact]
    public void UnknownCodeIsRejected() => Assert.Throws<ArgumentException>(() => new PithyxException("made.up.code"));

    [GeneratedRegex("^[a-z0-9_]+\\.[a-z0-9_]+\\.[a-z0-9_]+$")]
    private static partial Regex CodePattern();
}
