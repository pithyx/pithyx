using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlScalar = YamlDotNet.Core.Events.Scalar;

namespace Pithyx.Daemon.Apps.Compose;

/// <summary>
/// The YAML of a Compose file as plain data: mappings with string keys in source order, sequences and scalars with
/// their text. <c>@pithyx/cli</c> builds the same tree with the <c>yaml</c> package (<c>compose-yaml.ts</c>), so both
/// validators see one document.
/// </summary>
public abstract record ComposeNode;

/// <summary>A mapping; its keys are strings and unique.</summary>
public sealed record ComposeMap(IReadOnlyList<KeyValuePair<string, ComposeNode>> Entries) : ComposeNode
{
    public ComposeNode? Get(string key)
    {
        foreach (var (name, value) in Entries)
        {
            if (name == key)
            {
                return value;
            }
        }
        return null;
    }
}

public sealed record ComposeSequence(IReadOnlyList<ComposeNode> Items) : ComposeNode;

/// <summary>A scalar with its text; <paramref name="Plain"/> when unquoted and not a block scalar.</summary>
public sealed record ComposeScalar(string Value, bool Plain) : ComposeNode
{
    /// <summary>What the scalar stands for: plain scalars resolve like YAML 1.2, but only canonical decimals count as integers.</summary>
    public ComposeScalarKind Kind => !Plain ? ComposeScalarKind.Text
        : ComposeYaml.NullText().IsMatch(Value) ? ComposeScalarKind.Null
        : ComposeYaml.BoolText().IsMatch(Value) ? ComposeScalarKind.Bool
        : ComposeYaml.IntText().IsMatch(Value) ? ComposeScalarKind.WholeNumber
        : ComposeYaml.NumberText().IsMatch(Value) ? ComposeScalarKind.OtherNumber
        : ComposeScalarKind.Text;
}

public enum ComposeScalarKind
{
    Null,
    Bool,
    WholeNumber,
    OtherNumber,
    Text,
}

/// <summary>Reads the bytes of a Compose file into a <see cref="ComposeNode"/> with YamlDotNet's event stream.</summary>
public static partial class ComposeYaml
{
    private static readonly string[] _propertyReasons = ["directive", "anchor", "tag"];

    [GeneratedRegex(@"^(?:~|null|Null|NULL|)\z", RegexOptions.CultureInvariant)]
    internal static partial Regex NullText();

