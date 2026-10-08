using System.Globalization;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>
/// Whitespace as ECMAScript's <c>trim</c> and <c>\s</c> see it, so the catalog rules split text exactly as
/// <c>@pithyx/cli</c> does: .NET also counts U+0085 and not U+FEFF.
/// </summary>
internal static class EcmaText
{
    public static bool IsWhiteSpace(char character) =>
        character is '\t' or '\n' or '\v' or '\f' or '\r' or '\u2028' or '\u2029' or '\uFEFF'
        || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

    public static string Trim(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsWhiteSpace(text[start]))
        {
            start++;
        }
        while (end > start && IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary><c>text.split(/\s+/)</c> of a trimmed text: the words, or one empty word for an empty text.</summary>
    public static List<string> Words(string trimmed)
    {
        var words = new List<string>();
        var start = 0;
        for (var index = 0; index <= trimmed.Length; index++)
        {
            if (index == trimmed.Length || IsWhiteSpace(trimmed[index]))
            {
                if (index > start || trimmed.Length == 0)
                {
                    words.Add(trimmed[start..index]);
                }
                start = index + 1;
            }
        }
        return words;
    }
}
