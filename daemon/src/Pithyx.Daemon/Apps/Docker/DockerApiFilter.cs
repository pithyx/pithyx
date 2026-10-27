using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>What the filtering Docker proxy knows about the app behind a socket (D387).</summary>
/// <param name="AppId">The app.</param>
/// <param name="AppDirectory">The app's dataset; only folders in its <c>volumes/</c> may be bound.</param>
/// <param name="Network">The app's internal Docker network, which every container it starts joins.</param>
/// <param name="Internet">Whether the app holds <c>network.internet</c>, which image pulls need.</param>
public sealed record DockerProxyApp(string AppId, string AppDirectory, string Network, bool Internet);

/// <summary>What a request of the Engine API is, after its version prefix and query are taken off.</summary>
public enum DockerRouteKind
{
    /// <summary>Refused.</summary>
    Deny,

    /// <summary>Forwarded as it is (ping, version, image inspection).</summary>
    Pass,

    /// <summary>A list or event stream that gets the app's label filter.</summary>
    Filtered,

    /// <summary><c>POST /containers/create</c>: the body is checked and rewritten.</summary>
    CreateContainer,

    /// <summary>A request on one container, which must be the app's.</summary>
    Container,

    /// <summary><c>POST /containers/{id}/exec</c>: the container must be the app's, the body is checked.</summary>
    CreateExec,

    /// <summary>A request on one exec instance, whose container must be the app's.</summary>
    Exec,

    /// <summary><c>POST /images/create</c> with <c>fromImage</c>, only with <c>network.internet</c>.</summary>
    PullImage,

    /// <summary><c>POST /volumes/create</c>: the body is checked and labeled.</summary>
    CreateVolume,

    /// <summary>A request on one volume, which must be the app's.</summary>
    Volume,
}

/// <summary>The decision about one request: where it goes and whether the connection turns into a stream both ways.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">The container, exec instance or volume it is about.</param>
/// <param name="Hijack">Attach and exec start: after the answer both sides keep talking on the connection.</param>
/// <param name="Reason">Why it is refused, for the app's error message.</param>
public sealed record DockerRoute(DockerRouteKind Kind, string? Id = null, bool Hijack = false, string? Reason = null);

/// <summary>
/// The decisions of the filtering Docker socket (D387), pure functions so tests cover them: which Engine API calls an app
/// may make, the label filter on lists, and the checks and rewrites of what it creates. An app sees and controls only the
/// containers and volumes it started (label <c>com.pithyx.spawned-by</c>); they never get the host's namespaces, devices,
/// capabilities, ports or file system, join the app's internal network and get the box's limits. Building, pushing,
/// loading, committing and pruning are refused, and pulling needs <c>network.internet</c>.
/// </summary>
public static partial class DockerApiFilter
{
    /// <summary>The label of everything an app started through its socket; never <c>com.pithyx.app</c>, which marks backend containers.</summary>
    public const string SpawnedLabel = "com.pithyx.spawned-by";

    /// <summary>The largest request body the proxy reads (a container's configuration).</summary>
    public const int MaxBody = 1024 * 1024;

    private static readonly HashSet<string> _containerGets = new(StringComparer.Ordinal) { "json", "logs", "top", "stats", "changes", "export", "archive" };

    private static readonly HashSet<string> _containerPosts = new(StringComparer.Ordinal) { "start", "stop", "restart", "kill", "pause", "unpause", "wait", "resize", "rename" };

    /// <summary>Top-level keys of a container's configuration that may carry a value; any other one must be empty.</summary>
    private static readonly HashSet<string> _configKeys = new(StringComparer.Ordinal)
    {
        "Hostname", "Domainname", "User", "AttachStdin", "AttachStdout", "AttachStderr", "ExposedPorts", "Tty", "OpenStdin",
        "StdinOnce", "Env", "Cmd", "Healthcheck", "ArgsEscaped", "Image", "Volumes", "WorkingDir", "Entrypoint",
        "NetworkDisabled", "OnBuild", "Labels", "StopSignal", "StopTimeout", "Shell", "HostConfig", "NetworkingConfig",
    };

    /// <summary>HostConfig keys that may carry a value; the proxy checks or replaces some of them. Any other one must be empty.</summary>
    private static readonly HashSet<string> _hostConfigKeys = new(StringComparer.Ordinal)
    {
        "Binds", "Mounts", "Tmpfs", "Memory", "MemorySwap", "MemoryReservation", "NanoCpus", "CpuShares", "CpuQuota",
        "CpuPeriod", "AutoRemove", "RestartPolicy", "Init", "ShmSize", "ReadonlyRootfs", "CapDrop", "ConsoleSize",
        "LogConfig", "NetworkMode", "PidsLimit", "OomScoreAdj", "SecurityOpt", "IpcMode", "CgroupnsMode", "Isolation",
        "Runtime",
    };

