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
        using var response = await _admin.PostAsJsonAsync("/api/storage/plans", request, Token);
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
        var preview = await PreviewAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "mirror", diskIds = _disks0 });

        Assert.Equal(32, preview.GetProperty("planId").GetString()!.Length);
        var command = preview.GetProperty("commands")[0];
        Assert.Equal("zpool", command.GetProperty("program").GetString());
        var arguments = command.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).ToList();
        Assert.Equal(["tank", "mirror", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi3"], arguments[^4..]);
        Assert.Equal(2, (await (await _admin.GetAsync("/api/storage/pools", Token)).JsonAsync()).GetProperty("pools").GetArrayLength());
    }

    [Fact]
    public async Task ADiskWithDataNeedsItsSerialAndTheSystemDiskNeverGetsCommands()
    {
        var unconfirmed = await PreviewAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "mirror", diskIds = _disks1 });
        Assert.Equal(JsonValueKind.Null, unconfirmed.GetProperty("commands").ValueKind);
        Assert.Equal(JsonValueKind.Null, unconfirmed.GetProperty("planId").ValueKind);
        Assert.Equal("needsSerialConfirmation", unconfirmed.GetProperty("disks")[0].GetProperty("decision").GetString());

        var confirmed = await PreviewAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "mirror", diskIds = _disks1, confirmations = new Dictionary<string, string> { ["serial-VYXTEST01"] = "VYXTEST01" } });
        var programs = confirmed.GetProperty("commands").EnumerateArray().Select(command => $"{command.GetProperty("program").GetString()} {command.GetProperty("arguments")[0].GetString()}");
        Assert.Equal(["zpool labelclear", "wipefs --all", "zpool create", .. Enumerable.Repeat("zfs create", 5)], programs);

        var system = await PreviewAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "single", diskIds = _disks2, confirmations = new Dictionary<string, string> { ["serial-02609329D99"] = "02609329D99" } });
        Assert.Equal(JsonValueKind.Null, system.GetProperty("commands").ValueKind);
        Assert.Equal("system_disk", system.GetProperty("disks")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvalidRequestsFailWithTheirCodes()
    {
        async Task<string> CodeAsync(object request)
        {
            using var response = await _admin.PostAsJsonAsync("/api/storage/plans", request, Token);
            return await response.ErrorCodeAsync();
        }

        Assert.Equal("storage.pool.name_taken", await CodeAsync(new { action = "pool.create", encryption = "none", pool = "pithyxtestm", layout = "single", diskIds = _disks3 }));
        Assert.Equal("storage.pool.name_invalid", await CodeAsync(new { action = "pool.create", encryption = "none", pool = "Tank", layout = "single", diskIds = _disks3 }));
        Assert.Equal("storage.pool.disk_count", await CodeAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "raidz1", diskIds = _disks3 }));
        Assert.Equal("storage.pool.layout_invalid", await CodeAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "raid5", diskIds = _disks3 }));
        Assert.Equal("storage.disk.not_found", await CodeAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "single", diskIds = _disks4 }));
    }

    [Fact]
    public async Task ANewPoolNeedsTheEncryptionAnswerAndEncryptionWaitsForTheKeyVault()
    {
        async Task<string> CodeAsync(object request)
        {
            using var response = await _admin.PostAsJsonAsync("/api/storage/plans", request, Token);
            return await response.ErrorCodeAsync();
        }

        Assert.Equal("storage.pool.encryption_required", await CodeAsync(new { action = "pool.create", pool = "tank", layout = "mirror", diskIds = _disks0 }));
        Assert.Equal("storage.encryption.unavailable", await CodeAsync(new { action = "pool.create", pool = "tank", layout = "mirror", diskIds = _disks0, encryption = "key" }));
        Assert.Equal("storage.plan.action_invalid", await CodeAsync(new { action = "pool.import.all", pool = "tank" }));
    }

    [Fact]
    public async Task DestroyingAPoolNeedsItsNameAndListsItsDatasets()
    {
        using var mismatch = await _admin.PostAsJsonAsync("/api/storage/plans", new { action = "pool.destroy", pool = "pithyxtestm", confirmName = "pithyxtest" }, Token);
        Assert.Equal("storage.pool.name_mismatch", await mismatch.ErrorCodeAsync());

        var preview = await PreviewAsync(new { action = "pool.destroy", pool = "pithyxtestm", confirmName = "pithyxtestm" });
        Assert.Equal(["pithyxtestm"], preview.GetProperty("datasets").EnumerateArray().Select(dataset => dataset.GetString()));
        var command = Assert.Single(preview.GetProperty("commands").EnumerateArray());
        Assert.Equal(["destroy", "pithyxtestm"], command.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()));
    }

    [Fact]
    public async Task AReplacementDiskAlwaysNeedsItsSerialEvenWhenEmpty()
    {
        var pools = await (await _admin.GetAsync("/api/storage/pools", Token)).JsonAsync();
        var leaf = pools.GetProperty("pools")[0].GetProperty("vdevs")[0].GetProperty("children")[1].GetProperty("vdevGuid").GetString();

        var unconfirmed = await PreviewAsync(new { action = "pool.replace", pool = "pithyxtestm", vdev = leaf, diskIds = _disks3 });
        Assert.Equal(("needsSerialConfirmation", "confirm_replacement"), (unconfirmed.GetProperty("disks")[0].GetProperty("decision").GetString(), unconfirmed.GetProperty("disks")[0].GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, unconfirmed.GetProperty("commands").ValueKind);

        var confirmed = await PreviewAsync(new { action = "pool.replace", pool = "pithyxtestm", vdev = leaf, diskIds = _disks3, confirmations = new Dictionary<string, string> { ["serial-VYXTEST02"] = "VYXTEST02" } });
        var arguments = confirmed.GetProperty("commands")[0].GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).ToList();
        Assert.Equal(["replace", "pithyxtestm", leaf, "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2"], arguments);
    }

    [Fact]
    public async Task TheImportScanListsExportedPoolsWithTheGuardsVerdictAndImportsNothing()
    {
        using var response = await _admin.PostAsync("/api/storage/import/scan", null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scan = await response.JsonAsync();

        var pools = scan.GetProperty("pools").EnumerateArray().ToList();
        Assert.Equal(["oldtank", "archive"], pools.Select(pool => pool.GetProperty("name").GetString()));
        Assert.Equal("5249471309174385122", pools[0].GetProperty("poolGuid").GetString());
        Assert.True(pools[0].GetProperty("allowed").GetBoolean());
        Assert.Equal("serial-VYXTEST01", pools[0].GetProperty("vdevs")[0].GetProperty("diskId").GetString());
        Assert.Equal(("allowed", "importable"), (pools[0].GetProperty("disks")[0].GetProperty("decision").GetString(), pools[0].GetProperty("disks")[0].GetProperty("reason").GetString()));
        Assert.False(pools[1].GetProperty("canImport").GetBoolean());
        Assert.Equal(2, (await (await _admin.GetAsync("/api/storage/pools", Token)).JsonAsync()).GetProperty("pools").GetArrayLength());
    }

    [Fact]
    public async Task AReadOnlyImportPreviewShowsTheCommandAndTheNewFields()
    {
        var preview = await PreviewAsync(new { action = "pool.import.readonly", pool = "oldtank", poolGuid = "5249471309174385122" });

        Assert.Equal(32, preview.GetProperty("planId").GetString()!.Length);
        var arguments = preview.GetProperty("commands")[0].GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString());
        Assert.Equal(["import", "-o", "readonly=on", "-o", "cachefile=none", "-N", "-d", "/dev/disk/by-id", "5249471309174385122"], arguments);
        Assert.Equal(0, preview.GetProperty("warnings").GetArrayLength());
        Assert.False(preview.GetProperty("requiresConfirmName").GetBoolean());
        Assert.Equal(0, preview.GetProperty("unmounted").GetArrayLength());
    }

    [Fact]
    public async Task PoolOperationsRefuseWithTheirCodes()
    {
        async Task<string> CodeAsync(object request)
        {
            using var response = await _admin.PostAsJsonAsync("/api/storage/plans", request, Token);
            return await response.ErrorCodeAsync();
        }

        Assert.Equal("storage.import.preview_required", await CodeAsync(new { action = "pool.import", pool = "pithyxtestm" }));
        Assert.Equal("storage.import.not_found", await CodeAsync(new { action = "pool.import.readonly", pool = "x", poolGuid = "1" }));
        Assert.Equal("storage.import.not_importable", await CodeAsync(new { action = "pool.import.readonly", pool = "archive", poolGuid = "7733120945871106301" }));
        Assert.Equal("storage.vdev.not_raidz", await CodeAsync(new { action = "pool.raidz.expand", pool = "pithyxtestm", vdev = "11762469217994242773", diskIds = _disks3 }));
    }

    [Fact]
    public async Task AddingASingleDiskToTheMirrorWarnsAndWaitsForTheName()
    {
        var awaiting = await PreviewAsync(new { action = "pool.vdev.add", pool = "pithyxtestm", layout = "single", diskIds = _disks3 });

        Assert.Equal(["lowers_redundancy"], awaiting.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()));
        Assert.True(awaiting.GetProperty("requiresConfirmName").GetBoolean());
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (awaiting.GetProperty("planId").ValueKind, awaiting.GetProperty("commands").ValueKind));

        var confirmed = await PreviewAsync(new { action = "pool.vdev.add", pool = "pithyxtestm", layout = "single", diskIds = _disks3, confirmName = "pithyxtestm" });
        Assert.Equal(32, confirmed.GetProperty("planId").GetString()!.Length);
        Assert.Equal("--allow-replication-mismatch", confirmed.GetProperty("commands")[0].GetProperty("arguments")[1].GetString());
    }

    [Fact]
    public async Task ExportingAPoolListsItsDatasets()
    {
        var preview = await PreviewAsync(new { action = "pool.export", pool = "pithyxtestm" });

        Assert.Equal(["export", "pithyxtestm"], preview.GetProperty("commands")[0].GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()));
        Assert.Equal(["pithyxtestm"], preview.GetProperty("datasets").EnumerateArray().Select(dataset => dataset.GetString()));
    }

    [Fact]
    public async Task APlanRunsOnceAndOnlyForWhoeverSawIt()
    {
        using var unknown = await _admin.PostAsync($"/api/storage/plans/{new string('0', 32)}/run", null, Token);
        Assert.Equal((HttpStatusCode.NotFound, "storage.plan.not_found"), (unknown.StatusCode, await unknown.ErrorCodeAsync()));

        var preview = await PreviewAsync(new { action = "pool.create", encryption = "none", pool = "tank", layout = "mirror", diskIds = _disks0 });
        using var bob = await UserAsync();
        using var foreign = await bob.PostAsync($"/api/storage/plans/{preview.GetProperty("planId").GetString()}/run", null, Token);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
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
        using var preview = await bob.PostAsJsonAsync("/api/storage/plans", new { action = "pool.create", encryption = "none", pool = "tank", layout = "single", diskIds = _disks3 }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        using var scan = await bob.PostAsync("/api/storage/import/scan", null, Token);
        Assert.Equal(HttpStatusCode.Forbidden, scan.StatusCode);
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
