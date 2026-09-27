using System.Net;
using OtpNet;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Auth;

public sealed class AuthFlowTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;

    public AuthFlowTests() => _client = _factory.CreateApiClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task SetupCreatesTheAdminOnceAndSignsIn()
    {
        Assert.True((await (await _client.GetAsync("/api/setup", Token)).JsonAsync()).GetProperty("required").GetBoolean());

        using var setup = await _client.PostAsync("/api/setup", new { username = "Admin", password = AdminPassword, displayName = " Gabriel Ä. " });
        var body = await setup.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Matches("^PTHX-[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){5}-[0-9A-HJKMNP-TV-Z]{2}$", body.GetProperty("recoveryKey").GetString());
        Assert.Equal("admin", body.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("Gabriel Ä.", body.GetProperty("user").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/session", Token)).StatusCode);
        Assert.False((await (await _client.GetAsync("/api/setup", Token)).JsonAsync()).GetProperty("required").GetBoolean());

        using var second = await _client.PostAsync("/api/setup", new { username = "other", password = AdminPassword });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("auth.setup.already_done", await second.ErrorCodeAsync());
    }

    [Fact]
    public async Task PasswordChecksReportTheirArgon2Time()
    {
        await _client.SetupAdminAsync();
        using var login = await _client.LoginAsync("admin", AdminPassword);
        using var session = await _client.GetAsync("/api/auth/session", Token);

        Assert.Matches(@"^argon2;dur=\d+(\.\d)?$", Assert.Single(login.Headers.GetValues("Server-Timing")));
        Assert.False(session.Headers.Contains("Server-Timing"));
    }

    [Theory]
    [InlineData("1admin", "auth.username.invalid")]
    [InlineData("ad min", "auth.username.invalid")]
    [InlineData("ädmin", "auth.username.invalid")]
    [InlineData("admin.name", "auth.username.invalid")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", "auth.username.invalid")]
    [InlineData("", "auth.username.invalid")]
    [InlineData("root", "auth.username.reserved")]
    [InlineData("systemd-network", "auth.username.reserved")]
    public async Task SetupRejectsUserNamesLinuxCannotUse(string username, string expectedCode)
    {
        using var response = await _client.PostAsync("/api/setup", new { username, password = AdminPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task SetupRejectsAShortPassword()
    {
        using var response = await _client.PostAsync("/api/setup", new { username = "admin", password = "short" });
        var body = await response.JsonAsync();

        Assert.Equal("auth.password.too_short", body.GetProperty("code").GetString());
        Assert.Equal(12, body.GetProperty("params").GetProperty("minLength").GetInt32());
        Assert.Equal("The password needs at least 12 characters.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SetupRejectsACommonPassword()
    {
        using var response = await _client.PostAsync("/api/setup", new { username = "admin", password = "Qwertyuiopasdfghjkl" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.password.too_common", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUserFailTheSameWay()
    {
        await _client.SetupAdminAsync();
        using var anonymous = _factory.CreateApiClient();

        using var wrongPassword = await anonymous.LoginAsync("admin", "wrong password!!");
        using var unknownUser = await anonymous.LoginAsync("nobody-here", AdminPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal("auth.credentials.invalid", await wrongPassword.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal("auth.credentials.invalid", await unknownUser.ErrorCodeAsync());
    }

    [Fact]
    public async Task LoginAndLogout()
    {
        await _client.SetupAdminAsync();
        using var browser = _factory.CreateApiClient();

        using var login = await browser.LoginAsync("ADMIN", AdminPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/session", Token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/logout", new { })).StatusCode);

        using var afterLogout = await browser.GetAsync("/api/auth/session", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        Assert.Equal("auth.session.expired", await afterLogout.ErrorCodeAsync());
    }

    [Fact]
    public async Task StateChangingRequestsNeedTheCsrfHeader()
    {
        using var plain = _factory.CreateClient();

        using var response = await plain.PostAsync("/api/auth/login", new { username = "admin", password = AdminPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.csrf.header_missing", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task SixthAttemptWithinFiveMinutesIsRateLimited()
    {
        await _client.SetupAdminAsync();
        for (var i = 0; i < 5; i++)
        {
            using var attempt = await _client.LoginAsync("admin", "wrong password!!");
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var limited = await _client.LoginAsync("admin", AdminPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var problem = await limited.JsonAsync();
        Assert.Equal("auth.rate.limited", problem.GetProperty("code").GetString());
        Assert.Equal("about:blank", problem.GetProperty("type").GetString());
        Assert.Equal(300, int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture));

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        using var afterWindow = await _client.LoginAsync("admin", AdminPassword);
        Assert.Equal(HttpStatusCode.OK, afterWindow.StatusCode);
    }

    [Fact]
    public async Task SuccessfulLoginsAreNotLimited()
    {
        await _client.SetupAdminAsync();

        for (var i = 0; i < 8; i++)
        {
            using var login = await _client.LoginAsync("admin", AdminPassword);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task TotpProtectsTheLoginAndCodesWorkOnlyOnce()
    {
        await _client.SetupAdminAsync();
        var secret = (await (await _client.PostAsync("/api/auth/totp/enroll", new { })).JsonAsync()).GetProperty("secret").GetString()!;
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        string Code() => totp.ComputeTotp(_factory.Time.GetUtcNow().UtcDateTime);

        using var confirm = await _client.PostAsync("/api/auth/totp/confirm", new { code = Code() });
        var recoveryCodes = (await confirm.JsonAsync()).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recoveryCodes.Count);

        using var browser = _factory.CreateApiClient();
        var challenge = (await (await browser.LoginAsync("admin", AdminPassword)).JsonAsync()).GetProperty("challenge").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/session", Token)).StatusCode);

        using var wrong = await browser.PostAsync("/api/auth/login/totp", new { challenge, code = "000000" });
        Assert.Equal("auth.totp.invalid", await wrong.ErrorCodeAsync());

        // The code used for confirmation cannot be replayed within its time step.
        using var replay = await browser.PostAsync("/api/auth/login/totp", new { challenge, code = Code() });
        Assert.Equal("auth.totp.invalid", await replay.ErrorCodeAsync());

        _factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var accepted = await browser.PostAsync("/api/auth/login/totp", new { challenge, code = Code() });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/session", Token)).StatusCode);

        using var reuseChallenge = await browser.PostAsync("/api/auth/login/totp", new { challenge, recoveryCode = recoveryCodes[0] });
        Assert.Equal("auth.challenge.expired", await reuseChallenge.ErrorCodeAsync());

        using var other = _factory.CreateApiClient();
        var second = (await (await other.LoginAsync("admin", AdminPassword)).JsonAsync()).GetProperty("challenge").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync("/api/auth/login/totp", new { challenge = second, recoveryCode = recoveryCodes[0].ToLowerInvariant() })).StatusCode);

        using var third = _factory.CreateApiClient();
        var thirdChallenge = (await (await third.LoginAsync("admin", AdminPassword)).JsonAsync()).GetProperty("challenge").GetString()!;
        using var usedCode = await third.PostAsync("/api/auth/login/totp", new { challenge = thirdChallenge, recoveryCode = recoveryCodes[0] });
        Assert.Equal("auth.totp.invalid", await usedCode.ErrorCodeAsync());
    }

    [Fact]
    public async Task RecoveryResetsThePasswordAndReplacesTheKey()
    {
        var recoveryKey = await _client.SetupAdminAsync();
        await _client.PostAsync("/api/auth/totp/enroll", new { });
        const string NewPassword = "a brand new passphrase";
        using var anonymous = _factory.CreateApiClient();

        using var wrongKey = await anonymous.PostAsync("/api/auth/recover", new { username = "admin", recoveryKey = "0000-0000-0000-0000-0000-0000-00", newPassword = NewPassword });
        Assert.Equal("auth.recovery.invalid", await wrongKey.ErrorCodeAsync());

        using var recovered = await anonymous.PostAsync("/api/auth/recover", new { username = "admin", recoveryKey = recoveryKey.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var newKey = (await recovered.JsonAsync()).GetProperty("recoveryKey").GetString();
        Assert.NotEqual(recoveryKey, newKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/auth/session", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync("admin", AdminPassword)).StatusCode);
        var login = await (await anonymous.LoginAsync("admin", NewPassword)).JsonAsync();
        Assert.False(login.GetProperty("totpRequired").GetBoolean());

        using var oldKeyAgain = await anonymous.PostAsync("/api/auth/recover", new { username = "admin", recoveryKey, newPassword = NewPassword });
        Assert.Equal("auth.recovery.invalid", await oldKeyAgain.ErrorCodeAsync());

        using var withoutPrefix = await anonymous.PostAsync("/api/auth/recover", new { username = "admin", recoveryKey = newKey!["PTHX-".Length..], newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, withoutPrefix.StatusCode);
    }

    [Fact]
    public async Task SessionsExpireAfterSevenIdleDaysAndThirtyDaysAtTheLatest()
    {
        await _client.SetupAdminAsync();

        for (var day = 6; day <= 30; day += 6)
        {
            _factory.Time.Advance(TimeSpan.FromDays(6));
            var expected = day < 30 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
            Assert.Equal(expected, (await _client.GetAsync("/api/auth/session", Token)).StatusCode);
        }

        using var browser = _factory.CreateApiClient();
        await browser.LoginAsync("admin", AdminPassword);
        _factory.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/session", Token)).StatusCode);
    }
}
