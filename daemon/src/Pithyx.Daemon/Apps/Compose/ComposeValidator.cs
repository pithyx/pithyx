using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Compose;

/// <summary>The outcome of <see cref="ComposeValidator.Validate"/>.</summary>
/// <param name="Issues">Empty when the Compose file is valid for its manifest.</param>
/// <param name="Document">The parsed file when it is valid, the input of <see cref="ComposeRewriter"/>.</param>
public sealed record ComposeValidationResult(IReadOnlyList<ValidationIssue> Issues, ComposeMap? Document)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Checks a backend's <c>compose.yaml</c> against the allowlist <c>sdk/schema/compose.v1.rules.json</c> (embedded at
/// build) and against the manifest it belongs to (D310). <c>@pithyx/cli</c> runs the same checks on the same rules; the
/// corpus in <c>sdk/schema/fixtures/compose</c> holds both to the same verdicts and codes.
/// </summary>
public static partial class ComposeValidator
{
    private static readonly Lazy<ComposeRules> _rules = new(ComposeRules.Load);

    internal static ComposeRules Rules => _rules.Value;

    /// <summary>
    /// Validates the bytes of a Compose file for a manifest that <see cref="ManifestValidator"/> accepted. With
    /// <paramref name="dev"/> the file is the source form <c>backend/compose.dev.yaml</c> of <c>pithyx build</c>, where a
    /// service has <c>build</c> instead of <c>image</c> (<c>dev</c> in the rules); the box itself never reads that form.
    /// </summary>
    public static ComposeValidationResult Validate(ReadOnlySpan<byte> utf8, JsonElement manifest, bool dev = false)
    {
        var rules = _rules.Value;
        var (root, yamlIssues) = ComposeYaml.Read(utf8, (int)rules.Limit("fileBytes"), (int)rules.Limit("depth"));
        if (root is null)
        {
            return new ComposeValidationResult(yamlIssues, null);
        }
        var issues = new Checker(rules, manifest, dev).Check(root);
        return new ComposeValidationResult(issues, issues.Count == 0 ? (ComposeMap)root : null);
    }

    /// <summary>The source form next to a manifest's <c>backend.compose</c>: <c>backend/compose.yaml</c> gives <c>backend/compose.dev.yaml</c>.</summary>
    public static string DevComposePath(string compose)
    {
        var dot = compose.LastIndexOf('.');
        return $"{compose[..dot]}.dev{compose[dot..]}";
    }

    private sealed record Reference(string Name, string Path);

    private sealed record Mount(string Volume, string Path, bool ReadOnly);

    /// <summary>A mount as written: a named volume, or a tmpfs when <paramref name="Volume"/> is null.</summary>
    private sealed record ParsedMount(string Target, string? Volume, bool ReadOnly);

    private sealed class ServiceFacts(string name)
    {
        public string Name { get; } = name;

        public List<Mount> Mounts { get; } = [];

        public List<Reference> DependsOn { get; } = [];
    }

    private sealed class Checker(ComposeRules rules, JsonElement manifest, bool dev)
    {
        private readonly IReadOnlyDictionary<string, KeyRule> _serviceRules = dev ? rules.DevService : rules.Service;
        private readonly List<ValidationIssue> _issues = [];
        private readonly List<Reference> _rights = [];
        private readonly HashSet<string> _declaredVolumes = new(StringComparer.Ordinal);

        public List<ValidationIssue> Check(ComposeNode root)
        {
            if (root is not ComposeMap map)
            {
                Invalid("", "type");
                return _issues;
            }
            foreach (var (key, _) in map.Entries)
            {
                if (!rules.Top.ContainsKey(key))
                {
                    Unsupported($"/{ComposeYaml.EscapePointer(key)}", key);
                }
            }
            if (map.Get("volumes") is { } volumes)
            {
                VolumeDeclarations(volumes);
            }
            if (map.Get("services") is not { } services)
            {
                Invalid("/services", "required");
                return _issues;
            }
            if (Services(services) is { } facts)
            {
                CrossChecks(facts);
            }
            return _issues;
        }

        private void Add(string code, params (string Name, object? Value)[] parameters) => _issues.Add(new ValidationIssue(code, parameters));

        private void Invalid(string path, string reason) => Add(ErrorCodes.AppComposeValueInvalid, ("path", path), ("reason", reason));

        private void Unsupported(string path, string key) => Add(ErrorCodes.AppComposeKeyUnsupported, ("path", path), ("key", key));

        private void Mismatch(string path, string storage, string reason) =>
            Add(ErrorCodes.AppComposeStorageMismatch, ("path", path), ("storage", storage), ("reason", reason));

        private static string Child(string path, string key) => $"{path}/{ComposeYaml.EscapePointer(key)}";

