using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pithyx.Daemon.Apps.Compose;

/// <summary>Where an administrator mapped a storage request of the manifest.</summary>
/// <param name="HostPath">The folder on the pool.</param>
/// <param name="ReadOnly">The administrator granted less than the requested <c>readWrite</c>.</param>
/// <param name="Gid">The group that may write there (a share gid from 30000 to 39999), added to the services that mount it.</param>
public sealed record StorageMapping(string HostPath, bool ReadOnly, int? Gid);

/// <summary>What the box decided for an app's backend; everything else comes from the manifest.</summary>
/// <param name="Uid">The app's uid from 40000 to 49999, also its gid.</param>
/// <param name="AppDirectory">The app's dataset, <c>/&lt;pool&gt;/.pithyx/apps/&lt;id&gt;</c>.</param>
/// <param name="BridgeName">The Linux bridge of the app's network, <c>px-&lt;n&gt;</c> (D309).</param>
/// <param name="ImageIds">Each image reference of the Compose file and the id (<c>sha256:</c> config digest) the box checked against <c>images.lock.json</c>.</param>
/// <param name="Storage">The mapped storage requests by id; an optional one may be missing.</param>
/// <param name="InternetBridgeName">
/// For an app holding <c>network.internet</c>: the bridge of a second network that is not internal (<c>px-i&lt;n&gt;</c>), which
/// every service on the app's network joins as well; null keeps the backend without a way out.
/// </param>
public sealed record ComposeHost(
    int Uid,
    string AppDirectory,
    string BridgeName,
    IReadOnlyDictionary<string, string> ImageIds,
    IReadOnlyDictionary<string, StorageMapping> Storage,
    string? InternetBridgeName = null);

/// <summary>
/// Turns a Compose file that <see cref="ComposeValidator"/> accepted into the file the box runs (D310), as JSON (which
/// Compose reads as YAML). A pure function: the same input always gives the same bytes.
/// </summary>
public static class ComposeRewriter
{
    /// <summary>The most processes a container may run, against fork bombs.</summary>
    public const int PidsLimit = 1024;

    /// <summary>Makes the kernel's OOM killer pick app containers before the daemon and the system services.</summary>
    public const int OomScoreAdjust = 500;

    /// <summary>The name of the app's internal network inside the file.</summary>
    public const string NetworkKey = "app";

    /// <summary>The name of the network with a way out, for an app holding <c>network.internet</c>.</summary>
    public const string InternetKey = "internet";

    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The Compose project of an app: <c>pithyx-</c> plus the id with dots as underscores (ids have no underscores).</summary>
    public static string ProjectName(string appId) => "pithyx-" + appId.Replace('.', '_');

