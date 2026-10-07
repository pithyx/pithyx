using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// The window lease (D319) on the hub itself, with the fake clock: who holds it, the grace after a lost connection,
/// one deadline per lost connection, a reconnect, and the end of a session.
/// </summary>
public sealed class AppLeasesTests : IAsyncLifetime
{
    private const string AppId = "com.example.alpha";
    private readonly DaemonFactory _factory = new();
    private long _admin;
    private long _rita;
    private int _changes;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private TopicHub Hub => _factory.Services.GetRequiredService<TopicHub>();

    private AppLeases Leases => _factory.Services.GetRequiredService<AppLeases>();

    public async ValueTask InitializeAsync()
    {
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            _admin = await TestUsers.InsertAsync(connection, "admin", admin: true);
            _rita = await TestUsers.InsertAsync(connection, "rita");
            await connection.ExecuteInsertAppAsync(AppId);
        }
        _factory.Services.GetRequiredService<PermissionService>().Invalidate();
        await _factory.Services.GetRequiredService<AppRegistry>().ReloadAsync(Token);
        Leases.Changed += () => Interlocked.Increment(ref _changes);
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<TopicHub.Connection> SubscribeAsync(long userId, string session, string? topic = null)
    {
        var connection = Hub.Connect(JobTestSupport.Principal(userId, admin: userId == _admin), session);
        await Hub.SubscribeAsync(connection, "1", topic ?? AppContexts.TopicOf(AppId), Token);
        return connection;
    }

    private Dictionary<long, int> Holders => new(Leases.Holders(AppId));

    [Fact]
    public async Task OnlyUsersOfTheAppWithTheConcreteTopicHoldALease()
    {
        await SubscribeAsync(_rita, "r1");
        await SubscribeAsync(_admin, "a1", AppContexts.Pattern);
        await SubscribeAsync(_admin, "a2", "#");
        Assert.Empty(Holders);

        await SubscribeAsync(_admin, "a3");
        Assert.Equal(new Dictionary<long, int> { [_admin] = 1 }, Holders);
    }

    [Fact]
    public async Task EveryLostConnectionKeepsItsOwnTwoMinutes()
    {
        var first = await SubscribeAsync(_admin, "a1");
        Hub.Disconnect(first);
        Assert.Equal(new Dictionary<long, int> { [_admin] = 1 }, Holders);

        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await SubscribeAsync(_admin, "a2");
        // Back on another connection: one desktop, not two, and the first grace is over.
        Assert.Equal(new Dictionary<long, int> { [_admin] = 1 }, Holders);
        Hub.Disconnect(second);
        Assert.Equal(new Dictionary<long, int> { [_admin] = 1 }, Holders);

        var before = _changes;
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new Dictionary<long, int> { [_admin] = 1 }, Holders);
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Holders);
        Assert.True(_changes > before, "the end of the grace raises Changed");
    }

    [Fact]
    public async Task AnEndedSessionAndAnUnsubscribeEndTheLeaseAtOnce()
    {
        var reload = await SubscribeAsync(_admin, "a1");
        Hub.Disconnect(reload);
        var signedOut = await SubscribeAsync(_admin, "a1");
        signedOut.Close(WebSocketCloseCode.SessionEnded);
        Hub.Disconnect(signedOut);
        Assert.Empty(Holders);

        var open = await SubscribeAsync(_admin, "a2");
        Hub.Unsubscribe(open, "2", AppContexts.TopicOf(AppId));
        Assert.Empty(Holders);
    }

    [Fact]
    public void ALockedEncryptedDatasetIsReportedAsLocked()
    {
        var dataset = new Dataset("tank/media", "tank", "filesystem", 0, 0, 0, null, false, "aes-256-gcm", "unavailable", "tank", null, null, null, null, null, null, 0);
        Assert.Equal(AppContexts.Locked, AppContexts.ReasonOf("tank/media", new PoolList([], [dataset]), poolsRead: true));
        Assert.Equal(ShareAvailability.PoolMissing, AppContexts.ReasonOf("tank/media", new PoolList([], [dataset with { KeyStatus = "available" }]), poolsRead: true));

        using var manifest = JsonDocument.Parse("""{ "storage": [{ "id": "inbox", "access": "readWrite" }, { "id": "photos", "access": "read" }, { "id": "notes", "access": "readWrite" }] }""");
        var app = new InstalledApp(AppId, "1.0.0", "sideload", true, manifest.RootElement.Clone(), [], 40000, new string('0', 64), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, AppLogLevel.Info,
            [new StorageMapping("inbox", 1, "inbox", "readWrite"), new StorageMapping("photos", 2, "", "readWrite")]);
        var rights = new EffectivePermissions(false, new Dictionary<long, ShareAccess> { [1] = ShareAccess.ReadWrite, [2] = ShareAccess.ReadWrite }, new HashSet<string>());
        var states = AppContexts.StorageStates(app, rights, new Dictionary<long, string?> { [1] = AppContexts.Locked, [2] = null });
        Assert.Equal(
            [("inbox", AppStorageState.Locked, (ShareAccess?)null), ("photos", AppStorageState.Ready, ShareAccess.Read), ("notes", AppStorageState.Unmapped, null)],
            states.Select(state => (state.Id, state.State, state.Access)));
    }
}

internal static class AppRows
{
    /// <summary>An installed app straight in the registry's tables, granted to nobody.</summary>
    public static async Task ExecuteInsertAppAsync(this System.Data.Common.DbConnection connection, string appId)
    {
        await Dapper.SqlMapper.ExecuteAsync(connection, "INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (40000, 'app', @appId, '2026-01-01T00:00:00Z')", new { appId });
        await Dapper.SqlMapper.ExecuteAsync(
            connection,
            """
            INSERT INTO apps (id, version, manifest, source, icon, package_sha256, uid, enabled, installed_at, updated_at)
            VALUES (@appId, '1.0.0', @manifest, 'sideload', X'3c73766720', @sha, 40000, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """,
            new { appId, manifest = """{ "name": { "en": "Alpha" }, "entry": "frontend/index.html", "window": {} }""", sha = new string('0', 64) });
    }
}
