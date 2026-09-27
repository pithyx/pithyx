using System.Globalization;
using System.Text.Json;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Localization;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Localization;

/// <summary>
/// The desktop renders the same messages with i18next-icu (desktop/src/icu-golden.test.ts). Both must match
/// tools/i18n/icu-golden.json exactly.
/// </summary>
public sealed class IcuGoldenTests
{
    private static readonly JsonElement _golden = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(RepositoryFiles.Root, "tools", "i18n", "icu-golden.json"))).RootElement;

    public static TheoryData<string> ErrorCodesWithText => [.. ErrorMessages.English.Keys];

    public static TheoryData<int> CaseIndexes => [.. Enumerable.Range(0, _golden.GetProperty("cases").GetArrayLength())];

    [Theory]
    [MemberData(nameof(ErrorCodesWithText))]
    public void ErrorMessageMatchesGolden(string code)
    {
        Assert.True(_golden.GetProperty("errors").TryGetProperty(code, out var entry), $"tools/i18n/icu-golden.json lacks {code}");

        Assert.Equal(entry.GetProperty("expected").GetString(), ErrorMessages.Detail(code, Parameters(entry)));
    }

    [Fact]
    public void GoldenHasNoStaleErrorCodes() =>
        Assert.All(_golden.GetProperty("errors").EnumerateObject(), entry => Assert.Contains(entry.Name, ErrorMessages.English.Keys));

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void CaseMatchesGolden(int index)
    {
        var entry = _golden.GetProperty("cases")[index];

        var rendered = IcuMessageFormatter.Format(entry.GetProperty("message").GetString()!, Parameters(entry), CultureInfo.InvariantCulture);

        Assert.Equal(entry.GetProperty("expected").GetString(), rendered);
    }

    private static Dictionary<string, object?> Parameters(JsonElement entry) =>
        entry.GetProperty("params").EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.GetDecimal(),
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (object?)null,
            });
}
