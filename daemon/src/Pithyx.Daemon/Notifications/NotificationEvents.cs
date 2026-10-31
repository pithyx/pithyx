using System.Collections.Frozen;
using System.Text.Json;

namespace Pithyx.Daemon.Notifications;

/// <summary>An event the box reports to every administrator and to its own channels, a row of the matrix.</summary>
/// <param name="Code">The notification code.</param>
/// <param name="Category">What it is about, which groups the rows.</param>
/// <param name="Level">How serious it usually is; a channel's least level decides its default with it.</param>
public sealed record BoxEvent(string Code, NotificationCategory Category, NotificationLevel Level);

/// <summary>
/// The events of the notification matrix (D407): everything the daemon sends with
/// <see cref="NotificationService.CreateForAdministratorsAsync"/>. A new event of that kind belongs here, or its cells
/// cannot be changed (a test still has to check that each has its texts in both languages). The level is the event's usual one, so the
/// default a channel's categories and least level give it is the same in the matrix and at delivery.
/// </summary>
public static class NotificationEvents
{
    public static readonly IReadOnlyList<BoxEvent> All =
    [
        Of("storage.alert.poolFaulted", NotificationLevel.Error),
        Of("storage.alert.poolSuspended", NotificationLevel.Error),
        Of("storage.alert.poolDegraded", NotificationLevel.Error),
        Of("storage.alert.diskMissing", NotificationLevel.Error),
        Of("storage.alert.dataErrors", NotificationLevel.Error),
        Of("storage.alert.scrubErrors", NotificationLevel.Warning),
        Of("storage.alert.smartFailed", NotificationLevel.Error),
        Of("storage.alert.smartWarning", NotificationLevel.Warning),
        Of("storage.alert.capacityCritical", NotificationLevel.Error),
        Of("storage.alert.capacityWarning", NotificationLevel.Warning),
        Of("storage.alert.poolLocked", NotificationLevel.Warning),
        Of("storage.alert.poolLockedVault", NotificationLevel.Error),
        Of("storage.alert.poolLockedUsb", NotificationLevel.Warning),
        Of("storage.alert.poolLockedUsbFailed", NotificationLevel.Error),
        Of("storage.usb.unlocked", NotificationLevel.Info),
        Of("storage.alert.resolved", NotificationLevel.Info),
        Of("update.available", NotificationLevel.Info),
        Of("update.debian.available", NotificationLevel.Warning),
        Of("update.installed", NotificationLevel.Info),
        Of("update.rolled_back", NotificationLevel.Error),
        Of("update.restored", NotificationLevel.Warning),
        Of("update.source.changed", NotificationLevel.Warning),
        Of("store.updates.available", NotificationLevel.Info),
        Of("auth.signin.blocked", NotificationLevel.Warning),
        Of("apps.elevated.critical", NotificationLevel.Warning),
        Of("apps.device.missing", NotificationLevel.Warning),
        Of("store.catalog.failed", NotificationLevel.Warning),
        Of("system.mirror.degraded", NotificationLevel.Error),
        Of("system.ups.on_battery", NotificationLevel.Warning),
        Of("system.ups.online", NotificationLevel.Info),
        Of("system.ups.shutdown", NotificationLevel.Error),
        Of("system.ups.unreachable", NotificationLevel.Warning),
        Of("proxy.acme.renewal_failed", NotificationLevel.Warning),
        Of("jobs.job.succeeded", NotificationLevel.Info, NotificationCategory.System),
        Of("jobs.job.failed", NotificationLevel.Error, NotificationCategory.System),
        Of("jobs.job.cancelled", NotificationLevel.Info, NotificationCategory.System),
        Of("jobs.job.interrupted", NotificationLevel.Warning, NotificationCategory.System),
    ];

    public static readonly FrozenDictionary<string, BoxEvent> ByCode = All.ToFrozenDictionary(entry => entry.Code, StringComparer.Ordinal);

    // The category the channels use for the code; a job's depends on its kind there, here every box job is one row.
    private static BoxEvent Of(string code, NotificationLevel level, NotificationCategory? category = null) =>
        new(code, category ?? ChannelRules.CategoryOf(code, new Dictionary<string, JsonElement>()), level);
}
