using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>Where the box keeps its catalogs and whom it trusts.</summary>
/// <param name="Root">The folder of the catalogs' copies, <c>/var/lib/pithyx/catalogs</c> on a box.</param>
/// <param name="Keyring">The keys whose signatures the project's catalogs need, each for its channels.</param>
/// <param name="AllowFile">Catalogs may be local folders (<c>file://</c>), in simulation and tests only.</param>
/// <param name="BoxVersion">The Pithyx version the <c>pithyx</c> ranges of apps are matched against.</param>
public sealed record StoreOptions(string Root, IReadOnlyList<TrustedKey> Keyring, bool AllowFile, string BoxVersion)
{
    /// <summary>
    /// The project's signing keys (D328, D330): the <c>catalog</c> key signs the branch <c>catalog</c> and is trusted on
    /// both channels, the <c>next</c> key signs <c>catalog-next</c> and is trusted on the beta channel only. Their private
    /// halves live only in the environments <c>catalog-release</c> and <c>catalog-next</c> of the catalog repositories.
    /// </summary>
    public static readonly IReadOnlyList<TrustedKey> ProjectKeys =
    [
        new("pithyx-catalog-1", "EbAPiAJsXiXeihUGd7A2gbq1Y3NQIZb8kj+SNU18jHQ=", ["stable", "beta"]),
        new("pithyx-next-1", "qgalKAiB1QE9+wU6WWzXWg6U9A43usx6oCj0S3T6Y7A=", ["beta"]),
    ];
}

/// <summary>A verified copy of a catalog's branch as the box reads it, with the manifest of every version.</summary>
internal sealed record CatalogCopy(string Folder, string CatalogId, CatalogKind Kind, JsonElement Catalog, JsonElement Index, IReadOnlyDictionary<(string App, string Version), JsonElement> Manifests)
{
    public IEnumerable<(string App, string Version, JsonElement Entry)> Versions()
    {
        foreach (var app in Index.GetProperty("apps").EnumerateObject())
        {
            foreach (var version in app.Value.GetProperty("versions").EnumerateObject())
            {
                yield return (app.Name, version.Name, version.Value);
            }
        }
    }

    public JsonElement? Entry(string appId, string version) =>
        Index.GetProperty("apps").TryGetProperty(appId, out var app) && app.GetProperty("versions").TryGetProperty(version, out var entry) ? entry : null;

    public string VersionFolder(string appId, string version) => Path.Combine(Folder, "apps", appId, version);
}

/// <summary>
/// The Store's catalogs, updates and settings (S3, D328, D329). The catalogs are rows of <c>catalogs</c>; each enabled
/// one is fetched with <see cref="CatalogGit"/> at the daily check time (plus the box's own delay of up to an hour),
/// when it is added or switched on, and on demand, never otherwise. A fetch checks out the branch of the box's channel
/// into a staging folder, verifies <c>catalog.json</c>, the signature (the project's catalogs), the index with its
/// sequence and every file of every version, and only then replaces the verified copy; a failed fetch keeps the last
/// verified copy and is shown with the catalog. After each check the box works out the newer versions of installed
/// apps and applies each app's policy: automatic updates run as jobs of the box, others are announced to the
/// administrators. Installing from a catalog stages the package from the verified copy through the same checks as a
/// sideload, plus the index's hashes.
/// </summary>
public sealed partial class StoreService(
    SqliteConnectionFactory connections,
    CatalogGit git,
    StoreOptions options,
    AppRegistry registry,
    AppPackages packages,
    AppService apps,
    JobService jobs,
    NotificationService notifications,
    TimeProvider time,
    ILogger<StoreService> logger) : BackgroundService
{
    public const string Topic = "store/admin";
    public const string PolicySetting = "store.updates.policy";
    public const string TimeSetting = "store.updates.time";
    public const string ChannelSetting = "store.channel";
    public const string DelaySetting = "store.updates.delay";
    public const string LastCheckSetting = "store.updates.last";

    /// <summary>The settings a configuration backup carries in its part "Apps".</summary>
    public static readonly IReadOnlyList<string> BackupSettings = [PolicySetting, TimeSetting, ChannelSetting];

    public const string UpdatesAvailableCode = "store.updates.available";
    public const string CatalogFailedCode = "store.catalog.failed";

    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<long, bool> _busy = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Dictionary<long, CatalogCopy> _copies = [];
    private IReadOnlyList<AppUpdate> _updates = [];
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private CancellationTokenSource _reschedule = new();

    /// <summary>Raised after catalogs, their copies, the updates or the settings changed.</summary>
    public event Action? Changed;

    private static string BoxArchitecture => ImagesLock.HostArchitecture ?? "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureDelayAsync(stoppingToken);
        await LoadCopiesAsync(stoppingToken);
        RefreshUpdates();
        registry.Changed += RefreshUpdatesLater;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationTokenSource reschedule;
                lock (_lock)
                {
                    reschedule = _reschedule;
                }
                var settings = await SettingsAsync(stoppingToken);
                var wait = settings.NextCheckAt - time.GetUtcNow();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                try
                {
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, time, linked.Token);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The settings changed; the next check time is worked out again.
                    continue;
                }
                try
                {
                    await RunCheckAsync(scheduled: true, stoppingToken);
                }
