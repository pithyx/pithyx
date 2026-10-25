using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Auth;

/// <summary>Passkeys (D380) against an authenticator played by the test; the test client opens the box as http://localhost.</summary>
public sealed class PasskeyTests : IDisposable
{
    private const string Origin = "http://localhost";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly SoftAuthenticator _authenticator = new();

    public PasskeyTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        _authenticator.Dispose();
    }

    private const string AnnaPassword = "river-candle-orbit-7";

    private async Task<long> CreateAnnaAsync()
    {
        using var response = await _admin.PostAsync("/api/users", new { username = "anna", password = AnnaPassword, grants = new[] { new { resource = "right:desktopLogin", level = "allow" } } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> AddPasskeyAsync(HttpClient client, string password = AdminPassword, string name = "Laptop")
    {
        using var options = await client.PostAsync("/api/auth/passkeys/options", new { password });
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var body = await options.JsonAsync();
        var credential = _authenticator.Create(body.GetProperty("options"), Origin);
        using var created = await client.PostAsync("/api/auth/passkeys", new { ceremony = body.GetProperty("ceremony").GetString(), name, credential });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Token));
        return await created.JsonAsync();
    }

    private async Task<HttpResponseMessage> SignInAsync(HttpClient client, Func<JsonObject, JsonObject>? tamper = null)
    {
        using var options = await client.PostAsync("/api/auth/passkeys/sign-in/options", new { });
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var body = await options.JsonAsync();
        var credential = _authenticator.Get(body.GetProperty("options"), Origin);
        return await client.PostAsync("/api/auth/passkeys/sign-in", new { ceremony = body.GetProperty("ceremony").GetString(), credential = tamper?.Invoke(credential) ?? credential });
    }

    [Fact]
    public async Task APasskeySignsInWithoutPasswordAndTotp()
    {
        await _admin.SetupAdminAsync();
        var added = await AddPasskeyAsync(_admin);
        using var anonymous = _factory.CreateApiClient();

        using var signedIn = await SignInAsync(anonymous);
        using var session = await anonymous.GetAsync("/api/auth/session", Token);
        var list = await (await _admin.GetAsync("/api/auth/passkeys", Token)).JsonAsync();

        Assert.Equal("Laptop", added.GetProperty("name").GetString());
        Assert.Equal("localhost", added.GetProperty("rpId").GetString());
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal("admin", (await signedIn.JsonAsync()).GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.String, item.GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task AddingAPasskeyNeedsTheCurrentPassword()
    {
        await _admin.SetupAdminAsync();

        using var refused = await _admin.PostAsync("/api/auth/passkeys/options", new { password = "wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("auth.credentials.invalid", await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task ACeremonyIsUsedOnceAndAWrongSignatureIsRefused()
    {
        await _admin.SetupAdminAsync();
        await AddPasskeyAsync(_admin);
        using var anonymous = _factory.CreateApiClient();
        using var options = await anonymous.PostAsync("/api/auth/passkeys/sign-in/options", new { });
        var body = await options.JsonAsync();
        var ceremony = body.GetProperty("ceremony").GetString();
        var credential = _authenticator.Get(body.GetProperty("options"), Origin);

        using var first = await anonymous.PostAsync("/api/auth/passkeys/sign-in", new { ceremony, credential });
        using var replay = await anonymous.PostAsync("/api/auth/passkeys/sign-in", new { ceremony, credential });
        using var forged = await SignInAsync(anonymous, credential =>
        {
            var signature = Convert.FromBase64String(Base64(credential["response"]!["signature"]!.GetValue<string>()));
            signature[^1] ^= 0x01;
            credential["response"]!["signature"] = SoftAuthenticator.Base64Url(signature);
            return credential;
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("auth.challenge.expired", await replay.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal("auth.passkey.invalid", await forged.ErrorCodeAsync());
    }

    [Fact]
    public async Task AnAnswerForAnotherOriginIsRefused()
    {
        await _admin.SetupAdminAsync();
        await AddPasskeyAsync(_admin);
        using var anonymous = _factory.CreateApiClient();
        using var options = await anonymous.PostAsync("/api/auth/passkeys/sign-in/options", new { });
        var body = await options.JsonAsync();
        var credential = _authenticator.Get(body.GetProperty("options"), "https://evil.example");

        using var refused = await anonymous.PostAsync("/api/auth/passkeys/sign-in", new { ceremony = body.GetProperty("ceremony").GetString(), credential });

        Assert.Equal("auth.passkey.invalid", await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task ARemovedPasskeyOrADeactivatedAccountNoLongerSignsIn()
    {
        await _admin.SetupAdminAsync();
        var anna = await CreateAnnaAsync();
        using var user = _factory.CreateApiClient();
        (await user.LoginAsync("anna", AnnaPassword)).Dispose();
        var added = await AddPasskeyAsync(user, AnnaPassword, "Phone");
        using var anonymous = _factory.CreateApiClient();
        using (var allowed = await SignInAsync(anonymous))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using (var deactivate = await _admin.PatchAsJsonAsync($"/api/users/{anna}", new { disabled = true }, Token))
        {
            Assert.True(deactivate.IsSuccessStatusCode, await deactivate.Content.ReadAsStringAsync(Token));
        }
        using var disabled = await SignInAsync(_factory.CreateApiClient());
        using (var activate = await _admin.PatchAsJsonAsync($"/api/users/{anna}", new { disabled = false }, Token))
        {
            Assert.True(activate.IsSuccessStatusCode);
        }
        (await user.LoginAsync("anna", AnnaPassword)).Dispose();
        using var removed = await user.DeleteAsync($"/api/auth/passkeys/{added.GetProperty("id").GetInt64()}", Token);
        using var gone = await SignInAsync(_factory.CreateApiClient());

        Assert.Equal("auth.account.disabled", await disabled.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal("auth.passkey.invalid", await gone.ErrorCodeAsync());
    }

    [Fact]
    public async Task UsersSeeAndRemoveOnlyTheirOwnPasskeys()
    {
        await _admin.SetupAdminAsync();
        var added = await AddPasskeyAsync(_admin);
        await CreateAnnaAsync();
        using var user = _factory.CreateApiClient();
        (await user.LoginAsync("anna", AnnaPassword)).Dispose();

        var list = await (await user.GetAsync("/api/auth/passkeys", Token)).JsonAsync();
        using var removed = await user.DeleteAsync($"/api/auth/passkeys/{added.GetProperty("id").GetInt64()}", Token);

        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Equal("auth.passkey.not_found", await removed.ErrorCodeAsync());
    }

    [Fact]
    public async Task TheRecoveryKeyRemovesThePasskeys()
    {
        var recoveryKey = await _admin.SetupAdminAsync();
        await AddPasskeyAsync(_admin);

        using (var recovered = await _admin.PostAsync("/api/auth/recover", new { username = "admin", recoveryKey, newPassword = "another long password" }))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }
        using var refused = await SignInAsync(_factory.CreateApiClient());

        Assert.Equal("auth.passkey.invalid", await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task PasskeysNeedANameNotAnAddress()
    {
        using var client = _factory.CreateApiClient();
        client.BaseAddress = new Uri("http://192.168.1.20/");

        using var refused = await client.PostAsync("/api/auth/passkeys/sign-in/options", new { });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("auth.passkey.unavailable", await refused.ErrorCodeAsync());
    }

    private static string Base64(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        return padded + new string('=', (4 - (padded.Length % 4)) % 4);
    }

    /// <summary>A platform authenticator with ES256 keys, user verification and no attestation.</summary>
    private sealed class SoftAuthenticator : IDisposable
    {
        private readonly List<(byte[] Id, byte[] UserHandle, string RpId, ECDsa Key)> _credentials = [];
        private uint _counter;

        public void Dispose()
        {
            foreach (var credential in _credentials)
            {
                credential.Key.Dispose();
            }
        }

        public JsonObject Create(JsonElement options, string origin)
        {
            var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
            var userHandle = Decode(options.GetProperty("user").GetProperty("id").GetString()!);
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var id = RandomNumberGenerator.GetBytes(16);
            _credentials.Add((id, userHandle, rpId, key));
            var point = key.ExportParameters(false).Q;
            var coseKey = Cbor.Map((Cbor.Int(1), Cbor.Int(2)), (Cbor.Int(3), Cbor.Int(-7)), (Cbor.Int(-1), Cbor.Int(1)), (Cbor.Int(-2), Cbor.Bytes(point.X!)), (Cbor.Int(-3), Cbor.Bytes(point.Y!)));
            var idLength = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(idLength, (ushort)id.Length);
            // Flags: user present, user verified, attested credential data.
            byte[] authData = [.. AuthData(rpId, 0x45, 0), .. new byte[16], .. idLength, .. id, .. coseKey];
            var attestation = Cbor.Map((Cbor.Text("fmt"), Cbor.Text("none")), (Cbor.Text("attStmt"), Cbor.Map()), (Cbor.Text("authData"), Cbor.Bytes(authData)));
            return new JsonObject
            {
                ["id"] = Base64Url(id),
                ["rawId"] = Base64Url(id),
                ["type"] = "public-key",
                ["response"] = new JsonObject
                {
                    ["clientDataJSON"] = Base64Url(ClientData("webauthn.create", options, origin)),
                    ["attestationObject"] = Base64Url(attestation),
                    ["transports"] = new JsonArray("internal"),
                },
                ["clientExtensionResults"] = new JsonObject(),
            };
        }

        public JsonObject Get(JsonElement options, string origin)
        {
            var rpId = options.GetProperty("rpId").GetString()!;
            var (id, userHandle, _, key) = _credentials.Last(credential => credential.RpId == rpId);
            var authData = AuthData(rpId, 0x05, ++_counter);
            var clientData = ClientData("webauthn.get", options, origin);
            var signature = key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new JsonObject
            {
                ["id"] = Base64Url(id),
                ["rawId"] = Base64Url(id),
                ["type"] = "public-key",
                ["response"] = new JsonObject
                {
                    ["clientDataJSON"] = Base64Url(clientData),
                    ["authenticatorData"] = Base64Url(authData),
                    ["signature"] = Base64Url(signature),
                    ["userHandle"] = Base64Url(userHandle),
                },
                ["clientExtensionResults"] = new JsonObject(),
            };
        }

        private static byte[] AuthData(string rpId, byte flags, uint counter)
        {
            var count = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(count, counter);
            return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, .. count];
        }

        private static byte[] ClientData(string type, JsonElement options, string origin) =>
            JsonSerializer.SerializeToUtf8Bytes(new { type, challenge = options.GetProperty("challenge").GetString(), origin, crossOrigin = false });

        public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Decode(string base64Url) => Convert.FromBase64String(Base64(base64Url));
    }

    /// <summary>The few CBOR items an attestation needs (RFC 8949): integers, byte and text strings, maps.</summary>
    private static class Cbor
    {
        public static byte[] Int(long value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return [.. Head(3, (ulong)bytes.Length), .. bytes];
        }

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) => [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(entry => entry.Key.Concat(entry.Value))];

        private static byte[] Head(int major, ulong value)
        {
            var type = (byte)(major << 5);
            return value switch
            {
                < 24 => [(byte)(type | (byte)value)],
                <= byte.MaxValue => [(byte)(type | 24), (byte)value],
                <= ushort.MaxValue => [(byte)(type | 25), (byte)(value >> 8), (byte)value],
                _ => [(byte)(type | 26), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value],
            };
        }
    }
}
