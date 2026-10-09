using System.Globalization;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>
/// Whether a version is in a range of the manifest's <c>pithyx</c> (the syntax of npm's ranges: <c>||</c>, comparators,
/// hyphen ranges, x-ranges, <c>~</c> and <c>^</c>), by plain SemVer precedence (D330): unlike npm, a prerelease is in a
/// range whenever it sorts inside it, so a box on <c>0.2.0-beta.1</c> is in <c>&gt;=0.1.0</c>.
/// </summary>
public static class VersionRange
{
    public static bool Satisfies(string version, string range)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(range);
        return range.Split("||").Any(set => SetSatisfies(version, set.Trim()));
    }

    private static bool SetSatisfies(string version, string set)
    {
        if (set.Length == 0)
        {
            return true;
        }
        var hyphen = set.IndexOf(" - ", StringComparison.Ordinal);
        if (hyphen >= 0)
        {
            var low = Partial.Parse(set[..hyphen].Trim());
            var high = Partial.Parse(set[(hyphen + 3)..].Trim());
            var aboveLow = low.Lower() is not { } from || Compare(version, from) >= 0;
            var belowHigh = high.IsFull
                ? Compare(version, high.Text()) <= 0
                : high.UpperExclusive() is not { } to || Compare(version, to) < 0;
            return aboveLow && belowHigh;
        }
        foreach (var comparator in set.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ComparatorSatisfies(version, comparator))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ComparatorSatisfies(string version, string comparator)
    {
        var op = comparator.StartsWith(">=", StringComparison.Ordinal) || comparator.StartsWith("<=", StringComparison.Ordinal)
            ? comparator[..2]
            : comparator[0] is '>' or '<' or '=' or '~' or '^' ? comparator[..1] : "";
        var partial = Partial.Parse(comparator[op.Length..]);
        switch (op)
        {
            case "" or "=":
                return partial.IsFull
                    ? Compare(version, partial.Text()) == 0
                    : (partial.Lower() is not { } low || Compare(version, low) >= 0) && (partial.UpperExclusive() is not { } high || Compare(version, high) < 0);
            case ">=":
                return partial.Lower() is not { } atLeast || Compare(version, atLeast) >= 0;
            case ">":
                return partial.IsFull
                    ? Compare(version, partial.Text()) > 0
                    : partial.UpperExclusive() is { } above && Compare(version, above) >= 0;
            case "<":
                return partial.Lower() is { } below && Compare(version, below) < 0;
            case "<=":
                return partial.IsFull
                    ? Compare(version, partial.Text()) <= 0
                    : partial.UpperExclusive() is not { } upTo || Compare(version, upTo) < 0;
            case "~":
                return (partial.Lower() is not { } tildeLow || Compare(version, tildeLow) >= 0)
                    && (partial.Tilde() is not { } tildeHigh || Compare(version, tildeHigh) < 0);
            default:
                return (partial.Lower() is not { } caretLow || Compare(version, caretLow) >= 0)
                    && (partial.Caret() is not { } caretHigh || Compare(version, caretHigh) < 0);
        }
    }

    private static int Compare(string left, string right) => AppService.CompareVersions(left, right);

    /// <summary>A version with parts that may be missing or wildcards: <c>1</c>, <c>1.2</c>, <c>1.x</c>, <c>1.2.3-rc.1</c>.</summary>
    private readonly record struct Partial(long? Major, long? Minor, long? Patch, string? Prerelease)
    {
        public bool IsFull => Patch is not null;

        public static Partial Parse(string text)
        {
            var dash = text.IndexOf('-', StringComparison.Ordinal);
            var core = dash < 0 ? text : text[..dash];
            var prerelease = dash < 0 ? null : text[(dash + 1)..];
            var parts = core.Split('.');
            long? Part(int index) => index < parts.Length && parts[index] is not ("x" or "X" or "*" or "")
                ? long.Parse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture)
                : null;
            var major = Part(0);
            var minor = major is null ? null : Part(1);
            var patch = minor is null ? null : Part(2);
            return new Partial(major, minor, patch, patch is null ? null : prerelease);
        }

        public string Text() => $"{Major}.{Minor}.{Patch}{(Prerelease is null ? "" : "-" + Prerelease)}";

        /// <summary>The lowest version of the range the partial names, or null for any.</summary>
        public string? Lower() => Major is null ? null : IsFull ? Text() : $"{Major}.{Minor ?? 0}.0";

        /// <summary>The first version above the partial (<c>1.2</c> gives <c>1.3.0-0</c>), or null when none is above.</summary>
        public string? UpperExclusive() => Major is null ? null
            : Minor is null ? $"{Major + 1}.0.0-0"
            : Patch is null ? $"{Major}.{Minor + 1}.0-0"
            : $"{Major}.{Minor}.{Patch + 1}-0";

        public string? Tilde() => Major is null ? null : Minor is null ? $"{Major + 1}.0.0-0" : $"{Major}.{Minor + 1}.0-0";

        public string? Caret() => Major is null ? null
            : Major > 0 || Minor is null ? $"{Major + 1}.0.0-0"
            : Minor > 0 || Patch is null ? $"0.{Minor + 1}.0-0"
            : $"0.0.{Patch + 1}-0";
    }
}
