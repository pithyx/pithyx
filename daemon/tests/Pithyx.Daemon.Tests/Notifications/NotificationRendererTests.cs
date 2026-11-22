using System.Text.Json;
using Pithyx.Daemon.Notifications;

namespace Pithyx.Daemon.Tests.Notifications;

/// <summary>Notifications as the box writes them itself, in the recipient's language (D379, #209).</summary>
public sealed class NotificationRendererTests
{
    private static Dictionary<string, JsonElement> Params(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    [Fact]
    public void NumbersFollowTheLanguage()
    {
        var parameters = Params(new { pool = "tank", percent = 1234.5 });

        Assert.Equal("tank is 1,234.5 % full. Free some space soon, or writes will fail.", NotificationRenderer.Render("en", "storage.alert.capacityCritical", parameters, null, "nas").Body);
        Assert.Equal("tank ist zu 1.234,5 % belegt. Gib bald Platz frei, sonst schlagen Schreibvorgänge fehl.", NotificationRenderer.Render("de", "storage.alert.capacityCritical", parameters, null, "nas").Body);
    }

    [Fact]
    public void AFailedJobNamesItsKindAndTheErrorInTheLanguage()
    {
        var parameters = Params(new { kind = "storage.pool.create", errorCode = "storage.pool.name_taken", errorParams = new { name = "tank" } });

        var german = NotificationRenderer.Render("de", "jobs.job.failed", parameters, null, "nas");

        Assert.Equal("Pool erstellen: fehlgeschlagen", german.Title);
        Assert.Equal("Es gibt schon einen Pool namens tank.", german.Body);
        Assert.Equal("Gesendet von Pithyx auf nas.", german.Footer);
    }

    [Fact]
    public void AResolvedAlertNamesTheProblemAndAMissingValueSelectsNull()
    {
        var resolved = NotificationRenderer.Render("en", "storage.alert.resolved", Params(new { alert = "storage.alert.poolDegraded", pool = "tank" }), null, "nas");
        var ups = NotificationRenderer.Render("en", "system.ups.on_battery", Params(new { charge = (int?)null, minutes = 12 }), null, "nas");

        Assert.Equal("Resolved: Pool tank is degraded", resolved.Title);
        Assert.StartsWith("The power failed. The box shuts down", ups.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAppsTextStaysAsItIsWithItsNameInFront()
    {
        var rendered = NotificationRenderer.Render("de", "app.message", Params(new { title = "Sign-in {expired}", body = "Open the app." }), "VPN", "nas");

        Assert.Equal("VPN: Sign-in {expired}", rendered.Title);
        Assert.Equal("Open the app.", rendered.Body);
    }

    [Fact]
    public void AnUnknownLanguageOrCodeFallsBack()
    {
        var rendered = NotificationRenderer.Render("fr", "update.available", Params(new { version = "1.2.0", installed = "1.1.0" }), null, "nas");
        var unknown = NotificationRenderer.Render("en", "test.unknown", Params(new { }), null, "nas");

        Assert.Equal("Pithyx 1.2.0 is available", rendered.Title);
        Assert.Equal(("test.unknown", ""), (unknown.Title, unknown.Body));
    }
}