        private void VolumeDeclarations(ComposeNode node)
        {
            if (node is not ComposeMap map)
            {
                Invalid("/volumes", "type");
                return;
            }
            if (map.Entries.Count > rules.Limit("volumes"))
            {
                Invalid("/volumes", "range");
                return;
            }
            foreach (var (key, value) in map.Entries)
            {
                var path = Child("/volumes", key);
                if (!rules.Matches("volumeName", key))
                {
                    Invalid(path, "format");
                    continue;
                }
                _declaredVolumes.Add(key);
                if (value is ComposeMap options)
                {
                    foreach (var (option, _) in options.Entries)
                    {
                        Unsupported(Child(path, option), option);
                    }
                }
                else if (value is not ComposeScalar { Kind: ComposeScalarKind.Null })
                {
                    Invalid(path, "type");
                }
            }
        }

        private List<ServiceFacts>? Services(ComposeNode node)
        {
            if (node is not ComposeMap map)
            {
                Invalid("/services", "type");
                return null;
            }
            if (map.Entries.Count == 0 || map.Entries.Count > rules.Limit("services"))
            {
                Invalid("/services", "range");
                return null;
            }
            var all = new List<ServiceFacts>();
            foreach (var (key, value) in map.Entries)
            {
                var path = Child("/services", key);
                if (!rules.Matches("serviceName", key))
                {
                    Invalid(path, "format");
                }
                var facts = new ServiceFacts(key);
                if (value is ComposeMap service)
                {
                    Service(service, path, facts);
                }
                else
                {
                    Invalid(path, "type");
                }
                all.Add(facts);
            }
            return all;
        }

