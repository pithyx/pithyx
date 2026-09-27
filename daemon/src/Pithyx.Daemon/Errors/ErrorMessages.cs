using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Pithyx.Daemon.Localization;

namespace Pithyx.Daemon.Errors;

/// <summary>English error texts from <c>i18n/en/errors.json</c>, used for the <c>detail</c> fallback of problem details.</summary>
public static class ErrorMessages
{
    // The daemon runs with invariant globalization; invariant formatting matches English closely enough for a fallback.
    private static readonly CultureInfo _english = CultureInfo.InvariantCulture;

    public static FrozenDictionary<string, string> English { get; } = Load();

    public static string Detail(string code, IReadOnlyDictionary<string, object?> parameters) =>
        English.TryGetValue(code, out var pattern) ? IcuMessageFormatter.Format(pattern, parameters, _english) : code;

    private static FrozenDictionary<string, string> Load()
    {
        using var stream = typeof(ErrorMessages).Assembly.GetManifestResourceStream("i18n/en/errors.json")
            ?? throw new InvalidOperationException("Embedded resource i18n/en/errors.json is missing.");
        using var document = JsonDocument.Parse(stream);
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(document.RootElement, "", messages);
        return messages.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> messages)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(property.Value, key, messages);
            }
            else
            {
                messages[key] = property.Value.GetString() ?? "";
            }
        }
    }
}
