using System.Text.Json;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Updates;

/// <summary>One file of an update besides its manifest, with its size and SHA-256.</summary>
public sealed record UpdateFileEntry(string Path, string Sha256, long Size);

/// <summary>A Debian package in an update file.</summary>
public sealed record UpdatePackage(string Name, string Version, string Path);

/// <summary>The kernel and ZFS pair of <c>pithyx-kernel</c>.</summary>
public sealed record UpdateKernel(string Release, string Zfs);

/// <summary><c>manifest.json</c> of a <c>.pithyxupdate</c> (<c>tools/release/schema/update-manifest.v1.schema.json</c>, D338).</summary>
public sealed record UpdateManifest(
    int UpdateVersion,
    string Version,
    string Channel,
    string MinimumVersion,
    string Architecture,
    string Commit,
    DateTimeOffset PublishedAt,
    UpdateKernel Kernel,
    IReadOnlyList<UpdateFileEntry> Files,
    IReadOnlyList<UpdatePackage> Packages,
    string Changelog);

/// <summary>Where a channel's newest file lies (relative to <c>channel.json</c>), its size and SHA-256.</summary>
public sealed record UpdateChannelFile(string Name, string Url, long Size, string Sha256);

/// <summary><c>channel.json</c> of a channel (<c>tools/release/schema/update-channel.v1.schema.json</c>, D338).</summary>
public sealed record UpdateChannelDocument(
    int ChannelVersion,
    string Channel,
    long Sequence,
    DateTimeOffset PublishedAt,
    string Version,
    string MinimumVersion,
    string Changelog,
    UpdateChannelFile File);

/// <summary>The result of reading a signed document: the issues (none when it is good) and the document.</summary>
public sealed record UpdateDocumentResult<T>(IReadOnlyList<ValidationIssue> Issues, T? Document)
    where T : class;

/// <summary>
/// Reads update manifests and channels as <c>tools/release/update-file.ts</c> does, on the same corpus
/// (<c>tools/release/fixtures</c>): the signature over the exact bytes first (keys trusted for the box's channel), then
/// the document against its schema, its channel and, for a channel, its sequence.
/// </summary>
public static class UpdateDocuments
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    private static readonly Lazy<JsonSchemaCheck> _manifestSchema = new(() => JsonSchemaCheck.Load("update-manifest.v1.schema.json"));
    private static readonly Lazy<JsonSchemaCheck> _channelSchema = new(() => JsonSchemaCheck.Load("update-channel.v1.schema.json"));
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static bool IsChannel(string? channel) => channel is Stable or Beta;

    /// <summary>The channels whose files a box on <paramref name="boxChannel"/> installs: a stable box only stable ones.</summary>
    public static IReadOnlyList<string> AcceptedChannels(string boxChannel) => boxChannel == Stable ? [Stable] : [Stable, Beta];

    /// <summary>
    /// The manifest of an update file. <paramref name="allowUnsigned"/> is true only on a development box (D338): a
    /// missing signature is then accepted, a bad one never.
    /// </summary>
    public static UpdateDocumentResult<UpdateManifest> CheckManifest(
        ReadOnlyMemory<byte> bytes,
        byte[]? signature,
        IReadOnlyList<TrustedKey> keyring,
        string boxChannel,
        bool allowUnsigned)
    {
        if (signature is not null || !allowUnsigned)
        {
            if (IndexSignature.Reason(bytes, signature, keyring, boxChannel) is { } reason)
            {
                return new([new ValidationIssue(ErrorCodes.UpdateFileSignatureInvalid, ("reason", reason))], null);
            }
        }
        using var document = JsonSchemaCheck.ParseStrict(bytes);
        if (document is null || !_manifestSchema.Value.IsValid(document.RootElement))
        {
            return new([new ValidationIssue(ErrorCodes.UpdateFileInvalid)], null);
        }
        var manifest = document.RootElement.Deserialize<UpdateManifest>(_json)!;
        if (!AcceptedChannels(boxChannel).Contains(manifest.Channel))
        {
            return new([new ValidationIssue(ErrorCodes.UpdateFileChannel, ("channel", manifest.Channel), ("boxChannel", boxChannel))], null);
        }
        return new([], manifest);
    }

    /// <summary>A channel's <c>channel.json</c>; a sequence below <paramref name="knownSequence"/> is a replay.</summary>
    public static UpdateDocumentResult<UpdateChannelDocument> CheckChannel(
        ReadOnlyMemory<byte> bytes,
        byte[]? signature,
        IReadOnlyList<TrustedKey> keyring,
        string boxChannel,
        long? knownSequence)
    {
        if (IndexSignature.Reason(bytes, signature, keyring, boxChannel) is { } reason)
        {
            return new([new ValidationIssue(ErrorCodes.UpdateChannelSignatureInvalid, ("reason", reason))], null);
        }
        using var document = JsonSchemaCheck.ParseStrict(bytes);
        if (document is null || !_channelSchema.Value.IsValid(document.RootElement))
        {
            return new([new ValidationIssue(ErrorCodes.UpdateChannelInvalid)], null);
        }
        var channel = document.RootElement.Deserialize<UpdateChannelDocument>(_json)!;
        if (channel.Channel != boxChannel)
        {
            return new([new ValidationIssue(ErrorCodes.UpdateChannelMismatch, ("channel", channel.Channel), ("expected", boxChannel))], null);
        }
        if (knownSequence is { } known && channel.Sequence < known)
        {
            return new([new ValidationIssue(ErrorCodes.UpdateChannelSequenceOld, ("sequence", channel.Sequence), ("known", known))], null);
        }
        return new([], channel);
    }
}
