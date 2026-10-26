using System.Security.Cryptography;
using System.Text;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage.Encryption;

/// <summary>
/// The key of an encrypted pool as a person reads, prints and types it (D384): <c>PXPK-</c> and 56 Crockford Base32
/// characters in groups of four. They carry the 256-bit key and 24 bits of its SHA-256 hash, so a typo is caught before
/// ZFS is asked. ZFS gets the key as 64 hex characters (<c>keyformat=hex</c>) on stdin, never as an argument.
/// </summary>
public static class PoolKey
{
    public const string Prefix = "PXPK";

    private const int KeyBytes = 32;
    private const int CheckBytes = 3;
    private const int Characters = (KeyBytes + CheckBytes) * 8 / 5;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new random key, for example <c>PXPK-7K3M-QX2P-...</c>.</summary>
    public static string New()
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            return Format(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The key as ZFS takes it (64 lowercase hex characters), or <c>storage.encryption.key_invalid</c>.</summary>
    /// <param name="typed">The key as typed: case, spaces, dashes, the prefix and O, I or L for 0 and 1 do not matter.</param>
    public static string ToHex(string? typed)
    {
        var code = Secrets.NormalizeCode(typed ?? "", Prefix, Characters);
        if (code.Length != Characters || Decode(code) is not { } bytes)
        {
            throw new PithyxException(ErrorCodes.StorageEncryptionKeyInvalid);
        }
        try
        {
            var key = bytes.AsSpan(0, KeyBytes);
            return CryptographicOperations.FixedTimeEquals(Check(key), bytes.AsSpan(KeyBytes))
                ? Convert.ToHexStringLower(key)
                : throw new PithyxException(ErrorCodes.StorageEncryptionKeyInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The printed form of a 32-byte key.</summary>
    internal static string Format(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"A pool key has {KeyBytes} bytes.", nameof(key));
        }
        var bytes = new byte[KeyBytes + CheckBytes];
        key.CopyTo(bytes);
        Check(key).CopyTo(bytes, KeyBytes);
        var builder = new StringBuilder(Prefix, Prefix.Length + Characters + (Characters / 4) + 1);
        var buffer = 0;
        var bits = 0;
        var written = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (written % 4 == 0)
                {
                    builder.Append('-');
                }
                builder.Append(Alphabet[(buffer >> bits) & 31]);
                written++;
            }
        }
        CryptographicOperations.ZeroMemory(bytes);
        return builder.ToString();
    }

    private static byte[] Check(ReadOnlySpan<byte> key) => SHA256.HashData(key)[..CheckBytes];

    private static byte[]? Decode(string code)
    {
        var bytes = new byte[KeyBytes + CheckBytes];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in code)
        {
            var value = Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }
            buffer = ((buffer << 5) | value) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
            }
        }
        return bytes;
    }
}