    [GeneratedRegex(@"^(?:true|True|TRUE|false|False|FALSE)\z", RegexOptions.CultureInvariant)]
    internal static partial Regex BoolText();

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]{0,15})\z", RegexOptions.CultureInvariant)]
    internal static partial Regex IntText();

    // Everything Go's YAML library, YAML 1.1 or YAML 1.2 would read as a number: refused wherever a value is expected,
    // because the author meant either a string (quote it) or a canonical integer.
    [GeneratedRegex(@"^(?:[-+]?(?:0[bBoOxX][0-9a-fA-F_]+|[0-9][0-9_]*(?:\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|\.[0-9][0-9_]*(?:[eE][-+]?[0-9]+)?|\.(?:inf|Inf|INF))|\.(?:nan|NaN|NAN))\z", RegexOptions.CultureInvariant)]
    internal static partial Regex NumberText();

    public static string EscapePointer(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// Reads a Compose file, refusing what the box never accepts: more than <paramref name="fileBytes"/>, text that is
    /// not UTF-8, tabs and ambiguous line breaks, syntax errors, other than one document, nesting deeper than
    /// <paramref name="maxDepth"/>, directives, anchors and aliases, tags, duplicate keys, keys that are not strings and
    /// merge keys. Every refusal is <c>app.compose.yaml_invalid</c> with a <c>reason</c>.
    /// </summary>
    public static (ComposeNode? Root, IReadOnlyList<ValidationIssue> Issues) Read(ReadOnlySpan<byte> utf8, int fileBytes, int maxDepth)
    {
        if (utf8.Length > fileBytes)
        {
            return Fail("too_large");
        }
        if (!Utf8.IsValid(utf8))
        {
            return Fail("not_utf8");
        }
        // Unlike Encoding.UTF8, this keeps a byte order mark as U+FEFF, which the character check refuses.
        var text = new UTF8Encoding(false, true).GetString(utf8);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsAllowedCharacter(rune.Value))
            {
                return Fail("character");
            }
        }

        var events = new List<ParsingEvent>();
        var documents = 0;
        try
        {
            var parser = new Parser(new StringReader(text));
            var depth = 0;
            while (parser.MoveNext())
            {
                var current = parser.Current!;
                switch (current)
                {
                    case MappingStart or SequenceStart:
                        if (++depth > maxDepth)
                        {
                            return Fail("too_deep");
                        }
                        break;
                    case MappingEnd or SequenceEnd:
                        depth--;
                        break;
                    case DocumentStart:
                        documents++;
                        break;
                }
                events.Add(current);
            }
        }
        catch (YamlException)
        {
            return Fail("syntax");
        }
        if (documents != 1)
        {
            return Fail("documents");
        }

        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (HasDirective(text))
        {
            reasons.Add("directive");
        }
        foreach (var current in events)
        {
            switch (current)
            {
                case AnchorAlias:
                    reasons.Add("anchor");
                    break;
                case NodeEvent node:
                    if (!node.Anchor.IsEmpty)
                    {
                        reasons.Add("anchor");
                    }
                    if (!node.Tag.IsEmpty)
                    {
                        reasons.Add("tag");
                    }
                    break;
            }
        }
        if (reasons.Count > 0)
        {
            return (null, [.. _propertyReasons.Where(reasons.Contains).Select(reason => Issue(reason))]);
        }

        var issues = new List<ValidationIssue>();
        var index = events.FindIndex(current => current is DocumentStart) + 1;
        var root = Build(events, ref index, "", issues);
        return issues.Count > 0 ? (null, issues) : (root, []);
    }

    // DocumentStart.Tags also lists the default tag handles, so directives come from the scanner's tokens.
    private static bool HasDirective(string text)
    {
        var scanner = new Scanner(new StringReader(text), skipComments: true);
        while (scanner.MoveNext())
        {
            if (scanner.Current is YamlDotNet.Core.Tokens.VersionDirective or YamlDotNet.Core.Tokens.TagDirective)
            {
                return true;
            }
        }
        return false;
    }

    private static (ComposeNode?, IReadOnlyList<ValidationIssue>) Fail(string reason) => (null, [Issue(reason)]);

    private static ValidationIssue Issue(string reason, string? path = null) => path is null
        ? new ValidationIssue(ErrorCodes.AppComposeYamlInvalid, ("reason", reason))
        : new ValidationIssue(ErrorCodes.AppComposeYamlInvalid, ("reason", reason), ("path", path));

    /// <summary>
    /// Printable YAML characters, line feeds and carriage returns. Tabs, NEL, the Unicode line and paragraph separators
    /// and the byte order mark are refused, because YAML 1.1 and 1.2 parsers disagree about them.
    /// </summary>
    private static bool IsAllowedCharacter(int point) =>
        point is 0x0a or 0x0d
        || point is >= 0x20 and <= 0x7e
        || (point is >= 0xa0 and <= 0xd7ff && point is not (0x2028 or 0x2029))
        || (point is >= 0xe000 and <= 0xfffd && point != 0xfeff)
        || point >= 0x10000;

    private static ComposeNode Build(List<ParsingEvent> events, ref int index, string path, List<ValidationIssue> issues)
    {
        var current = events[index++];
        switch (current)
        {
            case YamlScalar scalar:
                return new ComposeScalar(scalar.Value, scalar.Style is ScalarStyle.Plain or ScalarStyle.Any);
            case SequenceStart:
                var items = new List<ComposeNode>();
                while (events[index] is not SequenceEnd)
                {
                    items.Add(Build(events, ref index, $"{path}/{items.Count}", issues));
                }
                index++;
                return new ComposeSequence(items);
            case MappingStart:
                return BuildMap(events, ref index, path, issues);
            default:
                throw new InvalidOperationException($"Unexpected YAML event {current.GetType().Name}.");
        }
    }

    private static ComposeMap BuildMap(List<ParsingEvent> events, ref int index, string path, List<ValidationIssue> issues)
    {
        var entries = new List<KeyValuePair<string, ComposeNode>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (events[index] is not MappingEnd)
        {
            if (events[index] is not YamlScalar key)
            {
                issues.Add(Issue("key_not_string", path));
                Skip(events, ref index);
                Skip(events, ref index);
                continue;
            }
            index++;
            var scalar = new ComposeScalar(key.Value, key.Style is ScalarStyle.Plain or ScalarStyle.Any);
            var keyPath = $"{path}/{EscapePointer(key.Value)}";
            if (key.Value == "<<")
            {
                issues.Add(Issue("merge_key", keyPath));
            }
            else if (scalar.Kind != ComposeScalarKind.Text)
            {
                issues.Add(Issue("key_not_string", path));
            }
            else if (!seen.Add(key.Value))
            {
                issues.Add(Issue("duplicate_key", keyPath));
            }
            else
            {
                entries.Add(new(key.Value, Build(events, ref index, keyPath, issues)));
                continue;
            }
            Skip(events, ref index);
        }
        index++;
        return new ComposeMap(entries);
    }

    private static void Skip(List<ParsingEvent> events, ref int index)
    {
        var depth = 0;
        do
        {
            depth += events[index++] switch
            {
                MappingStart or SequenceStart => 1,
                MappingEnd or SequenceEnd => -1,
                _ => 0,
            };
        }
        while (depth > 0);
    }
}