    /// <summary>Decides what a request is. <paramref name="path"/> is the path without query; a version prefix is ignored.</summary>
    public static DockerRoute Route(string method, string path, IReadOnlyDictionary<string, string> query)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(query);
        var parts = Unversioned(path).Trim('/').Split('/');
        var get = method is "GET" or "HEAD";
        switch (parts)
        {
            case ["_ping"] when get:
            case ["version"] when get:
                return new(DockerRouteKind.Pass);
            case ["containers", "json"] when get:
            case ["events"] when get:
            case ["volumes"] when get:
                return new(DockerRouteKind.Filtered);
            case ["containers", "create"] when method == "POST":
                return new(DockerRouteKind.CreateContainer);
            case ["containers", var id] when method == "DELETE":
                return new(DockerRouteKind.Container, Id(id));
            case ["containers", var id, var action] when get && _containerGets.Contains(action):
                return new(DockerRouteKind.Container, Id(id));
            case ["containers", var id, "archive"] when method == "PUT":
                return new(DockerRouteKind.Container, Id(id));
            case ["containers", var id, "attach"] when method == "POST":
                return new(DockerRouteKind.Container, Id(id), Hijack: true);
            case ["containers", var id, "exec"] when method == "POST":
                return new(DockerRouteKind.CreateExec, Id(id));
            case ["containers", var id, var action] when method == "POST" && _containerPosts.Contains(action):
                return new(DockerRouteKind.Container, Id(id));
            case ["exec", var id, "start"] when method == "POST":
                return new(DockerRouteKind.Exec, Id(id), Hijack: true);
            case ["exec", var id, "resize"] when method == "POST":
                return new(DockerRouteKind.Exec, Id(id));
            case ["exec", var id, "json"] when get:
                return new(DockerRouteKind.Exec, Id(id));
            case ["images", "create"] when method == "POST":
                return query.ContainsKey("fromSrc") || !query.ContainsKey("fromImage")
                    ? Deny("importing an image is refused; only pulls by name are allowed")
                    : new(DockerRouteKind.PullImage);
            case ["images", "json"] when get:
                return new(DockerRouteKind.Pass);
            case ["images", .. var image] when get && image.Length >= 2 && image[^1] == "json":
                return new(DockerRouteKind.Pass);
            case ["distribution", .. var name] when get && name.Length >= 2 && name[^1] == "json":
                return new(DockerRouteKind.PullImage);
            case ["volumes", "create"] when method == "POST":
                return new(DockerRouteKind.CreateVolume);
            case ["volumes", var name] when get || method == "DELETE":
                return new(DockerRouteKind.Volume, Id(name));
            case ["build", ..]:
            case ["commit"]:
            case [_, "prune"]:
            case ["images", "load"]:
            case ["images", .., "push"]:
                return Deny("building, committing, loading, pushing and pruning are refused");
            default:
                return Deny($"{method} {Unversioned(path)} is not allowed through this socket");
        }
    }

    /// <summary>A filter query parameter (<c>filters</c>) with the app's label added to what the app asked for.</summary>
    public static string LabelFilter(string? filters, string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        var node = string.IsNullOrEmpty(filters) ? new JsonObject() : JsonNode.Parse(filters) as JsonObject ?? new JsonObject();
        var label = SpawnedLabel + "=" + appId;
        // The Engine reads a filter as a list or as a map of value to true; both stay, with the app's label added.
        switch (node["label"])
        {
            case JsonArray list:
                list.Add(label);
                break;
            case JsonObject map:
                map[label] = true;
                break;
            default:
                node["label"] = new JsonArray(label);
                break;
        }
        return node.ToJsonString();
    }

    /// <summary>Whether an object the Engine returned (its <c>Config.Labels</c> or <c>Labels</c>) belongs to the app.</summary>
    public static bool Owns(JsonElement inspected, string appId)
    {
        var labels = inspected.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var containerLabels)
            ? containerLabels
            : inspected.TryGetProperty("Labels", out var objectLabels) ? objectLabels : default;
        return labels.ValueKind == JsonValueKind.Object
            && labels.TryGetProperty(SpawnedLabel, out var owner)
            && owner.ValueKind == JsonValueKind.String
            && owner.GetString() == appId;
    }

    /// <summary>
    /// Checks and rewrites the body of <c>POST /containers/create</c>. Returns the new body, or the reason it is refused;
    /// <paramref name="volumes"/> gets the named volumes it mounts, which must be the app's.
    /// </summary>
    public static (string? Body, string? Reason) CreateContainer(string body, DockerProxyApp app, List<string> volumes)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(volumes);
        if (Parse(body) is not JsonObject config)
        {
            return (null, "the body is not a JSON object");
        }
        foreach (var (key, value) in config)
        {
            if (!_configKeys.Contains(key) && !IsEmpty(value))
            {
                return (null, $"{key} is not allowed");
            }
        }
        if (config["Image"] is not JsonValue image || image.GetValueKind() != JsonValueKind.String)
        {
            return (null, "Image is required");
        }
        if (Labels(config, app.AppId) is { } labelProblem)
        {
            return (null, labelProblem);
        }

        var host = config["HostConfig"] as JsonObject ?? [];
        foreach (var (key, value) in host)
        {
            if (!_hostConfigKeys.Contains(key) && !IsEmpty(value))
            {
                return (null, $"HostConfig.{key} is not allowed");
            }
        }
        if (HostConfigProblem(host, app, volumes) is { } problem)
        {
            return (null, problem);
        }
        host["NetworkMode"] = app.Network;
        host["PidsLimit"] = Compose.ComposeRewriter.PidsLimit;
        host["OomScoreAdj"] = Compose.ComposeRewriter.OomScoreAdjust;
        host["SecurityOpt"] = new JsonArray("no-new-privileges:true");
        host["LogConfig"] = new JsonObject
        {
            ["Type"] = "journald",
            ["Config"] = new JsonObject { ["labels"] = SpawnedLabel, ["tag"] = "{{.Name}}" },
        };
        config["HostConfig"] = host;
        config["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = new JsonObject { [app.Network] = new JsonObject() } };
        return (config.ToJsonString(), null);
    }

    /// <summary>Checks the body of <c>POST /containers/{id}/exec</c>: no privileged exec.</summary>
    public static string? CreateExecProblem(string body)
    {
        if (Parse(body) is not JsonObject exec)
        {
            return "the body is not a JSON object";
        }
        return IsEmpty(exec["Privileged"]) ? null : "a privileged exec is not allowed";
    }

    /// <summary>Checks and labels the body of <c>POST /volumes/create</c>: the local driver without options.</summary>
    public static (string? Body, string? Reason) CreateVolume(string body, string appId)
    {
        if (Parse(body) is not JsonObject volume)
        {
            return (null, "the body is not a JSON object");
        }
        foreach (var (key, value) in volume)
        {
            if (key is not ("Name" or "Driver" or "Labels") && !IsEmpty(value))
            {
                return (null, $"{key} is not allowed");
            }
        }
        if (volume["Driver"] is JsonValue driver && driver.ToString() is not ("" or "local"))
        {
            return (null, "only the local driver without options is allowed");
        }
        if (Labels(volume, appId) is { } problem)
        {
            return (null, problem);
        }
        return (volume.ToJsonString(), null);
    }

    /// <summary>The path without a leading version such as <c>/v1.45</c>.</summary>
    public static string Unversioned(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var match = VersionPrefix().Match(path);
        return match.Success ? path[match.Length..] : path;
    }

    private static string? HostConfigProblem(JsonObject host, DockerProxyApp app, List<string> volumes)
    {
        if (host["NetworkMode"] is JsonValue mode && mode.ToString() is not ("" or "default" or "bridge"))
        {
            return "HostConfig.NetworkMode is set by the box";
        }
        if (host["RestartPolicy"] is JsonObject restart && restart["Name"] is JsonValue name && name.ToString() is not ("" or "no" or "on-failure"))
        {
            return "HostConfig.RestartPolicy may only be no or on-failure";
        }
        if (host["IpcMode"] is JsonValue ipc && ipc.ToString() is not ("" or "private" or "none" or "shareable"))
        {
            return "HostConfig.IpcMode may only be private, none or shareable";
        }
        if (host["CgroupnsMode"] is JsonValue cgroupns && cgroupns.ToString() is not ("" or "private"))
        {
            return "HostConfig.CgroupnsMode may only be private";
        }
        if (host["Runtime"] is JsonValue runtime && runtime.ToString() is not ("" or "runc"))
        {
            return "HostConfig.Runtime may only be runc";
        }
        if (host["Isolation"] is JsonValue isolation && isolation.ToString() is not ("" or "default"))
        {
            return "HostConfig.Isolation is not allowed";
        }
        if (host["SecurityOpt"] is JsonArray options && options.Any(option => option?.ToString() is not ("no-new-privileges" or "no-new-privileges:true")))
        {
            return "HostConfig.SecurityOpt is set by the box";
        }
        if (host["Binds"] is JsonArray binds)
        {
            foreach (var bind in binds)
            {
                if (Bind(bind?.ToString() ?? "", app, volumes) is { } problem)
                {
                    return problem;
                }
            }
        }
        if (host["Mounts"] is JsonArray mounts)
        {
            foreach (var mount in mounts)
            {
                if (Mount(mount as JsonObject, app, volumes) is { } problem)
                {
                    return problem;
                }
            }
        }
        return null;
    }

    /// <summary><c>SOURCE:TARGET[:OPTIONS]</c>: a named volume of the app or a folder in its dataset's <c>volumes/</c>.</summary>
    private static string? Bind(string bind, DockerProxyApp app, List<string> volumes)
    {
        var parts = bind.Split(':');
        if (parts.Length is < 2 or > 3 || parts[1].Length == 0 || !parts[1].StartsWith('/'))
        {
            return $"the bind {bind} is not SOURCE:TARGET[:OPTIONS]";
        }
        if (parts.Length == 3 && parts[2].Split(',').Any(option => option is not ("ro" or "rw" or "nocopy")))
        {
            return $"the bind {bind} may only have the options ro, rw and nocopy";
        }
        return Source(parts[0], app, volumes);
    }

    private static string? Mount(JsonObject? mount, DockerProxyApp app, List<string> volumes)
    {
        if (mount is null)
        {
            return "a mount is not an object";
        }
        foreach (var (key, value) in mount)
        {
            if (key is not ("Type" or "Source" or "Target" or "ReadOnly" or "TmpfsOptions" or "VolumeOptions" or "BindOptions" or "Consistency") && !IsEmpty(value))
            {
                return $"Mounts.{key} is not allowed";
            }
        }
        if (mount["BindOptions"] is JsonObject bindOptions && bindOptions.Any(option => !IsEmpty(option.Value)))
        {
            return "Mounts.BindOptions is not allowed";
        }
        if (mount["VolumeOptions"] is JsonObject volumeOptions && volumeOptions.Any(option => option.Key != "NoCopy" && !IsEmpty(option.Value)))
        {
            return "Mounts.VolumeOptions may only have NoCopy";
        }
        var type = mount["Type"]?.ToString();
        var source = mount["Source"]?.ToString() ?? "";
        return type switch
        {
            "tmpfs" => source.Length == 0 ? null : "a tmpfs mount has no source",
            "volume" when source.Length > 0 && !source.Contains('/', StringComparison.Ordinal) => Source(source, app, volumes),
            "bind" when source.StartsWith('/') => Source(source, app, volumes),
            _ => $"the mount type {type} with the source {source} is not allowed",
        };
    }

    /// <summary>A mount's source: a named volume (checked later) or exactly a folder of the app's <c>volumes/</c>.</summary>
    private static string? Source(string source, DockerProxyApp app, List<string> volumes)
    {
        if (!source.StartsWith('/'))
        {
            if (!VolumeName().IsMatch(source))
            {
                return $"the volume name {source} is not valid";
            }
            volumes.Add(source);
            return null;
        }
        var prefix = app.AppDirectory.TrimEnd('/') + "/" + AppStorage.VolumesFolder + "/";
        return source.StartsWith(prefix, StringComparison.Ordinal) && VolumeName().IsMatch(source[prefix.Length..])
            ? null
            : $"the host path {source} is not allowed; only named volumes and folders in the app's volumes are";
    }

    /// <summary>Refuses labels of the box and adds the app's.</summary>
    private static string? Labels(JsonObject target, string appId)
    {
        var labels = target["Labels"] as JsonObject ?? [];
        if (labels.Any(label => label.Key.StartsWith("com.pithyx", StringComparison.Ordinal)))
        {
            return "labels under com.pithyx are reserved";
        }
        labels[SpawnedLabel] = appId;
        target["Labels"] = labels;
        return null;
    }

    /// <summary>A value Docker treats as not set: null, false, 0, an empty string, list or object.</summary>
    public static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject map => map.Count == 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind.String => value.ToString().Length == 0,
            JsonValueKind.Number => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) == 0,
            _ => false,
        },
        _ => false,
    };

    private static JsonNode? Parse(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Id(string id) => Uri.UnescapeDataString(id);

    private static DockerRoute Deny(string reason) => new(DockerRouteKind.Deny, Reason: reason);

    [GeneratedRegex(@"^/v[0-9]+\.[0-9]+(?=/)")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex VolumeName();
}
