using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>The snapshot API in simulation mode (hand-written snapshot fixture of the two simulated pools).</summary>
public sealed class SnapshotApiTests : IAsyncLifetime
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

    private async Task<HttpClient> UserAsync()
    {
        var hash = await _factory.Services.GetRequiredService<PasswordHasher>().HashAsync(AdminPassword, Token);
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES ('bob', @hash, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')",
                new { hash });
        }
        var client = _factory.CreateApiClient();
        (await client.LoginAsync("bob", AdminPassword)).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, object? body = null) => method switch
    {
        "GET" => client.GetAsync(path, Token),
        "PUT" => client.PutAsJsonAsync(path, body, Token),
        _ => client.PostAsJsonAsync(path, body, Token),
    };

    public static TheoryData<string, string, object?> Endpoints() => new()
    {
        { "GET", "/api/storage/snapshots?dataset=pithyxtestm", null },
        { "POST", "/api/storage/snapshots", new { dataset = "pithyxtestm" } },
        { "POST", "/api/storage/snapshots/hold", new { dataset = "pithyxtestm", name = "before-upgrade" } },
        { "POST", "/api/storage/snapshots/release", new { dataset = "pithyxtestm", name = "before-upgrade" } },
        { "POST", "/api/storage/plans", new { action = "snapshot.destroy", pool = "pithyxtestm", dataset = "pithyxtestm", name = "pithyx-manual-20260920T101500Z" } },
        { "POST", "/api/storage/plans", new { action = "snapshot.rollback", pool = "pithyxtestm", dataset = "pithyxtestm", name = "before-upgrade", confirmName = "pithyxtestm" } },
        { "GET", "/api/storage/snapshots/schedules?dataset=pithyxtestm", null },
        { "PUT", "/api/storage/snapshots/schedules", new { dataset = "pithyxtestm", schedules = new[] { new { kind = "daily", enabled = true, keep = 7 } } } },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task UsersAreRefusedAndAnonymousCallersMustSignIn(string method, string path, object? body)
    {
        using var user = await UserAsync();
        using var anonymous = _factory.CreateApiClient();

        using var refused = await SendAsync(user, method, path, body);
        using var unauthenticated = await SendAsync(anonymous, method, path, body);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("auth.permission.denied", (await refused.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task AdministratorsSeeTheSnapshotsOfADataset()
    {
        var list = await (await _admin.GetAsync("/api/storage/snapshots?dataset=pithyxtestm", Token)).JsonAsync();

        Assert.Equal("pithyxtestm", list.GetProperty("dataset").GetString());
        var snapshots = list.GetProperty("snapshots").EnumerateArray().ToList();
        Assert.Equal(
            ["pithyx-manual-20260920T101500Z", "before-upgrade", "pithyx-daily-20260926T000000Z", "pithyx-daily-20260927T000000Z"],
            snapshots.Select(snapshot => snapshot.GetProperty("name").GetString()));
        Assert.Equal(("2026-09-24T08:30:12+00:00", 1), (snapshots[1].GetProperty("createdAt").GetString(), snapshots[1].GetProperty("holds").GetInt32()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshots[1].GetProperty("pithyxKind").ValueKind);
        Assert.Equal("daily", snapshots[2].GetProperty("pithyxKind").GetString());
    }

    [Fact]
    public async Task UnknownDatasetsAreNotFound()
    {
        using var response = await _admin.GetAsync("/api/storage/snapshots?dataset=nope", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("storage.snapshot.dataset_not_found", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SchedulesAreSavedPerDataset()
    {
        using var saved = await _admin.PutAsJsonAsync(
            "/api/storage/snapshots/schedules",
            new { dataset = "pithyxtests", schedules = new[] { new { kind = "hourly", enabled = true, keep = 48 } } },
            Token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var tests = await (await _admin.GetAsync("/api/storage/snapshots/schedules?dataset=pithyxtests", Token)).JsonAsync();
        var mirror = await (await _admin.GetAsync("/api/storage/snapshots/schedules?dataset=pithyxtestm", Token)).JsonAsync();

        Assert.Equal((true, 48), (tests.GetProperty("schedules")[0].GetProperty("enabled").GetBoolean(), tests.GetProperty("schedules")[0].GetProperty("keep").GetInt32()));
        Assert.All(mirror.GetProperty("schedules").EnumerateArray(), schedule => Assert.False(schedule.GetProperty("enabled").GetBoolean()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task CountsOutsideTheRangeAreRefused(int keep)
    {
        using var response = await _admin.PutAsJsonAsync(
            "/api/storage/snapshots/schedules",
            new { dataset = "pithyxtests", schedules = new[] { new { kind = "hourly", enabled = true, keep } } },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("storage.snapshot.schedule_invalid", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SnapshotsOfOthersCannotBeDestroyed()
    {
        using var response = await _admin.PostAsJsonAsync("/api/storage/plans", new { action = "snapshot.destroy", pool = "pithyxtestm", dataset = "pithyxtestm", name = "before-upgrade" }, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("storage.snapshot.not_pithyx", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ARollbackPreviewListsTheNewerSnapshotsAndWaitsForTheTypedName()
    {
        using var mismatch = await _admin.PostAsJsonAsync(
            "/api/storage/plans",
            new { action = "snapshot.rollback", pool = "pithyxtestm", dataset = "pithyxtestm", name = "pithyx-daily-20260926T000000Z", confirmName = "pithyxtest" },
            Token);
        var awaiting = await (await _admin.PostAsJsonAsync(
            "/api/storage/plans",
            new { action = "snapshot.rollback", pool = "pithyxtestm", dataset = "pithyxtestm", name = "pithyx-daily-20260926T000000Z" },
            Token)).JsonAsync();
        var ready = await (await _admin.PostAsJsonAsync(
            "/api/storage/plans",
            new { action = "snapshot.rollback", pool = "pithyxtestm", dataset = "pithyxtestm", name = "pithyx-daily-20260926T000000Z", confirmName = "pithyxtestm" },
            Token)).JsonAsync();

        Assert.Equal("storage.snapshot.confirmation_mismatch", (await mismatch.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, awaiting.GetProperty("planId").ValueKind);
        Assert.True(awaiting.GetProperty("requiresConfirmName").GetBoolean());
        Assert.Equal(["pithyxtestm@pithyx-daily-20260927T000000Z"], ready.GetProperty("datasets").EnumerateArray().Select(dataset => dataset.GetString()));
        Assert.Equal(
            ["zfs", "rollback", "-r", "pithyxtestm@pithyx-daily-20260926T000000Z"],
            ready.GetProperty("commands")[0].GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).Prepend(ready.GetProperty("commands")[0].GetProperty("program").GetString()));
        Assert.NotNull(ready.GetProperty("planId").GetString());
    }
}
