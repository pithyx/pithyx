using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Updates;

/// <summary>What the box checks an update file against.</summary>
/// <param name="Keyring">The keys it trusts (<see cref="UpdateKeys"/>).</param>
/// <param name="BoxChannel">Its channel: a stable box takes only stable files.</param>
/// <param name="AllowUnsigned">Only on a development box (<c>/etc/pithyx/environment</c> says dev, D338).</param>
/// <param name="InstalledVersion">The running version: the file must be newer and this at least its minimum.</param>
/// <param name="Architecture">The box's architecture as Debian names it.</param>
public sealed record UpdateExpectation(
    IReadOnlyList<TrustedKey> Keyring,
    string BoxChannel,
    bool AllowUnsigned,
    string InstalledVersion,
    string Architecture);

/// <summary>An update file that passed every check.</summary>
/// <param name="Directory">Where it is unpacked: <c>manifest.json</c> and <c>debs/</c>.</param>
/// <param name="Manifest">Its checked manifest.</param>
/// <param name="TrustedSignature">Whether a trusted key signed it; false only for a development file.</param>
/// <param name="Bytes">The size of the file.</param>
public sealed record VerifiedUpdate(string Directory, UpdateManifest Manifest, bool TrustedSignature, long Bytes);

/// <summary>
/// Reads a <c>.pithyxupdate</c> (D338) from a stream, as an upload or a download arrives: <c>manifest.json</c> and its
/// signature come first and are checked before anything else is written; every other entry must be a file the manifest
/// names, is written below <c>debs/</c> while its size and SHA-256 are counted, and must match; nothing may be missing.
/// Only then is the version checked against the box. A file that fails leaves nothing behind.
/// </summary>
public static class UpdateFile
{
    /// <summary>The largest update file a box accepts.</summary>
    public const long MaxBytes = 4L << 30;

    public const string ManifestFile = "manifest.json";
    public const string SignatureFile = "manifest.json.sig";

    private const int MaxManifestBytes = 1 << 20;
    private const int MaxSignatureBytes = 16 << 10;

    /// <summary>The box's architecture as Debian names it.</summary>
    public static string BoxArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Unpacks and checks the update from <paramref name="source"/> into <paramref name="directory"/> (created empty;
    /// removed again on any failure). Throws a <see cref="PithyxException"/> with an <c>update.file.*</c> code.
    /// </summary>
    public static async Task<VerifiedUpdate> ReadAsync(Stream source, string directory, UpdateExpectation expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expected);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(directory, "debs"));
        try
        {
            return await ReadIntoAsync(new LimitedStream(source, MaxBytes), directory, expected, cancellationToken);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static async Task<VerifiedUpdate> ReadIntoAsync(LimitedStream source, string directory, UpdateExpectation expected, CancellationToken cancellationToken)
    {
        await using var reader = new TarReader(source, leaveOpen: true);
        var manifestBytes = await ReadSmallAsync(await NextAsync(reader, cancellationToken), ManifestFile, MaxManifestBytes, cancellationToken);
        var entry = await NextAsync(reader, cancellationToken);
        byte[]? signature = null;
        if (entry?.Name == SignatureFile)
        {
            signature = await ReadSmallAsync(entry, SignatureFile, MaxSignatureBytes, cancellationToken);
            entry = await NextAsync(reader, cancellationToken);
        }

        var checkedManifest = UpdateDocuments.CheckManifest(manifestBytes, signature, expected.Keyring, expected.BoxChannel, expected.AllowUnsigned);
        if (checkedManifest.Document is not { } manifest)
        {
            throw checkedManifest.Issues[0].ToException();
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, ManifestFile), manifestBytes, cancellationToken);

        var pending = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        for (; entry is not null; entry = await NextAsync(reader, cancellationToken))
        {
            if (entry.EntryType == TarEntryType.Directory && entry.Name.TrimEnd('/') == "debs")
            {
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || !pending.Remove(entry.Name, out var listed))
            {
                throw Problem(ErrorCodes.UpdateFileUnexpectedEntry, ("path", entry.Name));
            }
            await WriteCheckedAsync(entry, listed, Path.Combine(directory, listed.Path), cancellationToken);
        }
        if (pending.Count > 0)
        {
            throw Problem(ErrorCodes.UpdateFileMissingEntry, ("path", pending.Keys.Order(StringComparer.Ordinal).First()));
        }

        // The end-of-archive blocks, so a download is read to its end and its hash covers the whole file.
        await source.CopyToAsync(Stream.Null, cancellationToken);
        CheckAgainstBox(manifest, expected);
        return new VerifiedUpdate(directory, manifest, signature is not null, source.BytesRead);
    }

    /// <summary>The checks that need the box: architecture, a newer version and the minimum version.</summary>
    public static void CheckAgainstBox(UpdateManifest manifest, UpdateExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(expected);
        if (manifest.Architecture != expected.Architecture)
        {
            throw Problem(ErrorCodes.UpdateFileArchitecture, ("architecture", manifest.Architecture), ("box", expected.Architecture));
        }
        if (AppService.CompareVersions(manifest.Version, expected.InstalledVersion) <= 0)
        {
            throw Problem(ErrorCodes.UpdateFileOlder, ("version", manifest.Version), ("installed", expected.InstalledVersion));
        }
        if (AppService.CompareVersions(expected.InstalledVersion, manifest.MinimumVersion) < 0)
        {
            throw Problem(ErrorCodes.UpdateFileMinimum, ("minimum", manifest.MinimumVersion), ("installed", expected.InstalledVersion));
        }
    }

    private static async Task<TarEntry?> NextAsync(TarReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.GetNextEntryAsync(copyData: false, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw Problem(ErrorCodes.UpdateFileInvalid);
        }
    }

    private static async Task<byte[]> ReadSmallAsync(TarEntry? entry, string name, int limit, CancellationToken cancellationToken)
    {
        if (entry is not { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile } || entry.Name != name
            || entry.Length > limit || entry.DataStream is null)
        {
            throw Problem(ErrorCodes.UpdateFileInvalid);
        }
        using var buffer = new MemoryStream((int)entry.Length);
        await entry.DataStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task WriteCheckedAsync(TarEntry entry, UpdateFileEntry listed, string path, CancellationToken cancellationToken)
    {
        if (entry.Length != listed.Size || entry.DataStream is null)
        {
            throw Problem(ErrorCodes.UpdateFileHashMismatch, ("path", listed.Path));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await entry.DataStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), listed.Sha256, StringComparison.Ordinal))
        {
            throw Problem(ErrorCodes.UpdateFileHashMismatch, ("path", listed.Path));
        }
    }

    private static PithyxException Problem(string code, params (string Name, object? Value)[] parameters) =>
        new ValidationIssue(code, parameters).ToException();

    /// <summary>Counts what was read and refuses more than the limit.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            BytesRead += read;
            if (BytesRead > limit)
            {
                throw Problem(ErrorCodes.UpdateFileTooLarge, ("limit", limit));
            }
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
