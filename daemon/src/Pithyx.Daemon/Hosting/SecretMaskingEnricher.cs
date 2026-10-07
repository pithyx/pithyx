using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// Replaces the values of log properties whose names look like secrets, at any nesting depth, and inside any text the
/// app tokens and tickets of D303 wherever they appear: <c>pithyx-token=</c> and <c>pithyx-ticket=</c> in a URL, and
/// bearer credentials.
/// </summary>
public sealed partial class SecretMaskingEnricher : ILogEventEnricher
{
    public const string Mask = "***";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var masked = MaskValue(name, value);
            if (!ReferenceEquals(masked, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, masked));
            }
        }
    }

    public static bool IsSensitive(string name) => SensitiveName().IsMatch(name);

    private static LogEventPropertyValue MaskValue(string name, LogEventPropertyValue value)
    {
        if (IsSensitive(name))
        {
            return new ScalarValue(Mask);
        }
        switch (value)
        {
            case StructureValue structure:
                var properties = structure.Properties.Select(p => new LogEventProperty(p.Name, MaskValue(p.Name, p.Value))).ToList();
                return properties.Zip(structure.Properties).All(pair => ReferenceEquals(pair.First.Value, pair.Second.Value))
                    ? value
                    : new StructureValue(properties, structure.TypeTag);
            case DictionaryValue dictionary:
                var elements = dictionary.Elements
                    .Select(e => KeyValuePair.Create(e.Key, MaskValue(e.Key.Value?.ToString() ?? "", e.Value)))
                    .ToList();
                return elements.Zip(dictionary.Elements).All(pair => ReferenceEquals(pair.First.Value, pair.Second.Value))
                    ? value
                    : new DictionaryValue(elements);
            case SequenceValue sequence:
                var items = sequence.Elements.Select(e => MaskValue("", e)).ToList();
                return items.Zip(sequence.Elements).All(pair => ReferenceEquals(pair.First, pair.Second))
                    ? value
                    : new SequenceValue(items);
            case ScalarValue { Value: string text } when CarriesCredential(text):
                return new ScalarValue(MaskCredentials(text));
            default:
                return value;
        }
    }

    /// <summary>Whether a text holds an app token, a ticket or a bearer credential.</summary>
    public static bool CarriesCredential(string text) => Credential().IsMatch(text);

    /// <summary>The text with every app token, ticket and bearer credential replaced by <see cref="Mask"/>.</summary>
    public static string MaskCredentials(string text) => Credential().Replace(text, match => match.Groups["name"].Value + Mask);

    [GeneratedRegex("password|passphrase|secret|token|cookie|authorization|recovery_?key|totp", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"(?<name>pithyx-(?:token|ticket)=|bearer\s+)[^\s&""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Credential();
}
