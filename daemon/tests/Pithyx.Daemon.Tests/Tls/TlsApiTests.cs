using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.Tests.Tls;

public sealed class TlsApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public TlsApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnyoneMayDownloadTheRootButOnlyAdministratorsSeeTheSettings()
    {
        using var anonymous = _factory.CreateApiClient();
        using var root = await anonymous.GetAsync("/api/system/tls/root-ca.crt", Token);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("application/x-x509-ca-cert", root.Content.Headers.ContentType?.MediaType);
        using var certificate = X509Certificate2.CreateFromPem(await root.Content.ReadAsStringAsync(Token));
        Assert.Contains("CN=Pithyx Root CA ", certificate.Subject, StringComparison.Ordinal);

        using var settings = await anonymous.GetAsync("/api/system/tls", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
    }

    [Fact]
    public async Task TheBoxHasARestrictedAuthorityAndItsOwnCertificate()
    {
        await _admin.SetupAdminAsync();

        var settings = await _admin.GetFromJsonAsync<JsonElement>("/api/system/tls", Token);

        // Simulation serves plain HTTP; the certificates exist all the same.
        Assert.False(settings.GetProperty("https").GetBoolean());
        Assert.Equal("authority", settings.GetProperty("source").GetString());
        var authority = settings.GetProperty("authority");
        Assert.True(authority.GetProperty("restricted").GetBoolean());
        Assert.Contains("local", authority.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()));
        Assert.Contains("192.168.0.0/16", authority.GetProperty("addressRanges").EnumerateArray().Select(range => range.GetString()));
        var names = settings.GetProperty("boxNames").EnumerateArray().Select(name => name.GetString()).ToList();
        Assert.Contains("localhost", names);
        Assert.Contains("127.0.0.1", names);
        Assert.Equal(authority.GetProperty("intermediate").GetProperty("subject").GetString(), settings.GetProperty("active").GetProperty("issuer").GetString());
        Assert.Equal(0, settings.GetProperty("issued").GetArrayLength());
    }

    [Fact]
    public async Task IssuesACertificateForAnotherDeviceAndKeepsNoKey()
    {
        await _admin.SetupAdminAsync();

        using var response = await _admin.PostAsJsonAsync("/api/system/tls/issued", new { names = new List<string> { "Printer.lan", "192.168.1.30" }, validityDays = 365, keyType = "rsa" }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var files = await response.JsonAsync();
        var id = files.GetProperty("certificate").GetProperty("id").GetInt64();
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(files.GetProperty("certificatePem").GetString()!);
        Assert.Equal(2, chain.Count);
        Assert.Equal(["printer.lan", "192.168.1.30"], CertificateAuthority.Names(chain[0]));
        Assert.Contains("BEGIN PRIVATE KEY", files.GetProperty("privateKeyPem").GetString(), StringComparison.Ordinal);

        var settings = await _admin.GetFromJsonAsync<JsonElement>("/api/system/tls", Token);
        var issued = Assert.Single(settings.GetProperty("issued").EnumerateArray());
        Assert.Equal("admin", issued.GetProperty("createdBy").GetString());
        Assert.True(issued.GetProperty("currentAuthority").GetBoolean());
        Assert.Equal("rsa", issued.GetProperty("keyType").GetString());

        var again = await _admin.GetStringAsync($"/api/system/tls/issued/{id}/certificate.pem", Token);
        Assert.DoesNotContain("PRIVATE KEY", again, StringComparison.Ordinal);
        Assert.Contains(files.GetProperty("certificatePem").GetString()!.Trim(), again, StringComparison.Ordinal);

        using var deleted = await _admin.DeleteAsync($"/api/system/tls/issued/{id}", Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await _admin.DeleteAsync($"/api/system/tls/issued/{id}", Token);
        Assert.Equal(ErrorCodes.TlsIssuedNotFound, await missing.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(new[] { "bank.example" }, 30, ErrorCodes.TlsNameNotPermitted)]
    [InlineData(new[] { "8.8.8.8" }, 30, ErrorCodes.TlsNameNotPermitted)]
    [InlineData(new[] { "nas..lan" }, 30, ErrorCodes.TlsNameInvalid)]
    [InlineData(new string[0], 30, ErrorCodes.TlsNamesMissing)]
    [InlineData(new[] { "nas.lan" }, 826, ErrorCodes.TlsValidityOutOfRange)]
    [InlineData(new[] { "nas.lan" }, 0, ErrorCodes.TlsValidityOutOfRange)]
    public async Task RefusesWhatTheAuthorityMayNotSign(string[] names, int days, string code)
    {
        await _admin.SetupAdminAsync();

        using var response = await _admin.PostAsJsonAsync("/api/system/tls/issued", new { names, validityDays = days, keyType = "ecdsa" }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task ExtraNamesGoIntoTheBoxsCertificate()
    {
        await _admin.SetupAdminAsync();

        using var outside = await _admin.PutAsJsonAsync("/api/system/tls/names", new { extraNames = new List<string> { "nas.example.org" } }, Token);
        Assert.Equal(ErrorCodes.TlsNameNotPermitted, await outside.ErrorCodeAsync());

        using var response = await _admin.PutAsJsonAsync("/api/system/tls/names", new { extraNames = new List<string> { "NAS.home.arpa" } }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.JsonAsync();
        Assert.Equal(["nas.home.arpa"], settings.GetProperty("extraNames").EnumerateArray().Select(name => name.GetString()));
        Assert.Contains("nas.home.arpa", settings.GetProperty("active").GetProperty("names").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public async Task ANewAuthorityWithAnOwnDomainReplacesTheOldOne()
    {
        await _admin.SetupAdminAsync();
        using (await _admin.PostAsJsonAsync("/api/system/tls/issued", new { names = new List<string> { "nas.lan" }, validityDays = 30, keyType = "ecdsa" }, Token))
        {
        }
        var before = await _admin.GetFromJsonAsync<JsonElement>("/api/system/tls", Token);

        using var response = await _admin.PostAsJsonAsync("/api/system/tls/authority", new { restricted = true, domains = new List<string> { "example.org" } }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await response.JsonAsync();

        var root = after.GetProperty("authority").GetProperty("root").GetProperty("fingerprint").GetString();
        Assert.NotEqual(before.GetProperty("authority").GetProperty("root").GetProperty("fingerprint").GetString(), root);
        Assert.Contains("example.org", after.GetProperty("authority").GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()));
        Assert.False(Assert.Single(after.GetProperty("issued").EnumerateArray()).GetProperty("currentAuthority").GetBoolean());
        using var issued = await _admin.PostAsJsonAsync("/api/system/tls/issued", new { names = new List<string> { "nas.example.org" }, validityDays = 30, keyType = "ecdsa" }, Token);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);

        using var unrestricted = await _admin.PostAsJsonAsync("/api/system/tls/authority", new { restricted = false, domains = Array.Empty<string>() }, Token);
        Assert.False((await unrestricted.JsonAsync()).GetProperty("authority").GetProperty("restricted").GetBoolean());
    }

    [Fact]
    public async Task PresentsAnUploadedCertificateUntilItIsRemoved()
    {
        await _admin.SetupAdminAsync();
        var now = DateTimeOffset.UtcNow;
        var root = CertificateAuthority.CreateRoot("other", NameScope.Unrestricted, now);
        var intermediate = CertificateAuthority.CreateIntermediate(root, "other", now);
        var own = CertificateAuthority.Issue(intermediate, ["nas.example.org"], 90, CertificateKeyType.Ecdsa, now);

        using var wrongKey = await _admin.PutAsJsonAsync("/api/system/tls/custom", new { certificatePem = own.CertificatePem, privateKeyPem = CertificateAuthority.Issue(intermediate, ["x.lan"], 1, CertificateKeyType.Ecdsa, now).PrivateKeyPem }, Token);
        Assert.Equal(ErrorCodes.TlsCustomKeyMismatch, await wrongKey.ErrorCodeAsync());

        using var response = await _admin.PutAsJsonAsync("/api/system/tls/custom", new { certificatePem = own.CertificatePem + CertificateAuthority.Pem(intermediate.Certificate), privateKeyPem = own.PrivateKeyPem }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.JsonAsync();
        Assert.Equal("custom", settings.GetProperty("source").GetString());
        Assert.Equal(CertificateAuthority.Fingerprint(own.Certificate), settings.GetProperty("active").GetProperty("fingerprint").GetString());
        var services = await _admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token);
        Assert.Equal(CertificateAuthority.Fingerprint(own.Certificate), services.GetProperty("ftpFingerprint").GetString());

        using var removed = await _admin.DeleteAsync("/api/system/tls/custom", Token);
        var back = await removed.JsonAsync();
        Assert.Equal("authority", back.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("custom").ValueKind);
    }

    [Fact]
    public async Task KeepsTheAuthorityAcrossARestart()
    {
        var first = new DaemonFactory { KeepDataDirectory = true };
        string fingerprint;
        using (var client = first.CreateApiClient())
        {
            fingerprint = CertificateAuthority.Fingerprint(X509Certificate2.CreateFromPem(await client.GetStringAsync("/api/system/tls/root-ca.crt", Token)));
        }
        var server = await File.ReadAllTextAsync(Path.Combine(first.DataDirectory, "tls", BoxCertificates.ServerFile), Token);
        first.Dispose();

        using var second = new DaemonFactory { DataDirectory = first.DataDirectory };
        using var again = second.CreateApiClient();
        using var root = X509Certificate2.CreateFromPem(await again.GetStringAsync("/api/system/tls/root-ca.crt", Token));
        Assert.Equal(fingerprint, CertificateAuthority.Fingerprint(root));
        // Nothing changed, so the box's certificate is not issued again.
        Assert.Equal(server, await File.ReadAllTextAsync(Path.Combine(first.DataDirectory, "tls", BoxCertificates.ServerFile), Token));
    }
}
