using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// The daemon's API for apps, <c>/api/app/**</c> (A11, D303), and <c>POST /api/apps/{appId}/tokens</c>, where the
/// desktop gets the tokens. Every call under <c>/api/app/</c> needs a host token; the app it acts for comes from that
/// token alone, never from the route, a parameter or the body. The desktop calls it for the app's window; the
/// window's frame never holds a host token.
/// </summary>
public static class AppApiEndpoints
{
    public const string DirectoryPermission = "directory.read";
    public const string NotificationsPermission = "notifications";

    public static void MapAppTokenEndpoint(this RouteGroupBuilder api)
    {
        api.MapPost("/apps/{appId}/tokens", CreateTokenAsync)
            .WithTags("Apps")
            .WithName("CreateAppToken")
            .WithDescription(
                "A token for an app the signed-in user may use (any app for administrators; app.app.not_found otherwise), bound to "
                + "the user, this session, the app and the window instance, valid 5 minutes. kind host is for the daemon's API "
                + "for apps (/api/app/**) and stays in the desktop; kind backend is for the app's backend (app.backend.missing "
                + "without one). Needs the session cookie and the CSRF header; ask again before expiresAt.");
    }

    public static void MapAppApi(this RouteGroupBuilder api)
    {
        var app = api.MapGroup("/app").WithTags("App API").RequireAuthorization(Policies.AppToken);
        app.MapPost("/logs", WriteLogs)
            .WithName("AppWriteLogs")
            .WithDescription(
                "Stores log entries of the token's app for its user, as source app/<id>. Debug entries only while an administrator "
                + "switched debug logging on for the app; at most 100 entries per request within the app's rate limit.");
        app.MapPost("/notifications", NotifyAsync)
            .WithName("AppNotify")
            .WithDescription(
                "A notification of the token's app for its user, with at most two buttons, data that comes back with a click and a tag "
                + "that replaces the app's earlier notification with the same tag. Needs the permission notifications.");
        app.MapGet("/storage", async (AppValueStore values, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
            {
                var (grant, _) = Caller(context, registry);
                return TypedResults.Ok(new AppStorageKeys(await values.KeysAsync(grant.AppId, grant.UserId, cancellationToken)));
            })
            .WithName("AppListStorageKeys")
            .WithDescription("The keys the token's app stored for its user.");
        app.MapGet("/storage/value", async ([FromQuery] string? key, AppValueStore values, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
            {
                var (grant, _) = Caller(context, registry);
                return TypedResults.Ok(new AppStorageValue(await values.GetAsync(grant.AppId, grant.UserId, key, cancellationToken)));
            })
            .WithName("AppGetStorageValue")
            .WithDescription("A value the token's app stored for its user under key, or null.");
        app.MapPut("/storage/value", async ([FromQuery] string? key, AppStorageValue body, AppValueStore values, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
            {
                var (grant, _) = Caller(context, registry);
                await values.SetAsync(grant.AppId, grant.UserId, key, body.Value, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("AppSetStorageValue")
            .WithDescription("Stores a value of the token's app for its user: keys of 1 to 128 characters, values of at most 64 KB of JSON, at most 1 MB per app and user (app.storage.too_large).");
        app.MapDelete("/storage/value", async ([FromQuery] string? key, AppValueStore values, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
            {
                var (grant, _) = Caller(context, registry);
                await values.RemoveAsync(grant.AppId, grant.UserId, key, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("AppDeleteStorageValue")
            .WithDescription("Removes a value of the token's app for its user; a missing one is no error.");
        app.MapGet("/directory/users", ListUsersAsync)
            .WithName("AppListUsers")
            .WithDescription("Every active user of the box with its groups. Needs the permission directory.read.");
        app.MapGet("/directory/groups", ListGroupsAsync)
            .WithName("AppListGroups")
            .WithDescription("Every group of the box but the built-in one. Needs the permission directory.read.");
        app.MapGet("/files/shares", async (AppFiles files, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await files.SharesAsync(Caller(context, registry).App, context.User, cancellationToken)))
            .WithName("AppListShares")
            .WithDescription("The shared folders the user can use, without their place on the host. Needs the permission files.shares.");
        app.MapGet("/files/list", ListFolderAsync)
            .WithName("AppListFolder")
            .WithDescription(
                "One page of a folder in a shared folder (share, needs files.shares) or a storage request of the manifest (storage), as "
                + "the user and with the user's rights. Parameters as for GET /api/files/{shareId}/list.");
        app.MapGet("/files/entry", async ([FromQuery] string? share, [FromQuery] string? storage, [FromQuery] string? path, AppFiles files, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await files.StatAsync(Caller(context, registry).App, context.User, new AppFileLocation(share, storage), path, cancellationToken)))
            .WithName("AppGetFileEntry")
            .WithDescription("A file or folder in a location, with whether the app may write it.");
        app.MapGet("/files/content", ReadFileAsync)
            .WithName("AppReadFile")
            .WithDescription("The content of a file in a location, with range requests. A shared folder needs files.shares and files.read.");
        app.MapPut("/files/content", WriteFileAsync)
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .WithName("AppWriteFile")
            .WithDescription(
                "Streams the body into the file at path (fs.entry.exists unless overwrite=true). A shared folder needs files.shares "
                + "and files.write, a storage request a mapping for reading and writing; the user needs write access either way.");
        app.MapPost("/files/folders", async ([FromQuery] string? share, [FromQuery] string? storage, [FromQuery] string? path, AppFiles files, AppRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Created((string?)null, await files.CreateFolderAsync(Caller(context, registry).App, context.User, new AppFileLocation(share, storage), path, cancellationToken)))
            .WithName("AppCreateFolder")
            .WithDescription("Creates a folder in a location, with the same rules as writing a file.");
        app.MapPost("/tickets", CreateTicket)
            .WithName("AppCreateTicket")
            .WithDescription(
                "A one-time ticket for one WebSocket to an endpoint of the app's backend (app.backend.endpoint_unknown for a name the "
                + "manifest does not declare), valid 30 seconds, bound to what the host token is bound to.");
    }

    private static async Task<Ok<AppToken>> CreateTokenAsync(string appId, AppTokenRequest request, AppTokens tokens, AppRegistry registry, PermissionService permissions, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == AppTokenKind.Ticket || !AppTokens.IsInstance(request.Instance))
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        var userId = CurrentUser.Id(context.User);
        if (registry.Find(appId) is not { } app || !registry.Allows(await permissions.ForUserAsync(userId, cancellationToken), appId))
        {
            throw new PithyxException(ErrorCodes.AppAppNotFound, new Dictionary<string, object?> { ["id"] = appId.Length > 100 ? appId[..100] : appId });
        }
        if (request.Kind == AppTokenKind.Backend && !app.Manifest.TryGetProperty("backend", out _))
        {
            throw new PithyxException(ErrorCodes.AppBackendMissing);
        }
        var sessionId = context.User.FindFirstValue(SessionAuthenticationHandler.SessionIdClaim) ?? throw new PithyxException(ErrorCodes.AuthSessionExpired);
        var issued = tokens.Issue(request.Kind, userId, sessionId, appId, request.Instance);
        return TypedResults.Ok(new AppToken(issued.Token, issued.ExpiresAt));
    }

    /// <summary>The grant of the request's host token and the app it names, still installed.</summary>
    private static (AppTokenGrant Grant, InstalledApp App) Caller(HttpContext context, AppRegistry registry)
    {
        var grant = AppTokenAuthenticationHandler.GrantOf(context);
        return (grant, registry.Find(grant.AppId) ?? throw new PithyxException(ErrorCodes.AppTokenInvalid));
    }

    private static Ok<AppLogResult> WriteLogs(AppLogBatch batch, LogService logs, AppRegistry registry, HttpContext context)
    {
        var (_, app) = Caller(context, registry);
        return TypedResults.Ok(logs.WriteApp(app.Id, context.User.FindFirstValue(ClaimTypes.Name) ?? "", batch, debug: app.LogLevel == AppLogLevel.Debug));
    }

    private static async Task<Created<Notification>> NotifyAsync(AppNotifyRequest request, NotificationService notifications, AppRegistry registry, HttpContext context, CancellationToken cancellationToken)
    {
        var (grant, app) = Caller(context, registry);
        AppFiles.Require(app, NotificationsPermission);
        var notification = await notifications.CreateForAppAsync(grant.UserId, app.Id, request, cancellationToken);
        return TypedResults.Created($"/api/notifications/{notification.Id}", notification);
    }

    private static async Task<Ok<IReadOnlyList<DirectoryUser>>> ListUsersAsync(SqliteConnectionFactory connections, AppRegistry registry, HttpContext context, CancellationToken cancellationToken)
    {
        AppFiles.Require(Caller(context, registry).App, DirectoryPermission);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var members = (await connection.QueryAsync<(long UserId, long GroupId)>("SELECT user_id, group_id FROM group_members ORDER BY group_id"))
            .ToLookup(row => row.UserId, row => row.GroupId);
        var users = await connection.QueryAsync<(long Id, string Username, string? DisplayName)>(
            "SELECT id, username, display_name FROM users WHERE disabled_at IS NULL ORDER BY username");
        return TypedResults.Ok<IReadOnlyList<DirectoryUser>>([.. users.Select(user => new DirectoryUser(user.Id, user.Username, user.DisplayName, [.. members[user.Id].Where(group => group != GroupReconciler.UsersGid)]))]);
    }

    private static async Task<Ok<IReadOnlyList<DirectoryGroup>>> ListGroupsAsync(SqliteConnectionFactory connections, AppRegistry registry, HttpContext context, CancellationToken cancellationToken)
    {
        AppFiles.Require(Caller(context, registry).App, DirectoryPermission);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var groups = await connection.QueryAsync<(long Id, string Name, string Description)>("SELECT id, name, description FROM groups WHERE builtin = 0 ORDER BY name");
        return TypedResults.Ok<IReadOnlyList<DirectoryGroup>>([.. groups.Select(group => new DirectoryGroup(group.Id, group.Name, group.Description))]);
    }

    private static async Task<Ok<AppFolderPage>> ListFolderAsync(
        [FromQuery] string? share,
        [FromQuery] string? storage,
        [FromQuery] string? path,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        AppFiles files,
        AppRegistry registry,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await files.ListAsync(
            Caller(context, registry).App,
            context.User,
            new AppFileLocation(share, storage),
            path,
            Parse(sort, FileSort.Name),
            Parse(order, SortOrder.Asc),
            limit,
            cursor,
            cancellationToken));

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        value is null ? fallback : Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !char.IsAsciiDigit(value[0])
            ? parsed
            : throw new PithyxException(ErrorCodes.FsOperationInvalid);

    private static async Task<FileStreamHttpResult> ReadFileAsync(
        [FromQuery] string? share,
        [FromQuery] string? storage,
        [FromQuery] string? path,
        AppFiles files,
        AppRegistry registry,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var (stream, stat, name) = await files.OpenReadAsync(Caller(context, registry).App, context.User, new AppFileLocation(share, storage), path, cancellationToken);
        context.Response.RegisterForDispose(stream);
        var headers = context.Response.Headers;
        headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "private, no-store";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(name);
        headers[HeaderNames.ContentDisposition] = disposition.ToString();
        return TypedResults.Stream(stream, "application/octet-stream", fileDownloadName: null, lastModified: stat.Modified, enableRangeProcessing: true);
    }

    private static async Task<Created<AppFileEntry>> WriteFileAsync(
        [FromQuery] string? share,
        [FromQuery] string? storage,
        [FromQuery] string? path,
        [FromQuery] bool? overwrite,
        AppFiles files,
        AppRegistry registry,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // The pool's free space is the limit, as for the desktop's uploads.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }
        var entry = await files.WriteAsync(Caller(context, registry).App, context.User, new AppFileLocation(share, storage), path, context.Request.Body, context.Request.ContentLength, overwrite == true, cancellationToken);
        return TypedResults.Created((string?)null, entry);
    }

    private static Ok<AppTicket> CreateTicket(AppTicketRequest request, AppTokens tokens, AppRegistry registry, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (grant, app) = Caller(context, registry);
        if (request.Endpoint is null || !app.Endpoints.Contains(request.Endpoint))
        {
            var endpoint = request.Endpoint ?? "";
            throw new PithyxException(ErrorCodes.AppBackendEndpointUnknown, new Dictionary<string, object?> { ["endpoint"] = endpoint.Length > 63 ? endpoint[..63] : endpoint });
        }
        var issued = tokens.IssueTicket(grant, request.Endpoint);
        return TypedResults.Ok(new AppTicket(issued.Token, issued.ExpiresAt));
    }
}
