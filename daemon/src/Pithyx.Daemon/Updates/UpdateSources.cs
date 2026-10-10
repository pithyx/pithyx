using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Updates;

/// <summary>An update source of <c>pithyx-source.json</c> (D339): a fork's channels and the keys that sign them.</summary>
public sealed record UpdateSourceDefinition(string Name, string Url, IReadOnlyList<TrustedKey> Keys);

/// <summary>A key of an update source as Settings shows it.</summary>
public sealed record UpdateSourceKey(string KeyId, string PublicKey, IReadOnlyList<string> Channels);

/// <summary>The update source the box follows.
/// <c>Official</c>: True for the Pithyx project's keys; false after a switch to another source in the recovery menu.
/// </summary>
public sealed record UpdateSourceInfo(string Name, string Url, bool Official, IReadOnlyList<UpdateSourceKey> Keys);

/// <summary>A source file an administrator saved in Settings for the recovery menu, which alone can switch to it.</summary>
public sealed record UpdateSourceCandidate(string Name, string Url, IReadOnlyList<UpdateSourceKey> Keys, string SavedBy, DateTimeOffset SavedAt);

/// <summary>
/// Reads and writes update sources (D339) with the same verdicts as <c>tools/release/update-file.ts</c> on the corpus
/// <c>tools/release/fixtures/source</c>. Only the recovery menu writes the box's source; Settings only saves a candidate.
/// </summary>
public static class UpdateSources
{
    public const string ProjectName = "Pithyx";
    public const string CandidateFileName = "source-candidate.json";
    public const int MaxBytes = 64 * 1024;

    private static readonly Lazy<JsonSchemaCheck> _schema = new(() => JsonSchemaCheck.Load("update-source.v1.schema.json"));

    public static UpdateDocumentResult<UpdateSourceDefinition> Check(ReadOnlyMemory<byte> bytes)
    {
        static UpdateDocumentResult<UpdateSourceDefinition> Invalid(string reason) =>
            new([new ValidationIssue(ErrorCodes.UpdateSourceInvalid, ("reason", reason))], null);

        using var document = bytes.Length > MaxBytes ? null : JsonSchemaCheck.ParseStrict(bytes);
        if (document is null)
        {
            return Invalid("json");
        }
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            && url.GetString() is { } text && (!text.StartsWith("https://", StringComparison.Ordinal) || !text.Contains("{channel}", StringComparison.Ordinal)))
        {
            return Invalid("url");
        }
        if (!_schema.Value.IsValid(root))
        {
            return Invalid("schema");
        }
        var keys = root.GetProperty("keys").EnumerateArray()
            .Select(key => new TrustedKey(
                key.GetProperty("keyId").GetString()!,
                key.GetProperty("publicKey").GetString()!,
                [.. key.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()!)]))
            .ToList();
        if (keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Count)
        {
            return Invalid("duplicate_key");
        }
        return new([], new UpdateSourceDefinition(root.GetProperty("name").GetString()!, root.GetProperty("url").GetString()!, keys));
    }

    /// <summary>The bytes of <c>pithyx-source.json</c> for a source, as typed at the console.</summary>
    public static byte[] Serialize(string name, string url, IEnumerable<TrustedKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var document = new JsonObject
        {
            ["sourceVersion"] = 1,
            ["name"] = name,
            ["url"] = url,
            ["keys"] = new JsonArray([.. keys.Select(key => (JsonNode)new JsonObject
            {
                ["keyId"] = key.KeyId,
                ["publicKey"] = key.PublicKey,
                ["channels"] = new JsonArray([.. key.Channels.Select(channel => (JsonNode)JsonValue.Create(channel))]),
            })]),
        };
        return Encoding.UTF8.GetBytes(document.ToJsonString(UpdateSystem.Json) + "\n");
    }

    /// <summary>The keys the box checks signatures against: the source's after a switch, otherwise the built-in ones.</summary>
    public static IReadOnlyList<TrustedKey> Keyring(UpdateSourceConfig config, IReadOnlyList<TrustedKey> builtIn)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Keys ?? builtIn;
    }

    public static UpdateSourceInfo Describe(UpdateSourceConfig config, IReadOnlyList<TrustedKey> builtIn)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new UpdateSourceInfo(config.Name ?? ProjectName, config.Url, config.Keys is null, Keys(Keyring(config, builtIn)));
    }

    public static IReadOnlyList<UpdateSourceKey> Keys(IEnumerable<TrustedKey> keys) =>
        [.. keys.Select(key => new UpdateSourceKey(key.KeyId, key.PublicKey, key.Channels))];

    /// <summary>
    /// What identifies the trust of a source: a change means another source, whose channel sequences start anew. The
    /// project's keys with another URL or a token (a mirror, a private test repository) are no switch.
    /// </summary>
    public static string Identity(UpdateSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Keys is null
            ? "project"
            : string.Join('\n', [config.Name ?? "", config.Url, .. config.Keys.Select(key => $"{key.KeyId}:{key.PublicKey}:{string.Join(',', key.Channels)}")]);
    }

    /// <summary>
    /// Switches the box to <paramref name="source"/>, or back to the project's keys and URL when it is null: writes or
    /// removes the source file (root, mode 600, without a token; the project's file of a test box goes with its token)
    /// and forgets the candidate. The daemon notices the switch at its next start.
    /// </summary>
    public static async Task SwitchAsync(string sourceFile, string candidateFile, UpdateSourceDefinition? source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        if (source is null)
        {
            DeleteIfPresent(sourceFile);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            var temporary = sourceFile + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(Serialize(source.Name, source.Url, source.Keys), cancellationToken);
            }
            File.Move(temporary, sourceFile, overwrite: true);
        }
        DeleteIfPresent(candidateFile);
    }

    public static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record StoredCandidate(JsonElement Source, string SavedBy, DateTimeOffset SavedAt);

    public static async Task SaveCandidateAsync(string candidateFile, ReadOnlyMemory<byte> bytes, string savedBy, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(bytes);
        await UpdateSystem.WriteJsonAsync(candidateFile, new StoredCandidate(document.RootElement.Clone(), savedBy, savedAt), cancellationToken);
    }

    /// <summary>The saved candidate, checked again; null when there is none or it does not pass any more.</summary>
    public static (UpdateSourceDefinition Source, string SavedBy, DateTimeOffset SavedAt)? ReadCandidate(string candidateFile)
    {
        if (UpdateSystem.ReadJson<StoredCandidate>(candidateFile) is not { } stored)
        {
            return null;
        }
        return Check(Encoding.UTF8.GetBytes(stored.Source.GetRawText())).Document is { } source ? (source, stored.SavedBy, stored.SavedAt) : null;
    }
}
