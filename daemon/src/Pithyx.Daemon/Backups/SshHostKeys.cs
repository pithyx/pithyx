using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pithyx.Daemon.Backups;

/// <summary>Host keys of SSH servers: reading them from a pasted line or from <c>ssh-keyscan</c>, and their fingerprints.</summary>
public static class SshHostKeys
{
    /// <summary>The key types a target may pin, in the order they are offered: Ed25519 first.</summary>
    public static readonly IReadOnlyList<string> Types =
        ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-rsa"];

    /// <summary>
    /// The key in <paramref name="line"/>: <c>&lt;type&gt; &lt;base64&gt;</c>, optionally with a host before it (a
    /// known_hosts or <c>ssh-keyscan</c> line) and a comment after it. Null when no line of a known type is there or the
    /// key does not name its own type.
    /// </summary>
    public static BackupHostKey? Parse(string? line)
    {
        var tokens = (line ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!Types.Contains(tokens[index], StringComparer.Ordinal))
            {
                continue;
            }
            var type = tokens[index];
            var encoded = tokens[index + 1];
            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return null;
            }
            return NamesType(blob, type) ? new BackupHostKey(type, $"{type} {encoded}", Fingerprint(blob)) : null;
        }
        return null;
    }

    /// <summary>The keys of <c>ssh-keyscan</c>'s output, one per type, Ed25519 first; comment lines are skipped.</summary>
    public static IReadOnlyList<BackupHostKey> ParseScan(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var keys = output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(Parse)
            .OfType<BackupHostKey>()
            .DistinctBy(key => key.Type)
            .OrderBy(key => Types.ToList().IndexOf(key.Type))
            .ToList();
        return keys;
    }

    /// <summary>The SHA-256 fingerprint of a key blob as OpenSSH prints it: <c>SHA256:</c> and base64 without padding.</summary>
    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    // A key blob starts with its own type as an SSH string (a big-endian length, then the name).
    private static bool NamesType(byte[] blob, string type)
    {
        if (blob.Length < 4)
        {
            return false;
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        return length == type.Length && blob.Length >= 4 + length && Encoding.ASCII.GetString(blob, 4, (int)length) == type;
    }
}
