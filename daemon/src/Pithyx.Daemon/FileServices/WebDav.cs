using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.StaticFiles;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.FileServices;

/// <summary>One entry of a PROPFIND answer.</summary>
/// <param name="Href">The entry's address below <c>/webdav/</c>, escaped, with a slash at the end for folders.</param>
/// <param name="Entry">What the file service says about it.</param>
public sealed record WebDavResource(string Href, FileEntry Entry);

/// <summary>The XML of WebDAV answers (RFC 4918), pure so it can be tested.</summary>
public static class WebDavXml
{
    private static readonly XNamespace _dav = "DAV:";
    private static readonly FileExtensionContentTypeProvider _types = new();

    /// <summary>The address of a path of the tree, every segment escaped; folders end with a slash.</summary>
    public static string Href(string path, bool folder)
    {
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var href = WebDav.Prefix + "/" + string.Join('/', segments);
        return folder && !href.EndsWith('/') ? href + "/" : href;
    }

    /// <summary>A multistatus answer with the usual properties of every resource.</summary>
    public static string Multistatus(IEnumerable<WebDavResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var document = new XDocument(new XElement(_dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", _dav.NamespaceName),
            resources.Select(resource =>
            {
                var entry = resource.Entry;
                var folder = entry.Kind == FileKind.Folder;
                var name = entry.Name;
                var properties = new XElement(_dav + "prop",
                    new XElement(_dav + "displayname", name),
                    new XElement(_dav + "resourcetype", folder ? new XElement(_dav + "collection") : null),
                    new XElement(_dav + "getlastmodified", entry.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement(_dav + "creationdate", entry.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                    new XElement(_dav + "supportedlock",
                        new XElement(_dav + "lockentry",
                            new XElement(_dav + "lockscope", new XElement(_dav + "exclusive")),
                            new XElement(_dav + "locktype", new XElement(_dav + "write")))),
                    new XElement(_dav + "lockdiscovery"));
                if (!folder)
                {
                    properties.Add(
                        new XElement(_dav + "getcontentlength", entry.Size.ToString(CultureInfo.InvariantCulture)),
                        new XElement(_dav + "getcontenttype", ContentType(name)),
                        new XElement(_dav + "getetag", ETag(entry)));
                }
                return new XElement(_dav + "response",
                    new XElement(_dav + "href", resource.Href),
                    new XElement(_dav + "propstat", properties, new XElement(_dav + "status", "HTTP/1.1 200 OK")));
            })));
        return Write(document);
    }

    /// <summary>The answer to PROPPATCH: every property that was asked for is accepted, so clients that set times go on.</summary>
    public static string PropPatched(string href, string request)
    {
        var names = new List<XName>();
        try
        {
            var body = XDocument.Parse(request);
            names.AddRange(body.Descendants(_dav + "prop").SelectMany(prop => prop.Elements()).Select(element => element.Name));
        }
        catch (XmlException)
        {
            // An empty or broken body sets nothing.
        }
        var document = new XDocument(new XElement(_dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", _dav.NamespaceName),
            new XElement(_dav + "response",
                new XElement(_dav + "href", href),
                new XElement(_dav + "propstat",
                    new XElement(_dav + "prop", names.Distinct().Select(name => new XElement(name))),
                    new XElement(_dav + "status", "HTTP/1.1 200 OK")))));
        return Write(document);
    }

    /// <summary>The answer to LOCK: an exclusive write lock with the token, which Pithyx does not enforce.</summary>
    public static string Locked(string href, string token) =>
        Write(new XDocument(new XElement(_dav + "prop", new XAttribute(XNamespace.Xmlns + "D", _dav.NamespaceName),
            new XElement(_dav + "lockdiscovery",
                new XElement(_dav + "activelock",
                    new XElement(_dav + "locktype", new XElement(_dav + "write")),
                    new XElement(_dav + "lockscope", new XElement(_dav + "exclusive")),
                    new XElement(_dav + "depth", "0"),
                    new XElement(_dav + "timeout", "Second-3600"),
                    new XElement(_dav + "locktoken", new XElement(_dav + "href", token)),
                    new XElement(_dav + "lockroot", new XElement(_dav + "href", href)))))));

    public static string ETag(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"\"{entry.Size.ToString("x", CultureInfo.InvariantCulture)}-{entry.Modified.ToUnixTimeMilliseconds().ToString("x", CultureInfo.InvariantCulture)}\"";
    }

    public static string ContentType(string name) => _types.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    private static string Write(XDocument document)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }
        // XmlWriter on a StringBuilder declares UTF-16; the body is sent as UTF-8.
        return text.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }
}

