using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Xml;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>
/// Checks an app's <c>icon.svg</c>: at most 64 KB of well-formed UTF-8 XML with an <c>svg</c> root in the SVG namespace
/// and a square <c>viewBox</c>, without a DOCTYPE, processing instructions, scripts, foreign content, embedded or
/// external images, event attributes, links outside the file, or <c>@import</c>, <c>url()</c> to anything but a
/// fragment and escapes in styles. <c>@pithyx/cli</c> applies the same rules; the corpus in
/// <c>sdk/schema/fixtures/icon</c> holds both alike. The icon is still served with <c>default-src 'none'; sandbox</c>.
/// </summary>
public static partial class IconValidator
{
    public const int MaxBytes = 64 * 1024;

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> _forbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "image", "iframe", "embed", "object", "audio", "video", "canvas",
    };

    private static readonly HashSet<string> _animationElements = new(StringComparer.OrdinalIgnoreCase) { "set", "animate" };

    public static IReadOnlyList<ValidationIssue> Validate(ReadOnlySpan<byte> svg)
    {
        if (svg.Length > MaxBytes)
        {
            return [Issue("too_large")];
        }
        if (svg.StartsWith("﻿"u8))
        {
            svg = svg[3..];
        }
        if (!Utf8.IsValid(svg))
        {
            return [Issue("malformed")];
        }
        var text = Encoding.UTF8.GetString(svg);
        // A DOCTYPE could declare entities; it is refused before parsing, wherever it appears.
        if (text.Contains("<!DOCTYPE", StringComparison.Ordinal))
        {
            return [Issue("doctype")];
        }

        var issues = new List<ValidationIssue>();
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var styleDepth = -1;
            var sawRoot = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (!sawRoot)
                        {
                            sawRoot = true;
                            if (reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace)
                            {
                                return [Issue("not_svg")];
                            }
                            if (!IsSquareViewBox(reader.GetAttribute("viewBox")))
                            {
                                Add(issues, Issue("viewbox"));
                            }
                        }
                        CheckElement(reader, issues);
                        if (reader.LocalName == "style" && !reader.IsEmptyElement)
                        {
                            styleDepth = reader.Depth;
                        }
                        break;
                    case XmlNodeType.EndElement when reader.Depth == styleDepth:
                        styleDepth = -1;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA when styleDepth >= 0:
                        CheckStyle(reader.Value, issues);
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        Add(issues, Issue("processing_instruction"));
                        break;
                    default:
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return [Issue("malformed")];
        }
        return issues;
    }

    private static void CheckElement(XmlReader reader, List<ValidationIssue> issues)
    {
        var element = reader.LocalName;
        if (_forbiddenElements.Contains(element))
        {
            Add(issues, Issue("element_forbidden", ("element", element)));
        }
        if (!reader.MoveToFirstAttribute())
        {
            return;
        }
        do
        {
            if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
            {
                continue;
            }
            var name = reader.LocalName;
            var value = reader.Value;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                Add(issues, Issue("event_attribute", ("attribute", name)));
            }
            else if (name == "href" && !value.Trim().StartsWith('#'))
            {
                Add(issues, Issue("external_href"));
            }
            else if (name == "attributeName" && _animationElements.Contains(element) && value.Contains("href", StringComparison.OrdinalIgnoreCase))
            {
                Add(issues, Issue("external_href"));
            }
            else if (name == "style")
            {
                CheckStyle(value, issues);
            }
            else if (ExternalUrl().IsMatch(value))
            {
                Add(issues, Issue("style_url"));
            }
        }
        while (reader.MoveToNextAttribute());
        reader.MoveToElement();
    }

    private static void CheckStyle(string style, List<ValidationIssue> issues)
    {
        if (style.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, Issue("style_import"));
        }
        if (ExternalUrl().IsMatch(style))
        {
            Add(issues, Issue("style_url"));
        }
        if (style.Contains('\\', StringComparison.Ordinal))
        {
            Add(issues, Issue("style_escape"));
        }
    }

    private static bool IsSquareViewBox(string? viewBox)
    {
        if (viewBox is null)
        {
            return false;
        }
        var parts = ViewBoxSeparator().Split(viewBox.Trim());
        if (parts.Length != 4 || !parts.All(part => Number().IsMatch(part)))
        {
            return false;
        }
        var width = double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
        var height = double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture);
        return width > 0 && width == height;
    }

    private static void Add(List<ValidationIssue> issues, ValidationIssue issue)
    {
        if (!issues.Exists(existing => existing.Code == issue.Code && existing.Params.SequenceEqual(issue.Params)))
        {
            issues.Add(issue);
        }
    }

    private static ValidationIssue Issue(string reason, params (string Name, object? Value)[] extra) =>
        new(ErrorCodes.AppIconInvalid, [("reason", reason), .. extra]);

    // url( with anything but a fragment reference (#id) after optional whitespace and quote.
    [GeneratedRegex("url\\((?![ \\t\\r\\n\\f]*['\"]?#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalUrl();

    [GeneratedRegex("[ \\t\\r\\n,]+", RegexOptions.CultureInvariant)]
    private static partial Regex ViewBoxSeparator();

    [GeneratedRegex("^[+-]?([0-9]+\\.?[0-9]*|\\.[0-9]+)([eE][+-]?[0-9]+)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}
