using System.Text.Json;
using Pithyx.Daemon.Localization;

namespace Pithyx.Daemon.Notifications;

/// <summary>A notification as text for a channel, in the recipient's language.</summary>
/// <param name="Title">The title, for an app's notification with the app's name in front.</param>
/// <param name="Body">The body; may be empty.</param>
/// <param name="Footer">Which box sent it.</param>
public sealed record RenderedNotification(string Title, string Body, string Footer);

/// <summary>
/// Renders notifications as the desktop does (D379): the texts of <c>notifications.json</c> with the job's kind, the
/// daemon's error and the resolved storage problem translated as well, and an app's own plain text as it is.
/// </summary>
public static class NotificationRenderer
{
    public const string TestCode = "notifications.channel.test";
    private const string FooterKey = "notifications:notifications.channel.footer";

    public static RenderedNotification Render(string language, string code, IReadOnlyDictionary<string, JsonElement> parameters, string? appName, string box)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(parameters);
        var arguments = Arguments(parameters);
        if (code.StartsWith("jobs.job.", StringComparison.Ordinal))
        {
            var kind = Text(parameters, "kind");
            arguments["kind"] = (kind is null ? null : MessageTexts.Format(language, $"jobs:kinds.{kind.Replace('.', '_')}", arguments))
                ?? MessageTexts.Format(language, "jobs:kinds.unknown", arguments);
        }
        if (Text(parameters, "errorCode") is { } errorCode)
        {
            var errorArguments = parameters.TryGetValue("errorParams", out var errorParams) && errorParams.ValueKind == JsonValueKind.Object
                ? Arguments(errorParams.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal))
                : [];
            arguments["error"] = MessageTexts.Format(language, $"errors:{errorCode}", errorArguments) ?? errorCode;
        }
        if (code == "storage.alert.resolved" && Text(parameters, "alert") is { } alert)
        {
            arguments["problem"] = MessageTexts.Format(language, $"notifications:{alert}.title", arguments) ?? alert;
        }
        var title = MessageTexts.Format(language, $"notifications:{code}.title", arguments) ?? code;
        var body = MessageTexts.Format(language, $"notifications:{code}.body", arguments) ?? "";
        if (appName is not null)
        {
            title = $"{appName}: {title}";
        }
        var footer = MessageTexts.Format(language, FooterKey, new Dictionary<string, object?> { ["box"] = box }) ?? box;
        return new RenderedNotification(title, body, footer);
    }

    private static string? Text(IReadOnlyDictionary<string, JsonElement> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Strings and numbers, as the desktop passes them; null becomes "null" for the messages that select on it.
    private static Dictionary<string, object?> Arguments(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            arguments[name] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => null,
            };
        }
        return arguments;
    }
}
