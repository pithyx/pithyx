using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// Elevated rights of app backends (D311, D388): granting a right of the tier high or critical needs a step-up, a
/// critical one also the typed name and tells the other administrators, a granted one can be revoked and granted again,
/// a revoked one stays revoked across updates, and every step-up and change is audited.
/// </summary>
public sealed class ElevatedRightsTests : IAsyncLifetime
{
    private const string AppId = "com.example.probe";
    private const string Pool = "pithyxtestm";
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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A backend whose one service uses the host's network and, with <paramref name="gpu"/>, a GPU node too, or with
    /// <paramref name="raw"/> the unfiltered Docker socket.
    /// </summary>
    private static byte[] Package(string version, bool gpu = false, bool raw = false)
    {
        var rights = "\"host.network\"" + (gpu ? ", \"device:/dev/dri/renderD128\"" : "") + (raw ? ", \"docker.socket.raw\"" : "");
        var manifest = TarBuilder.Manifest(AppId, version, rights,
            ",\n  \"backend\": { \"compose\": \"backend/compose.yaml\", \"endpoints\": { \"api\": { \"service\": \"web\", \"port\": 8080 } }, \"lifecycle\": \"onDemand\" }");
        var compose = TarBuilder.BackendCompose + "    network_mode: host\n" + (gpu ? "    devices: ['/dev/dri/renderD128']\n" : "")
            + (raw ? "    volumes: ['/var/run/docker.sock:/var/run/docker.sock']\n" : "");
        return TarBuilder.MinimalPackage(AppId, version, manifest)
            .File("backend/compose.yaml", compose)
            .File("backend/images.lock.json", TarBuilder.BackendLock())
            .End().Gzip();
    }