/// <summary>
/// WebDAV (step 12, D291) inside the daemon at <c>/webdav/</c>, off until switched on in Settings > File services. Clients
/// sign in with Basic authentication and their Pithyx password (<see cref="ProtocolSignIn"/>); the root lists the
/// shared folders the user may use over WebDAV, and every operation goes through the file service, so rights, owners
/// and the recycle bin are those of Files. Locks are granted but not enforced (class 2 for the clients that need it).
/// </summary>
public static partial class WebDav
{
    public const string Prefix = "/webdav";

    private static readonly string[] _methods = ["OPTIONS", "PROPFIND", "PROPPATCH", "GET", "HEAD", "PUT", "DELETE", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK"];

    public static void MapWebDav(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapMethods(Prefix, _methods, HandleAsync).ExcludeFromDescription().AllowAnonymous();
        app.MapMethods(Prefix + "/{**path}", _methods, HandleAsync).ExcludeFromDescription().AllowAnonymous();
    }

    private static async Task HandleAsync(HttpContext context, ProtocolSignIn signIn, ProtocolFiles files, SqliteConnectionFactory connections, ILoggerFactory loggers)
    {
        var response = context.Response;
        await using (var connection = await connections.OpenAsync(context.RequestAborted))
        {
            if (await connection.ExecuteScalarAsync<long>("SELECT webdav_enabled FROM file_services WHERE id = 1") != 1)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        if (context.Request.Method == "OPTIONS" && context.Request.Headers.Authorization.Count == 0)
        {
            Options(response);
            return;
        }
        var user = await SignInAsync(context, signIn);
        if (user is null)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            response.Headers.WWWAuthenticate = "Basic realm=\"Pithyx\", charset=\"UTF-8\"";
            return;
        }
        var path = PathOf(context.Request.Path);
        try
        {
            await DispatchAsync(context, user, files, path);
        }
        catch (PithyxException exception)
        {
            if (!response.HasStarted)
            {
                response.StatusCode = StatusOf(exception.Code, context.Request.Method);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailed(loggers.CreateLogger(typeof(WebDav)), context.Request.Method, exception);
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }

    private static async Task DispatchAsync(HttpContext context, ClaimsPrincipal user, ProtocolFiles files, string path)
    {
        var request = context.Request;
        var response = context.Response;
        var cancellationToken = context.RequestAborted;
        const string Protocol = Resources.WebDav;
        switch (request.Method)
        {
            case "OPTIONS":
                Options(response);
                return;
            case "PROPFIND":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    var self = await files.StatAsync(user, Protocol, target, cancellationToken);
                    var folder = self.Kind == FileKind.Folder;
                    var resources = new List<WebDavResource> { new(WebDavXml.Href(path, folder), self with { Name = target.IsRoot ? "" : FsPath.Split(path.Trim('/')).Name }) };
                    if (folder && request.Headers["Depth"].ToString() != "0")
                    {
                        foreach (var entry in await files.ListAsync(user, Protocol, target, cancellationToken))
                        {
                            if (entry.Kind is FileKind.File or FileKind.Folder)
                            {
                                resources.Add(new WebDavResource(WebDavXml.Href($"{path}/{entry.Name}", entry.Kind == FileKind.Folder), entry));
                            }
                        }
                    }
                    await XmlAsync(response, StatusCodes.Status207MultiStatus, WebDavXml.Multistatus(resources), cancellationToken);
                    return;
                }
            case "PROPPATCH":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    _ = await files.StatAsync(user, Protocol, target, cancellationToken);
                    using var reader = new StreamReader(request.Body, Encoding.UTF8);
                    await XmlAsync(response, StatusCodes.Status207MultiStatus, WebDavXml.PropPatched(WebDavXml.Href(path, false), await reader.ReadToEndAsync(cancellationToken)), cancellationToken);
                    return;
                }
            case "GET":
            case "HEAD":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    if ((await files.StatAsync(user, Protocol, target, cancellationToken)).Kind == FileKind.Folder)
                    {
                        response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                        return;
                    }
                    var (stream, stat, name) = await files.OpenReadAsync(user, target, cancellationToken);
                    var entry = new FileEntry(name, FileKind.File, stat.Size, stat.Modified);
                    await Results.Stream(
                        stream,
                        WebDavXml.ContentType(name),
                        lastModified: stat.Modified,
                        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(WebDavXml.ETag(entry)),
                        enableRangeProcessing: true).ExecuteAsync(context);
                    return;
                }
            case "PUT":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    try
                    {
                        var existed = await files.WriteAsync(user, target, request.Body, request.ContentLength, overwrite: true, cancellationToken);
                        response.StatusCode = existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
                    }
                    catch (PithyxException exception) when (exception.Code == ErrorCodes.FsEntryNotFound)
                    {
                        response.StatusCode = StatusCodes.Status409Conflict;
                    }
                    return;
                }
            case "MKCOL":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    if (request.ContentLength > 0)
                    {
                        response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                        return;
                    }
                    try
                    {
                        await files.MakeFolderAsync(user, target, cancellationToken);
                        response.StatusCode = StatusCodes.Status201Created;
                    }
                    catch (PithyxException exception) when (exception.Code == ErrorCodes.FsEntryNotFound)
                    {
                        response.StatusCode = StatusCodes.Status409Conflict;
                    }
                    catch (PithyxException exception) when (exception.Code is ErrorCodes.FsEntryExists or ErrorCodes.FsPathInvalid)
                    {
                        response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    }
                    return;
                }
            case "DELETE":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    await files.DeleteAsync(user, target, cancellationToken);
                    response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
            case "COPY":
            case "MOVE":
                {
                    if (DestinationOf(request) is not { } destination)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }
                    var source = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    var target = await files.ResolveAsync(user, Protocol, destination, cancellationToken);
                    var overwrite = !string.Equals(request.Headers["Overwrite"].ToString(), "F", StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        var existed = await files.TransferAsync(user, source, target, move: request.Method == "MOVE", overwrite, cancellationToken);
                        response.StatusCode = existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
                    }
                    catch (PithyxException exception) when (exception.Code == ErrorCodes.FsEntryExists)
                    {
                        response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    }
                    return;
                }
            case "LOCK":
                {
                    var target = await files.ResolveAsync(user, Protocol, path, cancellationToken);
                    var created = false;
                    if (!target.IsRoot && target.Path.Length > 0 && !await files.ExistsAsync(user, target, cancellationToken))
                    {
                        // Clients lock a new name before they write it: it becomes an empty file.
                        await files.WriteAsync(user, target, Stream.Null, 0, overwrite: false, cancellationToken);
                        created = true;
                    }
                    var token = $"opaquelocktoken:{Guid.NewGuid()}";
                    response.Headers["Lock-Token"] = $"<{token}>";
                    await XmlAsync(response, created ? StatusCodes.Status201Created : StatusCodes.Status200OK, WebDavXml.Locked(WebDavXml.Href(path, false), token), cancellationToken);
                    return;
                }
            case "UNLOCK":
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            default:
                response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
        }
    }

    /// <summary>The status of a refused request: no access hides what is there, read-only access forbids writing.</summary>
    public static int StatusOf(string code, string method) => code switch
    {
        ErrorCodes.FsShareNotFound or ErrorCodes.FsEntryNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.FsShareReadOnly or ErrorCodes.FsEntryAccessDenied or ErrorCodes.AuthPermissionDenied => StatusCodes.Status403Forbidden,
        ErrorCodes.FsPathInvalid or ErrorCodes.FsNameInvalid => method == "MKCOL" ? StatusCodes.Status405MethodNotAllowed : StatusCodes.Status403Forbidden,
        ErrorCodes.FsEntryExists => StatusCodes.Status409Conflict,
        ErrorCodes.FsShareUnavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorCodes.FsEntryIsFolder => StatusCodes.Status405MethodNotAllowed,
        ErrorCodes.FsUploadIncomplete => StatusCodes.Status400BadRequest,
        ErrorCodes.AuthRateLimited => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static void Options(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.Allow = string.Join(", ", _methods);
        response.Headers["DAV"] = "1, 2";
        response.Headers["MS-Author-Via"] = "DAV";
    }

    private static async Task<ClaimsPrincipal?> SignInAsync(HttpContext context, ProtocolSignIn signIn)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }
        try
        {
            return await signIn.SignInAsync(decoded[..colon], decoded[(colon + 1)..], Resources.WebDav, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.RequestAborted);
        }
        catch (PithyxException exception) when (exception.Code == ErrorCodes.AuthRateLimited)
        {
            return null;
        }
    }

    /// <summary>The path of the tree for a request path, unescaped.</summary>
    public static string PathOf(PathString requestPath)
    {
        var value = requestPath.Value ?? "";
        var below = value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? value[Prefix.Length..] : value;
        return "/" + string.Join('/', below.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The tree path of the Destination header, which may be an absolute URL or a path.</summary>
    public static string? DestinationOf(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers["Destination"].ToString();
        if (header.Length == 0)
        {
            return null;
        }
        var path = Uri.TryCreate(header, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : header;
        path = Uri.UnescapeDataString(path);
        return path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? PathOf(new PathString(path)) : null;
    }

    private static async Task XmlAsync(HttpResponse response, int status, string xml, CancellationToken cancellationToken)
    {
        response.StatusCode = status;
        response.ContentType = "application/xml; charset=utf-8";
        await response.WriteAsync(xml, Encoding.UTF8, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebDAV {Method} failed")]
    private static partial void LogFailed(ILogger logger, string method, Exception exception);
}
