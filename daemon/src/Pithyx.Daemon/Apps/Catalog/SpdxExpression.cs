using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>The licenses and exceptions of an SPDX license expression.</summary>
public sealed record SpdxLicenses(IReadOnlyList<string> Licenses, IReadOnlyList<string> Exceptions);

/// <summary>
/// Parses SPDX license expressions (<c>MIT</c>, <c>MIT OR Apache-2.0</c>,
/// <c>(GPL-2.0-only WITH Classpath-exception-2.0) AND BSD-3-Clause</c>) as <c>@pithyx/cli</c> does. Operators are upper
/// case; a <c>+</c> after a license is kept.
/// </summary>
public static partial class SpdxExpression
{
    private static readonly HashSet<string> _operators = new(StringComparer.Ordinal) { "AND", "OR", "WITH" };

    /// <summary>The licenses and exceptions of <paramref name="expression"/>, or null when it is not an expression.</summary>
    public static SpdxLicenses? Parse(string expression)
    {
        var tokens = Tokens(expression);
        var licenses = new List<string>();
        var exceptions = new List<string>();
        var position = 0;

        string? Token(int at) => at < tokens.Count ? tokens[at] : null;

        bool Atom()
        {
            var token = Token(position);
            if (token == "(")
            {
                position++;
                if (!Or() || Token(position) != ")")
                {
                    return false;
                }
                position++;
                return true;
            }
            if (token is null || _operators.Contains(token) || !Id().IsMatch(token))
            {
                return false;
            }
            licenses.Add(token);
            position++;
            if (Token(position) == "WITH")
            {
                var exception = Token(position + 1);
                if (exception is null || _operators.Contains(exception) || !Id().IsMatch(exception) || exception.EndsWith('+'))
                {
                    return false;
                }
                exceptions.Add(exception);
                position += 2;
            }
            return true;
        }

        bool And()
        {
            if (!Atom())
            {
                return false;
            }
            while (Token(position) == "AND")
            {
                position++;
                if (!Atom())
                {
                    return false;
                }
            }
            return true;
        }

        bool Or()
        {
            if (!And())
            {
                return false;
            }
            while (Token(position) == "OR")
            {
                position++;
                if (!And())
                {
                    return false;
                }
            }
            return true;
        }

        return Or() && position == tokens.Count ? new SpdxLicenses(licenses, exceptions) : null;
    }

    /// <summary>Parentheses and the runs of other characters between whitespace.</summary>
    private static List<string> Tokens(string expression)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < expression.Length)
        {
            var character = expression[index];
            if (character is '(' or ')')
            {
                tokens.Add(character.ToString());
                index++;
            }
            else if (EcmaText.IsWhiteSpace(character))
            {
                index++;
            }
            else
            {
                var start = index;
                while (index < expression.Length && expression[index] is not ('(' or ')') && !EcmaText.IsWhiteSpace(expression[index]))
                {
                    index++;
                }
                tokens.Add(expression[start..index]);
            }
        }
        return tokens;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]*\+?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Id();
}
