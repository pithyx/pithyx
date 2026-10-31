using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Notifications;

namespace Pithyx.Daemon.Tests.Notifications;

/// <summary>What a channel accepts, which notifications it gets and how often (D379).</summary>
public sealed class ChannelRulesTests
{
    private static readonly NotificationCategory[] _storage = [NotificationCategory.Storage];

    private static string Field(NotificationChannelRequest request) =>
        Assert.Throws<PithyxException>(() => ChannelRules.Check(request, null)).Parameters["field"]!.ToString()!;

    [Fact]
    public void AChannelNeedsANameItsTargetAndAtLeastOneKindOfEvent()
    {
        Assert.Equal("name", Field(new NotificationChannelRequest(" ", ChannelKind.Email, Recipients: ["a@example.com"], Categories: _storage)));
        Assert.Equal("recipients", Field(new NotificationChannelRequest("Mail", ChannelKind.Email, Categories: _storage)));
        Assert.Equal("recipients", Field(new NotificationChannelRequest("Mail", ChannelKind.Email, Recipients: ["Admin <a@example.com>"], Categories: _storage)));
        Assert.Equal("categories", Field(new NotificationChannelRequest("Mail", ChannelKind.Email, Recipients: ["a@example.com"])));
        Assert.Equal("url", Field(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "ftp://ntfy.example.com/box", Categories: _storage)));
        Assert.Equal("url", Field(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "https://user:secret@ntfy.example.com/box", Categories: _storage)));

        var (name, config, categories) = ChannelRules.Check(
            new NotificationChannelRequest(" Mail ", ChannelKind.Email, Recipients: [" a@example.com", "A@example.com", "b@example.com"], Categories: [NotificationCategory.Updates, NotificationCategory.Storage, NotificationCategory.Storage]),
            null);
        Assert.Equal("Mail", name);
        Assert.Equal(["a@example.com", "b@example.com"], config.Recipients);
        Assert.Equal([NotificationCategory.Storage, NotificationCategory.Updates], categories);
    }

    [Theory]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://0.0.0.0/hook")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://[fe80::1]/hook")]
    [InlineData("http://box.localhost/hook")]
    public void AWebhookNeverReachesTheBoxItself(string url)
    {
        Assert.Equal("url", Field(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: url, Categories: _storage)));
        ChannelRules.Check(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: url, Categories: _storage), null, allowLoopback: true);
    }

    [Fact]
    public void LocalNetworkAddressesAreFineForAWebhook()
    {
        Assert.False(ChannelRules.IsRefusedAddress(IPAddress.Parse("192.168.1.20")));
        Assert.False(ChannelRules.IsRefusedAddress(IPAddress.Parse("fd00::20")));
        Assert.True(ChannelRules.IsRefusedAddress(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Fact]
    public void HeadersKeepTheirSecretUnlessANewOneComes()
    {
        var first = ChannelRules.Check(
            new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "https://ntfy.example.com/box", Headers: [new WebhookHeader("Authorization", "Bearer one")], Categories: _storage),
            null).Config;
        var kept = ChannelRules.Check(
            new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "https://ntfy.example.com/box", Headers: [new WebhookHeader("authorization")], Categories: _storage),
            first).Config;

        Assert.Equal("Bearer one", Assert.Single(kept.Headers!).Value);
        Assert.Equal(WebhookFormat.Json, kept.Format);
        Assert.Equal("headers", Field(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "https://ntfy.example.com/box", Headers: [new WebhookHeader("Authorization")], Categories: _storage)));
        Assert.Equal("headers", Field(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "https://ntfy.example.com/box", Headers: [new WebhookHeader("Host", "evil")], Categories: _storage)));
        Assert.Equal("headers", Field(new NotificationChannelRequest("Hook", ChannelKind.Webhook, Url: "https://ntfy.example.com/box", Headers: [new WebhookHeader("X-Token", "a\r\nX-Other: b")], Categories: _storage)));
    }

    [Theory]
    [InlineData("storage.alert.poolDegraded", null, NotificationCategory.Storage)]
    [InlineData("update.available", null, NotificationCategory.Updates)]
    [InlineData("store.updates.available", null, NotificationCategory.Updates)]
    [InlineData("store.catalog.failed", null, NotificationCategory.Apps)]
    [InlineData("auth.signin.blocked", null, NotificationCategory.Security)]
    [InlineData("app.message", null, NotificationCategory.Apps)]
    [InlineData("system.ups.on_battery", null, NotificationCategory.System)]
    [InlineData("jobs.job.failed", "storage.pool.create", NotificationCategory.Storage)]
    [InlineData("jobs.job.failed", "apps.install", NotificationCategory.Apps)]
    [InlineData("jobs.job.failed", "system.update", NotificationCategory.Updates)]
    [InlineData("jobs.job.failed", "files.copy", NotificationCategory.System)]
    public void ANotificationBelongsToOneKindOfEvent(string code, string? kind, NotificationCategory category)
    {
        var parameters = kind is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement> { ["kind"] = JsonSerializer.SerializeToElement(kind) };

        Assert.Equal(category, ChannelRules.CategoryOf(code, parameters));
    }

    [Fact]
    public void AChannelGetsItsEventsFromItsLevelOn()
    {
        Assert.True(ChannelRules.Accepts(_storage, NotificationLevel.Warning, NotificationCategory.Storage, NotificationLevel.Error));
        Assert.False(ChannelRules.Accepts(_storage, NotificationLevel.Warning, NotificationCategory.Storage, NotificationLevel.Info));
        Assert.False(ChannelRules.Accepts(_storage, NotificationLevel.Info, NotificationCategory.Updates, NotificationLevel.Error));
        Assert.Equal(NotificationLevel.Error, ChannelRules.LevelOf(NotificationTone.Critical));
        Assert.Equal(NotificationLevel.Info, ChannelRules.LevelOf(NotificationTone.Success));
    }

    [Fact]
    public void RepeatsWaitAQuarterOfAnHourAndAChannelSendsTwentyAnHour()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));
        var throttle = new DeliveryThrottle(time);

        Assert.True(throttle.TryTake(1, "pool degraded"));
        Assert.False(throttle.TryTake(1, "pool degraded"));
        Assert.True(throttle.TryTake(2, "pool degraded"));
        time.Advance(DeliveryThrottle.Repeat);
        Assert.True(throttle.TryTake(1, "pool degraded"));

        for (var i = 2; i < DeliveryThrottle.PerHour; i++)
        {
            Assert.True(throttle.TryTake(1, $"event {i}"));
        }
        Assert.False(throttle.TryTake(1, "one more"));
        time.Advance(TimeSpan.FromMinutes(45));
        Assert.True(throttle.TryTake(1, "one more"));
    }
}
