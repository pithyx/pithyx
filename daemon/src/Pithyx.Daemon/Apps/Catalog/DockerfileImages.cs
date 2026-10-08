using System.Globalization;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>An image a Dockerfile pulls without a digest, at the line where its instruction starts (from 1).</summary>
public sealed record UnpinnedImage(int Line, string Image);

/// <summary>
/// The images a Dockerfile pulls without a digest, as <c>@pithyx/cli</c> finds them: every FROM, and every <c>--from</c>
/// of COPY and ADD that does not name an earlier stage, must be <c>scratch</c>, an earlier stage, or an image pinned with
/// <c>@sha256:</c> and no variable. Instructions may continue over lines with the escape character (<c>\</c> or the
/// <c>escape</c> parser directive); comments are skipped.
/// </summary>
public static partial class DockerfileImages
{
    private const string Space = @"[\t\n\v\f\r\p{Zs}\u2028\u2029\uFEFF]";
    private const string NotSpace = @"[^\t\n\v\f\r\p{Zs}\u2028\u2029\uFEFF]";

    public static IReadOnlyList<UnpinnedImage> Unpinned(string text)
    {
        var lines = text.Split('\n');
        var escape = "\\";
        var first = 0;
        while (first < lines.Length && Directive().IsMatch(lines[first]))
        {
            var directive = DirectiveValue().Match(lines[first]);
            if (directive.Success && directive.Groups[1].Value.Equals("escape", StringComparison.OrdinalIgnoreCase))
            {
                escape = directive.Groups[2].Value;
            }
            first++;
        }

        var unpinned = new List<UnpinnedImage>();
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var stageCount = 0;

        void Check(string? image, int line)
        {
            var name = image ?? "";
            var lower = name.ToLowerInvariant();
            if (lower == "scratch" || stages.Contains(lower))
            {
                return;
            }
            if (name.Contains('$', StringComparison.Ordinal) || !Pinned().IsMatch(name))
            {
                unpinned.Add(new UnpinnedImage(line, name));
            }
        }

        void Instruction(List<string> words, int line)
        {
            var keyword = words[0].ToUpperInvariant();
            if (keyword == "FROM")
            {
                var rest = words.Skip(1).Where(word => !word.StartsWith("--", StringComparison.Ordinal)).ToList();
                Check(rest.ElementAtOrDefault(0), line);
                if (rest.Count > 2 && rest[1].Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    stages.Add(rest[2].ToLowerInvariant());
                }
                stageCount++;
            }
            else if (keyword is "COPY" or "ADD")
            {
                foreach (var word in words.Skip(1))
                {
                    if (!word.StartsWith("--", StringComparison.Ordinal))
                    {
                        break;
                    }
                    if (word.ToLowerInvariant().StartsWith("--from=", StringComparison.Ordinal))
                    {
                        var source = word["--from=".Length..];
                        var isIndex = source.Length > 0 && source.All(char.IsAsciiDigit);
                        if (!isIndex || double.Parse(source, CultureInfo.InvariantCulture) >= stageCount)
                        {
                            Check(source, line);
                        }
                    }
                }
            }
        }

        var parts = new List<string>();
        var start = 0;
        for (var index = first; index < lines.Length; index++)
        {
            var trimmed = EcmaText.Trim(lines[index]);
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            if (parts.Count == 0)
            {
                start = index + 1;
            }
            if (trimmed.EndsWith(escape, StringComparison.Ordinal))
            {
                parts.Add(trimmed[..^escape.Length]);
                continue;
            }
            parts.Add(trimmed);
            Instruction(EcmaText.Words(EcmaText.Trim(string.Join(' ', parts))), start);
            parts.Clear();
        }
        if (parts.Count > 0)
        {
            Instruction(EcmaText.Words(EcmaText.Trim(string.Join(' ', parts))), start);
        }
        return unpinned;
    }

    [GeneratedRegex("^#" + Space + "*[A-Za-z]+" + Space + "*=", RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    [GeneratedRegex("^#" + Space + "*([A-Za-z]+)" + Space + "*=" + Space + "*(" + NotSpace + ")" + Space + @"*\z", RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveValue();

    [GeneratedRegex(@"@sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pinned();
}
