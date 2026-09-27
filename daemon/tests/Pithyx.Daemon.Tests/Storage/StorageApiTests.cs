using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>The storage API in simulation mode: the dev VM's disks with a mirror and a single-disk pool.</summary>
public sealed class StorageApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private static readonly string[] _disks0 = ["serial-VYXTEST02", "serial-VYXTEST03"];
    private static readonly string[] _disks1 = ["serial-VYXTEST01", "serial-VYXTEST02"];
    private static readonly string[] _disks2 = ["serial-02609329D99"];
    private static readonly string[] _disks3 = ["serial-VYXTEST02"];
    private static readonly string[] _disks4 = ["serial-NOPE"];
    private static readonly string[] _disks5 = ["/api/storage/pools", "/api/storage/disks"];

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

    private async Task<JsonElement> PreviewAsync(object request)
    {
        using var response = await _admin.PostAsJsonAsync("/api/storage/pools/preview", request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    [Fact]
    public async Task AdministratorsSeeThePoolsWithTheirVdevs()
    {
        var pools = await (await _admin.GetAsync("/api/storage/pools", Token)).JsonAsync();

        var names = pools.GetProperty("pools").EnumerateArray().Select(pool => pool.GetProperty("name").GetString());
        Assert.Equal(["pithyxtestm", "pithyxtests"], names);
        var mirror = pools.GetProperty("pools")[0].GetProperty("vdevs")[0];
        Assert.Equal(("mirror", "data", 2), (mirror.GetProperty("type").GetString(), mirror.GetProperty("role").GetString(), mirror.GetProperty("children").GetArrayLength()));
        Assert.Equal(2, pools.GetProperty("datasets").GetArrayLength());
    }

    [Fact]
    public async Task EveryDiskCarriesTheGuardsVerdict()
    {
        var list = await (await _admin.GetAsync("/api/storage/disks", Token)).JsonAsync();

        Assert.Equal("dev", list.GetProperty("environment").GetString());
        var verdicts = list.GetProperty("disks").EnumerateArray().ToDictionary(
            disk => disk.GetProperty("disk").GetProperty("name").GetString()!,
            disk => (disk.GetProperty("guard").GetProperty("decision").GetString(), disk.GetProperty("guard").GetProperty("reason").GetString()));
        Assert.Equal(("refused", "system_disk"), verdicts["sda"]);
        Assert.Equal(("needsSerialConfirmation", "has_data"), verdicts["sdb"]);
        Assert.Equal(("allowed", "empty"), verdicts["sdc"]);
        Assert.Equal(("allowed", "empty"), verdicts["sdd"]);
        Assert.Equal(("refused", "pool_member"), verdicts["sde"]);
        Assert.Equal(("refused", "pool_member"), verdicts["sdg"]);
    }

    [Fact]
    public async Task APreviewListsTheExactCommandsAndRunsNothing()
    {
        var preview = await PreviewAsync(new { name = "tank", layout = "mirror", diskIds = _disks0 });

        var command = Assert.Single(preview.GetProperty("commands").EnumerateArray());
        Assert.Equal("zpool", command.GetProperty("program").GetString());
        var arguments = command.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).ToList();
        Assert.Equal(["tank", "mirror", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi3"], arguments[^4..]);
        Assert.Equal(2, (await (await _admin.GetAsync("/api/storage/pools", Token)).JsonAsync()).GetProperty("pools").GetArrayLength());
    }

    [Fact]
    public async Task ADiskWithDataNeedsItsSerialAndTheSystemDiskNeverGetsCommands()
    {
        var unconfirmed = await PreviewAsync(new { name = "tank", layout = "mirror", diskIds = _disks1 });
        Assert.Equal(JsonValueKind.Null, unconfirmed.GetProperty("commands").ValueKind);
        Assert.Equal("needsSerialConfirmation", unconfirmed.GetProperty("disks")[0].GetProperty("decision").GetString());

        var confirmed = await PreviewAsync(new { name = "tank", layout = "mirror", diskIds = _disks1, confirmations = new Dictionary<string, string> { ["serial-VYXTEST01"] = "VYXTEST01" } });
        var programs = confirmed.GetProperty("commands").EnumerateArray().Select(command => $"{command.GetProperty("program").GetString()} {command.GetProperty("arguments")[0].GetString()}");
        Assert.Equal(["zpool labelclear", "wipefs --all", "zpool create"], programs);

        var system = await PreviewAsync(new { name = "tank", layout = "single", diskIds = _disks2, confirmations = new Dictionary<string, string> { ["serial-02609329D99"] = "02609329D99" } });
        Assert.Equal(JsonValueKind.Null, system.GetProperty("commands").ValueKind);
        Assert.Equal("system_disk", system.GetProperty("disks")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvalidRequestsFailWithTheirCodes()
    {
        async Task<string> CodeAsync(object request)
        {
            using var response = await _admin.PostAsJsonAsync("/api/storage/pools/preview", request, Token);
            return await response.ErrorCodeAsync();
        }

        Assert.Equal("storage.pool.name_taken", await CodeAsync(new { name = "pithyxtestm", layout = "single", diskIds = _disks3 }));
        Assert.Equal("storage.pool.name_invalid", await CodeAsync(new { name = "Tank", layout = "single", diskIds = _disks3 }));
        Assert.Equal("storage.pool.disk_count", await CodeAsync(new { name = "tank", layout = "raidz1", diskIds = _disks3 }));
        Assert.Equal("storage.pool.layout_invalid", await CodeAsync(new { name = "tank", layout = "raid5", diskIds = _disks3 }));
        Assert.Equal("storage.disk.not_found", await CodeAsync(new { name = "tank", layout = "single", diskIds = _disks4 }));
    }

    [Fact]
    public async Task UsersWhoAreNotAdministratorsGetNothing()
    {
        using var bob = await UserAsync();

        foreach (var path in _disks5)
        {
            using var response = await bob.GetAsync(path, Token);
            Assert.Equal((HttpStatusCode.Forbidden, "auth.permission.denied"), (response.StatusCode, await response.ErrorCodeAsync()));
        }
        using var preview = await bob.PostAsJsonAsync("/api/storage/pools/preview", new { name = "tank", layout = "single", diskIds = _disks3 }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        using var anonymous = await _factory.CreateApiClient().GetAsync("/api/storage/pools", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public void ThePoolTopicIsForAdministratorsOnly()
    {
        ClaimsPrincipal User(params string[] roles) =>
            new(new ClaimsIdentity([.. roles.Select(role => new Claim(ClaimTypes.Role, role))], "test"));

        Assert.True(PoolInventory.IsAllowed(User(SessionAuthenticationHandler.AdminRole), PoolInventory.Topic));
        Assert.False(PoolInventory.IsAllowed(User(), PoolInventory.Topic));
        Assert.False(PoolInventory.IsAllowed(new ClaimsPrincipal(new ClaimsIdentity()), PoolInventory.Topic));
    }
}
