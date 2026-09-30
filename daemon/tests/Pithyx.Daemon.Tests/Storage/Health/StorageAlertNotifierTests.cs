using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage.Health;

namespace Pithyx.Daemon.Tests.Storage.Health;

/// <summary>Which alert changes notify the administrators, against a real database.</summary>
public sealed class StorageAlertNotifierTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-notifier-").FullName;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-01T08:00:00Z", CultureInfo.InvariantCulture));
    private readonly SqliteConnectionFactory _connections;
    private readonly NotificationStore _store;
    private readonly NotificationService _notifications;
    private readonly StorageAlertNotifier _notifier;
    private long _adminId;
    private long _otherAdminId;
    private long _userId;

    public StorageAlertNotifierTests()
    {
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        _store = new NotificationStore(_connections);
        var hub = new TopicHub(new TopicRegistry([_store.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _notifications = new NotificationService(_store, _connections, hub, _time, Options.Create(new JsonOptions()), NullLogger<NotificationService>.Instance);
        _notifier = new StorageAlertNotifier(_connections, _notifications, _time, NullLogger<StorageAlertNotifier>.Instance);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_connections, _time, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
        await using var connection = await _connections.OpenAsync(Token);
        async Task<long> InsertAsync(string name, int admin) => await connection.ExecuteScalarAsync<long>(
            "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES (@name, 'x', @admin, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
            new { name, admin });
        _adminId = await InsertAsync("ada", 1);
        _otherAdminId = await InsertAsync("grace", 1);
        _userId = await InsertAsync("bob", 0);
    }

    public ValueTask DisposeAsync()
    {
        _notifier.Dispose();
        _notifications.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    private StorageAlert Alert(string key, string severity, string code, int percent = 85) => new(
        key,
        severity,
        code,
        new Dictionary<string, JsonElement> { ["pool"] = JsonSerializer.SerializeToElement("tank"), ["percent"] = JsonSerializer.SerializeToElement(percent) },
        _time.GetUtcNow(),
        false);

    private StorageAlert Warning(int percent = 85) => Alert("pool.capacity:tank", StorageAlerts.Warning, "storage.alert.capacityWarning", percent);

    private StorageAlert Critical() => Alert("pool.capacity:tank", StorageAlerts.Critical, "storage.alert.capacityCritical", 95);

    private IEnumerable<(string Code, NotificationTone Tone)> Codes(long userId) => _store.Of(userId).Reverse().Select(entry => (entry.Code, entry.Tone));

    [Fact]
    public async Task ANewAlertNotifiesEveryAdministratorOnceAndNobodyElse()
    {
        await _notifier.SyncAsync([Warning()], settled: false, Token);
        await _notifier.SyncAsync([Warning(86)], settled: true, Token);

        Assert.Equal([("storage.alert.capacityWarning", NotificationTone.Warning)], Codes(_adminId));
        Assert.Equal([("storage.alert.capacityWarning", NotificationTone.Warning)], Codes(_otherAdminId));
        Assert.Empty(_store.Of(_userId));
        var notification = _store.Of(_adminId)[0];
        Assert.Equal("tank", notification.Params["pool"].GetString());
        Assert.Equal(85, notification.Params["percent"].GetInt32());
        Assert.Equal("pool.capacity:tank", notification.Params["alertKey"].GetString());
    }

    [Fact]
    public async Task AnAlertThatBecomesMoreSevereNotifiesAgainButOneThatCalmsDownDoesNot()
    {
        await _notifier.SyncAsync([Warning()], settled: true, Token);
        await _notifier.SyncAsync([Critical()], settled: true, Token);
        await _notifier.SyncAsync([Warning()], settled: true, Token);
        await _notifier.SyncAsync([Critical()], settled: true, Token);

        Assert.Equal(
            [
                ("storage.alert.capacityWarning", NotificationTone.Warning),
                ("storage.alert.capacityCritical", NotificationTone.Critical),
                ("storage.alert.capacityCritical", NotificationTone.Critical),
            ],
            Codes(_adminId));
    }

    [Fact]
    public async Task AnAlertThatGoesAwayIsResolvedOnlyOnceTheStateIsSettled()
    {
        await _notifier.SyncAsync([Critical()], settled: true, Token);

        await _notifier.SyncAsync([], settled: false, Token);
        var whileUnsettled = _store.Of(_adminId).Count;
        await _notifier.SyncAsync([], settled: true, Token);
        await _notifier.SyncAsync([], settled: true, Token);

        Assert.Equal(1, whileUnsettled);
        Assert.Equal(
            [("storage.alert.capacityCritical", NotificationTone.Critical), (StorageAlertNotifier.ResolvedCode, NotificationTone.Info)],
            Codes(_adminId));
        var resolved = _store.Of(_adminId)[0];
        Assert.Equal("storage.alert.capacityCritical", resolved.Params["alert"].GetString());
        Assert.Equal("pool.capacity:tank", resolved.Params["alertKey"].GetString());
        Assert.Equal("tank", resolved.Params["pool"].GetString());
        await using var connection = await _connections.OpenAsync(Token);
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM storage_alert_notices"));
    }

    [Fact]
    public async Task AnAlertThatComesBackAfterItWasResolvedNotifiesAgain()
    {
        await _notifier.SyncAsync([Warning()], settled: true, Token);
        await _notifier.SyncAsync([], settled: true, Token);
        await _notifier.SyncAsync([Warning()], settled: true, Token);

        Assert.Equal(
            ["storage.alert.capacityWarning", StorageAlertNotifier.ResolvedCode, "storage.alert.capacityWarning"],
            Codes(_adminId).Select(entry => entry.Code));
    }

    [Fact]
    public async Task WithoutAdministratorsNothingIsRecordedSoTheFirstOneIsToldLater()
    {
        await using (var connection = await _connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync("UPDATE users SET is_admin = 0");
        }
        await _notifier.SyncAsync([Warning()], settled: true, Token);
        await using (var connection = await _connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync("UPDATE users SET is_admin = 1 WHERE id = @_adminId", new { _adminId });
        }

        await _notifier.SyncAsync([Warning()], settled: true, Token);

        Assert.Equal([("storage.alert.capacityWarning", NotificationTone.Warning)], Codes(_adminId));
    }

    [Fact]
    public async Task AnAlertStillPresentAfterARestartIsNotNotifiedAgain()
    {
        await _notifier.SyncAsync([Warning()], settled: true, Token);
        using var restartedStore = new NotificationStore(_connections);
        var hub = new TopicHub(new TopicRegistry([restartedStore.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        using var restartedService = new NotificationService(restartedStore, _connections, hub, _time, Options.Create(new JsonOptions()), NullLogger<NotificationService>.Instance);
        using var restarted = new StorageAlertNotifier(_connections, restartedService, _time, NullLogger<StorageAlertNotifier>.Instance);

        // After a restart, pool alerts come before SMART is read, then the settled state.
        await restarted.SyncAsync([], settled: false, Token);
        await restarted.SyncAsync([Warning()], settled: true, Token);
        await restartedStore.LoadAsync(Token);

        Assert.Single(restartedStore.Of(_adminId));
    }
}
