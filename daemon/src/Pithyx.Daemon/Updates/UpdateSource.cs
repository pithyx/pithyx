using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Updates;

/// <summary>
/// Where the box reads its channels (<c>/etc/pithyx/update-source.json</c>, root, mode 600, D338): a URL with
/// <c>{channel}</c> for <c>channel.json</c> and an optional bearer token for a private repository or mirror. The token
/// is never logged, shown or put into a support bundle. After a switch in the recovery menu (D339) the file is a
/// <c>pithyx-source.json</c> with <paramref name="Name"/> and <paramref name="Keys"/>, and the box trusts only those keys;
/// a file whose source does not pass its check counts as absent.
/// </summary>
public sealed record UpdateSourceConfig(string Url, string? Token, string? Name = null, IReadOnlyList<Apps.Catalog.TrustedKey>? Keys = null)
{
    public const string DefaultUrl = "https://github.com/pithyx/pithyx/releases/download/channel-{channel}/channel.json";

    public const string BoxFile = "/etc/pithyx/update-source.json";

    public static UpdateSourceConfig Default { get; } = new(DefaultUrl, null);

    public static UpdateSourceConfig Load(string path, UpdateSourceConfig fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.TryGetProperty("keys", out _))
            {
                return UpdateSources.Check(bytes).Document is { } source ? new UpdateSourceConfig(source.Url, null, source.Name, source.Keys) : fallback;
            }
            var url = root.TryGetProperty("url", out var value) && value.GetString() is { Length: > 0 } text ? text : fallback.Url;
            var token = root.TryGetProperty("token", out var secret) && secret.GetString() is { Length: > 0 } bearer ? bearer : null;
            return new UpdateSourceConfig(url, token);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>Records print every member; this one would print the token.</summary>
    public override string ToString() => $"UpdateSourceConfig {{ Url = {Url}, Token = {(Token is null ? "none" : Hosting.SecretMaskingEnricher.Mask)}, Name = {Name ?? "project"} }}";
}

/// <summary>
/// Reads a channel's <c>channel.json</c> and its signature and opens the update file it names, over https (or
/// <c>file:</c> in simulation). With a token and a GitHub release download URL it goes through GitHub's API for release
/// assets, which also serves private repositories; the token is not sent on to the storage the API redirects to.
/// </summary>
public sealed partial class UpdateSource(HttpClient http, bool allowFile)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public Uri ChannelUri(UpdateSourceConfig config, string channel)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Check(new Uri(config.Url.Replace("{channel}", channel, StringComparison.Ordinal)));
    }

    /// <summary>The bytes of <c>channel.json</c> and of <c>channel.json.sig</c> (null when it has none).</summary>
    public async Task<(byte[] Document, byte[]? Signature)> ReadChannelAsync(UpdateSourceConfig config, string channel, CancellationToken cancellationToken)
    {
        var uri = ChannelUri(config, channel);
        var document = await ReadAsync(config, uri, optional: false, cancellationToken) ?? [];
        var signature = await ReadAsync(config, new Uri(uri + ".sig"), optional: true, cancellationToken);
        return (document, signature);
    }

    /// <summary>Opens the update file at <paramref name="relative"/> to <c>channel.json</c>.</summary>
    public async Task<Stream> OpenFileAsync(UpdateSourceConfig config, string channel, string relative, CancellationToken cancellationToken)
    {
        var uri = Check(new Uri(ChannelUri(config, channel), relative));
        if (uri.IsFile)
        {
            return File.OpenRead(uri.LocalPath);
        }
        var response = await SendAsync(config, uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw Unreachable(status == HttpStatusCode.NotFound ? "missing" : "status", (int)status);
        }
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private async Task<byte[]?> ReadAsync(UpdateSourceConfig config, Uri uri, bool optional, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            return File.Exists(uri.LocalPath) ? await File.ReadAllBytesAsync(uri.LocalPath, cancellationToken) : optional ? null : throw Unreachable("missing", 404);
        }
        using var response = await SendAsync(config, uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return optional ? null : throw Unreachable("missing", 404);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Unreachable("status", (int)response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(UpdateSourceConfig config, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var target = config.Token is not null && GithubDownload().Match(uri.AbsoluteUri) is { Success: true } release
                ? await AssetUriAsync(config.Token, release, timeout.Token)
                : uri;
            if (target is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("pithyx", "1"));
            if (config.Token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
                if (target != uri)
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                }
            }
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unreachable("timeout", null);
        }
        catch (HttpRequestException)
        {
            throw Unreachable("network", null);
        }
    }

    /// <summary>The API address of a release asset (<c>/repos/{owner}/{repo}/releases/assets/{id}</c>), or null when the release has no such asset.</summary>
    private async Task<Uri?> AssetUriAsync(string token, Match release, CancellationToken cancellationToken)
    {
        var api = new Uri($"https://api.github.com/repos/{release.Groups["owner"].Value}/{release.Groups["repo"].Value}/releases/tags/{Uri.EscapeDataString(release.Groups["tag"].Value)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("pithyx", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Unreachable("status", (int)response.StatusCode);
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var name = Uri.UnescapeDataString(release.Groups["name"].Value);
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == name && asset.GetProperty("url").GetString() is { } url)
            {
                return new Uri(url);
            }
        }
        return null;
    }

    private Uri Check(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (allowFile && uri.IsFile)
            ? uri
            : throw Unreachable("scheme", null);

    private static PithyxException Unreachable(string reason, int? status)
    {
        var parameters = new Dictionary<string, object?> { ["reason"] = reason };
        if (status is not null)
        {
            parameters["status"] = status;
        }
        return new PithyxException(ErrorCodes.UpdateSourceUnreachable, parameters);
    }

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/releases/download/(?<tag>[^/]+)/(?<name>[^/?#]+)$")]
    private static partial Regex GithubDownload();
}
