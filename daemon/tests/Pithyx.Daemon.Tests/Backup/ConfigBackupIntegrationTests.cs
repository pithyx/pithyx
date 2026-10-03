using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Backup;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Backup;

/// <summary>
/// The password part of a configuration backup for real, as root on the dev VM (D293): two throwaway accounts get a
/// password, the backup reads their hashes, both get another password and one loses its Samba account, and the restore
/// gives both the hashes of the backup again. The accounts leave the system and the Samba passdb at the end.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ConfigBackupIntegrationTests
{
    private static readonly string[] _users = ["pxit-bk-ada", "pxit-bk-tom"];
    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<CommandResult> RunAsync(string program, params string[] arguments) =>
        await _commands.RunAsync(new CommandRequest(program, arguments, TimeSpan.FromMinutes(1)), Token);

    private static async Task<IReadOnlyDictionary<string, string>> PassdbAsync() =>
        (await RunAsync("pdbedit", "-L", "-w")).StandardOutput.Split('\n')
            .Select(line => line.Split(':'))
            .Where(fields => fields.Length >= 5 && _users.Contains(fields[0]))
            .ToDictionary(fields => fields[0], fields => fields[3], StringComparer.Ordinal);

    [Fact]
    public async Task ARestoreGivesTheAccountsTheirLinuxAndSambaPasswordsBack()
    {
        FileVdevs.RequireEnabled();
        Assert.SkipUnless(FileServiceSync.Installed("smbd"), "Needs samba (tools/dev-vm/provision.sh).");
        var work = Directory.CreateTempSubdirectory("pithyxci-backup-").FullName;
        var options = new BackupOptions(Apply: true, TlsDirectory: Path.Combine(work, "tls"), SshDirectory: null, ShadowPath: "/etc/shadow", WorkDirectory: Path.Combine(work, "run"));
        var passwords = new FileServicePasswords(_commands, new AccountOptions("/etc/passwd", Apply: true), NullLogger<FileServicePasswords>.Instance);
        try
        {
            var uid = 20960;
            foreach (var user in _users)
            {
                await RunAsync("useradd", "--uid", (uid++).ToString(System.Globalization.CultureInfo.InvariantCulture), "--gid", "users", "--no-user-group", "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", user);
                await passwords.SetAsync(user, "pxit-first-meadow-4", Token);
            }
            var names = _users.ToHashSet(StringComparer.Ordinal);
            var backup = await ConfigBackupService.ReadPasswordsAsync(_commands, options, names, Token);
            var backupShadow = ShadowParser.Hashes(Encoding.UTF8.GetString(backup[ConfigBackupService.ShadowEntry]).Replace("\n", ":0:0:99999:7:::\n", StringComparison.Ordinal), names);
            Assert.Equal(_users, backupShadow.Keys.Order(StringComparer.Ordinal));
            var backupPassdb = await PassdbAsync();
            Assert.Equal(2, backupPassdb.Count);

            foreach (var user in _users)
            {
                await passwords.SetAsync(user, "pxit-second-harbor-9", Token);
            }
            await RunAsync("pdbedit", "-x", "-u", _users[1]);
            Assert.NotEqual(backupShadow[_users[0]], ShadowParser.Hashes(await File.ReadAllTextAsync("/etc/shadow", Token), names)[_users[0]]);

            var failed = await ConfigBackupService.RestorePasswordsAsync(_commands, passwords, options, backup, names, Token);

            Assert.Empty(failed);
            var shadow = ShadowParser.Hashes(await File.ReadAllTextAsync("/etc/shadow", Token), names);
            Assert.All(_users, user => Assert.Equal(backupShadow[user], shadow[user]));
            Assert.Equal(backupPassdb, await PassdbAsync());
            Assert.All(_users, user => Assert.True(passwords.HasPassword(user, passwords.LinuxWithPassword()), user));
            Assert.Empty(Directory.EnumerateFiles(options.WorkDirectory));
        }
        finally
        {
            foreach (var user in _users)
            {
                await RunAsync("pdbedit", "-x", "-u", user);
                await RunAsync("userdel", user);
            }
            Directory.Delete(work, recursive: true);
        }
    }
}