#pragma warning disable CA1031 // A failed check is logged; the next one comes tomorrow.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogCheckFailed(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            registry.Changed -= RefreshUpdatesLater;
        }
    }

    /// <summary>
    /// The topic <c>store/admin</c>. It finds the service only when somebody subscribes: the service needs jobs and
    /// notifications, which need the topic hub, which needs every topic definition.
    /// </summary>
    public static TopicDefinition AdminDefinition(Func<StoreService> service) => new(
        Topic,
        typeof(StoreState),
        typeof(StoreState),
        async (_, cancellationToken) => await service().StateAsync(cancellationToken),
        (user, _) => user.IsInRole(SessionAuthenticationHandler.AdminRole));

    public async Task<StoreState> StateAsync(CancellationToken cancellationToken) =>
        new(await SettingsAsync(cancellationToken), await CatalogsAsync(cancellationToken), _updates);

    public async Task<StoreState> GetStateAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        return await StateAsync(cancellationToken);
    }

    // Settings.

    public async Task<StoreSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var values = (await connection.QueryAsync<(string Key, string Value)>(
                "SELECT key, value FROM settings WHERE key IN @keys", new { keys = new[] { PolicySetting, TimeSetting, ChannelSetting, DelaySetting, LastCheckSetting } }))
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var policy = values.TryGetValue(PolicySetting, out var p) && Enum.TryParse<UpdatePolicy>(p, ignoreCase: true, out var parsed) ? parsed : UpdatePolicy.Notify;
        var checkTime = values.TryGetValue(TimeSetting, out var t) && ParseTime(t) is not null ? t : "03:00";
        var channel = values.TryGetValue(ChannelSetting, out var c) && c == "beta" ? StoreChannel.Beta : StoreChannel.Stable;
        var delay = values.TryGetValue(DelaySetting, out var d) && int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        DateTimeOffset? last = values.TryGetValue(LastCheckSetting, out var l) ? DateTimeOffset.Parse(l, CultureInfo.InvariantCulture) : null;
        return new StoreSettings(policy, checkTime, channel, NextCheck(checkTime, delay, time.GetUtcNow(), TimeZoneInfo.Local).ToUniversalTime(), last?.ToUniversalTime());
    }

    public async Task<StoreSettings> SetSettingsAsync(ClaimsPrincipal caller, StoreSettingsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (ParseTime(request.Time) is null)
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        var before = await SettingsAsync(cancellationToken);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            foreach (var (key, value) in new[]
            {
                (PolicySetting, request.Policy.ToString().ToLowerInvariant()),
                (TimeSetting, request.Time),
                (ChannelSetting, request.Channel.ToString().ToLowerInvariant()),
            })
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value", new { key, value });
            }
        }
        Reschedule();
        LogSettings(request.Policy, request.Time, request.Channel);
        if (before.Channel != request.Channel)
        {
            // The other channel's copies are read from now on; fetch them right away.
            await LoadCopiesAsync(cancellationToken);
            RefreshUpdates();
            await StartCheckAsync(CurrentUser.Id(caller), cancellationToken);
        }
        Changed?.Invoke();
        return await SettingsAsync(cancellationToken);
    }

    /// <summary>HH:mm as minutes after midnight, or null.</summary>
    public static int? ParseTime(string? value) =>
        value is { Length: 5 } && value[2] == ':'
        && int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours < 24
        && int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes < 60
            ? (hours * 60) + minutes
            : null;

    /// <summary>The next check after <paramref name="now"/>: the set time plus the box's delay, in the box's time zone.</summary>
    public static DateTimeOffset NextCheck(string checkTime, int delaySeconds, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var minutes = ((ParseTime(checkTime) ?? 180) + (delaySeconds / 60)) % (24 * 60);
        var cron = CronSchedule.Parse(string.Create(CultureInfo.InvariantCulture, $"{minutes % 60} {minutes / 60} * * *"));
        var next = cron.Next(now, zone) ?? now.AddDays(1);
        return next.AddSeconds(delaySeconds % 60);
    }

    private async Task EnsureDelayAsync(CancellationToken cancellationToken)
    {
        // Each box asks at its own minute, so the boxes of a time zone do not all fetch at once (D329).
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO NOTHING",
            new { key = DelaySetting, value = RandomNumberGenerator.GetInt32(3600).ToString(CultureInfo.InvariantCulture) });
    }

    private void Reschedule()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _reschedule;
            _reschedule = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    // Catalogs.

    private sealed class CatalogRow
    {
        public long Id { get; init; }
        public string? CatalogId { get; init; }
        public string Kind { get; init; } = "";
        public string Url { get; init; } = "";
        public long Enabled { get; init; }
        public string? SshPrivateKey { get; init; }
        public string? SshPublicKey { get; init; }
        public string? HostKey { get; init; }
        public string? RegistryToken { get; init; }
        public long? SequenceStable { get; init; }
        public long? SequenceBeta { get; init; }
        public string? CheckedAt { get; init; }
        public string? VerifiedAt { get; init; }
        public string? VerifiedCommit { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorParams { get; init; }

        public CatalogKind CatalogKind => Enum.Parse<CatalogKind>(Kind, ignoreCase: true);
    }

    private const string RowColumns =
        """
        SELECT id AS Id, catalog_id AS CatalogId, kind AS Kind, url AS Url, enabled AS Enabled, ssh_private_key AS SshPrivateKey,
            ssh_public_key AS SshPublicKey, host_key AS HostKey, registry_token AS RegistryToken, sequence_stable AS SequenceStable,
            sequence_beta AS SequenceBeta, checked_at AS CheckedAt, verified_at AS VerifiedAt, verified_commit AS VerifiedCommit,
            error_code AS ErrorCode, error_params AS ErrorParams
        FROM catalogs
        """;

    private async Task<List<CatalogRow>> RowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<CatalogRow>(RowColumns + " ORDER BY CASE kind WHEN 'official' THEN 0 WHEN 'community' THEN 1 ELSE 2 END, id")];
    }

    private async Task<CatalogRow> RowAsync(long key, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<CatalogRow>(RowColumns + " WHERE id = @key", new { key })
            ?? throw new PithyxException(ErrorCodes.StoreCatalogNotFound, new Dictionary<string, object?> { ["catalog"] = key.ToString(CultureInfo.InvariantCulture) });
    }

    public async Task<IReadOnlyList<CatalogInfo>> CatalogsAsync(CancellationToken cancellationToken)
    {
        var channel = (await SettingsAsync(cancellationToken)).Channel;
        var rows = await RowsAsync(cancellationToken);
        Dictionary<long, CatalogCopy> copies;
        lock (_lock)
        {
            copies = _copies;
        }
        return [.. rows.Select(row => Info(row, copies.GetValueOrDefault(row.Id), channel))];
    }

    private CatalogInfo Info(CatalogRow row, CatalogCopy? copy, StoreChannel channel)
    {
        var status = row.Enabled == 0 ? CatalogStatus.Off
            : row.ErrorCode is not null ? CatalogStatus.Failed
            : row.VerifiedAt is null ? CatalogStatus.New
            : CatalogStatus.Ok;
        JsonElement? errorParams = row.ErrorParams is null ? null : JsonDocument.Parse(row.ErrorParams).RootElement.Clone();
        return new CatalogInfo(
            row.Id,
            row.CatalogId,
            row.CatalogKind,
            row.Url,
            row.Enabled == 1,
            row.CatalogKind != CatalogKind.Custom,
            copy is null ? null : Texts(copy.Catalog, "name"),
            copy is null ? null : Texts(copy.Catalog, "description"),
            copy is not null && copy.Catalog.TryGetProperty("homepage", out var homepage) ? homepage.GetString() : null,
            row.SshPublicKey,
            row.HostKey,
            row.RegistryToken is not null,
            status,
            Time(row.CheckedAt),
            Time(row.VerifiedAt),
            row.VerifiedCommit,
            channel == StoreChannel.Beta ? row.SequenceBeta : row.SequenceStable,
            copy?.Index.GetProperty("apps").EnumerateObject().Count() ?? 0,
            row.ErrorCode,
            errorParams,
            _busy.ContainsKey(row.Id));
    }

    /// <summary>Adds a custom catalog and fetches it in a job; for an SSH URL the box makes its deploy key first.</summary>
    public async Task<CatalogInfo> AddCatalogAsync(ClaimsPrincipal caller, AddCatalogRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var url = CatalogUrl.Parse(request.Url, git.AllowsFile);
        var rows = await RowsAsync(cancellationToken);
        if (rows.FirstOrDefault(row => string.Equals(row.Url, url.Url, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            throw new PithyxException(ErrorCodes.StoreCatalogExists, new Dictionary<string, object?> { ["catalog"] = existing.CatalogId ?? existing.Url });
        }
        string? privateKey = null;
        string? publicKey = null;
        if (url.Ssh)
        {
            (privateKey, publicKey) = await git.KeygenAsync($"pithyx catalog {url.Host}", cancellationToken);
        }
        long key;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            key = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO catalogs (kind, url, enabled, ssh_private_key, ssh_public_key, added_at)
                VALUES ('custom', @url, 1, @privateKey, @publicKey, @now) RETURNING id
                """,
                new { url = url.Url, privateKey, publicKey, now = SessionService.Format(time.GetUtcNow()) });
        }
        LogAdded(key, url.Url, url.Ssh);
        // A deploy key must be added to the repository first; the administrator fetches once it is there.
        if (!url.Ssh)
        {
            await StartSyncAsync(CurrentUser.Id(caller), key, cancellationToken);
        }
        Changed?.Invoke();
        var channel = (await SettingsAsync(cancellationToken)).Channel;
        return Info(await RowAsync(key, cancellationToken), null, channel);
    }

    /// <summary>Switches a catalog on (and fetches it) or off, or sets or removes its read token for images.</summary>
    public async Task<CatalogInfo> UpdateCatalogAsync(ClaimsPrincipal caller, long key, UpdateCatalogRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var row = await RowAsync(key, cancellationToken);
        if (request.RegistryToken is { Length: > 0 } candidate
            && (candidate.Length > 1000 || !candidate.Contains(':', StringComparison.Ordinal) || candidate.Any(char.IsControl)))
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        // The token goes only to the registry the catalog's CI pushes to (catalog.json's images), never to others.
        string? registryHost = null;
        if (request.RegistryToken is { Length: > 0 })
        {
            Dictionary<long, CatalogCopy> known;
            lock (_lock)
            {
                known = _copies;
            }
            registryHost = known.GetValueOrDefault(key)?.Catalog.GetProperty("images").GetString()?.Split('/')[0]
                ?? throw new PithyxException(ErrorCodes.StoreCatalogNotFound, new Dictionary<string, object?> { ["catalog"] = row.CatalogId ?? row.Url });
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (request.Enabled is { } enabled)
            {
                await connection.ExecuteAsync("UPDATE catalogs SET enabled = @enabled WHERE id = @key", new { enabled = enabled ? 1 : 0, key }, transaction);
            }
            if (request.RegistryToken is { } token)
            {
                await connection.ExecuteAsync(
                    "UPDATE catalogs SET registry_token = @token, registry_host = @registryHost WHERE id = @key",
                    new { token = token.Length == 0 ? null : token, registryHost, key },
                    transaction);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        if (request.Enabled == true && row.Enabled == 0)
        {
            await StartSyncAsync(CurrentUser.Id(caller), key, cancellationToken);
        }
        if (request.Enabled is not null)
        {
            LogEnabled(key, request.Enabled.Value);
            RefreshUpdates();
        }
        Changed?.Invoke();
        Dictionary<long, CatalogCopy> copies;
        lock (_lock)
        {
            copies = _copies;
        }
        return Info(await RowAsync(key, cancellationToken), copies.GetValueOrDefault(key), (await SettingsAsync(cancellationToken)).Channel);
    }

    /// <summary>Removes a custom catalog with its copy; its apps stay installed and get no more updates from it.</summary>
    public async Task RemoveCatalogAsync(ClaimsPrincipal caller, long key, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var row = await RowAsync(key, cancellationToken);
        if (row.CatalogKind != CatalogKind.Custom)
        {
            throw new PithyxException(ErrorCodes.StoreCatalogBuiltIn);
        }
        if (!_busy.TryAdd(key, true))
        {
            throw Busy(row);
        }
        try
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync("DELETE FROM catalogs WHERE id = @key", new { key });
            }
            lock (_lock)
            {
                _copies = _copies.Where(entry => entry.Key != key).ToDictionary();
            }
            var folder = Path.Combine(options.Root, FolderName(row));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            LogRemoved(key, row.Url);
        }
        finally
        {
            _busy.TryRemove(key, out _);
        }
        RefreshUpdates();
        Changed?.Invoke();
    }

    /// <summary>Fetches one catalog now, as a job of the caller.</summary>
    public async Task<Job> SyncAsync(ClaimsPrincipal caller, long key, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await RowAsync(key, cancellationToken);
        return await StartSyncAsync(CurrentUser.Id(caller), key, cancellationToken);
    }

    /// <summary>Checks now: fetches every enabled catalog and applies the update policy, as a job of the caller.</summary>
    public async Task<Job> CheckNowAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        return await StartCheckAsync(CurrentUser.Id(caller), cancellationToken);
    }

    private Task<Job> StartSyncAsync(long userId, long key, CancellationToken cancellationToken) =>
        jobs.StartAsync(userId, "store.sync", new { catalog = key }, async context =>
        {
            var row = await RowAsync(key, context.Cancellation);
            var channel = (await SettingsAsync(context.Cancellation)).Channel;
            await FetchAsync(row, channel, notify: false, rethrow: true, context.Cancellation);
            RefreshUpdates();
        }, cancellationToken);

    private Task<Job> StartCheckAsync(long userId, CancellationToken cancellationToken) =>
        jobs.StartAsync(userId, "store.check", null, async context => await RunCheckAsync(scheduled: false, context.Cancellation), cancellationToken);

    /// <summary>One check: every enabled catalog is fetched, then the updates are worked out and each app's policy applied.</summary>
    private async Task RunCheckAsync(bool scheduled, CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        try
        {
            var channel = (await SettingsAsync(cancellationToken)).Channel;
            foreach (var key in (await RowsAsync(cancellationToken)).Select(row => row.Id))
            {
                // Read again just before its fetch: a sync, a removal or a switch may have changed it meanwhile.
                var row = (await RowsAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == key);
                if (row is { Enabled: 1 })
                {
                    await FetchAsync(row, channel, notify: scheduled, rethrow: false, cancellationToken);
                }
            }
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    new { key = LastCheckSetting, value = SessionService.Format(time.GetUtcNow()) });
            }
            RefreshUpdates();
            await ApplyPoliciesAsync(cancellationToken);
            Changed?.Invoke();
        }
        finally
        {
            _checking.Release();
        }
    }

    private static string FolderName(CatalogRow row) => row.CatalogId ?? ".new-" + row.Id.ToString(CultureInfo.InvariantCulture);

    private static string Branch(StoreChannel channel) => channel == StoreChannel.Beta ? "catalog-next" : "catalog";

    private static string ChannelName(StoreChannel channel) => channel == StoreChannel.Beta ? "beta" : "stable";

    /// <summary>
    /// Fetches and verifies one catalog's branch, and replaces the verified copy only when everything holds. The
    /// outcome goes to the catalog's row; a failure keeps the copy and, for a scheduled check, notifies administrators.
    /// </summary>
    private async Task FetchAsync(CatalogRow row, StoreChannel channel, bool notify, bool rethrow, CancellationToken cancellationToken)
    {
        if (!_busy.TryAdd(row.Id, true))
        {
            if (rethrow)
            {
                throw Busy(row);
            }
            return;
        }
        Changed?.Invoke();
        string? staging = null;
        try
        {
            var url = CatalogUrl.Parse(row.Url, git.AllowsFile);
            var root = Path.Combine(options.Root, FolderName(row));
            var known = CatalogGit.KnownHosts(url, row.HostKey);
            if (url.Ssh && known.Count == 0)
            {
                // Trust on first use for servers other than GitHub: the key is pinned and shown with the catalog.
                var pinned = await git.KeyscanAsync(url, cancellationToken)
                    ?? throw new PithyxException(ErrorCodes.StoreCatalogUnreachable, new Dictionary<string, object?> { ["reason"] = "host_key" });
                await using (var connection = await connections.OpenAsync(cancellationToken))
                {
                    await connection.ExecuteAsync("UPDATE catalogs SET host_key = @pinned WHERE id = @id", new { pinned, id = row.Id });
                }
                known = [pinned];
            }
            var commit = await git.FetchAsync(url, Branch(channel), Path.Combine(root, "repo.git"), row.SshPrivateKey, known, cancellationToken);
            bool unchanged;
            lock (_lock)
            {
                unchanged = commit == row.VerifiedCommit && row.ErrorCode is null && _copies.TryGetValue(row.Id, out var held)
                    && held.Folder == Path.Combine(options.Root, row.CatalogId ?? "", ChannelName(channel));
            }
            if (unchanged)
            {
                // The verified copy is this commit already; nothing to check again.
                await using var connection = await connections.OpenAsync(cancellationToken);
                await connection.ExecuteAsync("UPDATE catalogs SET checked_at = @now WHERE id = @id", new { now = SessionService.Format(time.GetUtcNow()), id = row.Id });
                return;
            }
            staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            await git.CheckoutAsync(Path.Combine(root, "repo.git"), commit, staging, cancellationToken);
            var (catalogId, sequence) = await VerifyAsync(row, channel, staging, cancellationToken);
            var target = Path.Combine(options.Root, catalogId);
            if (root != target)
            {
                // A custom catalog's first fetch names its folder.
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                Directory.Move(root, target);
                staging = Path.Combine(target, Path.GetFileName(staging));
            }
            var copyFolder = Path.Combine(target, ChannelName(channel));
            var old = copyFolder + ".old-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(copyFolder))
            {
                Directory.Move(copyFolder, old);
            }
            Directory.Move(staging, copyFolder);
            staging = null;
            if (Directory.Exists(old))
            {
                Directory.Delete(old, recursive: true);
            }
            var now = SessionService.Format(time.GetUtcNow());
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    $"""
                    UPDATE catalogs SET catalog_id = @catalogId, {(channel == StoreChannel.Beta ? "sequence_beta" : "sequence_stable")} = @sequence,
                        checked_at = @now, verified_at = @now, verified_commit = @commit, error_code = NULL, error_params = NULL
                    WHERE id = @id
                    """,
                    new { catalogId, sequence, now, commit, id = row.Id });
            }
            var copy = ReadCopy(copyFolder, catalogId, row.CatalogKind);
            var stillThere = (await RowsAsync(cancellationToken)).Any(candidate => candidate.Id == row.Id);
            lock (_lock)
            {
                if (stillThere)
                {
                    _copies = new Dictionary<long, CatalogCopy>(_copies) { [row.Id] = copy };
                }
            }
            LogFetched(catalogId, channel, sequence, commit);
        }
        catch (Exception exception) when (exception is PithyxException or IOException or UnauthorizedAccessException or JsonException)
        {
            var code = exception is PithyxException problem ? problem.Code : ErrorCodes.StoreCatalogUnreachable;
            var parameters = exception is PithyxException known
                ? known.Parameters
                : new Dictionary<string, object?> { ["reason"] = "failed" }.AsReadOnly();
            await using (var connection = await connections.OpenAsync(CancellationToken.None))
            {
                await connection.ExecuteAsync(
                    "UPDATE catalogs SET checked_at = @now, error_code = @code, error_params = @parameters WHERE id = @id",
                    new { now = SessionService.Format(time.GetUtcNow()), code, parameters = JsonSerializer.Serialize(parameters), id = row.Id });
            }
            if (logger.IsEnabled(LogLevel.Warning))
            {
                LogFetchFailed(exception, row.CatalogId ?? row.Url, code);
            }
            if (notify)
            {
                await notifications.CreateForAdministratorsAsync(
                    new NotificationDraft(CatalogFailedCode, new Dictionary<string, object?> { ["catalog"] = row.CatalogId ?? row.Url, ["errorCode"] = code, ["errorParams"] = parameters }, NotificationTone.Warning),
                    CancellationToken.None);
            }
            if (rethrow)
            {
                throw;
            }
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            _busy.TryRemove(row.Id, out _);
            Changed?.Invoke();
        }
    }

    /// <summary>Verifies a checked-out branch; returns the catalog's id and the index's sequence. Throws the first problem.</summary>
    private async Task<(string CatalogId, long Sequence)> VerifyAsync(CatalogRow row, StoreChannel channel, string folder, CancellationToken cancellationToken)
    {
        var project = row.CatalogKind != CatalogKind.Custom;
        var catalogPath = Path.Combine(folder, "catalog.json");
        if (!File.Exists(catalogPath))
        {
            throw new PithyxException(ErrorCodes.StoreCatalogInvalidJson, new Dictionary<string, object?> { ["file"] = "catalog.json" });
        }
        var catalog = CatalogValidator.ValidateCatalog(await File.ReadAllBytesAsync(catalogPath, cancellationToken), allowReservedId: project);
        if (!catalog.IsValid)
        {
            throw catalog.Issues[0].ToException();
        }
        var catalogId = catalog.Document!.Value.GetProperty("id").GetString()!;
        if ((project || row.CatalogId is not null) && catalogId != row.CatalogId)
        {
            throw new PithyxException(ErrorCodes.StoreCatalogMismatch, new Dictionary<string, object?> { ["path"] = "/id", ["value"] = catalogId, ["expected"] = row.CatalogId ?? "" });
        }
        if (row.CatalogId is null && (await RowsAsync(cancellationToken)).Any(other => other.Id != row.Id && other.CatalogId == catalogId))
        {
            throw new PithyxException(ErrorCodes.StoreCatalogExists, new Dictionary<string, object?> { ["catalog"] = catalogId });
        }
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            throw new PithyxException(ErrorCodes.StoreCatalogInvalidJson, new Dictionary<string, object?> { ["file"] = "index.json" });
        }
        var indexBytes = await File.ReadAllBytesAsync(indexPath, cancellationToken);
        var signaturePath = Path.Combine(folder, "index.json.sig");
        var expected = new IndexExpectation(catalogId, ChannelName(channel), channel == StoreChannel.Beta ? row.SequenceBeta : row.SequenceStable);
        var index = project
            ? IndexSignature.CheckSignedIndex(indexBytes, File.Exists(signaturePath) ? await File.ReadAllBytesAsync(signaturePath, cancellationToken) : null, options.Keyring, expected)
            : CatalogValidator.ValidateIndex(indexBytes, expected);
        if (!index.IsValid)
        {
            throw index.Issues[0].ToException();
        }
        foreach (var app in index.Document!.Value.GetProperty("apps").EnumerateObject())
        {
            foreach (var version in app.Value.GetProperty("versions").EnumerateObject())
            {
                VerifyFiles(Path.Combine(folder, "apps", app.Name, version.Name), app.Name, version.Name, version.Value);
            }
        }
        return (catalogId, index.Document!.Value.GetProperty("sequence").GetInt64());
    }

    /// <summary>Every file of a version must have the SHA-256 and size its index entry names.</summary>
    internal static void VerifyFiles(string folder, string appId, string version, JsonElement entry)
    {
        var files = new List<(string Name, JsonElement File)>
        {
            ($"{appId}-{version}.pithyxapp", entry.GetProperty("package")),
            ("pithyx.json", entry.GetProperty("manifest")),
            ("icon.svg", entry.GetProperty("icon")),
        };
        if (entry.TryGetProperty("screenshots", out var screenshots))
        {
            files.AddRange(screenshots.EnumerateArray().Select(shot => ("screenshots/" + shot.GetProperty("name").GetString(), shot)));
        }
        foreach (var (name, file) in files)
        {
            var path = Path.Combine(folder, name);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length != file.GetProperty("size").GetInt64() || Sha256Of(path) != file.GetProperty("sha256").GetString())
            {
                throw new PithyxException(ErrorCodes.StorePackageHashMismatch, new Dictionary<string, object?> { ["path"] = $"apps/{appId}/{version}/{name}" });
            }
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static CatalogCopy ReadCopy(string folder, string catalogId, CatalogKind kind)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "catalog.json")));
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "index.json")));
        var manifests = new Dictionary<(string, string), JsonElement>();
        foreach (var app in index.RootElement.GetProperty("apps").EnumerateObject())
        {
            foreach (var version in app.Value.GetProperty("versions").EnumerateObject())
            {
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "apps", app.Name, version.Name, "pithyx.json")));
                manifests[(app.Name, version.Name)] = manifest.RootElement.Clone();
            }
        }
        return new CatalogCopy(folder, catalogId, kind, catalog.RootElement.Clone(), index.RootElement.Clone(), manifests);
    }

    /// <summary>Reads the verified copies of the box's channel at the start; a copy that cannot be read is left out.</summary>
    private async Task LoadCopiesAsync(CancellationToken cancellationToken)
    {
        var channel = (await SettingsAsync(cancellationToken)).Channel;
        var copies = new Dictionary<long, CatalogCopy>();
        foreach (var row in await RowsAsync(cancellationToken))
        {
            var folder = row.CatalogId is null ? null : Path.Combine(options.Root, row.CatalogId, ChannelName(channel));
            if (folder is null || !File.Exists(Path.Combine(folder, "index.json")))
            {
                continue;
            }
            try
            {
                copies[row.Id] = ReadCopy(folder, row.CatalogId!, row.CatalogKind);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                LogCopyUnreadable(exception, row.CatalogId!);
            }
        }
        lock (_lock)
        {
            _copies = copies;
        }
    }

    // Apps of the catalogs.

    /// <summary>The enabled catalogs' copies by catalog id, the project's first.</summary>
    private async Task<List<CatalogCopy>> EnabledCopiesAsync(CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(cancellationToken);
        Dictionary<long, CatalogCopy> copies;
        lock (_lock)
        {
            copies = _copies;
        }
        return [.. rows.Where(row => row.Enabled == 1 && copies.ContainsKey(row.Id)).Select(row => copies[row.Id])];
    }

    private List<CatalogCopy> EnabledCopies() => EnabledCopiesAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Whether this box can run a version: its architecture is listed and the box's version is in its range.</summary>
    private bool Fits(JsonElement entry) =>
        entry.GetProperty("architectures").EnumerateArray().Any(architecture => architecture.GetString() == BoxArchitecture)
        && VersionRange.Satisfies(options.BoxVersion, entry.GetProperty("pithyx").GetString() ?? "*");

    /// <summary>The newest version of an app in a copy that this box can run.</summary>
    private (string Version, JsonElement Entry)? Newest(CatalogCopy copy, string appId) =>
        copy.Index.GetProperty("apps").TryGetProperty(appId, out var app)
            ? app.GetProperty("versions").EnumerateObject()
                .Where(version => Fits(version.Value))
                .Select(version => ((string Version, JsonElement Entry)?)(version.Name, version.Value))
                .OrderByDescending(version => version!.Value.Version, Comparer<string>.Create(AppService.CompareVersions))
                .FirstOrDefault()
            : null;

    private StoreApp Summary(CatalogCopy copy, string appId, string version)
    {
        var manifest = copy.Manifests[(appId, version)];
        var installed = registry.Find(appId);
        return new StoreApp(
            copy.CatalogId,
            copy.Kind,
            appId,
            version,
            Texts(manifest, "name") ?? new Dictionary<string, string>(),
            Texts(manifest, "description"),
            manifest.TryGetProperty("author", out var author) && author.TryGetProperty("name", out var name) ? name.GetString() : null,
            manifest.TryGetProperty("license", out var license) ? license.GetString() ?? "" : "",
            $"/api/store/catalogs/{copy.CatalogId}/apps/{appId}/{version}/icon",
            installed?.Version,
            installed is not null && AppService.CompareVersions(version, installed.Version) > 0);
    }

    /// <summary>Apps of the enabled catalogs (or one of them) whose id, name, description or author contains the query.</summary>
    public async Task<StoreAppList> SearchAsync(ClaimsPrincipal caller, string? catalog, string? query, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var text = (query ?? "").Trim();
        var results = new List<StoreApp>();
        foreach (var copy in await EnabledCopiesAsync(cancellationToken))
        {
            if (catalog is not null && copy.CatalogId != catalog)
            {
                continue;
            }
            foreach (var app in copy.Index.GetProperty("apps").EnumerateObject())
            {
                // Only the official catalog may ship org.pithyx.*; its index was refused otherwise, this is a second guard.
                if (!CatalogValidator.MayUseReservedIds(copy.CatalogId) && app.Name.StartsWith("org.pithyx.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (Newest(copy, app.Name) is not { } newest)
                {
                    continue;
                }
                var summary = Summary(copy, app.Name, newest.Version);
                if (text.Length == 0 || Matches(summary, text))
                {
                    results.Add(summary);
                }
            }
        }
        return new StoreAppList([.. results.OrderBy(app => app.Name.GetValueOrDefault("en") ?? app.Id, StringComparer.CurrentCultureIgnoreCase).ThenBy(app => app.Catalog, StringComparer.Ordinal)]);
    }

    private static bool Matches(StoreApp app, string text) =>
        app.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
        || app.Name.Values.Any(name => name.Contains(text, StringComparison.OrdinalIgnoreCase))
        || app.Description?.Values.Any(description => description.Contains(text, StringComparison.OrdinalIgnoreCase)) == true
        || app.Author?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<CatalogCopy> CopyAsync(string catalogId, CancellationToken cancellationToken) =>
        (await EnabledCopiesAsync(cancellationToken)).FirstOrDefault(copy => copy.CatalogId == catalogId)
        ?? throw new PithyxException(ErrorCodes.StoreCatalogNotFound, new Dictionary<string, object?> { ["catalog"] = Shorten(catalogId) });

    private static PithyxException AppNotFound(string appId, string version) =>
        new(ErrorCodes.StoreAppNotFound, new Dictionary<string, object?> { ["id"] = Shorten(appId), ["version"] = Shorten(version) });

    /// <summary>The detail page of a catalog app: its newest fitting version, screenshots, source and every version.</summary>
    public async Task<StoreAppDetails> DetailsAsync(ClaimsPrincipal caller, string catalogId, string appId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var copy = await CopyAsync(catalogId, cancellationToken);
        var newest = Newest(copy, appId) ?? throw AppNotFound(appId, "");
        var entry = newest.Entry;
        var manifest = copy.Manifests[(appId, newest.Version)];
        var source = entry.GetProperty("source");
        var screenshots = entry.TryGetProperty("screenshots", out var shots)
            ? shots.EnumerateArray().Select(shot => $"/api/store/catalogs/{catalogId}/apps/{appId}/{newest.Version}/screenshots/{shot.GetProperty("name").GetString()}").ToList()
            : [];
        var versions = copy.Index.GetProperty("apps").GetProperty(appId).GetProperty("versions").EnumerateObject()
            .Select(version => new StoreAppVersion(
                version.Name,
                DateTimeOffset.Parse(version.Value.GetProperty("publishedAt").GetString()!, CultureInfo.InvariantCulture),
                version.Value.GetProperty("pithyx").GetString() ?? "*",
                Fits(version.Value),
                version.Value.TryGetProperty("dataVersion", out var data) ? data.GetInt32() : null))
            .OrderByDescending(version => version.Version, Comparer<string>.Create(AppService.CompareVersions))
            .ToList();
        return new StoreAppDetails(
            Summary(copy, appId, newest.Version),
            manifest,
            screenshots,
            manifest.TryGetProperty("homepage", out var homepage) ? homepage.GetString() : null,
            $"{source.GetProperty("repository").GetString()!.TrimEnd('/')}/tree/{source.GetProperty("commit").GetString()}/{source.GetProperty("path").GetString()}",
            versions);
    }

    /// <summary>
    /// A file of a version for the Store's page: its icon or a screenshot, from the verified copy (checked again against
    /// the index), with its media type. Served with <c>nosniff</c> and a sandboxing policy by the endpoint.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)> MediaAsync(ClaimsPrincipal caller, string catalogId, string appId, string version, string? screenshot, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var copy = await CopyAsync(catalogId, cancellationToken);
        var entry = copy.Entry(appId, version) ?? throw AppNotFound(appId, version);
        JsonElement file;
        string name;
        if (screenshot is null)
        {
            file = entry.GetProperty("icon");
            name = "icon.svg";
        }
        else
        {
            file = entry.TryGetProperty("screenshots", out var shots)
                ? shots.EnumerateArray().FirstOrDefault(shot => shot.GetProperty("name").GetString() == screenshot)
                : default;
            if (file.ValueKind != JsonValueKind.Object)
            {
                throw AppNotFound(appId, version);
            }
            name = "screenshots/" + screenshot;
        }
        var bytes = await File.ReadAllBytesAsync(Path.Combine(copy.VersionFolder(appId, version), name), cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.GetProperty("sha256").GetString())
        {
            throw new PithyxException(ErrorCodes.StorePackageHashMismatch, new Dictionary<string, object?> { ["path"] = $"apps/{appId}/{version}/{name}" });
        }
        var type = Path.GetExtension(name) switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            _ => "image/webp",
        };
        return (bytes, type);
    }

    /// <summary>
    /// Stages a catalog's package for the install dialog, like an upload: the package from the verified copy, checked
    /// against the index's hashes, the app, version and images it names, the box's version and architecture, and for an
    /// older version than the installed one the same <c>dataVersion</c>. Installing it uses the package's id.
    /// </summary>
    public async Task<AppPackagePreview> StageAsync(ClaimsPrincipal caller, string catalogId, string appId, string version, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var package = await StagePackageAsync(CurrentUser.Id(caller), catalogId, appId, version, cancellationToken);
        return apps.GetPreview(caller, package.Id);
    }

    private async Task<CheckedPackage> StagePackageAsync(long userId, string catalogId, string appId, string version, CancellationToken cancellationToken)
    {
        var copy = await CopyAsync(catalogId, cancellationToken);
        var entry = copy.Entry(appId, version) ?? throw AppNotFound(appId, version);
        if (!entry.GetProperty("architectures").EnumerateArray().Any(architecture => architecture.GetString() == BoxArchitecture))
        {
            throw AppNotFound(appId, version);
        }
        var range = entry.GetProperty("pithyx").GetString() ?? "*";
        if (!VersionRange.Satisfies(options.BoxVersion, range))
        {
            throw new PithyxException(ErrorCodes.AppPackageBoxTooOld, new Dictionary<string, object?> { ["range"] = range, ["version"] = options.BoxVersion });
        }
        if (registry.Find(appId) is { } installed && AppService.CompareVersions(version, installed.Version) < 0)
        {
            var target = entry.TryGetProperty("dataVersion", out var targetData) ? targetData.GetInt32() : 0;
            var current = installed.Manifest.TryGetProperty("dataVersion", out var installedData) ? installedData.GetInt32() : 0;
            if (target != current)
            {
                throw new PithyxException(ErrorCodes.AppPackageDowngradeUnsupported, new Dictionary<string, object?> { ["installed"] = current, ["target"] = target });
            }
        }
        Dictionary<string, IReadOnlyDictionary<string, LockedImage>>? images = null;
        if (entry.TryGetProperty("images", out var indexed))
        {
            images = indexed.EnumerateObject().ToDictionary(
                image => image.Name,
                image => (IReadOnlyDictionary<string, LockedImage>)image.Value.EnumerateObject().ToDictionary(
                    platform => platform.Name,
                    platform => new LockedImage(platform.Value.GetProperty("manifest").GetString()!, platform.Value.GetProperty("config").GetString()!),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        var origin = new PackageOrigin(
            catalogId,
            CatalogValidator.MayUseReservedIds(catalogId) && copy.Kind == CatalogKind.Official,
            appId,
            version,
            entry.GetProperty("package").GetProperty("sha256").GetString()!,
            entry.GetProperty("manifest").GetProperty("sha256").GetString()!,
            images);
        await using var stream = File.OpenRead(Path.Combine(copy.VersionFolder(appId, version), $"{appId}-{version}.pithyxapp"));
        return await packages.UploadAsync(userId, stream, cancellationToken, origin);
    }

    // Updates.

    public IReadOnlyList<AppUpdate> Updates => _updates;

    // The registry changes inside install jobs and requests; the updates are worked out after them, never in them.
    private void RefreshUpdatesLater() => _ = Task.Run(RefreshUpdates);

    /// <summary>
    /// Works out the newer versions of installed apps: from the catalog an app came from, or for a sideload from any
    /// enabled catalog that has its id (D308), the official one first; <c>org.pithyx.*</c> only from the official one.
    /// </summary>
    private void RefreshUpdates()
    {
        List<CatalogCopy> copies;
        UpdatePolicy policy;
        try
        {
            copies = EnabledCopies();
            policy = SettingsAsync(CancellationToken.None).GetAwaiter().GetResult().Policy;
        }
#pragma warning disable CA1031 // A failed refresh keeps the last list; the next change tries again.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogUpdatesFailed(exception);
            return;
        }
        var updates = new List<AppUpdate>();
        foreach (var app in registry.Apps.Values.OrderBy(app => app.Id, StringComparer.Ordinal))
        {
            var candidates = app.CatalogId is { } from
                ? copies.Where(copy => copy.CatalogId == from)
                : copies.Where(copy => !app.Id.StartsWith("org.pithyx.", StringComparison.Ordinal) || copy.Kind == CatalogKind.Official);
            foreach (var copy in candidates)
            {
                if (Newest(copy, app.Id) is not { } newest || AppService.CompareVersions(newest.Version, app.Version) <= 0)
                {
                    continue;
                }
                var manifest = copy.Manifests[(app.Id, newest.Version)];
                var had = Strings(app.Manifest, "permissions");
                var added = Strings(manifest, "permissions").Where(permission => !had.Contains(permission)).ToList();
                var elevated = (IsRoot(manifest) && !IsRoot(app.Manifest)) || Elevated(manifest).Except(Elevated(app.Manifest)).Any();
                var effective = app.UpdatePolicy ?? policy;
                updates.Add(new AppUpdate(app.Id, app.Version, newest.Version, copy.CatalogId, added, elevated, effective, effective == UpdatePolicy.Automatic && added.Count == 0 && !elevated));
                break;
            }
        }
        _updates = updates;
        Changed?.Invoke();
    }

    private static HashSet<string> Strings(JsonElement manifest, string property) =>
        manifest.TryGetProperty(property, out var values) ? values.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];

    private static bool IsRoot(JsonElement manifest) =>
        manifest.TryGetProperty("backend", out var backend) && backend.TryGetProperty("root", out var root) && root.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> Elevated(JsonElement manifest) =>
        manifest.TryGetProperty("backend", out var backend) && backend.TryGetProperty("elevated", out var rights)
            ? rights.EnumerateArray().Select(right => right.GetString()!)
            : [];

    /// <summary>
    /// After a check: automatic updates start as jobs of the box (one failure does not stop the others), and the
    /// administrators hear once of each other new version.
    /// </summary>
    private async Task ApplyPoliciesAsync(CancellationToken cancellationToken)
    {
        var announce = new List<AppUpdate>();
        foreach (var update in _updates)
        {
            if (update.Automatic)
            {
                try
                {
                    var package = await StagePackageAsync(JobService.Box, update.Catalog, update.Id, update.Version, cancellationToken);
                    await apps.StartInstallAsync(JobService.Box, package.Id, new InstallAppRequest(), cancellationToken);
                    LogAutomatic(update.Id, update.Version);
                }
#pragma warning disable CA1031 // One update that cannot start must not keep the others from starting.
                catch (Exception problem) when (problem is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogAutomaticFailed(problem, update.Id, update.Version);
                }
            }
            else if (update.Policy == UpdatePolicy.Notify && _announced.Add($"{update.Id}@{update.Version}"))
            {
                announce.Add(update);
            }
        }
        if (announce.Count > 0)
        {
            await notifications.CreateForAdministratorsAsync(
                new NotificationDraft(UpdatesAvailableCode, new Dictionary<string, object?> { ["count"] = announce.Count, ["apps"] = string.Join(", ", announce.Select(update => $"{update.Id} {update.Version}")) }, NotificationTone.Info),
                cancellationToken);
        }
    }

    private static Dictionary<string, string>? Texts(JsonElement element, string property) =>
        element.TryGetProperty(property, out var texts) && texts.ValueKind == JsonValueKind.Object
            ? texts.EnumerateObject().Where(text => text.Value.ValueKind == JsonValueKind.String).ToDictionary(text => text.Name, text => text.Value.GetString()!, StringComparer.Ordinal)
            : null;

    private static DateTimeOffset? Time(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string Shorten(string value) => value.Length > 100 ? value[..100] : value;

    private static PithyxException Busy(CatalogRow row) =>
        new(ErrorCodes.StoreCatalogBusy, new Dictionary<string, object?> { ["catalog"] = row.CatalogId ?? row.Url });

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Store settings: policy {Policy}, check time {Time}, channel {Channel}")]
    private partial void LogSettings(UpdatePolicy policy, string time, StoreChannel channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Added catalog {Key} from {Url} (SSH: {Ssh})")]
    private partial void LogAdded(long key, string url, bool ssh);

    [LoggerMessage(Level = LogLevel.Information, Message = "Catalog {Key} enabled: {Enabled}")]
    private partial void LogEnabled(long key, bool enabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed catalog {Key} ({Url})")]
    private partial void LogRemoved(long key, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetched catalog {CatalogId} ({Channel}): sequence {Sequence}, commit {Commit}")]
    private partial void LogFetched(string catalogId, StoreChannel channel, long sequence, string commit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching catalog {Catalog} failed with {Code}; the last verified copy stays")]
    private partial void LogFetchFailed(Exception exception, string catalog, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The copy of catalog {CatalogId} cannot be read")]
    private partial void LogCopyUnreadable(Exception exception, string catalogId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Store's check failed")]
    private partial void LogCheckFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Working out the app updates failed")]
    private partial void LogUpdatesFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the automatic update of {AppId} to {Version}")]
    private partial void LogAutomatic(string appId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The automatic update of {AppId} to {Version} could not start")]
    private partial void LogAutomaticFailed(Exception exception, string appId, string version);
}

/// <summary>Publishes <c>store/admin</c> when the Store's catalogs, updates or settings change.</summary>
public sealed partial class StoreChangePublisher(TopicHub hub, StoreService store, ILogger<StoreChangePublisher> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.Changed += Publish;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= Publish;
        return Task.CompletedTask;
    }

    // The state reads the database; it is published off the caller's thread.
    private void Publish() => _ = Task.Run(async () =>
    {
        try
        {
            hub.Publish(StoreService.Topic, await store.StateAsync(CancellationToken.None));
        }
#pragma warning disable CA1031 // A lost event is repaired by the next one; subscribers get a snapshot on subscribe.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogPublishFailed(exception);
        }
    });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the Store's state failed")]
    private partial void LogPublishFailed(Exception exception);
}