        private void Service(ComposeMap node, string path, ServiceFacts facts)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in node.Entries)
            {
                var keyPath = Child(path, key);
                if (!_serviceRules.TryGetValue(key, out var rule))
                {
                    Unsupported(keyPath, key);
                    continue;
                }
                switch (rule.Type)
                {
                    case "image":
                        Image(value, keyPath);
                        break;
                    case "build":
                        Build(value, keyPath);
                        break;
                    case "command":
                        Command(value, keyPath);
                        break;
                    case "environment":
                        KeyValues(value, keyPath, environment: true);
                        break;
                    case "labels":
                        KeyValues(value, keyPath, environment: false);
                        break;
                    case "mounts":
                        Mounts(value, keyPath, facts, targets);
                        break;
                    case "tmpfs":
                        Tmpfs(value, keyPath, targets);
                        break;
                    case "dependsOn":
                        DependsOn(value, keyPath, facts);
                        break;
                    case "ports":
                        Ports(value, keyPath);
                        break;
                    case "devices":
                        Devices(value, keyPath, rule.Right ?? "");
                        break;
                    case "capabilities":
                        Capabilities(value, keyPath, rule.Right);
                        break;
                    default:
                        if (Value(value, keyPath, rule) is not null && rule.Right is not null)
                        {
                            _rights.Add(new Reference(rule.Right, keyPath));
                        }
                        break;
                }
            }
            if (dev && node.Get("build") is not null)
            {
                if (node.Get("image") is not null)
                {
                    Invalid(Child(path, "build"), "image_and_build");
                }
            }
            else
            {
                Required(node, path, _serviceRules);
            }
        }

        /// <summary><c>build</c> of the source form: a folder next to the file, or <c>context</c> with options.</summary>
        private void Build(ComposeNode node, string path)
        {
            if (node is ComposeMap)
            {
                Object(node, path, rules.DevBuildKeys);
            }
            else
            {
                Value(node, path, rules.DevBuildKeys["context"]);
            }
        }

        private bool Required(ComposeMap node, string path, IReadOnlyDictionary<string, KeyRule> keys)
        {
            var complete = true;
            foreach (var (key, rule) in keys)
            {
                if (rule.Required && node.Get(key) is null)
                {
                    Invalid(Child(path, key), "required");
                    complete = false;
                }
            }
            return complete;
        }

        /// <summary>Checks a value of a plain rule type; returns the value when it is valid, null otherwise.</summary>
        private object? Value(ComposeNode node, string path, KeyRule rule)
        {
            switch (rule.Type)
            {
                case "string":
                    {
                        var text = Text(node, path);
                        if (text is null)
                        {
                            return null;
                        }
                        if (rule.Pattern is not null && !Matches(rule.Pattern, text))
                        {
                            Invalid(path, "format");
                            return null;
                        }
                        return text;
                    }
                case "bool":
                    if (node is not ComposeScalar { Kind: ComposeScalarKind.Bool } flag)
                    {
                        Invalid(path, "type");
                        return null;
                    }
                    return flag.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                case "int":
                    {
                        if (node is not ComposeScalar { Kind: ComposeScalarKind.WholeNumber } scalar)
                        {
                            Invalid(path, "type");
                            return null;
                        }
                        var number = double.Parse(scalar.Value, CultureInfo.InvariantCulture);
                        if (number < rule.Min!.Value.GetDouble() || number > rule.Max!.Value.GetDouble())
                        {
                            Invalid(path, "range");
                            return null;
                        }
                        return number;
                    }
                case "enum":
                    {
                        var text = Text(node, path);
                        if (text is null)
                        {
                            return null;
                        }
                        if (rule.Values is null || !rule.Values.Contains(text))
                        {
                            Invalid(path, "format");
                            return null;
                        }
                        return text;
                    }
                case "duration":
                    return Duration(node, path, rule);
                case "byteSize":
                    return ByteSize(node, path, rule);
                case "cpus":
                    return Cpus(node, path, rule);
                case "object":
                    return Object(node, path, rule.Keys ?? new Dictionary<string, KeyRule>());
                case "healthTest":
                    return HealthTest(node, path);
                case "buildArgs":
                    KeyValues(node, path, environment: true);
                    return node;
                default:
                    throw new InvalidOperationException($"Unknown rule type {rule.Type}.");
            }
        }

        private bool Matches(string pattern, string text) =>
            rules.Matches(pattern, text) && (pattern != "containerPath" || HasNoDotSegments(text));

        private ComposeMap? Object(ComposeNode node, string path, IReadOnlyDictionary<string, KeyRule> keys)
        {
            if (node is not ComposeMap map)
            {
                Invalid(path, "type");
                return null;
            }
            foreach (var (key, value) in map.Entries)
            {
                if (keys.TryGetValue(key, out var rule))
                {
                    Value(value, Child(path, key), rule);
                }
                else
                {
                    Unsupported(Child(path, key), key);
                }
            }
            Required(map, path, keys);
            return map;
        }

        /// <summary>A string value: quoted, or plain and not null, a boolean or a number; <c>$$</c> stands for <c>$</c>.</summary>
        private string? Text(ComposeNode node, string path)
        {
            if (node is not ComposeScalar { Kind: ComposeScalarKind.Text } scalar)
            {
                Invalid(path, "type");
                return null;
            }
            return CheckedText(scalar.Value, path);
        }

        private string? CheckedText(string value, string path)
        {
            if (value.Any(IsControl))
            {
                Invalid(path, "format");
                return null;
            }
            if (value.Replace("$$", "", StringComparison.Ordinal).Contains('$', StringComparison.Ordinal))
            {
                Invalid(path, "interpolation");
                return null;
            }
            return value.Replace("$$", "$", StringComparison.Ordinal);
        }

        // Control characters other than tab and line feed, C1 controls and the Unicode line separators.
        private static bool IsControl(char character) =>
            character is (>= '\u0000' and <= '\u0008') or (>= '\u000b' and <= '\u001f') or (>= '\u007f' and <= '\u009f') or '\u2028' or '\u2029';

        /// <summary>
        /// A value of an environment variable or label: a string, an integer or a boolean. Returns false when it is
        /// invalid; <paramref name="text"/> is null when the value is empty.
        /// </summary>
        private bool KeyValue(ComposeNode node, string path, out string? text)
        {
            text = null;
            if (node is not ComposeScalar scalar)
            {
                Invalid(path, "type");
                return false;
            }
            switch (scalar.Kind)
            {
                case ComposeScalarKind.Null:
                    return true;
                case ComposeScalarKind.Bool:
                    text = scalar.Value.ToLowerInvariant();
                    return true;
                case ComposeScalarKind.WholeNumber:
                    text = scalar.Value;
                    return true;
                case ComposeScalarKind.OtherNumber:
                    Invalid(path, "type");
                    return false;
                default:
                    text = CheckedText(scalar.Value, path);
                    return text is not null;
            }
        }

        private IReadOnlyList<ComposeNode>? List(ComposeNode node, string path)
        {
            if (node is not ComposeSequence sequence)
            {
                Invalid(path, "type");
                return null;
            }
            if (sequence.Items.Count > rules.Limit("entries"))
            {
                Invalid(path, "range");
                return null;
            }
            return sequence.Items;
        }

        private void Image(ComposeNode node, string path)
        {
            var text = Text(node, path);
            if (text is null)
            {
                return;
            }
            if (!rules.Matches("imageDigest", text))
            {
                Invalid(path, "unpinned");
            }
            else if (text.Length > rules.Limit("imageChars") || !rules.Matches("image", text))
            {
                Invalid(path, "format");
            }
        }

        private void Command(ComposeNode node, string path)
        {
            if (node is ComposeScalar)
            {
                Text(node, path);
                return;
            }
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                Text(items[index], $"{path}/{index}");
            }
        }

        private void KeyValues(ComposeNode node, string path, bool environment)
        {
            var pattern = environment ? "envName" : "labelKey";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Check(string key, string? value, string itemPath)
            {
                if (!rules.Matches(pattern, key))
                {
                    Invalid(itemPath, "format");
                }
                else if (!environment && IsReservedLabel(key))
                {
                    Invalid(itemPath, "reserved");
                }
                else if (environment && value is null)
                {
                    Invalid(itemPath, "no_value");
                }
                else if (seen.Contains(key))
                {
                    Invalid(itemPath, "duplicate");
                }
                seen.Add(key);
            }

            if (node is ComposeMap map)
            {
                if (map.Entries.Count > rules.Limit("entries"))
                {
                    Invalid(path, "range");
                    return;
                }
                foreach (var (key, value) in map.Entries)
                {
                    var itemPath = Child(path, key);
                    if (KeyValue(value, itemPath, out var text))
                    {
                        Check(key, text, itemPath);
                    }
                }
                return;
            }
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                var itemPath = $"{path}/{index}";
                if (Text(items[index], itemPath) is not { } text)
                {
                    continue;
                }
                var separator = text.IndexOf('=', StringComparison.Ordinal);
                Check(separator == -1 ? text : text[..separator], separator == -1 ? null : text[(separator + 1)..], itemPath);
            }
        }

        private void Mounts(ComposeNode node, string path, ServiceFacts facts, HashSet<string> targets)
        {
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                var itemPath = $"{path}/{index}";
                var mount = items[index] is ComposeMap map ? LongMount(map, itemPath) : ShortMount(items[index], itemPath);
                if (mount is null)
                {
                    continue;
                }
                if (!targets.Add(mount.Target))
                {
                    Invalid(itemPath, "duplicate");
                    continue;
                }
                if (mount.Volume is not null)
                {
                    facts.Mounts.Add(new Mount(mount.Volume, itemPath, mount.ReadOnly));
                }
            }
        }

        /// <summary><c>VOLUME:TARGET[:ro|rw]</c>; returns the mount when it is valid.</summary>
        private ParsedMount? ShortMount(ComposeNode node, string path)
        {
            var text = Text(node, path);
            if (text is null)
            {
                return null;
            }
            var parts = text.Split(':');
            if (parts.Length == 1)
            {
                Invalid(path, parts[0].StartsWith('/') ? "anonymous" : "format");
                return null;
            }
            var mode = parts.Length > 2 ? parts[2] : null;
            if (parts.Length > 3 || (mode is not null && mode is not ("ro" or "rw")))
            {
                Invalid(path, "format");
                return null;
            }
            return MountSource(parts[0], parts[1], mode == "ro", path);
        }

        private ParsedMount? MountSource(string source, string target, bool readOnly, string path)
        {
            if (IsHostPath(source))
            {
                HostPath(source, path);
                return null;
            }
            if (!rules.Matches("volumeName", source))
            {
                Invalid(path, "format");
                return null;
            }
            if (!Target(target, path))
            {
                return null;
            }
            if (!_declaredVolumes.Contains(source))
            {
                Add(ErrorCodes.AppComposeReferenceUnknown, ("path", path), ("name", source));
                return null;
            }
            return new ParsedMount(target, source, readOnly);
        }

        private void HostPath(string source, string path)
        {
            if (rules.DockerSockets.Contains(source))
            {
                // The raw socket is the same mount with the other right (D387); declaring both leaves one unused.
                _rights.Add(new Reference(DeclaresRawSocket() ? rules.DockerSocketRawRight : rules.DockerSocketRight, path));
            }
            else
            {
                Invalid(path, "host_path");
            }
        }

        private bool DeclaresRawSocket() =>
            manifest.TryGetProperty("backend", out var backend)
            && backend.TryGetProperty("elevated", out var elevated)
            && elevated.EnumerateArray().Any(right => right.GetString() == rules.DockerSocketRawRight);

        private bool Target(string target, string path)
        {
            if (!Matches("containerPath", target))
            {
                Invalid(path, "format");
                return false;
            }
            if (IsReservedTarget(target))
            {
                Invalid(path, "reserved");
                return false;
            }
            return true;
        }

        private ParsedMount? LongMount(ComposeMap node, string path)
        {
            var valid = true;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in node.Entries)
            {
                var keyPath = Child(path, key);
                if (!rules.MountKeys.TryGetValue(key, out var rule))
                {
                    Unsupported(keyPath, key);
                    valid = false;
                    continue;
                }
                var checkedValue = Value(value, keyPath, rule);
                valid &= checkedValue is not null;
                values[key] = checkedValue;
            }
            valid &= Required(node, path, rules.MountKeys);
            if (!valid)
            {
                return null;
            }
            var type = (string)values["type"]!;
            var target = (string)values["target"]!;
            var source = values.GetValueOrDefault("source") as string;
            if (type == "bind")
            {
                HostPath(source ?? "", path);
                return null;
            }
            if (type == "tmpfs")
            {
                if (source is not null)
                {
                    Unsupported(Child(path, "source"), "source");
                    return null;
                }
                return Target(target, path) ? new ParsedMount(target, null, false) : null;
            }
            if (node.Get("tmpfs") is not null)
            {
                Unsupported(Child(path, "tmpfs"), "tmpfs");
                return null;
            }
            if (source is null)
            {
                Invalid(path, "anonymous");
                return null;
            }
            return MountSource(source, target, values.GetValueOrDefault("read_only") is true, path);
        }

        private void Tmpfs(ComposeNode node, string path, HashSet<string> targets)
        {
            List<(ComposeNode Node, string Path)>? items;
            if (node is ComposeScalar)
            {
                items = [(node, path)];
            }
            else
            {
                items = List(node, path)?.Select((item, index) => (item, $"{path}/{index}")).ToList();
            }
            foreach (var (item, itemPath) in items ?? [])
            {
                var text = Text(item, itemPath);
                if (text is null)
                {
                    continue;
                }
                var separator = text.IndexOf(':', StringComparison.Ordinal);
                var target = separator == -1 ? text : text[..separator];
                var options = separator == -1 ? [] : text[(separator + 1)..].Split(',');
                if (!Target(target, itemPath) || !TmpfsOptions(options, itemPath))
                {
                    continue;
                }
                if (!targets.Add(target))
                {
                    Invalid(itemPath, "duplicate");
                }
            }
        }

        private bool TmpfsOptions(string[] options, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                var separator = option.IndexOf('=', StringComparison.Ordinal);
                if (separator == -1 || !seen.Add(option[..separator]))
                {
                    Invalid(path, "format");
                    return false;
                }
                var (name, value) = (option[..separator], option[(separator + 1)..]);
                if (name == "mode" && rules.Matches("tmpfsMode", value))
                {
                    continue;
                }
                if (name != "size" || !rules.Matches("byteSize", value))
                {
                    Invalid(path, "format");
                    return false;
                }
                var bytes = ByteCount(value);
                if (bytes == 0 || bytes > rules.Limit("tmpfsSizeBytes"))
                {
                    Invalid(path, "range");
                    return false;
                }
            }
            return true;
        }

        private void DependsOn(ComposeNode node, string path, ServiceFacts facts)
        {
            if (node is ComposeMap map)
            {
                if (map.Entries.Count > rules.Limit("entries"))
                {
                    Invalid(path, "range");
                    return;
                }
                foreach (var (key, value) in map.Entries)
                {
                    var itemPath = Child(path, key);
                    Object(value, itemPath, rules.DependsOnKeys);
                    facts.DependsOn.Add(new Reference(key, itemPath));
                }
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                var itemPath = $"{path}/{index}";
                if (Text(items[index], itemPath) is not { } name)
                {
                    continue;
                }
                if (!seen.Add(name))
                {
                    Invalid(itemPath, "duplicate");
                    continue;
                }
                facts.DependsOn.Add(new Reference(name, itemPath));
            }
        }

        private object? HealthTest(ComposeNode node, string path)
        {
            if (node is ComposeScalar)
            {
                return Text(node, path);
            }
            var items = List(node, path);
            if (items is null)
            {
                return null;
            }
            var texts = items.Select((item, index) => Text(item, $"{path}/{index}")).ToList();
            if (texts.Any(text => text is null))
            {
                return null;
            }
            var first = texts.Count > 0 ? texts[0] : null;
            var valid = (first == "NONE" && texts.Count == 1)
                || (first == "CMD" && texts.Count >= 2)
                || (first == "CMD-SHELL" && texts.Count == 2);
            if (!valid)
            {
                Invalid(path, "format");
                return null;
            }
            return texts;
        }

        private double? Duration(ComposeNode node, string path, KeyRule rule)
        {
            var text = Text(node, path);
            if (text is null)
            {
                return null;
            }
            if (!rules.Matches("duration", text))
            {
                Invalid(path, "format");
                return null;
            }
            var value = Microseconds(text);
            if (value < Microseconds(rule.Min!.Value.GetString()!) || value > Microseconds(rule.Max!.Value.GetString()!))
            {
                Invalid(path, "range");
                return null;
            }
            return value;
        }

        private double? ByteSize(ComposeNode node, string path, KeyRule rule)
        {
            string? text;
            if (node is ComposeScalar { Kind: ComposeScalarKind.WholeNumber } number)
            {
                text = number.Value;
            }
            else if ((text = Text(node, path)) is null)
            {
                return null;
            }
            if (!rules.Matches(rule.Pattern ?? "byteSize", text))
            {
                Invalid(path, "format");
                return null;
            }
            var bytes = ByteCount(text);
            var min = rule.Min is { } limit ? rules.Limit(limit.GetString()!) : 1;
            if (bytes < min || bytes > rules.Limit(rule.Max!.Value.GetString()!))
            {
                Invalid(path, "range");
                return null;
            }
            return bytes;
        }

        /// <summary>A CPU limit in cores: a plain decimal or a string, above 0.</summary>
        private double? Cpus(ComposeNode node, string path, KeyRule rule)
        {
            if (node is not ComposeScalar { Kind: ComposeScalarKind.WholeNumber or ComposeScalarKind.OtherNumber or ComposeScalarKind.Text } scalar)
            {
                Invalid(path, "type");
                return null;
            }
            if (!rules.Matches("cpus", scalar.Value))
            {
                Invalid(path, "format");
                return null;
            }
            var cores = double.Parse(scalar.Value, CultureInfo.InvariantCulture);
            if (cores <= 0 || cores > rules.Limit(rule.Max!.Value.GetString()!))
            {
                Invalid(path, "range");
                return null;
            }
            return cores;
        }

        private void Ports(ComposeNode node, string path)
        {
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                var itemPath = $"{path}/{index}";
                string? text;
                if (items[index] is ComposeScalar { Kind: ComposeScalarKind.WholeNumber } number)
                {
                    text = number.Value;
                }
                else if ((text = Text(items[index], itemPath)) is null)
                {
                    continue;
                }
                if (!rules.Matches("port", text))
                {
                    Invalid(itemPath, "format");
                    continue;
                }
                var range = text.Split('/')[0].Split('-').Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToList();
                var (low, high) = (range[0], range[^1]);
                if (low < 1 || high > 65535 || low > high)
                {
                    Invalid(itemPath, "range");
                }
            }
        }

        private void Devices(ComposeNode node, string path, string right)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                var itemPath = $"{path}/{index}";
                if (Text(items[index], itemPath) is not { } text)
                {
                    continue;
                }
                var parts = text.Split(':');
                var host = parts[0];
                var container = parts.Length > 1 ? parts[1] : host;
                var permissions = parts.Length > 2 ? parts[2] : "rw";
                if (parts.Length > 3
                    || !rules.Matches("devicePath", host)
                    || !rules.Matches("devicePath", container)
                    || !rules.Matches("devicePermissions", permissions))
                {
                    Invalid(itemPath, "format");
                    continue;
                }
                if (!seen.Add(host))
                {
                    Invalid(itemPath, "duplicate");
                    continue;
                }
                _rights.Add(new Reference(right + host, itemPath));
            }
        }

        private void Capabilities(ComposeNode node, string path, string? right)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = List(node, path);
            for (var index = 0; items is not null && index < items.Count; index++)
            {
                var itemPath = $"{path}/{index}";
                if (Text(items[index], itemPath) is not { } text)
                {
                    continue;
                }
                if (!rules.Matches("capability", text))
                {
                    Invalid(itemPath, "format");
                    continue;
                }
                if (!seen.Add(text))
                {
                    Invalid(itemPath, "duplicate");
                    continue;
                }
                if (right is not null)
                {
                    _rights.Add(new Reference(right + text, itemPath));
                }
            }
        }

        private void CrossChecks(List<ServiceFacts> services)
        {
            var names = services.Select(service => service.Name).ToHashSet(StringComparer.Ordinal);
            var backend = manifest.TryGetProperty("backend", out var value) ? value : (JsonElement?)null;

            foreach (var service in services)
            {
                foreach (var reference in service.DependsOn.Where(reference => !names.Contains(reference.Name)))
                {
                    Add(ErrorCodes.AppComposeReferenceUnknown, ("path", reference.Path), ("name", reference.Name));
                }
            }
            var edges = services.ToDictionary(service => service.Name, service => service.DependsOn.Select(reference => reference.Name).ToList(), StringComparer.Ordinal);
            foreach (var service in services.Where(service => Reaches(edges, service.Name)))
            {
                Invalid(Child(Child("/services", service.Name), "depends_on"), "cycle");
            }

            if (backend?.TryGetProperty("endpoints", out var endpoints) == true)
            {
                foreach (var endpoint in endpoints.EnumerateObject())
                {
                    var service = endpoint.Value.GetProperty("service").GetString()!;
                    if (!names.Contains(service))
                    {
                        Add(ErrorCodes.AppComposeReferenceUnknown, ("path", Child(Child("/backend/endpoints", endpoint.Name), "service")), ("name", service));
                    }
                }
            }
            if (backend?.TryGetProperty("network", out var network) == true)
            {
                var index = 0;
                foreach (var listed in network.GetProperty("services").EnumerateArray())
                {
                    var service = listed.GetString()!;
                    if (!names.Contains(service))
                    {
                        Add(ErrorCodes.AppComposeReferenceUnknown, ("path", Child("/backend/network/services", index.ToString(CultureInfo.InvariantCulture))), ("name", service));
                    }
                    index++;
                }
            }

            if (manifest.TryGetProperty("storage", out var storage))
            {
                var index = 0;
                foreach (var request in storage.EnumerateArray())
                {
                    StorageChecks(request, index++, services);
                }
            }

            var declared = backend?.TryGetProperty("elevated", out var elevated) == true
                ? elevated.EnumerateArray().Select(right => right.GetString()!).ToList()
                : [];
            foreach (var (name, path) in _rights)
            {
                if (!ManifestValidator.IsSupportedElevatedRight(name))
                {
                    Add(ErrorCodes.AppElevatedUnsupported, ("right", name), ("path", path));
                }
                else if (!declared.Contains(name))
                {
                    Add(ErrorCodes.AppComposeRightMissing, ("path", path), ("right", name));
                }
            }
            for (var index = 0; index < declared.Count; index++)
            {
                var right = declared[index];
                if (ManifestValidator.IsSupportedElevatedRight(right) && !_rights.Exists(used => used.Name == right))
                {
                    Add(ErrorCodes.AppComposeRightUnused, ("path", $"/backend/elevated/{index}"), ("right", right));
                }
            }
        }

        private void StorageChecks(JsonElement request, int index, List<ServiceFacts> services)
        {
            var id = request.GetProperty("id").GetString()!;
            var listed = request.TryGetProperty("services", out var names)
                ? names.EnumerateArray().Select(name => name.GetString()!).ToList()
                : [];
            for (var serviceIndex = 0; serviceIndex < listed.Count; serviceIndex++)
            {
                var path = $"/storage/{index}/services/{serviceIndex}";
                var service = services.Find(candidate => candidate.Name == listed[serviceIndex]);
                if (service is null)
                {
                    Add(ErrorCodes.AppComposeReferenceUnknown, ("path", path), ("name", listed[serviceIndex]));
                }
                else if (!service.Mounts.Exists(mount => mount.Volume == id))
                {
                    Mismatch(path, id, "not_mounted");
                }
            }
            var readOnly = request.GetProperty("access").GetString() == "read";
            foreach (var service in services)
            {
                foreach (var mount in service.Mounts.Where(mount => mount.Volume == id))
                {
                    if (!listed.Contains(service.Name))
                    {
                        Mismatch(mount.Path, id, "not_listed");
                    }
                    else if (readOnly && !mount.ReadOnly)
                    {
                        Mismatch(mount.Path, id, "read_only");
                    }
                }
            }
        }

        private bool IsReservedTarget(string target) =>
            target == "/" || rules.ReservedTargets.Any(reserved => target == reserved || target.StartsWith(reserved + "/", StringComparison.Ordinal));

        private bool IsReservedLabel(string key)
        {
            var lower = key.ToLowerInvariant();
            return rules.ReservedLabelPrefixes.Any(prefix => lower == prefix || lower.StartsWith(prefix + ".", StringComparison.Ordinal));
        }
    }

    private static bool HasNoDotSegments(string path) => path.Split('/').All(segment => segment is not ("." or ".."));

    private static bool IsHostPath(string source) =>
        source.Length == 0 || source.StartsWith('.') || source.StartsWith('~') || source.Contains('/', StringComparison.Ordinal) || source.Contains('\\', StringComparison.Ordinal);

    private static double Microseconds(string duration)
    {
        double total = 0;
        foreach (Match part in DurationPart().Matches(duration))
        {
            var unit = part.Groups[2].Value switch
            {
                "h" => 3_600_000_000d,
                "m" => 60_000_000d,
                "s" => 1_000_000d,
                "ms" => 1_000d,
                _ => 1d,
            };
            total += double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture) * unit;
        }
        return total;
    }

    internal static double ByteCount(string size)
    {
        var digits = size.TakeWhile(char.IsAsciiDigit).Count();
        var unit = digits < size.Length ? char.ToLowerInvariant(size[digits]) : 'b';
        var factor = unit switch
        {
            'k' => 1024d,
            'm' => 1024d * 1024,
            'g' => 1024d * 1024 * 1024,
            _ => 1d,
        };
        return double.Parse(size[..digits], CultureInfo.InvariantCulture) * factor;
    }

    private static bool Reaches(Dictionary<string, List<string>> edges, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(edges.GetValueOrDefault(start) ?? []);
        while (queue.TryDequeue(out var next))
        {
            if (next == start)
            {
                return true;
            }
            if (edges.TryGetValue(next, out var following) && seen.Add(next))
            {
                foreach (var name in following)
                {
                    queue.Enqueue(name);
                }
            }
        }
        return false;
    }

    [GeneratedRegex("([0-9]+)(ms|us|h|m|s)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();
}

/// <summary>One key of the allowlist: the check both validators implement (<c>type</c>) and its parameters.</summary>
internal sealed record KeyRule(
    string Type,
    bool Required,
    string? Pattern,
    IReadOnlyList<string>? Values,
    JsonElement? Min,
    JsonElement? Max,
    string? Right,
    IReadOnlyDictionary<string, KeyRule>? Keys);

/// <summary>A device class of the rules (D387): which character devices in its folder, or which link targets below it, it names.</summary>
/// <param name="Nodes">Names of character devices directly in the folder.</param>
/// <param name="Links">Targets of the links below the folder, taken instead of the links.</param>
internal sealed record DeviceClass(Regex? Nodes, Regex? Links);

/// <summary><c>sdk/schema/compose.v1.rules.json</c> as read by the daemon.</summary>
internal sealed class ComposeRules
{
    private readonly Dictionary<string, double> _limits;
    private readonly Dictionary<string, Regex> _patterns;

    private ComposeRules(JsonElement root)
    {
        _limits = root.GetProperty("limits").EnumerateObject().ToDictionary(limit => limit.Name, limit => limit.Value.GetDouble(), StringComparer.Ordinal);
        // .NET's $ also matches before a final line break, ECMAScript's only at the end; (?!\n) keeps both engines alike.
        _patterns = root.GetProperty("patterns").EnumerateObject().ToDictionary(
            pattern => pattern.Name,
            pattern => new Regex(pattern.Value.GetString()! + "(?!\\n)", RegexOptions.CultureInvariant),
            StringComparer.Ordinal);
        Top = KeyRules(root.GetProperty("top"));
        Service = KeyRules(root.GetProperty("service"));
        var dev = root.GetProperty("dev");
        var devService = new Dictionary<string, KeyRule>(Service, StringComparer.Ordinal);
        foreach (var (key, rule) in KeyRules(dev.GetProperty("service")))
        {
            devService[key] = rule;
        }
        DevService = devService;
        DevBuildKeys = KeyRules(dev.GetProperty("build").GetProperty("keys"));
        var mount = root.GetProperty("mount");
        MountKeys = KeyRules(mount.GetProperty("keys"));
        ReservedTargets = Strings(mount.GetProperty("reservedTargets"));
        DockerSockets = Strings(mount.GetProperty("dockerSockets"));
        DockerSocketRight = mount.GetProperty("dockerSocketRight").GetString()!;
        DockerSocketRawRight = mount.GetProperty("dockerSocketRawRight").GetString()!;
        DependsOnKeys = KeyRules(root.GetProperty("dependsOn").GetProperty("keys"));
        ReservedLabelPrefixes = Strings(root.GetProperty("reservedLabelPrefixes"));
        var elevated = root.GetProperty("elevated");
        BlockedCapabilities = Strings(elevated.GetProperty("blockedCapabilities"));
        HostNamespaceRights = Strings(elevated.GetProperty("hostNamespaceRights"));
        NamespacedDevices = Strings(elevated.GetProperty("namespacedDevices"));
        DeviceClasses = elevated.GetProperty("deviceClasses").EnumerateObject()
            .Where(entry => !entry.Name.StartsWith('$'))
            .ToDictionary(
                entry => entry.Name,
                entry => new DeviceClass(
                    entry.Value.TryGetProperty("nodes", out var nodes) ? new Regex(nodes.GetString()!, RegexOptions.CultureInvariant) : null,
                    entry.Value.TryGetProperty("links", out var links) ? new Regex(links.GetString()!, RegexOptions.CultureInvariant) : null),
                StringComparer.Ordinal);
        Version = root.GetProperty("rulesVersion").GetInt32();
    }

    public int Version { get; }

    public IReadOnlyDictionary<string, KeyRule> Top { get; }

    public IReadOnlyDictionary<string, KeyRule> Service { get; }

    /// <summary>The service keys of the source form <c>compose.dev.yaml</c>: <see cref="Service"/> plus <c>build</c>.</summary>
    public IReadOnlyDictionary<string, KeyRule> DevService { get; }

    public IReadOnlyDictionary<string, KeyRule> DevBuildKeys { get; }

    public IReadOnlyDictionary<string, KeyRule> MountKeys { get; }

    public IReadOnlyDictionary<string, KeyRule> DependsOnKeys { get; }

    public IReadOnlyList<string> ReservedTargets { get; }

    public IReadOnlyList<string> DockerSockets { get; }

    public string DockerSocketRight { get; }

    /// <summary>The right for the unfiltered socket (D387); a mount of the socket needs it or <see cref="DockerSocketRight"/>.</summary>
    public string DockerSocketRawRight { get; }

    /// <summary>The device classes by path (D387): what a device right on such a path stands for at the start.</summary>
    public IReadOnlyDictionary<string, DeviceClass> DeviceClasses { get; }

    public IReadOnlyList<string> ReservedLabelPrefixes { get; }

    /// <summary>Capabilities no app gets (D311).</summary>
    public IReadOnlyList<string> BlockedCapabilities { get; }

    /// <summary>Rights whose service runs in the host's user namespace, so never together with <c>backend.root</c>.</summary>
    public IReadOnlyList<string> HostNamespaceRights { get; }

    /// <summary>Devices a service can use inside the box's user namespace; every other device needs the host's.</summary>
    public IReadOnlyList<string> NamespacedDevices { get; }

    public double Limit(string name) => _limits[name];

    public bool Matches(string pattern, string text) => _patterns[pattern].IsMatch(text);

    public static ComposeRules Load()
    {
        using var stream = typeof(ComposeRules).Assembly.GetManifestResourceStream("schema/compose.v1.rules.json")
            ?? throw new InvalidOperationException("The compose rules are not embedded.");
        using var document = JsonDocument.Parse(stream);
        return new ComposeRules(document.RootElement);
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    private static Dictionary<string, KeyRule> KeyRules(JsonElement keys) =>
        keys.EnumerateObject().ToDictionary(key => key.Name, key => KeyRule(key.Value), StringComparer.Ordinal);

    private static KeyRule KeyRule(JsonElement rule) => new(
        rule.GetProperty("type").GetString()!,
        rule.TryGetProperty("required", out var required) && required.GetBoolean(),
        rule.TryGetProperty("pattern", out var pattern) ? pattern.GetString() : null,
        rule.TryGetProperty("values", out var values) ? Strings(values) : null,
        rule.TryGetProperty("min", out var min) ? min.Clone() : null,
        rule.TryGetProperty("max", out var max) ? max.Clone() : null,
        rule.TryGetProperty("right", out var right) ? right.GetString() : null,
        rule.TryGetProperty("keys", out var keys) ? KeyRules(keys) : null);
}
