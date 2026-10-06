// Talks to the Docker Engine API over its Unix socket with a plain HttpClient, as the daemon's DockerEngine will (D309),
// and saves the answers as fixtures. Started by tools/dev-vm/docker-spike.sh after its containers run; needs root.
//
//   dotnet docker-engine-probe.dll <socket> <fixture dir> <since, unix seconds> <app label value>
//
// Prints one PASS or FAIL line per check and exits 1 when a check failed.
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: docker-engine-probe <socket> <fixture dir> <since> <app label value>");
    return 2;
}

var (socketPath, fixtures, since, app) = (args[0], args[1], args[2], args[3]);
Directory.CreateDirectory(fixtures);

var handler = new SocketsHttpHandler
{
    ConnectCallback = async (_, cancellationToken) =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    },
};
// The host name is never resolved; the version prefix pins the API to Debian 13's Docker 26.1 (API 1.45).
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/v1.45/"), Timeout = TimeSpan.FromSeconds(30) };

var failed = 0;
void Pass(string check, string detail) => Console.WriteLine($"PASS  {check}: {detail}");
void Fail(string check, string detail)
{
    failed++;
    Console.WriteLine($"FAIL  {check}: {detail}");
}

async Task<string> Get(string path, string? fixture)
{
    using var response = await http.GetAsync(path);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"GET {path}: {(int)response.StatusCode} {body.Trim()}");
    }
    if (fixture is not null)
    {
        await File.WriteAllTextAsync(Path.Combine(fixtures, fixture), body);
    }
    return body;
}

static string Filters(string key, string value) => Uri.EscapeDataString($$"""{"{{key}}":["{{value}}"]}""");

try
{
    using var ping = await http.GetAsync("/_ping");
    var pong = await ping.Content.ReadAsStringAsync();
    var apiVersion = ping.Headers.TryGetValues("Api-Version", out var values) ? values.First() : "";
    if (pong == "OK" && apiVersion.Length > 0)
    {
        Pass("dotnet ping", $"/_ping answered OK over {socketPath}, Api-Version {apiVersion}");
    }
    else
    {
        Fail("dotnet ping", $"/_ping answered '{pong}', Api-Version '{apiVersion}'");
    }

    var version = JsonNode.Parse(await Get("version", "version.json"))!;
    var info = JsonNode.Parse(await Get("info", "info.json"))!;
    var security = info["SecurityOptions"]!.AsArray().Select(option => option!.GetValue<string>()).ToList();
    if (info["Driver"]!.GetValue<string>() == "overlay2" && security.Contains("name=userns"))
    {
        Pass("dotnet info", $"Docker {version["Version"]}, API {version["ApiVersion"]}, overlay2, userns, root dir {info["DockerRootDir"]}");
    }
    else
    {
        Fail("dotnet info", $"driver {info["Driver"]}, security options {string.Join(' ', security)}");
    }

    var containers = JsonNode.Parse(await Get($"containers/json?all=true&filters={Filters("label", $"com.pithyx.app={app}")}", "containers.json"))!.AsArray();
    var web = containers.FirstOrDefault(container => container!["Labels"]?["com.pithyx.service"]?.GetValue<string>() == "web");
    var oom = containers.FirstOrDefault(container => container!["Labels"]?["com.pithyx.service"]?.GetValue<string>() == "oom");
    if (web is null || oom is null)
    {
        Fail("dotnet containers", $"expected the services web and oom with com.pithyx.app={app}, found {containers.Count} containers");
    }
    else
    {
        var webInspect = JsonNode.Parse(await Get($"containers/{web["Id"]}/json", "container-web.json"))!;
        var oomInspect = JsonNode.Parse(await Get($"containers/{oom["Id"]}/json", "container-oom.json"))!;
        var health = webInspect["State"]?["Health"]?["Status"]?.GetValue<string>();
        var oomKilled = oomInspect["State"]?["OOMKilled"]?.GetValue<bool>();
        if (health == "healthy" && oomKilled == true)
        {
            Pass("dotnet containers", $"{containers.Count} containers by label, web is {health}, oom has OOMKilled=true");
        }
        else
        {
            Fail("dotnet containers", $"web health {health}, oom OOMKilled {oomKilled}");
        }

        var image = JsonNode.Parse(await Get($"images/{webInspect["Image"]}/json", "image.json"))!;
        await Get("images/json?digests=true", "images.json");
        Pass("dotnet image", $"{image["Id"]}, RepoDigests {image["RepoDigests"]!.ToJsonString()}");
    }

    var networks = JsonNode.Parse(await Get($"networks?filters={Filters("label", $"com.pithyx.app={app}")}", "networks.json"))!.AsArray();
    if (networks.Count > 0 && networks.All(network => network!["Internal"]!.GetValue<bool>()))
    {
        await Get($"networks/{networks[0]!["Id"]}", "network.json");
        Pass("dotnet networks", $"{networks.Count} internal networks by label");
    }
    else
    {
        Fail("dotnet networks", $"{networks.Count} networks by label, not all internal");
    }

    var until = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var history = await Get($"events?since={since}&until={until}", "events.jsonl");
    var events = history.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
    // Exec and health actions carry their details after a colon ("health_status: healthy").
    var actions = events.Select(e => e["Action"]!.GetValue<string>().Split(':')[0]).ToHashSet();
    if (actions.Contains("start") && actions.Contains("health_status") && actions.Contains("oom"))
    {
        Pass("dotnet events", $"{events.Count} past events, actions {string.Join(' ', actions.Order(StringComparer.Ordinal))}");
    }
    else
    {
        Fail("dotnet events", $"no start, health_status or oom among {string.Join(' ', actions)}");
    }

    await StreamEvents();
}
catch (Exception exception) when (exception is HttpRequestException or SocketException or TaskCanceledException or NullReferenceException or InvalidOperationException)
{
    Fail("dotnet engine", exception.Message);
}

return failed == 0 ? 0 : 1;

// Follows /events while it creates and removes a network, as SystemStateService will follow it without polling.
async Task StreamEvents()
{
    const string name = "pithyx-spike-probe";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var filters = Uri.EscapeDataString($$"""{"type":["network"],"network":["{{name}}"]}""");
    using var request = new HttpRequestMessage(HttpMethod.Get, $"events?filters={filters}");
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
    using var reader = new StreamReader(stream, Encoding.UTF8);

    var body = new JsonObject { ["Name"] = name, ["Internal"] = true, ["Labels"] = new JsonObject { ["com.pithyx.spike-probe"] = "1" } }.ToJsonString();
    using var created = await http.PostAsync("networks/create", new StringContent(body, Encoding.UTF8, "application/json"), timeout.Token);
    var id = JsonNode.Parse(await created.Content.ReadAsStringAsync(timeout.Token))?["Id"]?.GetValue<string>();
    if (!created.IsSuccessStatusCode || id is null)
    {
        Fail("dotnet event stream", $"POST networks/create answered {(int)created.StatusCode}");
        return;
    }
    using var removed = await http.DeleteAsync($"networks/{id}", timeout.Token);

    var lines = new List<string>();
    try
    {
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            lines.Add(line);
            if (JsonNode.Parse(line)?["Action"]?.GetValue<string>() == "destroy")
            {
                break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Fail("dotnet event stream", $"no destroy event within 20 s, got {lines.Count} lines");
        return;
    }
    await File.WriteAllLinesAsync(Path.Combine(fixtures, "events-stream.jsonl"), lines);
    Pass("dotnet event stream", $"streamed {lines.Count} events (network create and destroy) while the request stayed open");
}
