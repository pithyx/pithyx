using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Json.Schema;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>
/// One of the schemas in <c>sdk/schema</c> (embedded at build), evaluated as <c>@pithyx/cli</c> evaluates it: the
/// violations as (JSON Pointer, keyword) pairs, sorted, a missing or unknown field at its own pointer. Also the strict
/// JSON reading both share.
/// </summary>
internal sealed class JsonSchemaCheck
{
    private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

    private static readonly Comparer<(string Path, string Keyword)> _order = Comparer<(string Path, string Keyword)>.Create((a, b) =>
        a.Path == b.Path ? string.CompareOrdinal(a.Keyword, b.Keyword) : string.CompareOrdinal(a.Path, b.Path));

    private readonly JsonSchema _schema;
    private readonly JsonNode _document;

    private JsonSchemaCheck(JsonSchema schema, JsonNode document)
    {
        _schema = schema;
        _document = document;
    }

    /// <summary>Loads the embedded schema <c>schema/<paramref name="file"/></c>.</summary>
    public static JsonSchemaCheck Load(string file)
    {
        using var stream = typeof(JsonSchemaCheck).Assembly.GetManifestResourceStream($"schema/{file}")
            ?? throw new InvalidOperationException($"The schema {file} is not embedded.");
        var document = JsonNode.Parse(stream)!;
        AnchorPatterns(document);
        return new JsonSchemaCheck(JsonSchema.FromText(document.ToJsonString()), document);
    }

    /// <summary>
    /// Parses UTF-8 JSON and refuses an object with a key twice, which <c>JSON.parse</c> would accept by keeping the last
    /// one. Returns null when the bytes are not such a document.
    /// </summary>
    public static JsonDocument? ParseStrict(ReadOnlyMemory<byte> utf8)
    {
        if (!Utf8.IsValid(utf8.Span))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(utf8, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool IsValid(JsonElement root) => _schema.Evaluate(root, _evaluation).IsValid;

    public IReadOnlyList<(string Path, string Keyword)> Violations(JsonElement root)
    {
        var results = _schema.Evaluate(root, _evaluation);
        var violations = new SortedSet<(string Path, string Keyword)>(_order);
        if (results.IsValid)
        {
            return [];
        }
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null)
            {
                continue;
            }
            var path = detail.InstanceLocation.ToString();
            foreach (var keyword in detail.Errors.Keys)
            {
                if (keyword == "required")
                {
                    foreach (var missing in MissingProperties(detail.SchemaLocation, root, path))
                    {
                        violations.Add((path + "/" + EscapePointer(missing), "required"));
                    }
                }
                else if (keyword.Length == 0)
                {
                    // A false schema fails at the value itself; additionalProperties: false is the only one we use.
                    violations.Add((path, detail.EvaluationPath.ToString().Split('/')[^1]));
                }
                else
                {
                    violations.Add((path, keyword));
                }
            }
        }
        return [.. violations];
    }

    /// <summary>A param value as the CLI shows it: strings as they are, everything else as compact JSON.</summary>
    public static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };

    public static string EscapePointer(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private IEnumerable<string> MissingProperties(Uri schemaLocation, JsonElement root, string instancePath)
    {
        var schemaNode = Resolve(_document, Uri.UnescapeDataString(schemaLocation.Fragment.TrimStart('#')));
        var instance = Resolve(root, instancePath);
        if (schemaNode?["required"] is not JsonArray required || instance is not { ValueKind: JsonValueKind.Object } value)
        {
            return [];
        }
        return required.Select(name => name!.GetValue<string>()).Where(name => !value.TryGetProperty(name, out _));
    }

    private static JsonNode? Resolve(JsonNode? node, string pointer)
    {
        var current = node;
        foreach (var segment in Segments(pointer))
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
                _ => null,
            };
        }
        return current;
    }

    private static JsonElement? Resolve(JsonElement element, string pointer)
    {
        var current = element;
        foreach (var segment in Segments(pointer))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, CultureInfo.InvariantCulture, out var index) && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    private static IEnumerable<string> Segments(string pointer) =>
        pointer.Length == 0 ? [] : pointer[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));

    private static string Anchor(string pattern) => pattern.EndsWith('$') ? pattern + "(?!\\n)" : pattern;

    // .NET's $ also matches before a final line break, ECMAScript's only at the end; this keeps both engines alike.
    private static void AnchorPatterns(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (name == "pattern" && value is JsonValue pattern)
                    {
                        obj[name] = Anchor(pattern.GetValue<string>());
                    }
                    else if (name == "patternProperties" && value is JsonObject patterns)
                    {
                        var anchored = new JsonObject();
                        foreach (var (key, schema) in patterns.ToList())
                        {
                            patterns.Remove(key);
                            AnchorPatterns(schema);
                            // JsonSchema.Net finds a pattern again by its JSON Pointer token, where ~ reads as ~0.
                            anchored[Anchor(key).Replace("~", "\\x7E", StringComparison.Ordinal)] = schema;
                        }
                        obj[name] = anchored;
                    }
                    else
                    {
                        AnchorPatterns(value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    AnchorPatterns(item);
                }
                break;
        }
    }
}