    private async Task<JsonElement> UploadAsync(byte[] package)
    {
        using var response = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(package), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<HttpResponseMessage> InstallAsync(string packageId, object request) =>
        await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", request, Token);

    private async Task WaitAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.JsonAsync()).GetProperty("id").GetInt64();
        var jobs = _factory.Services.GetRequiredService<JobService>();
        Job? job = null;
        await JobTestSupport.WaitUntilAsync(async () => (job = await jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.Equal(JobState.Succeeded, job!.State);
    }

    /// <summary>Sends a change of the rights and waits for the backend's restart job, which holds the app meanwhile.</summary>
    private async Task SetAsync(object request)
    {
        (await _admin.PutAsJsonAsync($"/api/apps/{AppId}/elevated", request, Token)).EnsureSuccessStatusCode();
        var registry = _factory.Services.GetRequiredService<AppRegistry>();
        await JobTestSupport.WaitUntilAsync(() => Task.FromResult(!registry.IsBusy(AppId)), "the restart");
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString();

    private IReadOnlyList<AppElevatedState> Elevated() =>
        [.. _factory.Services.GetRequiredService<AppRegistry>().Details(_factory.Services.GetRequiredService<AppRegistry>().Find(AppId)!).Elevated
            .Select(right => new AppElevatedState(right.Right, right.Granted))];

    private async Task<List<string>> AuditAsync() =>
        [.. await _factory.QueryAsync<string>("SELECT action || ' ' || coalesce(json_extract(params, '$.right'), '') FROM audit_log ORDER BY id")];

    [Fact]
    public async Task AnInstallThatGrantsARightNeedsAStepUp()
    {
        var preview = await UploadAsync(Package("1.0.0"));
        Assert.Equal(["host.network"], preview.GetProperty("addedElevated").EnumerateArray().Select(right => right.GetString()));
        var packageId = preview.GetProperty("packageId").GetString()!;

        using var refused = await InstallAsync(packageId, new { pool = Pool });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("auth.step_up.required", await CodeAsync(refused));

        using var wrong = await InstallAsync(packageId, new { pool = Pool, password = "wrong" });
        Assert.Equal("auth.credentials.invalid", await CodeAsync(wrong));

        await WaitAsync(await InstallAsync(packageId, new { pool = Pool, password = AdminPassword }));
        Assert.Equal([new AppElevatedState("host.network", true)], Elevated());
        Assert.Equal(["auth.step_up.failed ", "auth.step_up.confirmed ", "app.elevated.granted host.network"], await AuditAsync());
    }

    [Fact]
    public async Task AnInstallCanLeaveItsRightsRevokedWithoutAStepUp()
    {
        var packageId = (await UploadAsync(Package("1.0.0"))).GetProperty("packageId").GetString()!;

        await WaitAsync(await InstallAsync(packageId, new { pool = Pool, elevated = false }));

        Assert.Equal([new AppElevatedState("host.network", false)], Elevated());
        Assert.Empty(await AuditAsync());
    }

    [Fact]
    public async Task AStepUpHoldsForFiveMinutesInItsSession()
    {
        var packageId = (await UploadAsync(Package("1.0.0"))).GetProperty("packageId").GetString()!;
        await WaitAsync(await InstallAsync(packageId, new { pool = Pool, elevated = false }));
        var grant = new { rights = new[] { new { right = "host.network", granted = true } } };
        var revoke = new { rights = new[] { new { right = "host.network", granted = false } } };

        using var refused = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/elevated", grant, Token);
        Assert.Equal("auth.step_up.required", await CodeAsync(refused));
        Assert.Equal(JsonValueKind.Null, (await _admin.GetFromJsonAsync<JsonElement>("/api/auth/step-up", Token)).GetProperty("until").ValueKind);

        (await _admin.PostAsJsonAsync("/api/auth/step-up", new { password = AdminPassword }, Token)).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.String, (await _admin.GetFromJsonAsync<JsonElement>("/api/auth/step-up", Token)).GetProperty("until").ValueKind);
        await SetAsync(grant);
        Assert.Equal([new AppElevatedState("host.network", true)], Elevated());

        // Revoking never needs a step-up; granting again within the five minutes needs no new one.
        _factory.Time.Advance(TimeSpan.FromMinutes(4));
        await SetAsync(revoke);
        await SetAsync(grant);
        await SetAsync(revoke);

        _factory.Time.Advance(TimeSpan.FromMinutes(2));
        using var expired = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/elevated", grant, Token);
        Assert.Equal("auth.step_up.required", await CodeAsync(expired));
        Assert.Equal([new AppElevatedState("host.network", false)], Elevated());

        using var other = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/elevated", new { rights = new[] { new { right = "host.pid", granted = true } } }, Token);
        Assert.Equal("app.permission.not_requested", await CodeAsync(other));

        Assert.Equal(
            [
                "auth.step_up.confirmed ",
                "app.elevated.granted host.network",
                "app.elevated.revoked host.network",
                "app.elevated.granted host.network",
                "app.elevated.revoked host.network",
            ],
            await AuditAsync());
    }

    [Fact]
    public async Task AnUpdateKeepsARevokedRightRevokedAndAsksForANewOne()
    {
        var first = (await UploadAsync(Package("1.0.0"))).GetProperty("packageId").GetString()!;
        await WaitAsync(await InstallAsync(first, new { pool = Pool, elevated = false }));

        // A GPU node is of the tier elevated: the administrator's click grants it, without a step-up.
        var update = await UploadAsync(Package("1.1.0", gpu: true));
        Assert.Equal(["device:/dev/dri/renderD128"], update.GetProperty("addedElevated").EnumerateArray().Select(right => right.GetString()));
        await WaitAsync(await InstallAsync(update.GetProperty("packageId").GetString()!, new { }));

        Assert.Equal([new AppElevatedState("host.network", false), new AppElevatedState("device:/dev/dri/renderD128", true)], Elevated());

        // Back to 1.0.0 drops the GPU right; a later update asks for it again.
        var back = await UploadAsync(Package("1.0.0"));
        Assert.Equal(["device:/dev/dri/renderD128"], back.GetProperty("removedElevated").EnumerateArray().Select(right => right.GetString()));
        await WaitAsync(await InstallAsync(back.GetProperty("packageId").GetString()!, new { }));
        Assert.Equal([new AppElevatedState("host.network", false)], Elevated());
    }

    [Fact]
    public async Task AStepUpWithTwoFactorSignInNeedsAFreshCode()
    {
        var secret = (await (await _admin.PostAsync("/api/auth/totp/enroll", new { })).JsonAsync()).GetProperty("secret").GetString()!;
        var totp = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret));
        string Code() => totp.ComputeTotp(_factory.Time.GetUtcNow().UtcDateTime);
        (await _admin.PostAsync("/api/auth/totp/confirm", new { code = Code() })).EnsureSuccessStatusCode();
        _factory.Time.Advance(TimeSpan.FromSeconds(30));

