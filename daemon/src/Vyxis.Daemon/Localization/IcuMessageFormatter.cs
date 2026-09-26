using System.Globalization;
using System.Text;

namespace Vyxis.Daemon.Localization;

/// <summary>
/// Renders the ICU MessageFormat subset the i18n files use: simple arguments, <c>plural</c> (with <c>=N</c>,
/// <c>one</c>, <c>other</c> and <c>#</c>), <c>select</c> and apostrophe quoting. The desktop renders the same files
/// with intl-messageformat; this exists for the English API fallback and later for out-of-band messages.
/// </summary>
public static class IcuMessageFormatter
{
    public static string Format(string pattern, IReadOnlyDictionary<string, object?> arguments, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder(pattern.Length);
        Render(pattern, arguments, culture, pluralNumber: null, builder);
        return builder.ToString();
    }

    private static void Render(
        string pattern,
        IReadOnlyDictionary<string, object?> arguments,
        CultureInfo culture,
        decimal? pluralNumber,
        StringBuilder builder)
    {
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                i = AppendQuoted(pattern, i, builder);
            }
            else if (c == '#' && pluralNumber is { } number)
            {
                builder.Append(number.ToString(culture));
                i++;
            }
            else if (c == '{')
            {
                var end = FindClosingBrace(pattern, i);
                RenderArgument(pattern[(i + 1)..end], arguments, culture, builder);
                i = end + 1;
            }
            else if (c == '}')
            {
                throw new FormatException($"Unbalanced '}}' at {i} in \"{pattern}\".");
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }
    }

    // ICU quoting: '' is a literal apostrophe; '{...}' outputs its content literally; any other ' is literal.
    private static int AppendQuoted(string pattern, int start, StringBuilder builder)
    {
        if (start + 1 < pattern.Length && pattern[start + 1] == '\'')
        {
            builder.Append('\'');
            return start + 2;
        }
        if (start + 1 < pattern.Length && pattern[start + 1] is '{' or '}' or '#')
        {
            var end = pattern.IndexOf('\'', start + 1);
            end = end == -1 ? pattern.Length : end;
            builder.Append(pattern, start + 1, end - start - 1);
            return Math.Min(end + 1, pattern.Length);
        }
        builder.Append('\'');
        return start + 1;
    }

    private static int FindClosingBrace(string pattern, int open)
    {
        var depth = 0;
        for (var i = open; i < pattern.Length; i++)
        {
            if (pattern[i] == '\'' && i + 1 < pattern.Length && pattern[i + 1] is '{' or '}')
            {
                var end = pattern.IndexOf('\'', i + 1);
                i = end == -1 ? pattern.Length : end;
                continue;
            }
            if (pattern[i] == '{')
            {
                depth++;
            }
            else if (pattern[i] == '}' && --depth == 0)
            {
                return i;
            }
        }
        throw new FormatException($"Unclosed '{{' at {open} in \"{pattern}\".");
    }

    private static void RenderArgument(
        string argument,
        IReadOnlyDictionary<string, object?> arguments,
        CultureInfo culture,
        StringBuilder builder)
    {
        var firstComma = argument.IndexOf(',', StringComparison.Ordinal);
        var name = (firstComma == -1 ? argument : argument[..firstComma]).Trim();
        arguments.TryGetValue(name, out var value);
        if (firstComma == -1)
        {
            builder.Append(value is null ? $"{{{name}}}" : FormatValue(value, culture));
            return;
        }

        var secondComma = argument.IndexOf(',', firstComma + 1);
        var type = (secondComma == -1 ? argument[(firstComma + 1)..] : argument[(firstComma + 1)..secondComma]).Trim();
        var options = secondComma == -1 ? "" : argument[(secondComma + 1)..];
        switch (type)
        {
            case "number":
                builder.Append(value is null ? $"{{{name}}}" : FormatValue(value, culture));
                break;
            case "plural":
                var number = ToDecimal(value);
                var cases = ParseCases(options);
                var exact = cases.GetValueOrDefault($"={number.ToString(CultureInfo.InvariantCulture)}");
                var chosen = exact ?? cases.GetValueOrDefault(PluralCategory(number)) ?? cases.GetValueOrDefault("other") ?? "";
                Render(chosen, arguments, culture, number, builder);
                break;
            case "select":
                var selectCases = ParseCases(options);
                var key = value is null ? "other" : FormatValue(value, CultureInfo.InvariantCulture);
                Render(selectCases.GetValueOrDefault(key) ?? selectCases.GetValueOrDefault("other") ?? "", arguments, culture, null, builder);
                break;
            default:
                throw new FormatException($"Unsupported argument type '{type}' for '{name}'.");
        }
    }

    private static Dictionary<string, string> ParseCases(string options)
    {
        var cases = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < options.Length)
        {
            while (i < options.Length && char.IsWhiteSpace(options[i]))
            {
                i++;
            }
            if (i >= options.Length)
            {
                break;
            }
            var open = options.IndexOf('{', i);
            if (open == -1)
            {
                throw new FormatException($"Missing message for case in \"{options}\".");
            }
            var selector = options[i..open].Trim();
            var close = FindClosingBrace(options, open);
            cases[selector] = options[(open + 1)..close];
            i = close + 1;
        }
        return cases;
    }

    // English and German share this rule. Other languages need their CLDR rules when they are added.
    private static string PluralCategory(decimal number) => number == 1 ? "one" : "other";

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0,
        IConvertible convertible => convertible.ToDecimal(CultureInfo.InvariantCulture),
        _ => decimal.Parse(value.ToString() ?? "0", CultureInfo.InvariantCulture),
    };

    private static string FormatValue(object value, CultureInfo culture) => value switch
    {
        IFormattable formattable => formattable.ToString(null, culture),
        _ => value.ToString() ?? "",
    };
}
