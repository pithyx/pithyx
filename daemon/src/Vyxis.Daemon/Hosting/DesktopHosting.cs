using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Vyxis.Daemon.Hosting;

/// <summary>
/// Serves the built desktop. Every path outside <c>/api/</c> and <c>/ws</c> that is not a file gets <c>index.html</c>,
/// so the desktop can handle its own URLs; API paths keep answering with problem details.
/// </summary>
public static partial class DesktopHosting
{
    // Vite puts content-hashed files under /assets/; they never change and may be cached forever.
    private const string ImmutableCache = "public, max-age=31536000, immutable";

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

        var files = new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(desktopDirectory),
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] =
                context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal) ? ImmutableCache : "no-cache",
        };
        app.UseStaticFiles(files);
        app.MapFallbackToFile($"{{*path:{PathConstraint}}}", "index.html", files).AllowAnonymous();
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
