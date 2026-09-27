using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Health;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;
using static Pithyx.Daemon.Tests.Storage.Health.StorageAlertsTests;

namespace Pithyx.Daemon.Tests.Storage.Health;

/// <summary>Alerts, mutes, scrubs and schedules through the API in simulation mode (the dev VM's pools and QEMU disks).</summary>
public sealed class StorageHealthApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateApiClient();
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private StorageAlertService Alerts => _factory.Services.GetRequiredService<StorageAlertService>();

    /// <summary>Plays a pool refresh with these pools and waits for the alerts to follow.</summary>
    private async Task SetPoolsAsync(params Pool[] pools)
    {
        _factory.Services.GetRequiredService<PoolInventory>().Replace(new PoolList(pools, []));
        Alerts.Settled = true;
        // The monitor refreshes on the same change; whichever call sees the change first publishes it.
        if (await Alerts.RefreshAsync(Token))
        {
            _factory.Services.GetRequiredService<TopicHub>().Publish(StorageAlertService.Topic, Alerts.Current);
        }
    }

    private static async Task<JsonElement> AlertsAsync(HttpClient client) => await (await client.GetAsync("/api/storage/alerts", Token)).JsonAsync();

    private static (string Key, bool Muted)[] Keys(JsonElement health) =>
        [.. health.GetProperty("alerts").EnumerateArray().Select(alert => (alert.GetProperty("key").GetString()!, alert.GetProperty("muted").GetBoolean()))];

    private async Task<long> MuteRowsAsync()
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM storage_alert_mutes");
    }

    private async Task<(HttpClient Client, long Id)> UserAsync(string name, bool admin)
    {
        var hash = await _factory.Services.GetRequiredService<PasswordHasher>().HashAsync(AdminPassword, Token);
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES (@name, @hash, @admin, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
                new { name, hash, admin = admin ? 1 : 0 });
        }
        var client = _factory.CreateApiClient();
        (await client.LoginAsync(name, AdminPassword)).EnsureSuccessStatusCode();
        return (client, id);
    }

    [Fact]
    public async Task TheSimulatedQemuDisksHaveNoSmartBecauseTheyAreVirtual()
    {
        var smart = (await AlertsAsync(_admin)).GetProperty("smart");
        for (var i = 0; i < 100 && smart.GetArrayLength() < 7; i++)
        {
            await Task.Delay(50, Token);
            smart = (await AlertsAsync(_admin)).GetProperty("smart");
        }

        Assert.Equal(7, smart.GetArrayLength());
        Assert.All(smart.EnumerateArray(), report => Assert.Equal(("unsupported", "virtual"), (report.GetProperty("state").GetString(), report.GetProperty("reason").GetString())));
        Assert.Empty(Keys(await AlertsAsync(_admin)));
    }

    [Fact]
    public async Task AMuteEndsWhenTheAlertGoesAwayAndComesBack()
    {
        await SetPoolsAsync(Pool(state: "DEGRADED"));
        Assert.Equal([("pool.degraded:tank", false)], Keys(await AlertsAsync(_admin)));

        using (var muted = await _admin.PostAsync("/api/storage/alerts/pool.degraded%3Atank/mute", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, muted.StatusCode);
            Assert.Equal([("pool.degraded:tank", true)], Keys(await muted.JsonAsync()));
        }
        await SetPoolsAsync(Pool(state: "DEGRADED", capacity: 20));
        Assert.Equal([("pool.degraded:tank", true)], Keys(await AlertsAsync(_admin)));

        await SetPoolsAsync(Pool());
        Assert.Empty(Keys(await AlertsAsync(_admin)));
        Assert.Equal(0, await MuteRowsAsync());

        await SetPoolsAsync(Pool(state: "DEGRADED"));
        Assert.Equal([("pool.degraded:tank", false)], Keys(await AlertsAsync(_admin)));
    }

    [Fact]
    public async Task AMuteHidesOnlyItsSeverityAndOnlyForItsUser()
    {
        var (other, _) = await UserAsync("carol", admin: true);
        await SetPoolsAsync(Pool(capacity: 85));
        (await _admin.PostAsync("/api/storage/alerts/pool.capacity%3Atank/mute", new { })).EnsureSuccessStatusCode();

        Assert.Equal([("pool.capacity:tank", true)], Keys(await AlertsAsync(_admin)));
        Assert.Equal([("pool.capacity:tank", false)], Keys(await AlertsAsync(other)));

        await SetPoolsAsync(Pool(capacity: 95));
        Assert.Equal([("pool.capacity:tank", false)], Keys(await AlertsAsync(_admin)));

        (await _admin.PostAsync("/api/storage/alerts/pool.capacity%3Atank/mute", new { })).EnsureSuccessStatusCode();
        using var unmuted = await _admin.DeleteAsync("/api/storage/alerts/pool.capacity%3Atank/mute", Token);
        Assert.Equal([("pool.capacity:tank", false)], Keys(await unmuted.JsonAsync()));
        other.Dispose();
    }

    [Fact]
    public async Task OnlyCurrentAlertsCanBeMuted()
    {
        using var response = await _admin.PostAsync("/api/storage/alerts/pool.degraded%3Anope/mute", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("storage.alert.not_found", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task EveryHealthEndpointIsForAdministratorsOnly()
    {
        var (user, _) = await UserAsync("bob", admin: false);
        await SetPoolsAsync(Pool(state: "DEGRADED"));
        var requests = new Func<Task<HttpResponseMessage>>[]
        {
            () => user.GetAsync("/api/storage/alerts", Token),
            () => user.PostAsync("/api/storage/alerts/pool.degraded%3Atank/mute", new { }),
            () => user.DeleteAsync("/api/storage/alerts/pool.degraded%3Atank/mute", Token),
            () => user.PostAsync("/api/storage/pools/tank/scrub", new { }),
            () => user.PostAsync("/api/storage/pools/tank/scrub/pause", new { }),
            () => user.DeleteAsync("/api/storage/pools/tank/scrub", Token),
            () => user.GetAsync("/api/storage/schedules", Token),
            () => user.PutAsJsonAsync("/api/storage/schedules", new { kind = "scrub", target = "tank", frequency = "weekly" }, Token),
        };

        foreach (var request in requests)
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("auth.permission.denied", await response.ErrorCodeAsync());
        }
        Assert.Equal(0, await MuteRowsAsync());
        user.Dispose();
    }

    [Fact]
    public async Task TheAlertsTopicIsForAdministratorsAndCarriesTheirOwnMutes()
    {
        var (_, carol) = await UserAsync("carol", admin: true);
        await SetPoolsAsync(Pool(state: "DEGRADED"));
        (await _admin.PostAsync("/api/storage/alerts/pool.degraded%3Atank/mute", new { })).EnsureSuccessStatusCode();
        var hub = _factory.Services.GetRequiredService<TopicHub>();
        static ClaimsPrincipal Principal(long id, bool admin) => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. admin ? [new Claim(ClaimTypes.Role, SessionAuthenticationHandler.AdminRole)] : Array.Empty<Claim>()],
            "test"));
        var admin = hub.Connect(Principal(1, admin: true), "a");
        var second = hub.Connect(Principal(carol, admin: true), "b");
        var user = hub.Connect(Principal(99, admin: false), "c");

        foreach (var connection in new[] { admin, second, user })
        {
            await hub.SubscribeAsync(connection, "s1", StorageAlertService.Topic, Token);
        }

        Assert.Equal([true], Messages(admin, "snap").Select(Muted));
        Assert.Equal([false], Messages(second, "snap").Select(Muted));
        Assert.Empty(Messages(user, "snap"));

        await SetPoolsAsync(Pool(state: "DEGRADED", capacity: 85));
        Assert.Contains(true, Messages(admin, "event").Select(Muted));
        Assert.DoesNotContain(true, Messages(second, "event").Select(Muted));
        Assert.Empty(Messages(user, "event"));
    }

    private static List<JsonElement> Messages(TopicHub.Connection connection, string type)
    {
        var messages = new List<JsonElement>();
        while (connection.Outbox.Reader.TryRead(out var payload))
        {
            var message = JsonDocument.Parse(payload).RootElement.Clone();
            if (message.GetProperty("type").GetString() == type)
            {
                messages.Add(message);
            }
        }
        return messages;
    }

    private static bool Muted(JsonElement message) =>
        message.GetProperty("data").GetProperty("alerts").EnumerateArray().First(alert => alert.GetProperty("key").GetString() == "pool.degraded:tank").GetProperty("muted").GetBoolean();

    [Fact]
    public async Task AScrubStartsAtOnceAndOnlyStopsWhenOneRuns()
    {
        using (var started = await _admin.PostAsync("/api/storage/pools/pithyxtestm/scrub", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, started.StatusCode);
        }
        using (var missing = await _admin.PostAsync("/api/storage/pools/nope/scrub", new { }))
        {
            Assert.Equal((HttpStatusCode.NotFound, "storage.pool.not_found"), (missing.StatusCode, await missing.ErrorCodeAsync()));
        }
        using (var stop = await _admin.DeleteAsync("/api/storage/pools/pithyxtestm/scrub", Token))
        {
            Assert.Equal((HttpStatusCode.Conflict, "storage.scrub.not_running"), (stop.StatusCode, await stop.ErrorCodeAsync()));
        }

        var running = new PoolScan("scrub", "scanning", false, DateTimeOffset.UtcNow, null, 100, 10, 0);
        await SetPoolsAsync(Pool("pithyxtestm", scan: running));
        using var again = await _admin.PostAsync("/api/storage/pools/pithyxtestm/scrub", new { });
        Assert.Equal((HttpStatusCode.Conflict, "storage.scrub.running"), (again.StatusCode, await again.ErrorCodeAsync()));
    }

    [Fact]
    public async Task SchedulesDefaultToMonthlyScrubsAndWeeklyShortTestsAndCanBeChanged()
    {
        var list = await (await _admin.GetAsync("/api/storage/schedules", Token)).JsonAsync();
        var schedules = list.GetProperty("schedules").EnumerateArray()
            .Select(entry => (entry.GetProperty("kind").GetString(), entry.GetProperty("target").GetString(), entry.GetProperty("frequency").GetString()))
            .ToList();
        Assert.Equal(
            [("scrub", "pithyxtestm", "monthly"), ("scrub", "pithyxtests", "monthly"), ("shortSelfTest", "", "weekly"), ("longSelfTest", "", "monthly")],
            schedules);

        using var changed = await _admin.PutAsJsonAsync("/api/storage/schedules", new { kind = "scrub", target = "pithyxtests", frequency = "off" }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var scrub = (await changed.JsonAsync()).GetProperty("schedules")[1];
        Assert.Equal(("off", JsonValueKind.Null), (scrub.GetProperty("frequency").GetString(), scrub.GetProperty("nextRunAt").ValueKind));

        foreach (var invalid in new object[]
        {
            new { kind = "scrub", target = "nope", frequency = "weekly" },
            new { kind = "scrub", target = "pithyxtests", frequency = "daily" },
            new { kind = "shortSelfTest", target = "pithyxtests", frequency = "weekly" },
            new { kind = "defrag", target = "", frequency = "weekly" },
        })
        {
            using var response = await _admin.PutAsJsonAsync("/api/storage/schedules", invalid, Token);
            Assert.Equal((HttpStatusCode.BadRequest, "storage.schedule.invalid"), (response.StatusCode, await response.ErrorCodeAsync()));
        }
    }
}