    public static string Rewrite(ComposeMap document, JsonElement manifest, ComposeHost host)
    {
        var appId = manifest.GetProperty("id").GetString()!;
        var version = manifest.GetProperty("version").GetString()!;
        var backend = manifest.GetProperty("backend");
        var root = backend.TryGetProperty("root", out var rootFlag) && rootFlag.GetBoolean();
        var storage = Storage(manifest, host);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("name", ProjectName(appId));
            writer.WritePropertyName("services");
            writer.WriteStartObject();
            var attached = false;
            foreach (var (name, node) in ((ComposeMap)document.Get("services")!).Entries)
            {
                writer.WritePropertyName(name);
                attached |= Service(writer, name, (ComposeMap)node, appId, version, root, storage, host);
            }
            writer.WriteEndObject();
            if (attached)
            {
                writer.WritePropertyName("networks");
                writer.WriteStartObject();
                Network(writer, NetworkKey, ProjectName(appId), host.BridgeName, appId, isInternal: true);
                if (host.InternetBridgeName is { } internet)
                {
                    Network(writer, InternetKey, ProjectName(appId) + "-internet", internet, appId, isInternal: false);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void Network(Utf8JsonWriter writer, string key, string name, string bridge, string appId, bool isInternal)
    {
        writer.WritePropertyName(key);
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("driver", "bridge");
        writer.WriteBoolean("internal", isInternal);
        writer.WritePropertyName("driver_opts");
        writer.WriteStartObject();
        writer.WriteString("com.docker.network.bridge.name", Escape(bridge));
        writer.WriteEndObject();
        writer.WritePropertyName("labels");
        writer.WriteStartObject();
        writer.WriteString("com.pithyx.app", appId);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private sealed record StorageRequest(bool ReadOnly, StorageMapping? Mapping);

    private static Dictionary<string, StorageRequest> Storage(JsonElement manifest, ComposeHost host)
    {
        var requests = new Dictionary<string, StorageRequest>(StringComparer.Ordinal);
        if (!manifest.TryGetProperty("storage", out var storage))
        {
            return requests;
        }
        foreach (var request in storage.EnumerateArray())
        {
            var id = request.GetProperty("id").GetString()!;
            var mapping = host.Storage.GetValueOrDefault(id);
            var required = request.GetProperty("required").GetBoolean();
            if (mapping is null && required && request.TryGetProperty("services", out _))
            {
                throw new ArgumentException($"The required storage {id} is not mapped.", nameof(host));
            }
            requests[id] = new StorageRequest(request.GetProperty("access").GetString() == "read" || mapping?.ReadOnly == true, mapping);
        }
        return requests;
    }

    /// <summary>Writes one service; returns whether it joins the app's network.</summary>
    private static bool Service(
        Utf8JsonWriter writer,
        string name,
        ComposeMap service,
        string appId,
        string version,
        bool root,
        Dictionary<string, StorageRequest> storage,
        ComposeHost host)
    {
        var groups = new SortedSet<int>();
        writer.WriteStartObject();
        foreach (var (key, value) in service.Entries)
        {
            switch (key)
            {
                case "image":
                    var reference = ((ComposeScalar)value).Value;
                    var id = host.ImageIds.GetValueOrDefault(reference)
                        ?? throw new ArgumentException($"The image {reference} has no checked id.", nameof(host));
                    writer.WriteString("image", id);
                    break;
                case "volumes":
                    writer.WritePropertyName("volumes");
                    Mounts(writer, (ComposeSequence)value, storage, groups, host);
                    break;
                case "labels":
                    break;
                case "cpus":
                    writer.WriteNumber("cpus", double.Parse(((ComposeScalar)value).Value, CultureInfo.InvariantCulture));
                    break;
                default:
                    writer.WritePropertyName(key);
                    Node(writer, value);
                    break;
            }
        }

        writer.WriteString("pull_policy", "never");
        writer.WriteString("user", root ? "0:0" : $"{host.Uid}:{host.Uid}");
        if (groups.Count > 0)
        {
            writer.WritePropertyName("group_add");
            writer.WriteStartArray();
            foreach (var gid in groups)
            {
                writer.WriteStringValue(gid.ToString(CultureInfo.InvariantCulture));
            }
            writer.WriteEndArray();
        }
        var attached = service.Get("network_mode") is null;
        if (attached)
        {
            writer.WritePropertyName("networks");
            writer.WriteStartArray();
            writer.WriteStringValue(NetworkKey);
            if (host.InternetBridgeName is not null)
            {
                writer.WriteStringValue(InternetKey);
            }
            writer.WriteEndArray();
        }
        writer.WriteString("restart", "no");
        writer.WritePropertyName("logging");
        writer.WriteStartObject();
        writer.WriteString("driver", "journald");
        writer.WritePropertyName("options");
        writer.WriteStartObject();
        writer.WriteString("labels", "com.pithyx.app,com.pithyx.service");
        writer.WriteString("tag", "{{.Name}}");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteNumber("pids_limit", PidsLimit);
        writer.WriteNumber("oom_score_adj", OomScoreAdjust);
        writer.WritePropertyName("security_opt");
        writer.WriteStartArray();
        writer.WriteStringValue("no-new-privileges:true");
        writer.WriteEndArray();
        writer.WritePropertyName("labels");
        writer.WriteStartObject();
        foreach (var (label, text) in Labels(service.Get("labels")))
        {
            writer.WriteString(label, text);
        }
        writer.WriteString("com.pithyx.app", appId);
        writer.WriteString("com.pithyx.service", name);
        writer.WriteString("com.pithyx.version", version);
        writer.WriteEndObject();
        writer.WriteEndObject();
        return attached;
    }

    /// <summary>Named volumes become bind mounts of folders the box prepared; tmpfs mounts stay as they are.</summary>
    private static void Mounts(Utf8JsonWriter writer, ComposeSequence mounts, Dictionary<string, StorageRequest> storage, SortedSet<int> groups, ComposeHost host)
    {
        writer.WriteStartArray();
        foreach (var mount in mounts.Items)
        {
            string volume;
            string target;
            bool readOnly;
            if (mount is ComposeMap map)
            {
                if (((ComposeScalar)map.Get("type")!).Value == "tmpfs")
                {
                    Node(writer, map);
                    continue;
                }
                volume = ((ComposeScalar)map.Get("source")!).Value;
                target = ((ComposeScalar)map.Get("target")!).Value;
                readOnly = map.Get("read_only") is ComposeScalar flag && flag.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var parts = ((ComposeScalar)mount).Value.Split(':');
                (volume, target, readOnly) = (parts[0], parts[1], parts.Length > 2 && parts[2] == "ro");
            }

            // An optional storage request that is not mapped is a folder of the app's dataset, so data never lands in the container.
            var source = $"{host.AppDirectory}/volumes/{volume}";
            if (storage.TryGetValue(volume, out var request) && request.Mapping is not null)
            {
                source = request.Mapping.HostPath;
                readOnly |= request.ReadOnly;
                if (request.Mapping.Gid is { } gid)
                {
                    groups.Add(gid);
                }
            }
            else if (request?.ReadOnly == true)
            {
                readOnly = true;
            }
            writer.WriteStartObject();
            writer.WriteString("type", "bind");
            writer.WriteString("source", Escape(source));
            writer.WriteString("target", target);
            writer.WriteBoolean("read_only", readOnly);
            writer.WritePropertyName("bind");
            writer.WriteStartObject();
            writer.WriteBoolean("create_host_path", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>The labels of the file as a mapping, in source order; a label without a value is empty.</summary>
    private static IEnumerable<(string Name, string Value)> Labels(ComposeNode? labels)
    {
        switch (labels)
        {
            case ComposeMap map:
                foreach (var (name, value) in map.Entries)
                {
                    var scalar = (ComposeScalar)value;
                    yield return (name, scalar.Kind switch
                    {
                        ComposeScalarKind.Null => "",
                        ComposeScalarKind.Bool => scalar.Value.ToLowerInvariant(),
                        _ => scalar.Value,
                    });
                }
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    var text = ((ComposeScalar)item).Value;
                    var separator = text.IndexOf('=', StringComparison.Ordinal);
                    yield return separator == -1 ? (text, "") : (text[..separator], text[(separator + 1)..]);
                }
                break;
        }
    }

    /// <summary>A value of the file as Compose would read it: plain booleans, integers and nulls keep their type.</summary>
    private static void Node(Utf8JsonWriter writer, ComposeNode node)
    {
        switch (node)
        {
            case ComposeMap map:
                writer.WriteStartObject();
                foreach (var (key, value) in map.Entries)
                {
                    writer.WritePropertyName(key);
                    Node(writer, value);
                }
                writer.WriteEndObject();
                break;
            case ComposeSequence sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Items)
                {
                    Node(writer, item);
                }
                writer.WriteEndArray();
                break;
            case ComposeScalar scalar:
                switch (scalar.Kind)
                {
                    case ComposeScalarKind.Null:
                        writer.WriteNullValue();
                        break;
                    case ComposeScalarKind.Bool:
                        writer.WriteBooleanValue(scalar.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
                        break;
                    case ComposeScalarKind.WholeNumber:
                        writer.WriteNumberValue(long.Parse(scalar.Value, CultureInfo.InvariantCulture));
                        break;
                    default:
                        writer.WriteStringValue(scalar.Value);
                        break;
                }
                break;
        }
    }

    /// <summary>Text the box adds itself, such as a folder name with a <c>$</c>, must not reach Compose's interpolation.</summary>
    private static string Escape(string text) => text.Replace("$", "$$", StringComparison.Ordinal);
}
