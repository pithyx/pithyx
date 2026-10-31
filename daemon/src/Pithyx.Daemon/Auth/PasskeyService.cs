using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Dapper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// Passkeys (D380): discoverable WebAuthn credentials with user verification, so one replaces the password and TOTP at
/// the desktop sign-in. The relying party is the name the browser opened the box under; the box never asks a metadata
/// service, and attestation is not requested. Each ceremony keeps its options in memory for five minutes and is used once.
/// </summary>
public sealed class PasskeyService(SqliteConnectionFactory connections, TimeProvider time)
{
    public const int MaxPerUser = 20;
    public const int MaxNameLength = 64;
    private const int MaxCeremonies = 1000;
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Ceremony> _ceremonies = new(StringComparer.Ordinal);

    private sealed record Ceremony(long? UserId, string Address, RelyingParty Party, string Options, DateTimeOffset ExpiresAt);

    /// <summary>The name and origin a ceremony is bound to.</summary>
    public sealed record RelyingParty(string Id, string Origin);

    /// <summary>
    /// The relying party of a request: the host name it was sent to, over HTTPS (or plain HTTP on <c>localhost</c>,
    /// which browsers treat as secure). An address cannot be a relying party.
    /// </summary>
    public static RelyingParty For(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.Host.ToLowerInvariant();
        var unavailable = new PithyxException(ErrorCodes.AuthPasskeyUnavailable, new Dictionary<string, object?> { ["host"] = host });
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            throw unavailable;
        }
        if (!request.IsHttps && host != "localhost")
        {
            throw unavailable;
        }
        return new RelyingParty(host, $"{request.Scheme}://{request.Host.Value}".ToLowerInvariant());
    }

    /// <summary>Options for a new passkey of the user, after the password was checked.</summary>
    public async Task<(string Ceremony, JsonElement Options)> RegistrationOptionsAsync(
        long userId, string username, string? displayName, RelyingParty party, string address, CancellationToken cancellationToken)
    {
        var existing = await ListRowsAsync(userId, cancellationToken);
        if (existing.Count >= MaxPerUser)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyLimit, new Dictionary<string, object?> { ["max"] = MaxPerUser });
        }
        var options = Fido2(party).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(userId), Name = username, DisplayName = displayName ?? username },
            ExcludeCredentials = [.. existing.Where(row => row.RpId == party.Id).Select(row => new PublicKeyCredentialDescriptor(row.CredentialId))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        var json = options.ToJson();
        return (Store(new Ceremony(userId, address, party, json, time.GetUtcNow() + _lifetime)), Parse(json));
    }

    /// <summary>Checks the authenticator's answer and keeps the new passkey under <paramref name="name"/>.</summary>
    public async Task<PasskeyInfo> RegisterAsync(long userId, string ceremony, string name, JsonElement credential, string address, CancellationToken cancellationToken)
    {
        var trimmed = NormalizeName(name);
        var pending = Take(ceremony, address);
        if (pending.UserId != userId)
        {
            throw new PithyxException(ErrorCodes.AuthChallengeExpired);
        }
        var response = Read<AuthenticatorAttestationRawResponse>(credential);
        RegisteredPublicKeyCredential registered;
        try
        {
            registered = await Fido2(pending.Party).MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = CredentialCreateOptions.FromJson(pending.Options),
                    IsCredentialIdUniqueToUserCallback = async (args, token) => !await ExistsAsync(args.CredentialId, token),
                },
                cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyInvalid);
        }

        var now = Format(time.GetUtcNow());
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM passkeys WHERE user_id = @userId", new { userId }, transaction) >= MaxPerUser)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyLimit, new Dictionary<string, object?> { ["max"] = MaxPerUser });
        }
        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO passkeys (user_id, credential_id, public_key, sign_count, rp_id, name, transports, backed_up, created_at)
            VALUES (@userId, @credentialId, @publicKey, @signCount, @rpId, @name, @transports, @backedUp, @now)
            RETURNING id
            """,
            new
            {
                userId,
                credentialId = registered.Id,
                publicKey = registered.PublicKey,
                signCount = (long)registered.SignCount,
                rpId = pending.Party.Id,
                name = trimmed,
                transports = JsonSerializer.Serialize((registered.Transports ?? []).Select(transport => transport.ToString())),
                backedUp = registered.IsBackedUp,
                now,
            },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return new PasskeyInfo(id, trimmed, pending.Party.Id, registered.IsBackedUp, now, null);
    }

    /// <summary>Options for signing in with any passkey of this name: the authenticator offers the ones it holds.</summary>
    public (string Ceremony, JsonElement Options) SignInOptions(RelyingParty party, string address)
    {
        var options = Fido2(party).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        var json = options.ToJson();
        return (Store(new Ceremony(null, address, party, json, time.GetUtcNow() + _lifetime)), Parse(json));
    }

    /// <summary>Checks a sign-in answer and returns the user it belongs to; the caller checks the account itself.</summary>
    public async Task<long> SignInAsync(string ceremony, JsonElement credential, string address, CancellationToken cancellationToken)
    {
        var pending = Take(ceremony, address);
        if (pending.UserId is not null)
        {
            throw new PithyxException(ErrorCodes.AuthChallengeExpired);
        }
        var response = Read<AuthenticatorAssertionRawResponse>(credential);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(Row.Select + " WHERE credential_id = @id", new { id = response.RawId });
        // A passkey of another name of the box would fail the check anyway; refusing it first says so plainly.
        if (row is null || row.RpId != pending.Party.Id)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyInvalid);
        }
        VerifyAssertionResult result;
        try
        {
            result = await Fido2(pending.Party).MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = AssertionOptions.FromJson(pending.Options),
                    StoredPublicKey = row.PublicKey,
                    StoredSignatureCounter = (uint)row.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(UserHandle(row.UserId))),
                },
                cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyInvalid);
        }
        await connection.ExecuteAsync(
            "UPDATE passkeys SET sign_count = @signCount, backed_up = @backedUp, last_used_at = @now WHERE id = @Id",
            new { signCount = (long)result.SignCount, backedUp = result.IsBackedUp, now = Format(time.GetUtcNow()), row.Id });
        return row.UserId;
    }

    public async Task<IReadOnlyList<PasskeyInfo>> ListAsync(long userId, CancellationToken cancellationToken) =>
        [.. (await ListRowsAsync(userId, cancellationToken)).Select(row => new PasskeyInfo(row.Id, row.Name, row.RpId, row.BackedUp != 0, row.CreatedAt, row.LastUsedAt))];

    public async Task DeleteAsync(long userId, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteAsync("DELETE FROM passkeys WHERE id = @id AND user_id = @userId", new { id, userId }) == 0)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyNotFound);
        }
    }

    /// <summary>The WebAuthn user handle: the account id, which is never given out again, as eight bytes.</summary>
    internal static byte[] UserHandle(long userId)
    {
        var handle = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(handle, userId);
        return handle;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyNameInvalid, new Dictionary<string, object?> { ["maxLength"] = MaxNameLength });
        }
        return trimmed;
    }

    private static Fido2 Fido2(RelyingParty party) => new(new Fido2Configuration
    {
        RPID = party.Id,
        RPName = "Pithyx",
        Origins = new HashSet<string>(StringComparer.Ordinal) { party.Origin },
    });

    private static T Read<T>(JsonElement credential)
    {
        try
        {
            return credential.Deserialize<T>() ?? throw new PithyxException(ErrorCodes.AuthPasskeyInvalid);
        }
        catch (JsonException)
        {
            throw new PithyxException(ErrorCodes.AuthPasskeyInvalid);
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private string Store(Ceremony ceremony)
    {
        RemoveExpired();
        if (_ceremonies.Count >= MaxCeremonies)
        {
            throw new PithyxException(ErrorCodes.AuthRateLimited, new Dictionary<string, object?> { ["retryAfterSeconds"] = 60 });
        }
        var token = Secrets.NewToken();
        _ceremonies[Key(token)] = ceremony;
        return token;
    }

    /// <summary>Removes the ceremony: an answer is checked once, right or wrong.</summary>
    private Ceremony Take(string token, string address)
    {
        RemoveExpired();
        return _ceremonies.TryRemove(Key(token ?? ""), out var ceremony) && ceremony.Address == address
            ? ceremony
            : throw new PithyxException(ErrorCodes.AuthChallengeExpired);
    }

    private static string Key(string token) => Convert.ToHexString(Secrets.Sha256(token));

    private void RemoveExpired()
    {
        var now = time.GetUtcNow();
        foreach (var (key, ceremony) in _ceremonies)
        {
            if (ceremony.ExpiresAt <= now)
            {
                _ceremonies.TryRemove(key, out _);
            }
        }
    }

    private async Task<bool> ExistsAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM passkeys WHERE credential_id = @credentialId)", new { credentialId });
    }

    private async Task<List<Row>> ListRowsAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<Row>(Row.Select + " WHERE user_id = @userId ORDER BY created_at, id", new { userId })];
    }

    private static string Format(DateTimeOffset value) => SessionService.Format(value);

    private sealed record Row(long Id, long UserId, byte[] CredentialId, byte[] PublicKey, long SignCount, string RpId, string Name, long BackedUp, string CreatedAt, string? LastUsedAt)
    {
        public const string Select =
            "SELECT id AS Id, user_id AS UserId, credential_id AS CredentialId, public_key AS PublicKey, sign_count AS SignCount, rp_id AS RpId, name AS Name, backed_up AS BackedUp, created_at AS CreatedAt, last_used_at AS LastUsedAt FROM passkeys";
    }
}
