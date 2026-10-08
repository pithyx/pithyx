using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Apps.Manifest;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// Serves the frontends of installed apps at <c>/apps/&lt;id&gt;/&lt;path&gt;</c> from the installed version on the app
/// pool (D303). Every answer under <c>/apps/</c>, a 404 included, carries a <c>Content-Security-Policy</c> with
/// <c>sandbox allow-scripts allow-forms</c>, so a page opened directly in a tab runs in an opaque origin and never
/// with the user's session; <c>connect-src https:</c> only for an app holding <c>network.internet</c>. With
/// <c>nosniff</c> and <c>Access-Control-Allow-Origin: *</c>, because the sandboxed window loads its module scripts in
/// CORS mode from that opaque origin. The files are public like the desktop's own: a sandboxed window sends no cookie,
/// so who may use an app is decided where it is launched and where its tokens come from, not here. Paths follow the
/// package rules (<see cref="PackageLayout.IsSafe"/>), so no encoded or dotted path reaches the file system; a disabled
/// or unavailable app, a folder, a link and <c>api</c>/<c>api-*</c> (the backend's URLs) are 404.
/// </summary>
public sealed class AppFrontendHosting(RequestDelegate next, AppRegistry registry)
{
    public const string Prefix = "/apps";

    private const string Policy =
        "sandbox allow-scripts allow-forms; default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob:; font-src 'self' data:; media-src 'self' data: blob:; worker-src 'self' blob:; "
        + "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'self'; connect-src 'self'";

    private static readonly FileExtensionContentTypeProvider _types = new();

    /// <summary>The policy of an app's frontend; <c>connect-src https:</c> only with <c>network.internet</c>.</summary>
    public static string ContentSecurityPolicy(bool internet) => internet ? Policy + " https:" : Policy;

    /// <summary>
    /// What every answer under <c>/apps/&lt;id&gt;/</c> carries, the proxy's included: the sandboxing policy, <c>nosniff</c>,
    /// no referrer (a URL may carry <c>?pithyx-token=</c>), and readable from the sandboxed window's opaque origin.
    /// </summary>
    public static void Sandbox(IHeaderDictionary headers, bool internet)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy(internet);
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.AccessControlAllowOrigin] = "*";
        headers["Referrer-Policy"] = "no-referrer";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Path.StartsWithSegments(Prefix, StringComparison.Ordinal, out var rest))
        {
            await next(context);
            return;
        }
        var headers = context.Response.Headers;
        var segments = (rest.Value ?? "").TrimStart('/').Split('/', 2);
        var app = segments.Length == 2 ? registry.Find(segments[0]) : null;
        Sandbox(headers, app?.HasInternet == true);
        headers[HeaderNames.CacheControl] = "no-cache";

        if (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            headers[HeaderNames.Allow] = "GET, HEAD";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (app is null || File(app, segments[1]) is not { } file)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = _types.TryGetContentType(file.Name, out var type)
            ? type is "text/html" or "text/css" or "text/javascript" or "application/javascript" ? type + "; charset=utf-8" : type
            : "application/octet-stream";
        context.Response.ContentLength = file.Length;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await context.Response.SendFileAsync(file.FullName, context.RequestAborted);
        }
    }

    private FileInfo? File(InstalledApp app, string path)
    {
        // Kestrel decoded the path except %2F; whatever is left that a package could not contain is not a file.
        if (!PackageLayout.IsSafe(path) || PackageLayout.IsReservedFrontendPath(path) || registry.FrontendOf(app.Id) is not { } frontend)
        {
            return null;
        }
        var file = new FileInfo(Path.Combine(frontend, path));
        // The daemon wrote every file itself; a link or a folder is never served.
        return file is { Exists: true, LinkTarget: null } && !file.Attributes.HasFlag(FileAttributes.Directory) ? file : null;
    }
}
