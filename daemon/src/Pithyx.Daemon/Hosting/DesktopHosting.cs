using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// Serves the built desktop. Every path outside <c>/api/</c> and <c>/ws</c> that is not a file gets <c>index.html</c>,
/// so the desktop can handle its own URLs; API paths keep answering with problem details.
/// </summary>
public static partial class DesktopHosting
{
    // Vite puts content-hashed files under /assets/; they never change and may be cached forever.
    private const string ImmutableCache = "public, max-age=31536000, immutable";

    // The build writes Brotli and gzip copies next to every text asset; the best one the browser accepts is served.
    private static readonly (string Token, string Extension)[] _encodings = [("br", ".br"), ("gzip", ".gz")];
    private const string EncodingItem = "pithyx.content-encoding";

    // CreateSlimBuilder has no regex route constraint, so the exclusions are a small constraint of their own.
    public const string PathConstraint = "desktoppath";

    /// <summary>Registers the route constraint that keeps the SPA fallback away from API and WebSocket paths.</summary>
    public static IServiceCollection AddDesktopHosting(this IServiceCollection services) =>
        services.Configure<RouteOptions>(options => options.SetParameterPolicy<DesktopPathConstraint>(PathConstraint));

    public static void MapDesktop(this WebApplication app, string desktopDirectory)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!File.Exists(Path.Combine(desktopDirectory, "index.html")))
        {
            LogMissing(app.Logger, desktopDirectory);
            return;
        }

        var provider = new PhysicalFileProvider(desktopDirectory);
        var files = new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = new PrecompressedContentTypes(),
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                // App pages run in sandboxed iframes with an opaque origin; their module scripts and styles load in
                // CORS mode. These files are public and served without credentials, so any origin may read them.
                if (context.Context.Request.Path.StartsWithSegments("/apps", StringComparison.Ordinal))
                {
                    headers[HeaderNames.AccessControlAllowOrigin] = "*";
                }
                headers[HeaderNames.CacheControl] =
                    context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal) ? ImmutableCache : "no-cache";
                if (context.Context.Items[EncodingItem] is string encoding)
                {
                    headers[HeaderNames.ContentEncoding] = encoding;
                    headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
                }
            },
        };
        app.Use((context, next) =>
        {
            ServePrecompressed(context, provider);
            return next(context);
        });
        app.UseStaticFiles(files);
        app.MapFallbackToFile($"{{*path:{PathConstraint}}}", "index.html", files).AllowAnonymous();
    }

    /// <summary>Points the request at the .br or .gz copy of a static file when one exists and the browser accepts it.</summary>
    private static void ServePrecompressed(HttpContext context, PhysicalFileProvider provider)
    {
        var path = context.Request.Path;
        if (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) || !path.HasValue)
        {
            return;
        }
        var accepted = context.Request.Headers.AcceptEncoding.ToString();
        foreach (var (token, extension) in _encodings)
        {
            if (accepted.Contains(token, StringComparison.OrdinalIgnoreCase) && provider.GetFileInfo(path.Value + extension).Exists)
            {
                context.Request.Path = path.Value + extension;
                context.Items[EncodingItem] = token;
                return;
            }
        }
    }

    /// <summary>Content types by the original extension, so <c>index-abc.js.br</c> is still JavaScript.</summary>
    private sealed class PrecompressedContentTypes : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider _types = new();

        public bool TryGetContentType(string subpath, out string contentType)
        {
            ArgumentNullException.ThrowIfNull(subpath);
            var original = subpath.EndsWith(".br", StringComparison.Ordinal) || subpath.EndsWith(".gz", StringComparison.Ordinal) ? subpath[..^3] : subpath;
            return _types.TryGetContentType(original, out contentType!);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No desktop build in {Directory}; only the API is served. Run pnpm build or use the Vite dev server.")]
    private static partial void LogMissing(ILogger logger, string directory);

    private sealed class DesktopPathConstraint : IRouteConstraint
    {
        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
        {
            ArgumentNullException.ThrowIfNull(values);
            var path = values.TryGetValue(routeKey, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
            // Paths that look like files are left to the static files, so a missing asset is a 404, not index.html.
            var lastSegment = path[(path.LastIndexOf('/') + 1)..];
            return !(path.Equals("api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || lastSegment.Contains('.', StringComparison.Ordinal));
        }
    }
}
