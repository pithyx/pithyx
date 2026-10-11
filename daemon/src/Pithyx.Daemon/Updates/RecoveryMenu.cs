using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Network;

namespace Pithyx.Daemon.Updates;

/// <summary>
/// The console menu of the GRUB entry "Pithyx recovery" and of every snapshot's entry (D338), on tty1 without a
/// password: whoever has the console controls the box anyway (GRUB has no password, pools are unencrypted in 1.0), so
/// the menu asks for confirmations against mistakes instead, checks every file's signature and logs every action. It
/// rolls back to a snapshot, installs an update file from a USB stick, resets the network to DHCP and restarts, through
/// the same code as Settings. It is also the only place that switches the update source (D339), for example to a fork:
/// from a USB stick, from the file an administrator saved in Settings, or typed. Started from a snapshot's entry
/// (read-only), it offers only the rollback to it and the restart; <c>pithyx.rollback=auto:&lt;snapshot&gt;</c> (an update whose new version never confirmed its boot) rolls
/// back without asking.
/// </summary>
public sealed partial class RecoveryMenu(
    UpdateSystem system,
    ICommandRunner commands,
    ILoggerFactory loggers,
    TextReader input,
    TextWriter output,
    string sourceFile = UpdateSourceConfig.BoxFile)
{
    private readonly ILogger<RecoveryMenu> _logger = loggers.CreateLogger<RecoveryMenu>();

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var cmdline = File.Exists("/proc/cmdline") ? await File.ReadAllTextAsync("/proc/cmdline", cancellationToken) : "";
        var rollback = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(argument => argument.StartsWith("pithyx.rollback=", StringComparison.Ordinal))?["pithyx.rollback=".Length..];
        var fromSnapshot = cmdline.Contains("rootflags=subvol=@snapshots/", StringComparison.Ordinal);

        if (rollback is not null && rollback.StartsWith("auto:", StringComparison.Ordinal))
        {
            await LineAsync($"The update did not start correctly. Restoring the previous version (snapshot {rollback[5..]}) now.");
            await RollBackAsync(rollback[5..], automatic: true, cancellationToken);
            return 0;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await output.WriteAsync("\u001b[H\u001b[2J");
            await LineAsync($"Pithyx recovery ({system.InstalledVersion()})");
            await LineAsync("");
            await LineAsync("This menu needs no password: whoever can use this console controls the box.");
            await LineAsync("Secure the console and any remote management (BMC, iLO, IPMI) of this machine.");
            await LineAsync("");
            var snapshots = await system.WithTopAsync(top => Task.FromResult(UpdateSystem.ReadSnapshots(top)), cancellationToken);
            if (rollback is not null && snapshots.FirstOrDefault(snapshot => snapshot.Name == rollback) is { } chosen)
            {
                await LineAsync($"You started the snapshot of Pithyx {chosen.Version} from {chosen.CreatedAt:yyyy-MM-dd HH:mm} UTC.");
            }
            await LineAsync("  [1] Roll back to a snapshot");
            if (!fromSnapshot)
            {
                await LineAsync("  [2] Install an update file from a USB stick");
                await LineAsync("  [3] Reset the network to DHCP");
                await LineAsync("  [5] Change the update source");
            }
            await LineAsync("  [4] Restart");
            await LineAsync("");
            await output.WriteAsync("Choice: ");
            switch ((await input.ReadLineAsync(cancellationToken))?.Trim())
            {
                case "1":
                    await ChooseSnapshotAsync(snapshots, rollback, cancellationToken);
                    break;
                case "2" when !fromSnapshot:
                    await InstallFromUsbAsync(cancellationToken);
                    break;
                case "3" when !fromSnapshot:
                    await ResetNetworkAsync(cancellationToken);
                    break;
                case "5" when !fromSnapshot:
                    await ChangeSourceAsync(cancellationToken);
                    break;
                case "4":
                    LogAction("restart", "");
                    await system.RebootAsync();
                    return 0;
                case null:
                    return 0;
                default:
                    break;
            }
        }
        return 0;
    }

    private async Task ChooseSnapshotAsync(IReadOnlyList<SnapshotInfo> snapshots, string? preselected, CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0)
        {
            await PauseAsync("There is no snapshot yet. Snapshots are taken before every update.");
            return;
        }
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            await LineAsync($"  [{index + 1}] Pithyx {snapshot.Version} from {snapshot.CreatedAt:yyyy-MM-dd HH:mm} UTC ({snapshot.Reason}){(snapshot.Name == preselected ? " <- started" : "")}");
        }
        await output.WriteAsync("Snapshot number: ");
        if (!int.TryParse(await input.ReadLineAsync(cancellationToken), out var number) || number < 1 || number > snapshots.Count)
        {
            await PauseAsync("No snapshot chosen; nothing changed.");
            return;
        }
        var chosen = snapshots[number - 1];
        await LineAsync("The system goes back to this snapshot, settings changed since then are lost. Pools and their files stay as they are.");
        await output.WriteAsync($"Type the version {chosen.Version} to roll back: ");
        if ((await input.ReadLineAsync(cancellationToken))?.Trim() != chosen.Version)
        {
            await PauseAsync("The version did not match; nothing changed.");
            return;
        }
        await RollBackAsync(chosen.Name, automatic: false, cancellationToken);
    }

    /// <summary>The rollback with the history of the system it replaces carried over, then the restart.</summary>
    private async Task RollBackAsync(string snapshot, bool automatic, CancellationToken cancellationToken)
    {
        var (version, history, trial, target) = await system.WithTopAsync(async top =>
        {
            var root = Path.Combine(top, SystemSnapshots.Root);
            var release = Path.Combine(root, system.Paths.Release.TrimStart('/'));
            var current = File.Exists(release)
                ? File.ReadLines(release).FirstOrDefault(line => line.StartsWith("PITHYX_VERSION=", StringComparison.Ordinal))?["PITHYX_VERSION=".Length..] ?? "unknown"
                : "unknown";
            var entries = await UpdateCommands.HistoryAsync(Path.Combine(root, UpdateCommands.DatabasePath.TrimStart('/')), cancellationToken);
            var waiting = UpdateSystem.ReadJson<UpdateTrial>(Path.Combine(root, system.Paths.TrialFile.TrimStart('/')));
            var info = UpdateSystem.ReadSnapshots(top).FirstOrDefault(entry => entry.Name == snapshot);
            return (current, entries, waiting, info);
        }, cancellationToken);
        if (target is null)
        {
            await PauseAsync($"The snapshot {snapshot} does not exist.");
            return;
        }
        UpdateOutcome outcome;
        if (automatic && trial is not null)
        {
            outcome = new UpdateOutcome(trial.HistoryId, "rolled_back", ErrorCodes.UpdateApplyFailed,
                new Dictionary<string, object?> { ["step"] = "health", ["command"] = "boot", ["exitCode"] = 1 }, DateTimeOffset.UtcNow, history);
        }
        else
        {
            var id = history.Count == 0 ? 1 : history.Max(entry => entry.Id) + 1;
            var entry = new UpdateHistoryEntry(id, "rollback", "recovery", version, target.Version, snapshot, "succeeded", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            outcome = new UpdateOutcome(id, "succeeded", null, null, DateTimeOffset.UtcNow, [.. history, entry]);
        }
        LogAction("rollback", snapshot);
        var layout = await system.LayoutAsync(cancellationToken);
        await system.RollbackAsync(snapshot, layout.FileSystemUuid ?? throw new PithyxException(ErrorCodes.UpdateSystemUnsupported), outcome, cancellationToken);
        await LineAsync($"Pithyx {target.Version} is back. Restarting.");
        await system.RebootAsync();
    }

    private async Task InstallFromUsbAsync(CancellationToken cancellationToken)
    {
        var files = await FindStickFilesAsync("*.pithyxupdate", cancellationToken);
        if (files.Count == 0)
        {
            await PauseAsync("No .pithyxupdate file was found on a USB stick. Put the file into the stick's top folder and try again.");
            return;
        }
        for (var index = 0; index < files.Count; index++)
        {
            await LineAsync($"  [{index + 1}] {files[index]}");
        }
        await output.WriteAsync("File number: ");
        if (!int.TryParse(await input.ReadLineAsync(cancellationToken), out var number) || number < 1 || number > files.Count)
        {
            await PauseAsync("No file chosen; nothing changed.");
            return;
        }
        await LineAsync("Checking the file (signature and every package)...");
        VerifiedUpdate verified;
        try
        {
            await using var stream = File.OpenRead(files[number - 1]);
            verified = await UpdateFile.ReadAsync(stream, system.Paths.StagedDirectory, await ExpectationAsync(cancellationToken), cancellationToken);
        }
        catch (PithyxException exception)
        {
            await PauseAsync($"The file was refused: {exception.Code} {JsonSerializer.Serialize(exception.Parameters)}");
            return;
        }
        finally
        {
            await UnmountSticksAsync();
        }
        await LineAsync($"Pithyx {verified.Manifest.Version} ({verified.Manifest.Channel}), {(verified.TrustedSignature ? "signed by a trusted key" : "UNSIGNED development file")}.");
        await LineAsync("A snapshot is taken first; the box restarts afterwards and rolls back by itself when the new version does not start.");
        await output.WriteAsync("Type install to install it: ");
        if ((await input.ReadLineAsync(cancellationToken))?.Trim() != "install")
        {
            await PauseAsync("Nothing changed.");
            return;
        }
        var installed = system.InstalledVersion();
        var historyId = await InsertHistoryAsync(installed, verified.Manifest.Version, cancellationToken);
        LogAction("install", verified.Manifest.Version);
        await system.LaunchApplyAsync(new UpdateApplyRequest(historyId, verified.Directory, installed, verified.Manifest.Version, false,
            await UpdateCommands.HistoryAsync(UpdateCommands.DatabasePath, cancellationToken)), cancellationToken);
        while (await system.WaitForProgressAsync(cancellationToken) is var progress)
        {
            if (progress is null)
            {
                continue;
            }
            await LineAsync($"  {progress.Step}: {progress.State}");
            if (progress.State is "restarting" or "rolled_back")
            {
                await LineAsync("Restarting.");
                return;
            }
            if (progress.State == "failed")
            {
                await PauseAsync($"The update failed: {progress.ErrorCode} {JsonSerializer.Serialize(progress.ErrorParams)}. Nothing changed.");
                return;
            }
        }
    }

    private async Task<UpdateExpectation> ExpectationAsync(CancellationToken cancellationToken)
    {
        var channel = UpdateDocuments.Stable;
        try
        {
            await using var connection = await new SqliteConnectionFactory(UpdateCommands.DatabasePath).OpenAsync(cancellationToken);
            channel = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = UpdateService.ChannelSetting }) ?? channel;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A box without its database still takes stable files.
        }
        var environment = DeploymentEnvironment.Load(DeploymentEnvironment.DefaultDirectory);
        var keyring = UpdateSources.Keyring(UpdateSourceConfig.Load(sourceFile, UpdateSourceConfig.Default), UpdateKeys.Project);
        return new UpdateExpectation(keyring, channel, environment.Kind == DeploymentKind.Dev, system.InstalledVersion(), UpdateFile.BoxArchitecture);
    }

    private static async Task<long> InsertHistoryAsync(string from, string to, CancellationToken cancellationToken)
    {
        await using var connection = await new SqliteConnectionFactory(UpdateCommands.DatabasePath).OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO system_updates (kind, source, from_version, to_version, state, started_at) VALUES ('update', 'recovery', @from, @to, 'running', @now) RETURNING id",
            new { from, to, now = Auth.SessionService.Format(DateTimeOffset.UtcNow) });
    }

    private const string StickRoot = "/run/pithyx/usb";

    /// <summary>
    /// Mounts every partition with a file system a stick has (FAT, exFAT, ext, NTFS) read-only and lists the files of
    /// <paramref name="pattern"/> in its top folder. The system disks (RAID members, the ESPs) are never touched.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindStickFilesAsync(string pattern, CancellationToken cancellationToken)
    {
        var listing = await commands.RunAsync(new CommandRequest("lsblk", ["--json", "--paths", "--list", "--output", "PATH,FSTYPE,MOUNTPOINT"], TimeSpan.FromSeconds(30)), cancellationToken);
        if (listing.ExitCode != 0)
        {
            return [];
        }
        using var document = JsonDocument.Parse(listing.StandardOutput);
        var files = new List<string>();
        var index = 0;
        foreach (var device in document.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            var fstype = device.GetProperty("fstype").GetString();
            var mounted = device.GetProperty("mountpoint").GetString();
            if (fstype is not ("vfat" or "exfat" or "ext4" or "ext3" or "ext2" or "ntfs") || mounted is not null)
            {
                continue;
            }
            var mountPoint = Path.Combine(StickRoot, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(mountPoint);
            var mount = await commands.RunAsync(new CommandRequest("mount", ["-o", "ro,nosuid,nodev,noexec", device.GetProperty("path").GetString()!, mountPoint], TimeSpan.FromSeconds(30)), cancellationToken);
            if (mount.ExitCode == 0)
            {
                files.AddRange(Directory.EnumerateFiles(mountPoint, pattern).Order(StringComparer.Ordinal));
            }
        }
        return files;
    }

    private async Task UnmountSticksAsync()
    {
        if (!Directory.Exists(StickRoot))
        {
            return;
        }
        foreach (var mountPoint in Directory.EnumerateDirectories(StickRoot))
        {
            await commands.RunAsync(new CommandRequest("umount", [mountPoint], TimeSpan.FromSeconds(30)), CancellationToken.None);
        }
    }

    private async Task ResetNetworkAsync(CancellationToken cancellationToken)
    {
        await output.WriteAsync("Reset every network card to DHCP and remove bonds, VLANs and name servers? Type yes: ");
        if ((await input.ReadLineAsync(cancellationToken))?.Trim() != "yes")
        {
            await PauseAsync("Nothing changed.");
            return;
        }
        var host = new NetworkHost(commands, DaemonMode.Real, DeploymentEnvironment.Load(DeploymentEnvironment.DefaultDirectory), loggers.CreateLogger<NetworkHost>());
        await NetworkService.ResetStoredAsync(new SqliteConnectionFactory(UpdateCommands.DatabasePath), host, cancellationToken);
        LogAction("network-reset", "");
        await PauseAsync("The network is back on DHCP; it applies at the next start.");
    }

    private string CandidateFile => Path.Combine(system.Paths.State, UpdateSources.CandidateFileName);

    /// <summary>
    /// Switches the update source (D339): after a switch the box trusts only the new source's keys. Every way ends in
    /// the same check of <c>pithyx-source.json</c> and the same confirmation, the source's name typed.
    /// </summary>
    private async Task ChangeSourceAsync(CancellationToken cancellationToken)
    {
        var current = UpdateSources.Describe(UpdateSourceConfig.Load(sourceFile, UpdateSourceConfig.Default), UpdateKeys.Project);
        var candidate = UpdateSources.ReadCandidate(CandidateFile);
        await LineAsync("");
        await LineAsync($"This box follows the update source {current.Name}{(current.Official ? " (the Pithyx project)" : "")}: {current.Url}");
        await LineAsync("Whoever signs a source's updates can change everything on this box. Switch only to a source you trust.");
        await LineAsync("  [1] Read pithyx-source.json from a USB stick");
        if (candidate is var (saved, savedBy, savedAt))
        {
            await LineAsync($"  [2] Use the file saved in Settings: {saved.Name} (by {savedBy}, {savedAt:yyyy-MM-dd HH:mm} UTC)");
        }
        await LineAsync("  [3] Type the source");
        if (!current.Official)
        {
            await LineAsync("  [4] Back to the Pithyx project");
        }
        await LineAsync("  [0] Back");
        await output.WriteAsync("Choice: ");
        switch ((await input.ReadLineAsync(cancellationToken))?.Trim())
        {
            case "1":
                await SourceFromStickAsync(cancellationToken);
                break;
            case "2" when candidate is var (chosen, _, _):
                await ConfirmSourceAsync(chosen, cancellationToken);
                break;
            case "3":
                await TypedSourceAsync(cancellationToken);
                break;
            case "4" when !current.Official:
                await LineAsync("The box then trusts only the keys of the Pithyx project again and reads its releases.");
                await output.WriteAsync($"Type {UpdateSources.ProjectName} to switch back: ");
                if ((await input.ReadLineAsync(cancellationToken))?.Trim() != UpdateSources.ProjectName)
                {
                    await PauseAsync("Nothing changed.");
                    return;
                }
                await UpdateSources.SwitchAsync(sourceFile, CandidateFile, null, cancellationToken);
                LogAction("source", UpdateSources.ProjectName);
                await PauseAsync("The box follows the Pithyx project from its next start. Restart with [4].");
                break;
            default:
                break;
        }
    }

    private async Task SourceFromStickAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            var files = await FindStickFilesAsync("pithyx-source*.json", cancellationToken);
            if (files.Count == 0)
            {
                await PauseAsync("No pithyx-source.json was found on a USB stick. Put the file into the stick's top folder and try again.");
                return;
            }
            for (var index = 0; index < files.Count; index++)
            {
                await LineAsync($"  [{index + 1}] {files[index]}");
            }
            await output.WriteAsync("File number: ");
            if (!int.TryParse(await input.ReadLineAsync(cancellationToken), out var number) || number < 1 || number > files.Count
                || new FileInfo(files[number - 1]).Length > UpdateSources.MaxBytes)
            {
                await PauseAsync("No file chosen; nothing changed.");
                return;
            }
            bytes = await File.ReadAllBytesAsync(files[number - 1], cancellationToken);
        }
        finally
        {
            await UnmountSticksAsync();
        }
        await CheckedSourceAsync(bytes, cancellationToken);
    }

    private async Task TypedSourceAsync(CancellationToken cancellationToken)
    {
        await output.WriteAsync("Name: ");
        var name = (await input.ReadLineAsync(cancellationToken))?.Trim() ?? "";
        await output.WriteAsync("Channel URL (https, with {channel}): ");
        var url = (await input.ReadLineAsync(cancellationToken))?.Trim() ?? "";
        var keys = new List<Apps.Catalog.TrustedKey>();
        do
        {
            await output.WriteAsync("Key id: ");
            var keyId = (await input.ReadLineAsync(cancellationToken))?.Trim() ?? "";
            await output.WriteAsync("Public key (base64): ");
            var publicKey = (await input.ReadLineAsync(cancellationToken))?.Trim() ?? "";
            await output.WriteAsync("Channels (stable,beta): ");
            var channels = (await input.ReadLineAsync(cancellationToken))?.Trim() is { Length: > 0 } typed ? typed : "stable,beta";
            keys.Add(new(keyId, publicKey, channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            await output.WriteAsync("Another key? Type yes, or press Enter: ");
        }
        while ((await input.ReadLineAsync(cancellationToken))?.Trim() == "yes" && keys.Count < 4);
        await CheckedSourceAsync(UpdateSources.Serialize(name, url, keys), cancellationToken);
    }

    private async Task CheckedSourceAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var result = UpdateSources.Check(bytes);
        if (result.Document is not { } source)
        {
            await PauseAsync($"The source was refused: {result.Issues[0].Code} {JsonSerializer.Serialize(result.Issues[0].Params)}. Nothing changed.");
            return;
        }
        await ConfirmSourceAsync(source, cancellationToken);
    }

    private async Task ConfirmSourceAsync(UpdateSourceDefinition source, CancellationToken cancellationToken)
    {
        await LineAsync("");
        await LineAsync($"Name: {source.Name}");
        await LineAsync($"URL:  {source.Url}");
        foreach (var key in source.Keys)
        {
            await LineAsync($"Key {key.KeyId} ({string.Join(", ", key.Channels)}): {key.PublicKey}");
        }
        await LineAsync("Compare every key with the one the source publishes.");
        await LineAsync("After the switch this box trusts only these keys; updates of the Pithyx project are refused until you switch back here.");
        await LineAsync("The source may change the system and its database so that going back needs a reinstall. Pools and their files are never touched.");
        await output.WriteAsync($"Type the name {source.Name} to switch: ");
        if ((await input.ReadLineAsync(cancellationToken))?.Trim() != source.Name)
        {
            await PauseAsync("The name did not match; nothing changed.");
            return;
        }
        await UpdateSources.SwitchAsync(sourceFile, CandidateFile, source, cancellationToken);
        LogAction("source", $"{source.Name} {source.Url}");
        await PauseAsync($"The box follows {source.Name} from its next start. Restart with [4].");
    }

    private Task LineAsync(string text) => output.WriteLineAsync(text);

    private async Task PauseAsync(string message)
    {
        await LineAsync(message);
        await output.WriteAsync("Press Enter.");
        await input.ReadLineAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery console: {Action} {Target}")]
    private partial void LogAction(string action, string target);
}