        using var withoutCode = await _admin.PostAsJsonAsync("/api/auth/step-up", new { password = AdminPassword }, Token);
        Assert.Equal("auth.totp.invalid", await CodeAsync(withoutCode));
        (await _admin.PostAsJsonAsync("/api/auth/step-up", new { password = AdminPassword, totpCode = Code() }, Token)).EnsureSuccessStatusCode();

        Assert.Equal(["auth.step_up.failed ", "auth.step_up.confirmed "], await AuditAsync());
    }

    [Fact]
    public async Task AStepUpBelongsToItsSession()
    {
        (await _admin.PostAsJsonAsync("/api/auth/step-up", new { password = AdminPassword }, Token)).EnsureSuccessStatusCode();
        using var other = _factory.CreateApiClient();
        await other.LoginAsync("admin", AdminPassword);

        Assert.Equal(JsonValueKind.Null, (await other.GetFromJsonAsync<JsonElement>("/api/auth/step-up", Token)).GetProperty("until").ValueKind);
    }

    [Fact]
    public async Task ACriticalRightNeedsTheTypedNameAndTellsTheOtherAdministrators()
    {
        var other = await _factory.InsertUserAsync("ada", admin: true);
        var preview = await UploadAsync(Package("1.0.0", raw: true));
        var packageId = preview.GetProperty("packageId").GetString()!;

        using var withoutName = await InstallAsync(packageId, new { pool = Pool, password = AdminPassword });
        Assert.Equal(HttpStatusCode.BadRequest, withoutName.StatusCode);
        Assert.Equal("app.elevated.confirmation_required", await CodeAsync(withoutName));
        using var wrongName = await InstallAsync(packageId, new { pool = Pool, confirmCritical = "com.example.other" });
        Assert.Equal("app.elevated.confirmation_required", await CodeAsync(wrongName));

        await WaitAsync(await InstallAsync(packageId, new { pool = Pool, confirmCritical = AppId }));

        Assert.Equal([new AppElevatedState("host.network", true), new AppElevatedState("docker.socket.raw", true)], Elevated());
        var details = _factory.Services.GetRequiredService<AppRegistry>().Details(_factory.Services.GetRequiredService<AppRegistry>().Find(AppId)!);
        Assert.Equal([Daemon.Apps.Manifest.RiskTier.High, Daemon.Apps.Manifest.RiskTier.Critical], details.Elevated.Select(right => right.Tier));
        var notified = await _factory.QueryAsync<(long UserId, string Code, string Params)>("SELECT user_id, code, params FROM notifications WHERE code LIKE 'apps.%' ORDER BY id");
        var notification = Assert.Single(notified);
        Assert.Equal(other, notification.UserId);
        Assert.Equal(AppService.CriticalGrantedCode, notification.Code);
        Assert.Contains("docker.socket.raw", notification.Params, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrantingACriticalRightAgainNeedsTheTypedNameToo()
    {
        var packageId = (await UploadAsync(Package("1.0.0", raw: true))).GetProperty("packageId").GetString()!;
        await WaitAsync(await InstallAsync(packageId, new { pool = Pool, elevated = false }));
        (await _admin.PostAsJsonAsync("/api/auth/step-up", new { password = AdminPassword }, Token)).EnsureSuccessStatusCode();
        var grant = new { rights = new[] { new { right = "docker.socket.raw", granted = true } } };

        using var refused = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/elevated", grant, Token);
        Assert.Equal("app.elevated.confirmation_required", await CodeAsync(refused));

        await SetAsync(new { rights = grant.rights, confirmCritical = AppId });
        Assert.Contains(new AppElevatedState("docker.socket.raw", true), Elevated());
        // Revoking it takes one click.
        await SetAsync(new { rights = new[] { new { right = "docker.socket.raw", granted = false } } });
        Assert.Contains(new AppElevatedState("docker.socket.raw", false), Elevated());
    }
}
