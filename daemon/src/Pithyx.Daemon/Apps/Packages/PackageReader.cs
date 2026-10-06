using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Packages;

/// <summary>How large an app package may be.</summary>
/// <param name="MaxCompressedBytes">The package file itself.</param>
/// <param name="MaxUnpackedBytes">All its files together.</param>
/// <param name="MaxEntries">Files and folders.</param>
/// <param name="MaxManifestBytes"><c>pithyx.json</c>.</param>
public sealed record PackageLimits(long MaxCompressedBytes, long MaxUnpackedBytes, int MaxEntries, int MaxManifestBytes)
{
    /// <summary>Enough for any frontend; backends with image files (A14) bring their own limits.</summary>
    public static PackageLimits Default { get; } = new(256L * 1024 * 1024, 512L * 1024 * 1024, 10_000, 64 * 1024);
}

/// <summary>One regular file of an unpacked package.</summary>
/// <param name="Path">Relative, with <c>/</c>, checked by <see cref="PackageLayout.IsSafe"/>.</param>
/// <param name="Size">In bytes.</param>
public sealed record PackageFile(string Path, long Size);

/// <summary>A package unpacked into <paramref name="Directory"/>.</summary>
/// <param name="Directory">Holds exactly the regular files of the package, with folders 0755 and files 0644.</param>
/// <param name="Files">The regular files in archive order.</param>
/// <param name="UnpackedBytes">Their sizes together.</param>
/// <param name="CompressedBytes">The size of the package file.</param>
/// <param name="Sha256">SHA-256 of the package file, lowercase hex.</param>
public sealed record UnpackedPackage(string Directory, IReadOnlyList<PackageFile> Files, long UnpackedBytes, long CompressedBytes, string Sha256);

/// <summary>
/// Unpacks an app package, a gzip-compressed tar archive (D301), from an untrusted stream into an empty folder. It reads
/// ustar and pax archives and GNU long names, and takes regular files and folders only: links, devices, FIFOs, sparse
/// files and global headers are refused, as are names that leave the package or could mean another file (see
/// <see cref="PackageLayout.IsSafe"/>), duplicates (also in another case), a long name that does not continue the
/// entry's own name, numeric fields that are not plain octal, a wrong header checksum, an archive that ends early and
/// data after its end. Modes, owners and times in the archive are ignored. The limits are checked while reading, so a
/// gzip bomb stops at the first byte too many. Nothing is kept when it throws; the caller deletes the folder.
/// </summary>
public static class PackageReader
{
    private const int BlockSize = 512;

    private const UnixFileMode FileMode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode FolderMode755 = FileMode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const int MaxExtendedHeaderBytes = 64 * 1024;
    private const int MaxLongNameBytes = 4096;

    // A tar writer pads the archive to its record size (10 KB for GNU tar and libarchive); zeros beyond that are data.
    private const int MaxPaddingBytes = 64 * 1024;

    private static readonly HashSet<string> _ignoredPaxKeys = new(StringComparer.Ordinal)
    {
        "mtime", "atime", "ctime", "uid", "gid", "uname", "gname", "comment", "charset", "hdrcharset",
    };

    private static readonly string[] _ignoredPaxPrefixes = ["SCHILY.xattr.", "LIBARCHIVE.xattr.", "SCHILY.dev", "SCHILY.ino", "SCHILY.nlink", "LIBARCHIVE.creationtime"];

    public static async Task<UnpackedPackage> UnpackAsync(Stream package, string directory, PackageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(limits);
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new InvalidOperationException($"{directory} is not empty.");
        }

