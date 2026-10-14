using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// Argon2id (libsodium through NSec) in PHC string format, so the parameters travel with every hash and can be raised
/// later: hashes with older parameters are replaced on the next successful login.
/// </summary>
/// <remarks>
/// m = 64 MiB, t = 3, p = 1 takes about 150 ms on an Intel N100. At most two hashes run at the same time, which keeps
/// the memory peak at 128 MiB even under a burst of logins and inside the daemon's 200 MB RSS budget.
/// </remarks>
public sealed class PasswordHasher : IDisposable
{
    public const int MemoryKib = 64 * 1024;
    public const int Passes = 3;
    public const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "$argon2id$v=19$";

    private readonly SemaphoreSlim _slots = new(2);
    private readonly Lazy<Task<string>> _dummyHash;
    private readonly long _memoryKib;
    private readonly long _passes;

    public PasswordHasher()
        : this(MemoryKib, Passes)
    {
    }

    /// <summary>Weaker parameters for the in-memory test daemon, where hundreds of sign-ins would otherwise dominate.</summary>
    internal PasswordHasher(long memoryKib, long passes)
    {
        _memoryKib = memoryKib;
        _passes = passes;
        _dummyHash = new Lazy<Task<string>>(() => HashAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)), CancellationToken.None));
    }

    public Task<string> HashAsync(string password, CancellationToken cancellationToken) =>
        HashAsync(password, _memoryKib, _passes, cancellationToken);

    internal async Task<string> HashAsync(string password, long memoryKib, long passes, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt, memoryKib, passes, cancellationToken);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}m={memoryKib},t={passes},p={Parallelism}${Encode(salt)}${Encode(hash)}");
    }

    /// <summary>Checks a password. <c>NeedsRehash</c> is true when the hash uses weaker parameters than today.</summary>
    public async Task<(bool Valid, bool NeedsRehash)> VerifyAsync(string password, string phc, CancellationToken cancellationToken)
    {
        if (!TryParse(phc, out var memory, out var passes, out var salt, out var expected))
        {
            return (false, false);
        }
        var actual = await DeriveAsync(password, salt, memory, passes, cancellationToken);
        var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
        return (valid, valid && (memory < _memoryKib || passes < _passes));
    }

    /// <summary>Spends the same time as a real check, so unknown user names cannot be told apart by timing.</summary>
    public async Task WasteTimeAsync(string password, CancellationToken cancellationToken) =>
        await VerifyAsync(password, await _dummyHash.Value, cancellationToken);

    /// <summary>A 32-byte key from a password, for example to encrypt a configuration backup; shares the hash slots.</summary>
    public Task<byte[]> DeriveKeyAsync(string password, byte[] salt, long memoryKib, long passes, CancellationToken cancellationToken) =>
        DeriveAsync(password, salt, memoryKib, passes, cancellationToken);

    public void Dispose() => _slots.Dispose();

    private async Task<byte[]> DeriveAsync(string password, byte[] salt, long memoryKib, long passes, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(
                () =>
                {
                    var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
                    {
                        DegreeOfParallelism = Parallelism,
                        MemorySize = memoryKib,
                        NumberOfPasses = passes,
                    });
                    return algorithm.DeriveBytes(Encoding.UTF8.GetBytes(password), salt, HashBytes);
                },
                cancellationToken);
        }
        finally
        {
            _slots.Release();
            ServerTiming.AddArgon2(Stopwatch.GetElapsedTime(started));
        }
    }

    private static bool TryParse(string phc, out long memory, out long passes, out byte[] salt, out byte[] hash)
    {
        memory = passes = 0;
        salt = hash = [];
        // $argon2id$v=19$m=65536,t=3,p=1$<salt>$<hash>
        var parts = phc.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }
        foreach (var setting in parts[3].Split(','))
        {
            var pair = setting.Split('=');
            if (pair.Length != 2 || !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }
            switch (pair[0])
            {
                case "m":
                    memory = value;
                    break;
                case "t":
                    passes = value;
                    break;
                case "p" when value != Parallelism:
                    return false;
                default:
                    break;
            }
        }
        try
        {
            salt = Decode(parts[4]);
            hash = Decode(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }
        return memory > 0 && passes > 0 && hash.Length == HashBytes;
    }

    // PHC strings use standard base64 without padding.
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Decode(string text) => Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
}
