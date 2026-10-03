using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.FileServices;

/// <summary>
/// SMB and SFTP for real, as root on the dev VM: a shared folder on a throwaway pool with the ACLs of D286, a Samba of
/// its own (own port, own state folders, the generated configuration) and an sshd of its own with the generated drop-in,
/// both on localhost only. Three throwaway accounts: rita may write and use SFTP, tom may read, nina has no access.
/// The services of the machine are not touched; the accounts leave the system and the Samba passdb at the end.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class FileServiceIntegrationTests
{
    private const string Password = "pxit-river-candle-7";
    private const int SmbPort = 14450;
    private const int SshPort = 12222;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<CommandResult> RunAsync(string program, params string[] arguments) =>
        await _commands.RunAsync(new CommandRequest(program, arguments, TimeSpan.FromMinutes(2)), Token);

    private static async Task<CommandResult> SmbAsync(string user, string share, string commands) =>
        await RunAsync("smbclient", $"//127.0.0.1/{share}", "-p", SmbPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", $"{user}%{Password}", "-m", "SMB3", "-c", commands);

    private static async Task<CommandResult> SftpAsync(string user, string batch, string directory)
    {
        var file = Path.Combine(directory, $"batch-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(file, batch, Token);
        return await _commands.RunAsync(new CommandRequest("sshpass",
        [
            "-e", "sftp", "-o", "BatchMode=no", "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null",
            "-o", "PubkeyAuthentication=no", "-P", SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-b", file, $"{user}@127.0.0.1",
        ], TimeSpan.FromMinutes(1)), Token);
    }

    [Fact]
    public async Task UsersGetExactlyTheirRightsOverSmbAndSftpAndSmbDeletesLandInTheRecycleBin()
    {
        FileVdevs.RequireEnabled();
        Assert.SkipUnless(FileServiceSync.Installed("smbd") && FileServiceSync.Installed("sshd") && File.Exists("/usr/bin/smbclient") && File.Exists("/usr/bin/sshpass"),
            "Needs samba, openssh-server, smbclient and sshpass (tools/dev-vm/provision.sh).");
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var work = Directory.CreateTempSubdirectory("pithyxci-fs-").FullName;
        var runRoot = $"/run/pithyxci-{Guid.NewGuid():N}";
        Process? smbd = null;
        Process? sshd = null;
        string[] users = ["pxit-rita", "pxit-tom", "pxit-nina"];
        try
        {
            var dataset = $"{vdevs.PoolName}/pithyx/shares/media";
            await vdevs.RunAsync("zfs", ["create", "-o", "acltype=posixacl", "-o", "xattr=sa", dataset]);
            var share = (await vdevs.RunAsync("zfs", ["get", "-H", "-o", "value", "mountpoint", dataset])).Trim();
            // On a box a share lives below /<pool>; here the temporary folders above it must let users pass like that.
            for (var folder = Path.GetDirectoryName(share); OperatingSystem.IsLinux() && folder is not null && folder != "/"; folder = Path.GetDirectoryName(folder))
            {
                File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.OtherExecute);
            }

            // Accounts as the reconciler makes them, with uids of the Pithyx range no dev user has.
            var uid = 20950;
            foreach (var user in users)
            {
                await RunAsync("useradd", "--uid", (uid++).ToString(System.Globalization.CultureInfo.InvariantCulture), "--gid", "users", "--no-user-group", "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", user);
            }
            await RunAsync("groupadd", "--system", "-f", SshdConfig.SftpGroup);
            await RunAsync("groupadd", "--system", "-f", SshdConfig.UsersGroup);
            await RunAsync("gpasswd", "-a", "pxit-rita", SshdConfig.SftpGroup);
            foreach (var user in users)
            {
                await RunAsync("gpasswd", "-a", user, SshdConfig.UsersGroup);
            }
            var accountOptions = new AccountOptions("/etc/passwd", Apply: true);
            var passwords = new FileServicePasswords(_commands, accountOptions, NullLogger<FileServicePasswords>.Instance);
            foreach (var user in users)
            {
                await passwords.SetAsync(user, Password, Token);
            }
            Assert.All(users, user => Assert.True(passwords.HasPassword(user, passwords.LinuxWithPassword()), user));

            // The share's ACL (D286), then the recycle bin folders Samba needs.
            // Named users stand in for the share's groups here; the groups have their own test.
            const string Entries = "u::rwX,g::---,o::---,m::rwX,u:20950:rwX,u:20951:r-X";
            foreach (var command in ShareAcls.Commands(share, Entries))
            {
                Assert.Equal(0, (await _commands.RunAsync(command, Token)).ExitCode);
            }
            var connections = new SqliteConnectionFactory(Path.Combine(work, "pithyx.db"));
            await new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
            long shareId;
            await using (var connection = await connections.OpenAsync(Token))
            {
                await TestUsers.InsertAsync(connection, "pxit-rita", id: 20950, uid: 20950);
                shareId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES ('Media', @share, @dataset, 'x', 'x') RETURNING id", new { share, dataset });
            }
            using var recycle = new SmbRecycle(connections, new TrashService(connections, new ShareService(connections, TimeProvider.System), TimeProvider.System), NullLogger<SmbRecycle>.Instance);
            recycle.Prepare(share);

            // Samba with the generated configuration, isolated from the machine's.
            var config = SmbConfig.Render("PXIT", [new SmbShare("Media", share, ["pxit-tom"], ["pxit-rita"], Encrypt: false)]);
            var state = Directory.CreateDirectory(Path.Combine(work, "samba")).FullName;
            config = config.Replace($"   include = {SmbConfig.LocalInclude}\n",
                $"""
                   smb ports = {SmbPort}
                   interfaces = lo
                   bind interfaces only = yes
                   pid directory = {state}
                   lock directory = {state}
                   state directory = {state}
                   cache directory = {state}
                   ncalrpc dir = {state}/ncalrpc

                """, StringComparison.Ordinal);
            var configPath = Path.Combine(work, "smb.conf");
            await File.WriteAllTextAsync(configPath, config, Token);
            Assert.Equal(0, (await RunAsync("testparm", "--suppress-prompt", "-s", configPath)).ExitCode);
            // A session of its own, so Samba's signal to its process group when it ends never reaches the test run, and
            // stdin a pipe that stays open: smbd serves a socket on stdin (a run from a remote shell) as an inetd
            // connection, and in the foreground it ends when stdin closes.
            var smbdStart = new ProcessStartInfo("setsid") { RedirectStandardInput = true };
            foreach (var argument in (string[])["/usr/sbin/smbd", "--foreground", "--no-process-group", "-s", configPath])
            {
                smbdStart.ArgumentList.Add(argument);
            }
            smbd = Process.Start(smbdStart)!;
            await WaitForPortAsync(SmbPort);

            // rita writes, and a delete lands in the recycle bin with a row in her name.
            var put = await SmbAsync("pxit-rita", "Media", $"mkdir docs; put {configPath} docs/note.txt; ls docs/*");
            Assert.True(put.ExitCode == 0, put.StandardOutput + put.StandardError);
            Assert.Contains("note.txt", put.StandardOutput, StringComparison.Ordinal);
            // Files from SMB are not executable, and the named users keep their rights through the ACL mask.
            Assert.Equal("660", (await RunAsync("stat", "-c", "%a", Path.Combine(share, "docs", "note.txt"))).StandardOutput.Trim());
            var noteAcl = (await RunAsync("getfacl", "--numeric", Path.Combine(share, "docs", "note.txt"))).StandardOutput;
            Assert.Matches(@"user:20950:rw[x-]\s+#effective:rw-", noteAcl);
            Assert.Matches(@"user:20951:r-[x-]\s+#effective:r--", noteAcl);
            var delete = await SmbAsync("pxit-rita", "Media", "del docs/note.txt");
            Assert.Equal(0, delete.ExitCode);
            Assert.False(File.Exists(Path.Combine(share, "docs", "note.txt")));
            Assert.Equal(1, await recycle.IngestAsync(shareId, share, Token));
            await using (var connection = await connections.OpenAsync(Token))
            {
                var row = await connection.QuerySingleAsync<(long Id, string Path, long? By)>("SELECT id, original_path, deleted_by FROM trash_items");
                Assert.Equal(("docs/note.txt", (long?)20950), (row.Path, row.By));
                Assert.True(File.Exists(Path.Combine(share, FsPath.TrashFolder, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
            // The recycle bin is not reachable over SMB.
            var bin = await SmbAsync("pxit-rita", "Media", $"ls {FsPath.TrashFolder}/*");
            Assert.NotEqual(0, bin.ExitCode);

            // tom reads only, nina gets nothing.
            var tom = await SmbAsync("pxit-tom", "Media", $"ls; put {configPath} tom.txt");
            Assert.Contains("docs", tom.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("NT_STATUS_ACCESS_DENIED", tom.StandardOutput + tom.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(share, "tom.txt")));
            var nina = await SmbAsync("pxit-nina", "Media", "ls");
            Assert.NotEqual(0, nina.ExitCode);

            // A restore point shows as a previous version.
            await vdevs.RunAsync("zfs", ["snapshot", $"{dataset}@{Pithyx.Daemon.Storage.Snapshots.SnapshotNames.For("daily", DateTimeOffset.UtcNow)}"]);
            var versions = await SmbAsync("pxit-rita", "Media", "allinfo docs");
            Assert.Contains("@GMT-", versions.StandardOutput, StringComparison.Ordinal);

            // SFTP: rita sees only her shared folder; tom has no SFTP right and cannot sign in.
            var keys = Directory.CreateDirectory(Path.Combine(work, "ssh")).FullName;
            await RunAsync("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(keys, "host"));
            var sftpRoot = Path.Combine(runRoot, "sftp");
            var sshdConfig = Path.Combine(keys, "sshd_config");
            await File.WriteAllTextAsync(sshdConfig,
                $"""
                Port {SshPort}
                ListenAddress 127.0.0.1
                HostKey {Path.Combine(keys, "host")}
                PidFile {Path.Combine(keys, "sshd.pid")}
                UsePAM yes
                KbdInteractiveAuthentication no
                PasswordAuthentication no
                Subsystem sftp internal-sftp

                """ + SshdConfig.Render(sftpRoot), Token);
            Assert.Equal(0, (await RunAsync("/usr/sbin/sshd", "-t", "-f", sshdConfig)).ExitCode);
            sshd = Process.Start("/usr/sbin/sshd", ["-D", "-f", sshdConfig]);
            await WaitForPortAsync(SshPort);
            var views = new SftpViews(_commands, SystemFiles.Host, new FileServiceOptions(Apply: true, SftpRoot: sftpRoot), NullLogger<SftpViews>.Instance);
            var shareMode = (await RunAsync("stat", "-c", "%a", share)).StandardOutput;
            await views.ReconcileAsync(["pxit-rita"], [new SftpViewEntry("pxit-rita", "Media", share)], Token);
            // A second run finds the view mounted and must leave the shared folder behind it as it is.
            await views.ReconcileAsync(["pxit-rita"], [new SftpViewEntry("pxit-rita", "Media", share)], Token);
            Assert.Equal(shareMode, (await RunAsync("stat", "-c", "%a", share)).StandardOutput);

            Environment.SetEnvironmentVariable("SSHPASS", Password);
            var sftp = await SftpAsync("pxit-rita", $"ls /\nput {configPath} /Media/sftp.txt\nls /Media\n", work);
            Assert.True(sftp.ExitCode == 0, sftp.StandardOutput + sftp.StandardError);
            Assert.Contains("/Media", sftp.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("sftp.txt", sftp.StandardOutput, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(share, "sftp.txt")));
            Assert.NotEqual(0, (await SftpAsync("pxit-tom", "ls /\n", work)).ExitCode);

            // Taking the right away takes the folder out of the view.
            await views.ReconcileAsync(["pxit-rita"], [], Token);
            Assert.DoesNotContain(MountTable.ParseMountInfo(await File.ReadAllTextAsync("/proc/self/mountinfo", Token)), mount => mount.Target.StartsWith(sftpRoot + "/", StringComparison.Ordinal));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSHPASS", null);
            Stop(smbd);
            Stop(sshd);
            if (Directory.Exists(runRoot))
            {
                foreach (var mount in MountTable.ParseMountInfo(await File.ReadAllTextAsync("/proc/self/mountinfo", CancellationToken.None)).Where(mount => mount.Target.StartsWith(runRoot + "/", StringComparison.Ordinal)))
                {
                    await _commands.RunAsync(new CommandRequest("umount", ["--lazy", mount.Target]), CancellationToken.None);
                }
                Directory.Delete(runRoot, recursive: true);
            }
            foreach (var user in users)
            {
                await _commands.RunAsync(new CommandRequest("pdbedit", ["-x", "-u", user]), CancellationToken.None);
                await _commands.RunAsync(new CommandRequest("userdel", [user]), CancellationToken.None);
            }
            Directory.Delete(work, recursive: true);
        }
    }

    private static void Stop(Process? process)
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        process?.Dispose();
    }

    /// <summary>Waits until a socket listens on the port, read from <c>/proc/net/tcp</c> without connecting.</summary>
    private static async Task WaitForPortAsync(int port)
    {
        var hex = $":{port:X4} ";
        for (var attempt = 0; attempt < 100; attempt++)
        {
            foreach (var table in (string[])["/proc/net/tcp", "/proc/net/tcp6"])
            {
                // Columns: sl local_address rem_address st ...; state 0A is LISTEN.
                if ((await File.ReadAllLinesAsync(table, Token)).Skip(1).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Any(fields => fields.Length > 3 && (fields[1] + " ").EndsWith(hex, StringComparison.Ordinal) && fields[3] == "0A"))
                {
                    return;
                }
            }
            await Task.Delay(100, Token);
        }
        Assert.Fail($"Nothing listens on port {port}.");
    }
}
