using System.Text.Json;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.Notifications;

/// <summary>The notification categories of apps (D390): the manifest's <c>notifications.categories</c>, which users can mute.</summary>
public static class NotificationCategories
{
    /// <summary>The categories a manifest declares, in its order.</summary>
    public static IReadOnlyList<(string Id, Dictionary<string, string> Name, Dictionary<string, string>? Description)> Of(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("notifications", out var notifications) || !notifications.TryGetProperty("categories", out var categories))
        {
            return [];
        }
        return [.. categories.EnumerateArray().Select(category => (
            category.GetProperty("id").GetString()!,
            Texts(category.GetProperty("name"))!,
            category.TryGetProperty("description", out var description) ? Texts(description) : null))];
    }

    /// <summary>The categories of the enabled apps the user may open, with what the user muted.</summary>
    public static async Task<AppNotificationCategoryList> ListAsync(long userId, NotificationService service, AppRegistry registry, PermissionService permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(permissions);
        var rights = await permissions.ForUserAsync(userId, cancellationToken);
        var muted = await service.MutedAsync(userId, cancellationToken);
        var list = new List<AppNotificationCategory>();
        foreach (var app in registry.Apps.Values.Where(app => registry.Allows(rights, app.Id)).OrderBy(app => app.Id, StringComparer.Ordinal))
        {
            var appName = Texts(app.Manifest.GetProperty("name"))!;
            list.AddRange(Of(app.Manifest).Select(category => new AppNotificationCategory(app.Id, appName, category.Id, category.Name, category.Description, muted.Contains((app.Id, category.Id)))));
        }
        return new AppNotificationCategoryList(list);
    }

    private static Dictionary<string, string>? Texts(JsonElement texts) =>
        texts.ValueKind == JsonValueKind.Object ? texts.EnumerateObject().ToDictionary(text => text.Name, text => text.Value.GetString() ?? "", StringComparer.Ordinal) : null;
}
