using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;

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

    /// <summary>The temporary folder of the files; imports in tests search only here (<c>-d</c>).</summary>
    public string Directory => _directory;

    /// <summary>A file in the folder that is not a vdev yet, for a vdev added later.</summary>
    public string AddFile(string name)
    {
        var path = Path.Combine(_directory, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(FileSize);
        return path;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Skips the calling test unless the storage integration job runs it.</summary>
    public static void RequireEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("PITHYX_STORAGE_INTEGRATION") == "1",
            "Real ZFS tests run only in the CI storage job (PITHYX_STORAGE_INTEGRATION=1, as root).");

    public static FileVdevs Create(int count)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("pithyxci-").FullName;
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

    /// <summary>
    /// Runs a command like <see cref="RunAsync"/> and, when <c>PITHYX_RECORD_DIR</c> is set, writes its output as a fixture
    /// (<c>&lt;dir&gt;/&lt;scenario&gt;/&lt;id&gt;.json</c>, <c>.stdout</c>, <c>.stderr</c>) with the random pool name
    /// replaced by <paramref name="alias"/> and the temporary folder by <c>/tmp/pithyxci-fixture</c>. JSON output keeps only
    /// this pool and its datasets, never other pools of the machine. A failing command is recorded too and returned, not
    /// asserted.
    /// </summary>
    public async Task<CommandResult> RecordAsync(string scenario, string id, string alias, string command, IReadOnlyList<string> arguments)
    {
        var result = await Commands.RunAsync(new CommandRequest(command, arguments, TimeSpan.FromMinutes(5)), Token);
        if (Environment.GetEnvironmentVariable("PITHYX_RECORD_DIR") is { Length: > 0 } root)
        {
            string Anonymize(string text) => text.Replace(_directory, "/tmp/pithyxci-fixture", StringComparison.Ordinal).Replace(PoolName, alias, StringComparison.Ordinal);
            var folder = System.IO.Directory.CreateDirectory(Path.Combine(root, scenario)).FullName;
            var definition = new { command, args = arguments.Select(Anonymize), exitCode = result.ExitCode };
            await File.WriteAllTextAsync(Path.Combine(folder, $"{id}.json"), JsonSerializer.Serialize(definition, _indented) + "\n", Token);
            await File.WriteAllTextAsync(Path.Combine(folder, $"{id}.stdout"), OnlyPool(Anonymize(result.StandardOutput), alias), Token);
            if (result.StandardError.Length > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, $"{id}.stderr"), Anonymize(result.StandardError), Token);
            }
        }
        return result;
    }

    private static string OnlyPool(string output, string pool)
    {
        if (!output.TrimStart().StartsWith('{'))
        {
            return output;
        }
        var document = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsObject();
        foreach (var key in new[] { "pools", "datasets" })
        {
            if (document[key] is System.Text.Json.Nodes.JsonObject entries)
            {
                foreach (var name in entries.Select(entry => entry.Key).Where(name => name != pool && !name.StartsWith(pool + "/", StringComparison.Ordinal)).ToList())
                {
                    entries.Remove(name);
                }
            }
        }
        return document.ToJsonString() + "\n";
    }

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    public async ValueTask DisposeAsync()
    {
        // Fails harmlessly when the pool was never created or is already destroyed.
        await Commands.RunAsync(new CommandRequest("zpool", ["destroy", "-f", PoolName]), CancellationToken.None);
        foreach (var device in _loopDevices)
        {
            await Commands.RunAsync(new CommandRequest("losetup", ["-d", device]), CancellationToken.None);
        }
        System.IO.Directory.Delete(_directory, recursive: true);
    }
}
