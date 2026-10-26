using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage.Encryption;

/// <summary>
/// Reads and writes the key files on a stick (D385). The stick is mounted on one folder of root's for each access and
/// unmounted again right after, so it can be pulled at any other time; one access runs at a time (the caller's gate).
/// </summary>
/// <param name="commands">Runs mount and umount.</param>
/// <param name="mountPoint"><c>/run/pithyx/usb-key</c> on a box.</param>
/// <param name="logger">Logs failures, never a key.</param>
public sealed partial class UsbKeyDrive(ICommandRunner commands, string mountPoint, ILogger<UsbKeyDrive> logger)
{
    public string MountPoint { get; } = mountPoint;

    /// <summary>
    /// The key files of the given pools that the stick holds, by pool GUID: the key in hex, or null for a file that is not
    /// a key (damaged or edited). Throws when the stick cannot be mounted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> ReadAsync(UsbStick stick, IReadOnlyCollection<string> poolGuids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stick);
        ArgumentNullException.ThrowIfNull(poolGuids);
        await MountAsync(UsbKeys.MountReadOnly(stick, Prepared()), stick, cancellationToken);
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (var guid in poolGuids)
            {
                var path = Path.Combine(MountPoint, UsbKeys.Folder, UsbKeys.FileName(guid));
                try
                {
                    if (File.Exists(path))
                    {
                        // A key file is 65 bytes; anything much larger is not one and is not read whole.
                        keys[guid] = new FileInfo(path).Length <= 1024 ? UsbKeys.ParseKey(await File.ReadAllTextAsync(path, cancellationToken)) : null;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    LogReadFailed(stick.Device, guid, exception);
                    keys[guid] = null;
                }
            }
        }
        finally
        {
            await UnmountAsync(stick);
        }
        return keys;
    }

    /// <summary>
    /// Writes the pool's key file (beside and then renamed, so an older key is replaced in one step), reads it back and
    /// unmounts. Any failure ends as <see cref="ErrorCodes.StorageUsbWriteFailed"/>; the stick's other files stay as they were.
    /// </summary>
    public async Task WriteAsync(UsbStick stick, string poolGuid, string hexKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stick);
        await MountAsync(UsbKeys.MountWritable(stick, Prepared()), stick, cancellationToken);
        string? problem = null;
        try
        {
            var folder = Path.Combine(MountPoint, UsbKeys.Folder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, UsbKeys.FileName(poolGuid));
            var temporary = $"{path}.new";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                // FAT and exFAT have no owners and ignore it; ext keeps the file for root alone.
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, options))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(EncryptionKeyText(hexKey));
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            if (UsbKeys.ParseKey(await File.ReadAllTextAsync(path, cancellationToken)) != hexKey)
            {
                problem = "the key read back differs";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(stick.Device, exception);
            problem = exception.Message;
        }
        // Unmounting writes what the stick still caches; until it returns, pulling the stick could lose the key.
        if (!await UnmountAsync(stick))
        {
            problem ??= "the stick could not be unmounted";
        }
        if (problem is not null)
        {
            throw new PithyxException(ErrorCodes.StorageUsbWriteFailed, new Dictionary<string, object?> { ["stick"] = UsbKeys.Describe(stick), ["reason"] = problem });
        }
    }

    private static string EncryptionKeyText(string hexKey) => UsbKeys.ParseKey(hexKey) is { } key ? key + "\n" : throw new ArgumentException("Not a hex pool key.", nameof(hexKey));

    private string Prepared()
    {
        Directory.CreateDirectory(MountPoint);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(MountPoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return MountPoint;
    }

    private async Task MountAsync(CommandRequest mount, UsbStick stick, CancellationToken cancellationToken)
    {
        CommandResult result;
        try
        {
            result = await commands.RunAsync(mount, cancellationToken);
        }
        catch (TimeoutException)
        {
            result = new CommandResult(-1, "", "mount timed out", TimeSpan.Zero);
            // A mount that timed out may still finish; it must not stay behind.
            await UnmountAsync(stick);
        }
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.StorageUsbUnreadable, new Dictionary<string, object?> { ["stick"] = UsbKeys.Describe(stick), ["reason"] = result.StandardError.Trim() });
        }
    }

    private async Task<bool> UnmountAsync(UsbStick stick)
    {
        try
        {
            var result = await commands.RunAsync(UsbKeys.Unmount(MountPoint), CancellationToken.None);
            if (result.ExitCode == 0)
            {
                return true;
            }
            LogUnmountFailed(stick.Device, result.ExitCode, result.StandardError);
        }
        catch (TimeoutException exception)
        {
            LogUnmountFailed(stick.Device, -1, exception.Message);
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the key file of pool {PoolGuid} on {Device} failed")]
    private partial void LogReadFailed(string device, string poolGuid, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing a pool key to {Device} failed")]
    private partial void LogWriteFailed(string device, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unmounting {Device} failed with {ExitCode}: {StandardError}")]
    private partial void LogUnmountFailed(string device, int exitCode, string standardError);
}
