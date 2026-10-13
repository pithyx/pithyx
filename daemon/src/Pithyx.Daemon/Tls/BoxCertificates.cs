using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Network;

namespace Pithyx.Daemon.Tls;

/// <summary>
/// The certificates of the box (D346): its authority (root and intermediate), its own server certificate from that
/// authority, an optional uploaded certificate, and the certificates issued for other devices. The web ports and
/// FTPS take <see cref="ServerOptions"/> and <see cref="Context"/> on every handshake, so a renewed certificate
/// applies without a restart. The box's certificate follows its host name and addresses (netlink events through
/// <see cref="NetworkChange"/> and the network settings), and is renewed 30 days before it ends.
/// </summary>
public sealed partial class BoxCertificates(
    TlsOptions options,
    SqliteConnectionFactory connections,
    NetworkHost host,
    NetworkService network,
    TimeProvider time,
    ILogger<BoxCertificates> logger) : BackgroundService
{
    public const string ExtraNamesSetting = "tls.extraNames";
    public const int MaxExtraNames = 20;
    public const int MaxIssuedNames = 100;
    public const int MaxPemLength = 64 * 1024;

    public const string RootFile = "root-ca.pem";
    public const string RootKeyFile = "root-ca.key";
    public const string IntermediateFile = "intermediate-ca.pem";
    public const string IntermediateKeyFile = "intermediate-ca.key";
    public const string ServerFile = "box.pem";
    public const string ServerKeyFile = "box.key";
    public const string CustomFile = "custom.pem";
    public const string CustomKeyFile = "custom.key";

    // The self-signed FTPS certificate of D291; the box's own certificate replaced it.
    private const string LegacyFtpsFile = "ftps.pfx";

    // Cards whose addresses no client connects to: the Docker bridges of the apps and of Docker itself.
    private static readonly string[] _ignoredCards = ["docker", "br-", "veth", "px-", "virbr"];

    private sealed record Loaded(
        SigningCertificate Root,
        SigningCertificate Intermediate,
        X509Certificate2 Server,
        X509Certificate2Collection? Custom,
        X509Certificate2 Active,
        SslStreamCertificateContext Context,
        SslServerAuthenticationOptions ServerOptions);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Channel<bool> _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile Loaded? _loaded;

    public bool Https => options.Serve;

    /// <summary>The certificate and chain the box presents; <see cref="LoadAsync"/> runs before the first handshake.</summary>
    public SslStreamCertificateContext Context => Current.Context;

    /// <summary>The TLS settings of the web ports: the current certificate, HTTP/2 and HTTP/1.1, TLS 1.2 and 1.3.</summary>
    public SslServerAuthenticationOptions ServerOptions => Current.ServerOptions;

    /// <summary>The certificate the box presents now.</summary>
    public X509Certificate2 Active => Current.Active;

    /// <summary>The same, or null before <see cref="LoadAsync"/> ran.</summary>
    public X509Certificate2? ActiveOrNull => _loaded?.Active;

    private Loaded Current => _loaded ?? throw new InvalidOperationException("The certificates are not loaded yet.");

    /// <summary>Raised after the certificates were loaded or renewed; the authority may have changed.</summary>
    public event Action? Changed;

    /// <summary>The root of the box's authority, for checking certificates it issued.</summary>
    public X509Certificate2 Root => Current.Root.Certificate;

    /// <summary>Whether the box's authority may sign <paramref name="name"/>.</summary>
    public bool Permits(string name) => NameScope.FromCertificate(Current.Root.Certificate).Permits(name);

    /// <summary>
    /// A server certificate for <paramref name="names"/> from the box's authority with the chain to present, kept in
    /// memory only (the reverse proxy's hosts, D347); null when a name lies outside the authority's scope.
    /// </summary>
    public (X509Certificate2 Certificate, X509Certificate2Collection Chain)? IssueServer(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var loaded = Current;
        var scope = NameScope.FromCertificate(loaded.Root.Certificate);
        if (names.Count == 0 || !names.All(scope.Permits))
        {
            return null;
        }
        var issued = CertificateAuthority.Issue(loaded.Intermediate, names, CertificateAuthority.ServerDays, CertificateKeyType.Ecdsa, time.GetUtcNow());
        return (issued.Certificate, new X509Certificate2Collection(new[] { loaded.Intermediate.Certificate, loaded.Root.Certificate }));
    }

    /// <summary>Loads the certificates, making what is missing, invalid or due; also after a restore.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await LoadLockedAsync(newScope: null, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke();
    }

    public async Task<TlsSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        var loaded = Current;
        var extra = await ExtraNamesAsync(cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(long Id, string Certificate, string KeyType, string RootFingerprint, string CreatedAt, string? CreatedBy)>(
            "SELECT id, certificate, key_type, root_fingerprint, created_at, created_by FROM tls_issued ORDER BY id DESC");
        var rootFingerprint = CertificateAuthority.Fingerprint(loaded.Root.Certificate);
        var issued = rows.Select(row =>
        {
            using var certificate = X509Certificate2.CreateFromPem(row.Certificate);
            return new IssuedCertificate(
                row.Id,
                Info(certificate),
                row.KeyType == "rsa" ? CertificateKeyType.Rsa : CertificateKeyType.Ecdsa,
                DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture),
                row.CreatedBy,
                row.RootFingerprint == rootFingerprint);
        }).ToList();
        var scope = NameScope.FromCertificate(loaded.Root.Certificate);
        return new TlsSettings(
            options.Serve,
            loaded.Custom is null ? ActiveCertificateSource.Authority : ActiveCertificateSource.Custom,
            Info(loaded.Active),
            new AuthorityInfo(
                Info(loaded.Root.Certificate),
                Info(loaded.Intermediate.Certificate),
                scope.Restricted,
                scope.Domains,
                [.. scope.Addresses.Select(range => range.ToString())]),
            CertificateAuthority.Names(loaded.Server),
            extra,
            NameScope.Local(HostName(), SearchDomains(), []).Domains,
            loaded.Custom is { } custom ? Info(custom[0]) : null,
            issued);
    }

    /// <summary>The root in PEM, for devices to trust.</summary>
    public string RootPem => CertificateAuthority.Pem(Current.Root.Certificate);

    public async Task SetExtraNamesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count > MaxExtraNames)
        {
            throw new PithyxException(ErrorCodes.TlsNamesTooMany, new Dictionary<string, object?> { ["max"] = MaxExtraNames });
        }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var scope = NameScope.FromCertificate(Current.Root.Certificate);
            var valid = names.Select(NameScope.Name).Distinct(StringComparer.Ordinal).ToList();
            EnsurePermitted(scope, valid);
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    new { key = ExtraNamesSetting, value = JsonSerializer.Serialize(valid) });
            }
            await LoadLockedAsync(newScope: null, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Replaces the authority with a new root and intermediate, and the box's certificate with one from them.</summary>
    public async Task CreateAuthorityAsync(CreateAuthorityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = request.Restricted
            ? NameScope.Local(HostName(), SearchDomains(), request.Domains ?? [])
            : NameScope.Unrestricted;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await LoadLockedAsync(scope, cancellationToken);
            LogAuthorityCreated(scope.Restricted, scope.Domains);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke();
    }

    public async Task<IssuedCertificateFiles> IssueAsync(IssueCertificateRequest request, string? username, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var names = (request.Names ?? []).Select(NameScope.Name).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            throw new PithyxException(ErrorCodes.TlsNamesMissing);
        }
        if (names.Count > MaxIssuedNames)
        {
            throw new PithyxException(ErrorCodes.TlsNamesTooMany, new Dictionary<string, object?> { ["max"] = MaxIssuedNames });
        }
        if (request.ValidityDays is < 1 or > CertificateAuthority.MaxIssuedDays)
        {
            throw new PithyxException(ErrorCodes.TlsValidityOutOfRange, new Dictionary<string, object?> { ["min"] = 1, ["max"] = CertificateAuthority.MaxIssuedDays });
        }
        var loaded = Current;
        EnsurePermitted(NameScope.FromCertificate(loaded.Root.Certificate), names);
        var now = time.GetUtcNow();
        var issued = CertificateAuthority.Issue(loaded.Intermediate, names, request.ValidityDays, request.KeyType, now);
        var chain = issued.CertificatePem + CertificateAuthority.Pem(loaded.Intermediate.Certificate);
        long id;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            id = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO tls_issued (certificate, chain, key_type, root_fingerprint, created_at, created_by)
                VALUES (@certificate, @chain, @keyType, @root, @createdAt, @username)
                RETURNING id
                """,
                new
                {
                    certificate = issued.CertificatePem,
                    chain,
                    keyType = request.KeyType == CertificateKeyType.Rsa ? "rsa" : "ecdsa",
                    root = CertificateAuthority.Fingerprint(loaded.Root.Certificate),
                    createdAt = now.ToString("O", CultureInfo.InvariantCulture),
                    username,
                });
        }
        LogIssued(id, names);
        var entry = new IssuedCertificate(id, Info(issued.Certificate), request.KeyType, now, username, CurrentAuthority: true);
        issued.Certificate.Dispose();
        return new IssuedCertificateFiles(entry, chain, issued.PrivateKeyPem);
    }

    /// <summary>The issued certificate with the intermediate it was issued under, without its key.</summary>
    public async Task<string> IssuedChainAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>("SELECT chain FROM tls_issued WHERE id = @id", new { id })
            ?? throw IssuedNotFound(id);
    }

    /// <summary>Forgets an issued certificate. Without a revocation list it stays valid on devices until it ends.</summary>
    public async Task DeleteIssuedAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteAsync("DELETE FROM tls_issued WHERE id = @id", new { id }) == 0)
        {
            throw IssuedNotFound(id);
        }
    }

    /// <summary>Presents <paramref name="request"/>'s certificate from now on, after checking it.</summary>
    public async Task SetCustomAsync(CustomCertificateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chain = ParseCustom(request.CertificatePem, request.PrivateKeyPem, time.GetUtcNow());
        foreach (var certificate in chain)
        {
            certificate.Dispose();
        }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            WriteFile(CustomFile, request.CertificatePem.Trim() + "\n");
            WriteFile(CustomKeyFile, request.PrivateKeyPem.Trim() + "\n");
            await LoadLockedAsync(newScope: null, cancellationToken);
            var fingerprint = CertificateAuthority.Fingerprint(Current.Active);
            LogCustomSet(fingerprint);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Goes back to the box's own certificate.</summary>
    public async Task RemoveCustomAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            File.Delete(PathOf(CustomFile));
            File.Delete(PathOf(CustomKeyFile));
            await LoadLockedAsync(newScope: null, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// An uploaded certificate with its chain, checked: PEM that parses, a private key that belongs to the first
    /// certificate, and a validity that already started and has not ended. Throws <c>tls.custom.*</c>.
    /// </summary>
    public static X509Certificate2Collection ParseCustom(string? certificatePem, string? privateKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(certificatePem) || string.IsNullOrWhiteSpace(privateKeyPem)
            || certificatePem.Length > MaxPemLength || privateKeyPem.Length > MaxPemLength)
        {
            throw new PithyxException(ErrorCodes.TlsCustomInvalid);
        }
        var chain = new X509Certificate2Collection();
        try
        {
            chain.ImportFromPem(certificatePem);
        }
        catch (CryptographicException exception)
        {
            throw new PithyxException(ErrorCodes.TlsCustomInvalid, innerException: exception);
        }
        if (chain.Count == 0)
        {
            throw new PithyxException(ErrorCodes.TlsCustomInvalid);
        }
        X509Certificate2 leaf;
        try
        {
            leaf = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
            // The certificate parsed above, so what failed is the key: unreadable, encrypted or another certificate's.
            throw new PithyxException(ErrorCodes.TlsCustomKeyMismatch, innerException: exception);
        }
        if (leaf.NotAfter.ToUniversalTime() <= now.UtcDateTime || leaf.NotBefore.ToUniversalTime() > now.UtcDateTime)
        {
            var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            leaf.Dispose();
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
            throw new PithyxException(ErrorCodes.TlsCustomExpired, new Dictionary<string, object?> { ["notAfter"] = notAfter.ToString("O", CultureInfo.InvariantCulture) });
        }
        chain[0].Dispose();
        chain[0] = leaf;
        return chain;
    }

    /// <summary>
    /// The names of the box's own certificate: the host name, its mDNS name, the host name in each search domain,
    /// <c>localhost</c>, the extra names, then every address and the loopback addresses, as far as
    /// <paramref name="scope"/> permits them (a single name outside it would make a device refuse the certificate).
    /// </summary>
    public static IReadOnlyList<string> BoxNames(string hostName, IEnumerable<string> searchDomains, IEnumerable<string> extra, IEnumerable<IPAddress> addresses, NameScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        ArgumentNullException.ThrowIfNull(searchDomains);
        ArgumentNullException.ThrowIfNull(extra);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(scope);
        var host = hostName.ToLowerInvariant();
        var names = new List<string> { host, $"{host}.local" };
        names.AddRange(searchDomains.Select(domain => $"{host}.{domain.ToLowerInvariant()}"));
        names.Add("localhost");
        names.AddRange(extra);
        var ordered = addresses
            .Where(address => !address.IsIPv6LinkLocal && !address.IsIPv6Multicast)
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            // Without the zone of a card; a certificate carries the bare address.
            .Select(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address)
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(address => address.ToString());
        names.AddRange(ordered);
        names.Add("127.0.0.1");
        names.Add("::1");
        return [.. names.Where(name => IsValidName(name) && scope.Permits(name)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The search domains of <c>/etc/resolv.conf</c> (<c>search</c> and <c>domain</c> lines).</summary>
    public static IReadOnlyList<string> ParseSearchDomains(string resolvConf)
    {
        ArgumentNullException.ThrowIfNull(resolvConf);
        return [.. resolvConf.Split('\n')
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1 && parts[0] is "search" or "domain")
            .SelectMany(parts => parts.Skip(1))
            .Select(domain => domain.TrimEnd('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0 && IsValidName(domain))
            .Distinct(StringComparer.Ordinal)];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Signal() => _refresh.Writer.TryWrite(true);
        void AddressChanged(object? sender, EventArgs args) => Signal();
        void NetworkChanged(NetworkState state) => Signal();
        if (options.ReadHost)
        {
            NetworkChange.NetworkAddressChanged += AddressChanged;
        }
        network.Changed += NetworkChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(UntilDue());
                try
                {
                    await _refresh.Reader.ReadAsync(wait.Token);
                    // DHCP brings an address, routes and name servers in a burst; renew once after it.
                    await Task.Delay(TimeSpan.FromSeconds(3), time, stoppingToken);
                    _refresh.Reader.TryRead(out _);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The next renewal is due.
                }
                try
                {
                    await LoadAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogRefreshFailed(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            network.Changed -= NetworkChanged;
        }
    }

    // Until the first certificate is due for renewal, checked at least once a day (the clock may jump).
    private TimeSpan UntilDue()
    {
        if (_loaded is not { } loaded)
        {
            return TimeSpan.FromMinutes(1);
        }
        var now = time.GetUtcNow().UtcDateTime;
        var due = new[]
        {
            loaded.Server.NotAfter.ToUniversalTime() - CertificateAuthority.ServerRenewal,
            loaded.Intermediate.Certificate.NotAfter.ToUniversalTime() - CertificateAuthority.IntermediateRenewal,
            loaded.Custom?[0].NotAfter.ToUniversalTime() ?? DateTime.MaxValue,
        }.Min() - now;
        return due < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : due > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : due;
    }

    private async Task LoadLockedAsync(NameScope? newScope, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var hostName = HostName();
        Directory.CreateDirectory(options.Directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(options.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var root = newScope is null ? ReadSigning(RootFile, RootKeyFile) : null;
        if (root is null || root.Certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime)
        {
            root = CertificateAuthority.CreateRoot(hostName, newScope ?? NameScope.Local(hostName, SearchDomains(), []), now);
            WriteSigning(RootFile, RootKeyFile, root);
            var fingerprint = CertificateAuthority.Fingerprint(root.Certificate);
            LogRootCreated(fingerprint);
        }

        var intermediate = ReadSigning(IntermediateFile, IntermediateKeyFile);
        if (intermediate is null
            || !CertificateAuthority.IsIssuedBy(intermediate.Certificate, root.Certificate)
            || intermediate.Certificate.NotAfter.ToUniversalTime() - CertificateAuthority.IntermediateRenewal <= now.UtcDateTime)
        {
            intermediate = CertificateAuthority.CreateIntermediate(root, hostName, now);
            WriteSigning(IntermediateFile, IntermediateKeyFile, intermediate);
            var fingerprint = CertificateAuthority.Fingerprint(intermediate.Certificate);
            LogIntermediateCreated(fingerprint, intermediate.Certificate.NotAfter);
        }

        var names = BoxNames(hostName, SearchDomains(), await ExtraNamesAsync(cancellationToken), Addresses(), NameScope.FromCertificate(root.Certificate));
        var server = ReadServer();
        if (server is null
            || !CertificateAuthority.IsIssuedBy(server, intermediate.Certificate)
            || server.NotAfter.ToUniversalTime() - CertificateAuthority.ServerRenewal <= now.UtcDateTime
            || !CertificateAuthority.Names(server).ToHashSet(StringComparer.Ordinal).SetEquals(names))
        {
            var issued = CertificateAuthority.Issue(intermediate, names, CertificateAuthority.ServerDays, CertificateKeyType.Ecdsa, now);
            WriteFile(ServerFile, issued.CertificatePem);
            WriteFile(ServerKeyFile, issued.PrivateKeyPem);
            server = issued.Certificate;
            LogServerIssued(names, server.NotAfter);
        }

        X509Certificate2Collection? custom = null;
        if (File.Exists(PathOf(CustomFile)) && File.Exists(PathOf(CustomKeyFile)))
        {
            try
            {
                custom = ParseCustom(File.ReadAllText(PathOf(CustomFile)), File.ReadAllText(PathOf(CustomKeyFile)), now);
            }
            catch (PithyxException exception)
            {
                // An expired or broken upload must not take the web interface down; the box's own certificate takes over.
                LogCustomUnusable(exception.Code);
            }
        }

        File.Delete(PathOf(LegacyFtpsFile));

        var active = custom?[0] ?? server;
        var chain = custom is not null
            ? new X509Certificate2Collection(custom.Skip(1).ToArray())
            : new X509Certificate2Collection(new[] { intermediate.Certificate, root.Certificate });
        var context = SslStreamCertificateContext.Create(active, chain, offline: true);
        _loaded = new Loaded(root, intermediate, server, custom, active, context, new SslServerAuthenticationOptions
        {
            ServerCertificateContext = context,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
        });
    }

    private async Task<IReadOnlyList<string>> ExtraNamesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = ExtraNamesSetting });
        return json is null ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    private string HostName() => host.HostName.ToLowerInvariant();

    private IReadOnlyList<string> SearchDomains()
    {
        if (!options.ReadHost)
        {
            return [];
        }
        try
        {
            return ParseSearchDomains(File.ReadAllText("/etc/resolv.conf"));
        }
        catch (IOException)
        {
            return [];
        }
    }

    private List<IPAddress> Addresses()
    {
        if (!options.ReadHost)
        {
            return [];
        }
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == OperationalStatus.Up
                && card.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && !_ignoredCards.Any(prefix => card.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .SelectMany(card => card.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address))
            .ToList();
    }

    private static void EnsurePermitted(NameScope scope, IEnumerable<string> names)
    {
        if (names.FirstOrDefault(name => !scope.Permits(name)) is { } outside)
        {
            throw new PithyxException(ErrorCodes.TlsNameNotPermitted, new Dictionary<string, object?> { ["name"] = outside });
        }
    }

    private static bool IsValidName(string name)
    {
        try
        {
            NameScope.Name(name);
            return true;
        }
        catch (PithyxException)
        {
            return false;
        }
    }

    public static CertificateInfo Info(X509Certificate2 certificate) => new(
        certificate.Subject,
        certificate.Issuer,
        CertificateAuthority.Names(certificate),
        new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
        CertificateAuthority.Fingerprint(certificate));

    private static PithyxException IssuedNotFound(long id) =>
        new(ErrorCodes.TlsIssuedNotFound, new Dictionary<string, object?> { ["id"] = id });

    private string PathOf(string file) => Path.Combine(options.Directory, file);

    private SigningCertificate? ReadSigning(string certificateFile, string keyFile)
    {
        try
        {
            if (!File.Exists(PathOf(certificateFile)) || !File.Exists(PathOf(keyFile)))
            {
                return null;
            }
            var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(PathOf(certificateFile)));
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(PathOf(keyFile)));
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || !publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            {
                LogFileUnusable(certificateFile);
                return null;
            }
            return new SigningCertificate(certificate, key);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or IOException)
        {
            LogFileUnusable(certificateFile);
            return null;
        }
    }

    private X509Certificate2? ReadServer()
    {
        try
        {
            return File.Exists(PathOf(ServerFile)) && File.Exists(PathOf(ServerKeyFile))
                ? X509Certificate2.CreateFromPemFile(PathOf(ServerFile), PathOf(ServerKeyFile))
                : null;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or IOException)
        {
            LogFileUnusable(ServerFile);
            return null;
        }
    }

    private void WriteSigning(string certificateFile, string keyFile, SigningCertificate signing)
    {
        WriteFile(keyFile, signing.Key.ExportPkcs8PrivateKeyPem() + "\n");
        WriteFile(certificateFile, CertificateAuthority.Pem(signing.Certificate));
    }

    // Written next to the target and renamed over it, readable by root only.
    private void WriteFile(string file, string content)
    {
        var path = PathOf(file);
        var temporary = path + ".new";
        File.WriteAllText(temporary, content);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the box's root certificate authority {Fingerprint}")]
    private partial void LogRootCreated(string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the intermediate certificate authority {Fingerprint}, valid until {NotAfter}")]
    private partial void LogIntermediateCreated(string fingerprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issued the box's certificate for {Names}, valid until {NotAfter}")]
    private partial void LogServerIssued(IReadOnlyList<string> names, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created a new certificate authority (restricted {Restricted}, domains {Domains})")]
    private partial void LogAuthorityCreated(bool restricted, IReadOnlyList<string> domains);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issued certificate {Id} for {Names}")]
    private partial void LogIssued(long id, IReadOnlyList<string> names);

    [LoggerMessage(Level = LogLevel.Information, Message = "The box presents the uploaded certificate {Fingerprint}")]
    private partial void LogCustomSet(string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The uploaded certificate is not usable ({Code}); the box presents its own")]
    private partial void LogCustomUnusable(string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The certificate file {File} is unreadable or does not match its key; it is made again")]
    private partial void LogFileUnusable(string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Renewing the box's certificates failed")]
    private partial void LogRefreshFailed(Exception exception);
}
