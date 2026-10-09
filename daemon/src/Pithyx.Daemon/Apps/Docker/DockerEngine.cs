using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>A container of an app as the Engine lists it.</summary>
/// <param name="Id">The full container id.</param>
/// <param name="Name">The name without the leading slash.</param>
/// <param name="App">The label <c>com.pithyx.app</c>.</param>
/// <param name="Service">The label <c>com.pithyx.service</c>.</param>
/// <param name="State"><c>created</c>, <c>running</c>, <c>exited</c> and the like.</param>
/// <param name="Addresses">The container's IPv4 address on each network, by network name.</param>
public sealed record DockerContainer(string Id, string Name, string? App, string? Service, string State, IReadOnlyDictionary<string, string> Addresses);

/// <summary>An Engine event about an app's container (D309): the action without the details after a colon.</summary>
/// <param name="Action"><c>start</c>, <c>die</c>, <c>oom</c>, <c>health_status</c> and the like.</param>
/// <param name="Detail">What follows the colon, such as <c>healthy</c>.</param>
/// <param name="ContainerId">The container's id.</param>
/// <param name="App">The label <c>com.pithyx.app</c>.</param>
/// <param name="Service">The label <c>com.pithyx.service</c>.</param>
/// <param name="ExitCode">The exit code of a <c>die</c>.</param>
/// <param name="Time">When it happened.</param>
public sealed record DockerEvent(string Action, string? Detail, string ContainerId, string App, string? Service, int? ExitCode, DateTimeOffset Time);

/// <summary>Where the Engine answers and how the box treats Docker.</summary>
/// <param name="Apply">Real mode: the daemon owns Docker's configuration and runs it; in simulation nothing is touched.</param>
/// <param name="SocketPath">The Engine's Unix socket.</param>
/// <param name="ConfigPath">Docker's <c>daemon.json</c>, owned by the daemon.</param>
/// <param name="DropInPath">The systemd drop-in of <c>docker.service</c>.</param>
/// <param name="SubuidPath"><c>/etc/subuid</c>.</param>
/// <param name="SubgidPath"><c>/etc/subgid</c>.</param>
public sealed record DockerOptions(
    bool Apply,
    string SocketPath = "/run/docker.sock",
    string ConfigPath = "/etc/docker/daemon.json",
    string DropInPath = "/etc/systemd/system/docker.service.d/pithyx.conf",
    string SubuidPath = "/etc/subuid",
    string SubgidPath = "/etc/subgid");

/// <summary>
/// The Docker Engine API on its Unix socket (D309: API 1.45 of Debian's Docker 26.1), never the CLI's output: listing an
/// app's containers, finding, loading and pulling images, and following the events of app containers. Only the
/// daemon's own requests use it; nothing an app sends reaches the Engine.
/// </summary>
public sealed class DockerEngine(DockerOptions options) : IDisposable
{
    public const string ApiVersion = "v1.45";

    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        UseProxy = false,
    })
    {
        BaseAddress = new Uri($"http://docker/{ApiVersion}/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Whether the Engine answers.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _client.GetAsync("_ping", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Every container of an app, running or not.</summary>
    public async Task<IReadOnlyList<DockerContainer>> ContainersAsync(string appId, CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [$"com.pithyx.app={appId}"] }));
        return DockerResponses.Containers(await GetTextAsync($"containers/json?all=1&filters={filters}", cancellationToken));
    }

    /// <summary>The id of an image by id or reference, or null when the Engine does not have it.</summary>
    public async Task<string?> ImageIdAsync(string image, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"images/{Uri.EscapeDataString(image)}/json", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await RequireSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("Id").GetString();
    }

    /// <summary>Loads an image archive (<c>docker save</c>) and returns the ids it loaded.</summary>
    public async Task<IReadOnlyList<string>> LoadAsync(Stream archive, CancellationToken cancellationToken)
    {
        using var content = new StreamContent(archive);
        content.Headers.ContentType = new("application/x-tar");
        using var response = await _client.PostAsync("images/load?quiet=1", content, cancellationToken);
        await RequireSuccessAsync(response, cancellationToken);
        return DockerResponses.LoadedImages(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// Pulls an image by its platform manifest's digest (<c>name@sha256:...</c>) for one architecture, with
    /// <paramref name="credentials"/> (<c>user:token</c>) for a private registry.
    /// </summary>
    public async Task PullAsync(string reference, string architecture, CancellationToken cancellationToken, string? credentials = null)
    {
        var query = $"images/create?fromImage={Uri.EscapeDataString(reference)}&platform={Uri.EscapeDataString("linux/" + architecture)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, query);
        if (credentials is not null && credentials.IndexOf(':', StringComparison.Ordinal) is var colon and > 0)
        {
            var registry = reference[..Math.Max(reference.IndexOf('/', StringComparison.Ordinal), 0)];
            var auth = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["username"] = credentials[..colon],
                ["password"] = credentials[(colon + 1)..],
                ["serveraddress"] = registry,
            });
            request.Headers.Add("X-Registry-Auth", Convert.ToBase64String(auth).Replace('+', '-').Replace('/', '_'));
        }
        using var response = await _client.SendAsync(request, cancellationToken);
        await RequireSuccessAsync(response, cancellationToken);
        // The answer is a stream of progress lines; an error arrives as a line, with status 200.
        if (DockerResponses.StreamError(await response.Content.ReadAsStringAsync(cancellationToken)) is { } error)
        {
            throw new DockerEngineException(error);
        }
    }

    /// <summary>Removes an image no container uses any more; one still in use stays.</summary>
    public async Task RemoveImageAsync(string image, CancellationToken cancellationToken)
    {
        using var response = await _client.DeleteAsync($"images/{Uri.EscapeDataString(image)}", cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Conflict))
        {
            await RequireSuccessAsync(response, cancellationToken);
        }
    }

    /// <summary>Follows the events of app containers until the Engine stops or the token is cancelled.</summary>
    public async IAsyncEnumerable<DockerEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["type"] = ["container"],
            ["label"] = ["com.pithyx.app"],
            ["event"] = ["create", "start", "die", "oom", "kill", "stop", "destroy", "health_status"],
        }));
        using var response = await _client.GetAsync($"events?filters={filters}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await RequireSuccessAsync(response, cancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (DockerResponses.Event(line) is { } item)
            {
                yield return item;
            }
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<string> GetTextAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        await RequireSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new DockerEngineException(DockerResponses.Message(body) ?? $"HTTP {(int)response.StatusCode}");
        }
    }
}

/// <summary>The Engine refused a request or reported an error; the message is the Engine's.</summary>
public sealed class DockerEngineException(string message) : Exception(message)
{
    public DockerEngineException()
        : this("The Docker Engine failed.")
    {
    }

    public DockerEngineException(string message, Exception inner)
        : this(message + ": " + inner?.Message)
    {
    }
}
