using System.Security.Cryptography;
using System.Text;

namespace Pithyx.Daemon.Auth;

/// <summary>Random tokens and human-typeable codes.</summary>
public static class Secrets
{
    // Crockford Base32: no I, L, O, U, so codes survive being read aloud or written down.
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A URL-safe random token with the given number of random bytes.</summary>
    public static string NewToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>A random Crockford Base32 code in groups, for example <c>7K3M-QX2P-...</c>.</summary>
    public static string NewCode(int characters, int groupSize)
    {
        var builder = new StringBuilder(characters + (characters / groupSize));
        for (var i = 0; i < characters; i++)
        {
            if (i > 0 && i % groupSize == 0)
            {
                builder.Append('-');
            }
            builder.Append(CrockfordAlphabet[RandomNumberGenerator.GetInt32(CrockfordAlphabet.Length)]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Canonical form of a typed code: upper case, no separators, O read as 0 and I or L as 1. A leading
    /// <paramref name="prefix"/> is dropped when <paramref name="length"/> characters follow it, so a code is accepted with
    /// and without its prefix.
    /// </summary>
    public static string NormalizeCode(string input, string prefix = "", int length = 0)
    {
        ArgumentNullException.ThrowIfNull(input);
        var builder = new StringBuilder(input.Length);
        foreach (var c in input.ToUpperInvariant())
        {
            switch (c)
            {
                case '-' or ' ':
                    continue;
                case 'O':
                    builder.Append('0');
                    break;
                case 'I' or 'L':
                    builder.Append('1');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        var code = builder.ToString();
        return prefix.Length > 0 && code.Length == prefix.Length + length && code.StartsWith(prefix, StringComparison.Ordinal)
            ? code[prefix.Length..]
            : code;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