        await using var counted = new CountingStream(package, limits.MaxCompressedBytes);
        await using var gzip = new GZipStream(counted, CompressionMode.Decompress, leaveOpen: true);
        await using var checksum = new Crc32Stream(gzip);
        var reader = new BlockReader(checksum);
        var state = new State(directory, limits);
        var header = new byte[BlockSize];
        try
        {
            while (true)
            {
                if (!await reader.ReadBlockAsync(header, cancellationToken))
                {
                    throw Invalid("truncated");
                }
                if (IsZero(header))
                {
                    if (!await reader.ReadBlockAsync(header, cancellationToken))
                    {
                        throw Invalid("truncated");
                    }
                    if (!IsZero(header))
                    {
                        throw Invalid("format");
                    }
                    if (state.PendingName is not null || state.PendingPax is not null)
                    {
                        throw Invalid("extended_header");
                    }
                    await reader.ExpectOnlyPaddingAsync(MaxPaddingBytes, cancellationToken);
                    break;
                }
                await ReadEntryAsync(reader, header, state, cancellationToken);
            }
        }
        catch (InvalidDataException exception)
        {
            throw Invalid("gzip", exception);
        }
        // GZipStream ignores data after the last member and a missing trailer. A package is one member, so the file
        // ends with the CRC-32 and size of exactly what was decompressed.
        if (await counted.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw Invalid("trailing_data");
        }
        if (!counted.EndsWith(checksum.Trailer()))
        {
            throw Invalid("gzip");
        }
        return new UnpackedPackage(directory, state.Files, state.UnpackedBytes, counted.Count, counted.Hash());
    }

    private static async Task ReadEntryAsync(BlockReader reader, byte[] header, State state, CancellationToken cancellationToken)
    {
        if (!ChecksumMatches(header))
        {
            throw Invalid("checksum");
        }
        var posix = header.AsSpan(257, 8).SequenceEqual("ustar\000"u8);
        var gnu = header.AsSpan(257, 8).SequenceEqual("ustar  \0"u8);
        if (!posix && !gnu)
        {
            throw Invalid("format");
        }
        var type = (char)header[156];
        var size = Octal(header.AsSpan(124, 12));
        switch (type)
        {
            case 'x':
                if (state.PendingPax is not null || state.PendingName is not null || size > MaxExtendedHeaderBytes)
                {
                    throw Invalid("extended_header");
                }
                state.PendingPax = ParsePax(await reader.ReadDataAsync((int)size, cancellationToken));
                return;
            case 'L':
                if (state.PendingPax is not null || state.PendingName is not null || size > MaxLongNameBytes || size == 0)
                {
                    throw Invalid("extended_header");
                }
                state.PendingName = LongName(await reader.ReadDataAsync((int)size, cancellationToken));
                return;
            case 'g' or 'K':
                throw Invalid("extended_header");
            default:
                break;
        }

        var ownName = OwnName(header, posix);
        var pax = state.PendingPax;
        var extendedName = pax?.Path ?? state.PendingName;
        state.PendingPax = null;
        state.PendingName = null;
        // The name an extractor that ignores the extended header would use must lead to the same file.
        if (extendedName is not null && ownName.Length > 0 && !extendedName.StartsWith(ownName, StringComparison.Ordinal))
        {
            throw Invalid("name_mismatch", Shown(extendedName));
        }
        var name = extendedName ?? ownName;
        if (pax?.Size is { } paxSize)
        {
            size = paxSize;
        }

        if (type is not ('0' or '\0' or '5'))
        {
            throw Invalid("entry_type", Shown(name));
        }
        if (++state.Entries > state.Limits.MaxEntries)
        {
            throw TooLarge("files", state.Limits.MaxEntries);
        }
        var directory = type == '5';
        var path = Normalize(name, directory);
        if (path is null)
        {
            // The archive's own root folder ("./"), which tar writes when it packs a folder's content.
            if (!directory || size != 0)
            {
                throw Layout(name, "unsafe");
            }
            return;
        }
        if (!PackageLayout.IsSafe(path))
        {
            throw Layout(name, "unsafe");
        }
        if (directory)
        {
            if (size != 0)
            {
                throw Invalid("format");
            }
            state.AddFolder(path);
            state.CreateFolder(path);
            return;
        }

        state.AddFile(path);
        if (path == PackageLayout.ManifestFile && size > state.Limits.MaxManifestBytes)
        {
            throw TooLarge("manifest", state.Limits.MaxManifestBytes / 1024);
        }
        if (size > state.Limits.MaxUnpackedBytes - state.UnpackedBytes)
        {
            throw TooLarge("unpacked", state.Limits.MaxUnpackedBytes / (1024 * 1024));
        }
        var target = state.Resolve(path);
        if (path.LastIndexOf('/') is var slash and > 0)
        {
            state.CreateFolder(path[..slash]);
        }
        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // The archive's modes never count: no setuid, no executables.
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(file.SafeFileHandle, FileMode644);
            }
            await reader.CopyDataAsync(file, size, cancellationToken);
        }
        state.UnpackedBytes += size;
        state.Files.Add(new PackageFile(path, size));
    }

    /// <summary>The path below the package root, or null for the root folder itself. A leading <c>./</c> is dropped.</summary>
    private static string? Normalize(string name, bool directory)
    {
        var path = name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;
        if (directory && path.EndsWith('/'))
        {
            path = path[..^1];
        }
        return directory && path is "" or "." ? null : path;
    }

    private static string OwnName(byte[] header, bool posix)
    {
        var name = Text(header.AsSpan(0, 100));
        // GNU headers keep times where ustar has the prefix.
        var prefix = posix ? Text(header.AsSpan(345, 155)) : "";
        return prefix.Length > 0 ? prefix + "/" + name : name;
    }

    /// <summary>A NUL-terminated field in strict UTF-8, with nothing but NULs after the end.</summary>
    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end >= 0 && field[end..].ContainsAnyExcept((byte)0))
        {
            throw Invalid("format");
        }
        return Utf8(end < 0 ? field : field[..end]) ?? throw Invalid("format");
    }

    private static string LongName(byte[] data)
    {
        var end = data.AsSpan().IndexOf((byte)0);
        if (end <= 0 || data.AsSpan(end).ContainsAnyExcept((byte)0))
        {
            throw Invalid("extended_header");
        }
        return Utf8(data.AsSpan(0, end)) ?? throw Invalid("extended_header");
    }

    private static Pax ParsePax(byte[] data)
    {
        string? path = null;
        long? size = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        while (position < data.Length)
        {
            // "<length> <key>=<value>\n", where length counts the whole record.
            var space = data.AsSpan(position).IndexOf((byte)' ');
            if (space is <= 0 or > 10 || !long.TryParse(data.AsSpan(position, space), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length < space + 3 || length > data.Length - position || data[position + (int)length - 1] != '\n')
            {
                throw Invalid("extended_header");
            }
            var record = data.AsSpan(position + space + 1, (int)length - space - 2);
            position += (int)length;
            var equals = record.IndexOf((byte)'=');
            if (equals <= 0 || Utf8(record[..equals]) is not { } key || !keys.Add(key))
            {
                throw Invalid("extended_header");
            }
            var value = record[(equals + 1)..];
            switch (key)
            {
                case "path":
                    if (value.IsEmpty || value.Contains((byte)0) || Utf8(value) is not { } text)
                    {
                        throw Invalid("extended_header");
                    }
                    path = text;
                    break;
                case "size":
                    if (value.IsEmpty || value.Length > 18 || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    {
                        throw Invalid("extended_header");
                    }
                    size = parsed;
                    break;
                default:
                    if (!_ignoredPaxKeys.Contains(key) && !_ignoredPaxPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        throw Invalid("extended_header");
                    }
                    break;
            }
        }
        return new Pax(path, size);
    }

    /// <summary>Octal digits with optional leading spaces, ended by a NUL or a space; base-256 and signs are refused.</summary>
    private static long Octal(ReadOnlySpan<byte> field)
    {
        var index = 0;
        while (index < field.Length && field[index] == ' ')
        {
            index++;
        }
        long value = 0;
        var digits = 0;
        for (; index < field.Length && field[index] is >= (byte)'0' and <= (byte)'7'; index++, digits++)
        {
            value = (value << 3) | (long)(field[index] - '0');
        }
        if (digits is 0 or > 11 || (index < field.Length && field[index..].ContainsAnyExcept((byte)0, (byte)' ')))
        {
            throw Invalid("format");
        }
        return value;
    }

    private static bool ChecksumMatches(byte[] header)
    {
        long stored;
        try
        {
            stored = Octal(header.AsSpan(148, 8));
        }
        catch (PithyxException)
        {
            return false;
        }
        long unsigned = 0;
        long signed = 0;
        for (var index = 0; index < BlockSize; index++)
        {
            var value = index is >= 148 and < 156 ? (byte)' ' : header[index];
            unsigned += value;
            signed += (sbyte)value;
        }
        return stored == unsigned || stored == signed;
    }

    private static bool IsZero(byte[] block) => !block.AsSpan().ContainsAnyExcept((byte)0);

    private static string? Utf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A name as an error may show it: no longer than 200 characters.</summary>
    private static string Shown(string name) => name.Length > 200 ? name[..200] : name;

    private static PithyxException Invalid(string reason, Exception? inner = null) =>
        new(ErrorCodes.AppPackageInvalid, new Dictionary<string, object?> { ["reason"] = reason }, inner);

    private static PithyxException Invalid(string reason, string path) =>
        new(ErrorCodes.AppPackageInvalid, new Dictionary<string, object?> { ["reason"] = reason, ["path"] = path });

    private static PithyxException Layout(string path, string reason) =>
        new(ErrorCodes.AppPackageLayoutInvalid, new Dictionary<string, object?> { ["path"] = Shown(path), ["reason"] = reason });

    private static PithyxException TooLarge(string reason, long limit) =>
        new(ErrorCodes.AppPackageTooLarge, new Dictionary<string, object?> { ["reason"] = reason, ["limit"] = limit });

    private sealed record Pax(string? Path, long? Size);

    private sealed class State(string directory, PackageLimits limits)
    {
        // Every path seen, in any case: true for folders.
        private readonly Dictionary<string, bool> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new(StringComparer.Ordinal);

        public PackageLimits Limits { get; } = limits;

        public List<PackageFile> Files { get; } = [];

        public long UnpackedBytes { get; set; }

        public int Entries { get; set; }

        public Pax? PendingPax { get; set; }

        public string? PendingName { get; set; }

        public string Resolve(string path)
        {
            var full = Path.GetFullPath(Path.Combine(directory, path));
            if (!full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw Layout(path, "unsafe");
            }
            return full;
        }

        /// <summary>Creates a folder and its parents below the package root with mode 0755.</summary>
        public void CreateFolder(string path)
        {
            var current = directory;
            foreach (var segment in path.Split('/'))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                {
                    if (OperatingSystem.IsLinux())
                    {
                        Directory.CreateDirectory(current, FolderMode755);
                    }
                    else
                    {
                        Directory.CreateDirectory(current);
                    }
                }
            }
        }

        public void AddFile(string path)
        {
            AddParents(path);
            if (!_paths.TryAdd(path, false))
            {
                throw Layout(path, "duplicate");
            }
        }

        public void AddFolder(string path)
        {
            AddParents(path);
            if (_paths.TryGetValue(path, out var folder))
            {
                // The same folder twice is harmless; a file of that name or a folder in another case is not.
                if (!folder || !_folders.Contains(path))
                {
                    throw Layout(path, "duplicate");
                }
                return;
            }
            _paths[path] = true;
            _folders.Add(path);
        }

        private void AddParents(string path)
        {
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                var parent = path[..slash];
                if (_paths.TryGetValue(parent, out var folder))
                {
                    if (!folder || !_folders.Contains(parent))
                    {
                        throw Layout(parent, "duplicate");
                    }
                }
                else
                {
                    _paths[parent] = true;
                    _folders.Add(parent);
                }
            }
        }
    }

    /// <summary>Reads the decompressed archive in 512-byte blocks.</summary>
    private sealed class BlockReader(Stream source)
    {
        private readonly byte[] _padding = new byte[BlockSize];

        /// <summary>False at the end of the stream before the first byte of the block; throws when it ends inside one.</summary>
        public async Task<bool> ReadBlockAsync(byte[] block, CancellationToken cancellationToken)
        {
            var read = await source.ReadAtLeastAsync(block, BlockSize, throwOnEndOfStream: false, cancellationToken);
            return read == BlockSize || (read == 0 ? false : throw Invalid("truncated"));
        }

        public async Task<byte[]> ReadDataAsync(int size, CancellationToken cancellationToken)
        {
            var data = new byte[size];
            if (await source.ReadAtLeastAsync(data, size, throwOnEndOfStream: false, cancellationToken) != size)
            {
                throw Invalid("truncated");
            }
            await SkipPaddingAsync(size, cancellationToken);
            return data;
        }

        public async Task CopyDataAsync(Stream target, long size, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var left = size;
                while (left > 0)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken);
                    if (read == 0)
                    {
                        throw Invalid("truncated");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    left -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            await SkipPaddingAsync(size, cancellationToken);
        }

        /// <summary>After the end marker: only zeros, and not more than a tar writer pads.</summary>
        public async Task ExpectOnlyPaddingAsync(int maxBytes, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes || buffer.AsSpan(0, read).ContainsAnyExcept((byte)0))
                {
                    throw Invalid("trailing_data");
                }
            }
        }

        private async Task SkipPaddingAsync(long size, CancellationToken cancellationToken)
        {
            var padding = (int)((BlockSize - (size % BlockSize)) % BlockSize);
            if (padding > 0 && await source.ReadAtLeastAsync(_padding.AsMemory(0, padding), padding, throwOnEndOfStream: false, cancellationToken) != padding)
            {
                throw Invalid("truncated");
            }
        }
    }

    /// <summary>The CRC-32 and length of the decompressed data, as a gzip trailer has them.</summary>
    private sealed class Crc32Stream(Stream inner) : Stream
    {
        private static readonly uint[] _table = Enumerable.Range(0, 256).Select(index =>
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }
            return value;
        }).ToArray();

        private uint _crc = 0xFFFFFFFFu;
        private uint _length;

        public byte[] Trailer()
        {
            var trailer = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer, ~_crc);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), _length);
            return trailer;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Packages are read asynchronously.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            foreach (var value in buffer.Span[..read])
            {
                _crc = _table[(_crc ^ value) & 0xFF] ^ (_crc >> 8);
            }
            _length = unchecked(_length + (uint)read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Counts and hashes what the decompressor reads, and stops it at the size limit.</summary>
    private sealed class CountingStream(Stream inner, long limit) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Count { get; private set; }

        private readonly byte[] _last = new byte[8];

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        /// <summary>Whether the bytes read so far end with <paramref name="trailer"/>.</summary>
        public bool EndsWith(ReadOnlySpan<byte> trailer) => Count >= trailer.Length && _last.AsSpan(_last.Length - trailer.Length).SequenceEqual(trailer);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Count; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Packages are read asynchronously.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Count += read;
            if (Count > limit)
            {
                throw TooLarge("size", limit / (1024 * 1024));
            }
            _hash.AppendData(buffer.Span[..read]);
            var chunk = buffer.Span[..read];
            if (chunk.Length >= _last.Length)
            {
                chunk[^_last.Length..].CopyTo(_last);
            }
            else if (chunk.Length > 0)
            {
                _last.AsSpan(chunk.Length).CopyTo(_last);
                chunk.CopyTo(_last.AsSpan(_last.Length - chunk.Length));
            }
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
