using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Pithyx.Daemon.Backup;

/// <summary>The key derivation settings and the parts of a sealed backup.</summary>
/// <param name="MemoryKib">Argon2id memory in KiB.</param>
/// <param name="Passes">Argon2id passes.</param>
/// <param name="Salt">The Argon2id salt.</param>
/// <param name="Nonce">The AES-GCM nonce.</param>
/// <param name="Header">Everything before the ciphertext; authenticated as associated data.</param>
/// <param name="Ciphertext">The encrypted zip.</param>
/// <param name="Tag">The AES-GCM tag.</param>
public sealed record BackupEnvelope(long MemoryKib, long Passes, byte[] Salt, byte[] Nonce, byte[] Header, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// The file format of a configuration backup (D293): the magic <c>PITHYXCB</c>, a format byte, the Argon2id memory
/// (KiB) and passes as little-endian uint32, the parallelism byte, a 16-byte salt and a 12-byte nonce, then a zip
/// encrypted with AES-256-GCM under the key Argon2id derives from the backup password, and the 16-byte tag. The
/// header is the associated data, so a changed setting fails like a wrong password.
/// </summary>
public static class BackupArchive
{
    public const int Format = 1;
    public const int SaltBytes = 16;
    public const int KeyBytes = 32;

    /// <summary>The largest archive the daemon reads; a backup holds no files of users, only configuration.</summary>
    public const int MaxArchiveBytes = 16 * 1024 * 1024;

    /// <summary>The largest unpacked content, so a crafted zip cannot fill the memory.</summary>
    public const long MaxContentBytes = 256L * 1024 * 1024;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Parallelism = 1;
    private const int HeaderBytes = 8 + 1 + 4 + 4 + 1 + SaltBytes + NonceBytes;

    // Bounds for a header from a file: below them the key would be weak, above them reading it would exhaust the box.
    private const long MinMemoryKib = 8 * 1024;
    private const long MaxMemoryKib = 1024 * 1024;
    private const long MaxPasses = 10;

    private static readonly byte[] _magic = "PITHYXCB"u8.ToArray();

    /// <summary>Encrypts a packed backup with a key from <see cref="Auth.PasswordHasher.DeriveKeyAsync"/>.</summary>
    public static byte[] Seal(byte[] plaintext, byte[] key, byte[] salt, long memoryKib, long passes)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != SaltBytes)
        {
            throw new ArgumentException($"The salt must have {SaltBytes} bytes.", nameof(salt));
        }
        var header = new byte[HeaderBytes];
        _magic.CopyTo(header, 0);
        header[8] = Format;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(9), checked((uint)memoryKib));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(13), checked((uint)passes));
        header[17] = Parallelism;
        salt.CopyTo(header, 18);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        nonce.CopyTo(header, 18 + SaltBytes);
        var archive = new byte[HeaderBytes + plaintext.Length + TagBytes];
        header.CopyTo(archive, 0);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, archive.AsSpan(HeaderBytes, plaintext.Length), archive.AsSpan(HeaderBytes + plaintext.Length), header);
        return archive;
    }

    /// <summary>The parts of an archive, or null when the bytes are no backup of a format this daemon reads.</summary>
    public static BackupEnvelope? Parse(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length < HeaderBytes + TagBytes || !archive.AsSpan(0, 8).SequenceEqual(_magic) || archive[8] != Format || archive[17] != Parallelism)
        {
            return null;
        }
        long memory = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(9));
        long passes = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(13));
        if (memory is < MinMemoryKib or > MaxMemoryKib || passes is < 1 or > MaxPasses)
        {
            return null;
        }
        return new BackupEnvelope(
            memory,
            passes,
            archive[18..(18 + SaltBytes)],
            archive[(18 + SaltBytes)..HeaderBytes],
            archive[..HeaderBytes],
            archive[HeaderBytes..^TagBytes],
            archive[^TagBytes..]);
    }

    /// <summary>The packed backup, or null when the key is wrong or the archive was changed.</summary>
    public static byte[]? Open(BackupEnvelope envelope, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var plaintext = new byte[envelope.Ciphertext.Length];
        using var aes = new AesGcm(key, TagBytes);
        try
        {
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, envelope.Header);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>Packs named entries into a zip.</summary>
    public static byte[] Pack(IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (name, content) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>The entries of a packed backup.</summary>
    /// <exception cref="InvalidDataException">The zip is damaged, has unsafe names or unpacks to more than <paramref name="maxContentBytes"/>.</exception>
    public static IReadOnlyDictionary<string, byte[]> Unpack(byte[] packed, long maxContentBytes = MaxContentBytes)
    {
        ArgumentNullException.ThrowIfNull(packed);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        using var zip = new ZipArchive(new MemoryStream(packed, writable: false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (!IsSafeName(entry.FullName) || entries.ContainsKey(entry.FullName))
            {
                throw new InvalidDataException($"The backup has an unexpected entry {entry.FullName}.");
            }
            using var stream = entry.Open();
            using var content = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                total += read;
                if (total > maxContentBytes)
                {
                    throw new InvalidDataException("The backup unpacks to more than the daemon reads.");
                }
                content.Write(chunk, 0, read);
            }
            entries[entry.FullName] = content.ToArray();
        }
        return entries;
    }

    // Relative names of letters, digits, dots, dashes and underscores in folders; never a way out of the folder.
    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 200
        && name.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
}
