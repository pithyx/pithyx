using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Sparse files in a temporary folder for a throwaway ZFS pool, driven through the real <see cref="ProcessCommandRunner"/>.
/// Tests that use it run only as root on a disposable CI runner with <c>PITHYX_STORAGE_INTEGRATION=1</c>; they never name
/// a block device except the loop devices they attach themselves. Disposing destroys the pool, detaches the loop devices
/// and deletes the files.
/// </summary>
internal sealed class FileVdevs : IAsyncDisposable
{
    public const string Category = "StorageIntegration";
    private const long FileSize = 256L * 1024 * 1024;

    private readonly string _directory;
    private readonly List<string> _loopDevices = [];

    private FileVdevs(string directory, IReadOnlyList<string> files)
    {
        _directory = directory;
        Files = files;
    }

    public ProcessCommandRunner Commands { get; } = new(NullLogger<ProcessCommandRunner>.Instance);

    public string PoolName { get; } = $"pithyxci-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";

    public IReadOnlyList<string> Files { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Skips the calling test unless the storage integration job runs it.</summary>
    public static void RequireEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("PITHYX_STORAGE_INTEGRATION") == "1",
            "Real ZFS tests run only in the CI storage job (PITHYX_STORAGE_INTEGRATION=1, as root).");

    public static FileVdevs Create(int count)
    {
        var directory = Directory.CreateTempSubdirectory("pithyxci-").FullName;
        var files = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(directory, $"vdev{i}.img");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(FileSize);
            }
            files.Add(path);
        }
        return new FileVdevs(directory, files);
    }

    /// <summary>Creates the pool on the files without mounting it. <paramref name="vdevType"/> is empty for a stripe.</summary>
    public Task CreatePoolAsync(params string[] vdevType) =>
        RunAsync("zpool", ["create", "-m", "none", PoolName, .. vdevType, .. Files]);

    /// <summary>
    /// Creates a single-file pool whose datasets mount below a temporary altroot (<see cref="AltRoot"/>), never on the
    /// host's own folders, with the Pithyx dataset layout of a new pool.
    /// </summary>
    public async Task CreateLayoutPoolAsync()
    {
        Directory.CreateDirectory(AltRoot);
        await RunAsync("zpool", ["create", "-R", AltRoot, "-O", "canmount=off", PoolName, .. Files]);
        foreach (var arguments in ZfsPlans.Datasets(PoolName))
        {
            await RunAsync("zfs", ["create", .. arguments]);
        }
    }

    /// <summary>Where a pool made by <see cref="CreateLayoutPoolAsync"/> mounts its datasets.</summary>
    public string AltRoot => Path.Combine(_directory, "root");

    public async Task<string> AttachLoopDeviceAsync(string file)
    {
        var device = (await RunAsync("losetup", ["--find", "--show", file])).Trim();
        _loopDevices.Add(device);
        return device;
    }

    public async Task<string> RunAsync(string command, IReadOnlyList<string> arguments)
    {
        var result = await Commands.RunAsync(new CommandRequest(command, arguments), Token);
        Assert.True(result.ExitCode == 0, $"{command} {string.Join(' ', arguments)} failed with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    public async ValueTask DisposeAsync()
    {
        // Fails harmlessly when the pool was never created or is already destroyed.
        await Commands.RunAsync(new CommandRequest("zpool", ["destroy", "-f", PoolName]), CancellationToken.None);
        foreach (var device in _loopDevices)
        {
            await Commands.RunAsync(new CommandRequest("losetup", ["-d", device]), CancellationToken.None);
        }
        Directory.Delete(_directory, recursive: true);
    }
}
