using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Shares;

/// <summary>
/// Each user's quota on a shared folder (D411). <c>share_user_quotas</c> is what the box wants; the shared folders'
/// datasets get it as <c>userquota@&lt;uid&gt;</c> at start, when a quota or a share changes and when a pool comes
/// back. What every user uses there is read with one <c>zfs get</c> after each read of the pools (that is, after writes
/// and pool events), kept in memory and announced on <c>shares/quotas</c>; a request never runs a command for it.
/// Homes have a quota of their own (D348), and Apps has none. In simulation nothing runs and no space is known.
/// </summary>
public sealed partial class ShareQuotas(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    PoolInventory pools,
    ShareChanges shareChanges,
    Users.HomeStorageOptions storage,
    TopicHub hub,
    ILogger<ShareQuotas> logger) : BackgroundService
{
    public const string Topic = "shares/quotas";

    /// <summary>The smallest quota; ZFS would take less, but a user could not even keep the folders of a share.</summary>
    public const long MinQuotaBytes = Users.HomeService.MinimumQuotaBytes;

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _running = new(1, 1);
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>> _empty = new Dictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>>();

    private IReadOnlyDictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>> _spaces = _empty;
    private string _fingerprint = "";

    // A random start, so a restarted daemon does not repeat a revision a client saw before.
    private long _revision = Random.Shared.Next(1, int.MaxValue);

    /// <summary>The topic for administrators; it finds the service only when somebody subscribes (the service needs the topic hub).</summary>
    public static TopicDefinition Definition(Func<ShareQuotas> service) => new(
        Topic,
        typeof(ShareQuotaRevision),
        typeof(ShareQuotaRevision),
        (_, _) => ValueTask.FromResult<object>(new ShareQuotaRevision(Interlocked.Read(ref service()._revision))),
        (user, _) => user.IsInRole(SessionAuthenticationHandler.AdminRole));

    /// <summary>Everybody who may write to the shared folder and everybody with a quota there, with their space.</summary>
    public async Task<ShareQuotaList> ListAsync(ClaimsPrincipal caller, long shareId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var dataset = await DatasetAsync(connection, shareId);
        var quotas = (await connection.QueryAsync<(long UserId, long Bytes)>(
            "SELECT user_id, quota_bytes FROM share_user_quotas WHERE share_id = @shareId", new { shareId })).ToDictionary(row => row.UserId, row => row.Bytes);
        var names = (await connection.QueryAsync<(long Id, string? DisplayName)>("SELECT id, display_name FROM users")).ToDictionary(row => row.Id, row => row.DisplayName);
        var spaces = Volatile.Read(ref _spaces).GetValueOrDefault(dataset);
        var users = await Permissions.PermissionService.AllUsersAsync(connection, null);
        return new ShareQuotaList(shareId, MinQuotaBytes, [.. users
            .Where(user => quotas.ContainsKey(user.Id) || (!user.Disabled && user.Permissions.Share(shareId) == ShareAccess.ReadWrite))
            .OrderBy(user => user.Username, StringComparer.Ordinal)
            .Select(user => new ShareUserQuota(
                user.Id,
                user.Username,
                names.GetValueOrDefault(user.Id),
                user.Uid is { } uid ? spaces?.GetValueOrDefault(uid)?.UsedBytes : null,
                quotas.TryGetValue(user.Id, out var bytes) ? bytes : null))]);
    }

    /// <summary>Sets or removes a user's quota on the shared folder and applies it before it answers.</summary>
    public async Task<ShareQuotaList> SetAsync(ClaimsPrincipal caller, long shareId, long userId, ShareQuotaRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.QuotaBytes is { } bytes && bytes < MinQuotaBytes)
        {
            throw new PithyxException(ErrorCodes.FsQuotaInvalid, new Dictionary<string, object?> { ["minBytes"] = MinQuotaBytes });
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await DatasetAsync(connection, shareId);
            if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE id = @userId", new { userId }) == 0)
            {
                throw new PithyxException(ErrorCodes.UsersUserNotFound);
            }
            await connection.ExecuteAsync(
                request.QuotaBytes is null
                    ? "DELETE FROM share_user_quotas WHERE share_id = @shareId AND user_id = @userId"
                    : """
                      INSERT INTO share_user_quotas (share_id, user_id, quota_bytes) VALUES (@shareId, @userId, @bytes)
                      ON CONFLICT (share_id, user_id) DO UPDATE SET quota_bytes = excluded.quota_bytes
                      """,
                new { shareId, userId, bytes = request.QuotaBytes });
        }
        await RefreshAsync(cancellationToken);
        Announce();
        return await ListAsync(caller, shareId, cancellationToken);
    }

    /// <summary>The dataset of a shared folder that can have quotas; throws for anything else.</summary>
    private static async Task<string> DatasetAsync(System.Data.Common.DbConnection connection, long shareId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(string? Dataset, long Home, string? System)?>(
            "SELECT dataset, home, system FROM shares WHERE id = @shareId", new { shareId });
        return row switch
        {
            null or { Home: 1 } => throw new PithyxException(ErrorCodes.FsShareNotFound),
            { System: not null } => throw new PithyxException(ErrorCodes.FsShareSystem),
            { Dataset: null } => throw new PithyxException(ErrorCodes.FsShareNoDataset),
            { Dataset: { } dataset } => dataset,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!storage.UseZfs)
        {
            return;
        }
        void Signal() => _requests.Writer.TryWrite(true);
        void SharesChanged(long revision) => Signal();
        pools.Refreshed += Signal;
        pools.Changed += Signal;
        shareChanges.Changed += SharesChanged;
        try
        {
            Signal();
            await foreach (var _ in _requests.Reader.ReadAllAsync(stoppingToken))
            {
                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            pools.Refreshed -= Signal;
            pools.Changed -= Signal;
            shareChanges.Changed -= SharesChanged;
        }
    }

    /// <summary>
    /// Sets every quota that differs on the shared folders that can be reached now and reads every user's space there.
    /// Never throws but for cancellation; the next change tries again.
    /// </summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!storage.UseZfs)
        {
            return;
        }
        await _running.WaitAsync(cancellationToken);
        try
        {
            var current = pools.Current;
            await using var connection = await connections.OpenAsync(cancellationToken);
            var datasets = (await connection.QueryAsync<string>("SELECT dataset FROM shares WHERE dataset IS NOT NULL AND home = 0 AND system IS NULL"))
                .Where(dataset => ShareAvailability.Reason(dataset, current, pools.Available) is null)
                .ToList();
            var uids = (await connection.QueryAsync<long>("SELECT uid FROM users WHERE uid IS NOT NULL")).Where(Users.AccountReconciler.IsManaged).ToList();
            var spaces = datasets.Count == 0 || uids.Count == 0 ? _empty : await ReadAsync(datasets, uids, cancellationToken);
            var wanted = (await connection.QueryAsync<(string Dataset, long Uid, long Bytes)>(
                """
                SELECT shares.dataset, users.uid, share_user_quotas.quota_bytes FROM share_user_quotas
                JOIN shares ON shares.id = share_user_quotas.share_id JOIN users ON users.id = share_user_quotas.user_id
                WHERE shares.dataset IS NOT NULL AND users.uid IS NOT NULL
                """)).ToDictionary(row => (row.Dataset, row.Uid), row => row.Bytes);
            var changes = spaces is null ? [] : ZfsUserSpaces.Changes(wanted, spaces, uids);
            foreach (var change in changes)
            {
                var result = await commands.RunAsync(change, cancellationToken);
                if (result.ExitCode != 0)
                {
                    LogSetFailed(string.Join(' ', change.Arguments), result.ExitCode);
                }
            }
            if (changes.Count > 0)
            {
                spaces = await ReadAsync(datasets, uids, cancellationToken);
            }
            if (spaces is null)
            {
                return;
            }
            Volatile.Write(ref _spaces, spaces);
            var fingerprint = JsonSerializer.Serialize(spaces);
            if (Interlocked.Exchange(ref _fingerprint, fingerprint) != fingerprint)
            {
                Announce();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefreshFailed(exception);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<long, ZfsUserSpace>>?> ReadAsync(IReadOnlyList<string> datasets, IReadOnlyList<long> uids, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(ZfsUserSpaces.Read(datasets, uids), cancellationToken);
        if (result.ExitCode != 0)
        {
            LogReadFailed(result.ExitCode);
            return null;
        }
        return ZfsUserSpaces.Parse(result.StandardOutput);
    }

    private void Announce() => hub.Publish(Topic, new ShareQuotaRevision(Interlocked.Increment(ref _revision)));

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!ShareService.IsAdmin(user))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    public override void Dispose()
    {
        _running.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the users' space on the shared folders failed with exit code {ExitCode}")]
    private partial void LogReadFailed(int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting a user quota failed ({Arguments}, exit code {ExitCode})")]
    private partial void LogSetFailed(string arguments, int exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bringing the user quotas of the shared folders in line failed")]
    private partial void LogRefreshFailed(Exception exception);
}
