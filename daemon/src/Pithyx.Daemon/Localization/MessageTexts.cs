using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Pithyx.Daemon.Localization;

/// <summary>
/// The texts the box writes itself in a recipient's language (D379, #209): notifications, errors and job kinds from
/// <c>i18n/&lt;language&gt;/</c>, built into the daemon. The daemon keeps invariant globalization, so each language
/// brings its number format here and its plural rule in <see cref="IcuMessageFormatter"/>.
/// </summary>
public static class MessageTexts
{
    public const string DefaultLanguage = "en";

    /// <summary>The languages the box can write in.</summary>
    public static readonly FrozenSet<string> Languages = new[] { "en", "de" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] _namespaces = ["notifications", "errors", "jobs"];

    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> _texts = Load();

    private static readonly FrozenDictionary<string, CultureInfo> _cultures = new Dictionary<string, CultureInfo>(StringComparer.Ordinal)
    {
        ["en"] = Culture(".", ","),
        ["de"] = Culture(",", "."),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>A supported language for the given one, or English.</summary>
    public static string Pick(string? language) =>
        language is not null && Languages.Contains(language) ? language : DefaultLanguage;

    /// <summary>The text of <paramref name="key"/> (for example <c>notifications:update.available.title</c>) with its arguments, in the language or in English.</summary>
    public static string? Format(string language, string key, IReadOnlyDictionary<string, object?> arguments)
    {
        var picked = Pick(language);
        var pattern = _texts[picked].GetValueOrDefault(key) ?? _texts[DefaultLanguage].GetValueOrDefault(key);
        if (pattern is null)
        {
            return null;
        }
        try
        {
            return IcuMessageFormatter.Format(pattern, arguments, _cultures[picked]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a text exists for the key in English, the reference.</summary>
    public static bool Has(string key) => _texts[DefaultLanguage].ContainsKey(key);

    private static CultureInfo Culture(string decimalSeparator, string groupSeparator)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = decimalSeparator;
        culture.NumberFormat.NumberGroupSeparator = groupSeparator;
        return culture;
    }

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var languages = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Languages)
        {
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in _namespaces)
            {
                var resource = $"i18n/{language}/{name}.json";
                using var stream = typeof(MessageTexts).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Embedded resource {resource} is missing.");
                using var document = JsonDocument.Parse(stream);
                Flatten(document.RootElement, $"{name}:", texts);
            }
            languages[language] = texts.ToFrozenDictionary(StringComparer.Ordinal);
        }
        return languages.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> texts)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.EndsWith(':') ? prefix + property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(property.Value, key, texts);
            }
            else if (property.Value.ValueKind == JsonValueKind.String)
            {
                texts[key] = property.Value.GetString()!;
            }
        }
    }
}
