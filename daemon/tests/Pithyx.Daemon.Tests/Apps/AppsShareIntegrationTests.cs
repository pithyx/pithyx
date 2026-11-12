using System.Diagnostics;
using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// Apps for real, as root on the dev VM (D409): two app datasets on a throwaway pool, the read-only tmpfs with a bind
/// mount of each app's <c>volumes/</c> that follows no link, and a Samba of its own with the generated section. What SMB
/// creates belongs to the app (the app's uid, or the remapped root of an app that runs as root), the app reads it and
/// SMB reads what the app wrote, no link, <c>..</c> or folder change leads out of an app's folder, a reader cannot
/// write, chmod and ACLs from the client change nothing, and a file held open over SMB does not keep an app's dataset
/// from being destroyed once Samba let go of the share. The machine's services are not touched.
/// </summary>
[Trait("Category", FileVdevs.Category)]
[Collection(LinuxAccounts.Name)]
public sealed class AppsShareIntegrationTests
{
    private const string Password = "pxit-river-candle-8";
    private const int SmbPort = 14451;
    private const string Alpha = "com.example.alpha";
    private const string Beta = "com.example.beta";
    private const int AppUid = 40951;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<CommandResult> RunAsync(string program, params string[] arguments) =>
        await _commands.RunAsync(new CommandRequest(program, arguments, TimeSpan.FromMinutes(2)), Token);

    private static string Port => SmbPort.ToString(CultureInfo.InvariantCulture);

    private static async Task<string> SmbAsync(string user, string commands)
    {
        var result = await RunAsync("smbclient", "//127.0.0.1/Apps", "-p", Port, "-U", $"{user}%{Password}", "-m", "SMB3", "-c", commands);
        return result.StandardOutput + result.StandardError;
    }

    private static async Task<string> OwnerAsync(string path) => (await RunAsync("stat", "-c", "%u:%g %a", path)).StandardOutput.Trim();

    [Fact]
    public async Task SmbAndAppsShareTheAppsFilesAndNothingLeadsOutOfAnAppsFolder()
    {
        FileVdevs.RequireEnabled();
        Assert.SkipUnless(FileServiceSync.Installed("smbd") && File.Exists("/usr/bin/smbclient") && File.Exists("/usr/bin/smbcacls") && File.Exists("/usr/bin/setpriv"),
            "Needs samba, smbclient (with smbcacls) and setpriv (tools/dev-vm/provision.sh).");
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var pool = vdevs.PoolName;
        // The app's uid must pass through every folder above the pool, as it does through /<pool> on a box; the
        // temporary folders of the run are private.
        for (var folder = new DirectoryInfo(vdevs.AltRoot); folder is not null && folder.FullName != "/dev/shm" && folder.FullName != "/"; folder = folder.Parent)
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(folder.FullName, File.GetUnixFileMode(folder.FullName) | UnixFileMode.OtherExecute);
            }
        }
        var work = Directory.CreateTempSubdirectory("pithyxci-apps-").FullName;
        var runRoot = $"/run/pithyxci-{Guid.NewGuid():N}";
        var root = Path.Combine(runRoot, "apps");
        Process? smbd = null;
        Process? holder = null;
        string[] users = ["pxit-ada", "pxit-tom"];
        try
        {
            // Two apps as the runtime leaves them: alpha runs as its uid, beta as root in its container (D359).
            var volumes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, owner) in new[] { (Alpha, (long)AppUid), (Beta, DockerHost.RemapBase) })
            {
                var dataset = ZfsPlans.AppDataset(pool, id);
                await vdevs.RunAsync("zfs", ["create", dataset]);
                var mount = (await vdevs.RunAsync("zfs", ["get", "-H", "-o", "value", "mountpoint", dataset])).Trim();
                volumes[id] = Directory.CreateDirectory(Path.Combine(mount, AppStorage.VolumesFolder, "data")).FullName;
                await vdevs.RunAsync("chown", ["-R", $"{owner}:{owner}", Path.Combine(mount, AppStorage.VolumesFolder)]);
            }
            await File.WriteAllTextAsync(Path.Combine(volumes[Beta], "beta-marker.txt"), "beta", Token);
            Directory.CreateDirectory(runRoot);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "parent-marker.txt"), "parent", Token);
            // Links an app made point into its container; on the box they would lead to the host's files or another app.
            File.CreateSymbolicLink(Path.Combine(volumes[Alpha], "passwd"), "/etc/passwd");
            File.CreateSymbolicLink(Path.Combine(volumes[Alpha], "etc"), "/etc");
            File.CreateSymbolicLink(Path.Combine(volumes[Alpha], "other"), volumes[Beta]);
            File.CreateSymbolicLink(Path.Combine(volumes[Alpha], "up"), "../../../..");

            var pools = new PoolInventory();
            var disks = new DiskInventory();
            var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
            var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
            using var state = new SystemStateService(vdevs.Commands, disks, pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
            await state.RefreshPoolsAsync(Token);
            var connections = new SqliteConnectionFactory(Path.Combine(work, "pithyx.db"));
            await new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @pool)", new { key = AppRegistry.PoolSetting, pool });
            }
            var storage = new AppStorageOptions(UseZfs: true, "/");
            var permissions = new PermissionService(connections);
            var registry = new AppRegistry(connections, permissions, pools, storage, DeviceFs.Real);
            await registry.ReloadAsync(Token);
            using var activity = new FileServiceActivity(new StorageUsage(), TimeProvider.System);
            using var apps = new AppsShare(connections, _commands, SystemFiles.Host, pools, registry, storage, new AppsShareOptions(Apply: true, root),
                permissions, new AppBackends(), activity, new ShareChanges(), TimeProvider.System, NullLogger<AppsShare>.Instance);
            await apps.ReconcileAsync(Token);
            // A second run finds everything in place and changes nothing.
            await apps.ReconcileAsync(Token);

            var mountInfo = (await File.ReadAllLinesAsync("/proc/self/mountinfo", Token)).Where(line => line.Contains($" {runRoot}/", StringComparison.Ordinal)).ToList();
            Assert.Single(mountInfo, line => line.Contains($" {root} ", StringComparison.Ordinal) && line.Contains(" ro,", StringComparison.Ordinal) && line.Contains(" - tmpfs ", StringComparison.Ordinal));
            foreach (var id in new[] { Alpha, Beta })
            {
                var bind = Assert.Single(mountInfo, line => line.Contains($" {root}/{id} ", StringComparison.Ordinal));
                Assert.Contains($" /{AppStorage.VolumesFolder} ", bind, StringComparison.Ordinal);
                Assert.Contains("nosymfollow", bind, StringComparison.Ordinal);
            }
            Assert.Equal([Alpha, Beta], Directory.EnumerateDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // Nothing goes next to the app folders, even for root, and no link is followed below them.
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(Path.Combine(root, "top.txt"), "x"));
            Assert.ThrowsAny<IOException>(() => File.ReadAllText(Path.Combine(root, Alpha, "data", "passwd")));
            Assert.ThrowsAny<IOException>(() => Directory.EnumerateFileSystemEntries(Path.Combine(root, Alpha, "data", "other")).ToList());
            Assert.ThrowsAny<IOException>(() => Directory.EnumerateFileSystemEntries(Path.Combine(root, Alpha, "data", "up")).ToList());

            // Accounts as the reconciler makes them; ada may write, tom may read.
            var uid = 20960;
            foreach (var user in users)
            {
                var added = await RunAsync("useradd", "--uid", (uid++).ToString(CultureInfo.InvariantCulture), "--gid", "users", "--no-user-group", "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", user);
                Assert.True(added.ExitCode == 0, added.StandardError);
            }
            var passwords = new FileServicePasswords(_commands, new AccountOptions("/etc/passwd", Apply: true), NullLogger<FileServicePasswords>.Instance);
            foreach (var user in users)
            {
                await passwords.SetAsync(user, Password, Token);
            }

            var config = SmbConfig.Render("PXIT", [new SmbShare(AppsShare.Name, root, ["pxit-tom"], ["pxit-ada"], Encrypt: false, SmbShareKind.Apps)]);
            var samba = Directory.CreateDirectory(Path.Combine(work, "samba")).FullName;
            config = config.Replace($"   include = {SmbConfig.LocalInclude}\n",
                $"""
                   smb ports = {SmbPort}
                   interfaces = lo
                   bind interfaces only = yes
                   pid directory = {samba}
                   lock directory = {samba}
                   state directory = {samba}
                   cache directory = {samba}
                   ncalrpc dir = {samba}/ncalrpc

                """, StringComparison.Ordinal);
            var configPath = Path.Combine(work, "smb.conf");
            await File.WriteAllTextAsync(configPath, config, Token);
            Assert.Equal(0, (await RunAsync("testparm", "--suppress-prompt", "-s", configPath)).ExitCode);
            var start = new ProcessStartInfo("setsid") { RedirectStandardInput = true };
            foreach (var argument in (string[])["/usr/sbin/smbd", "--foreground", "--no-process-group", "-s", configPath])
            {
                start.ArgumentList.Add(argument);
            }
            smbd = Process.Start(start)!;
            await WaitForPortAsync(SmbPort);

            // What ada puts into an app's folder belongs to the app, and the app can change it.
            var put = await SmbAsync("pxit-ada", $"put {configPath} {Alpha}/data/smb.txt; mkdir {Alpha}/data/smbdir; put {configPath} {Beta}/data/smb.txt");
            Assert.True(File.Exists(Path.Combine(volumes[Alpha], "smb.txt")), put);
            // Samba sets only the owner; the group stays the user's (users), which gets no rights.
            Assert.Equal($"{AppUid}:100 600", await OwnerAsync(Path.Combine(volumes[Alpha], "smb.txt")));
            Assert.Equal($"{AppUid}:100 700", await OwnerAsync(Path.Combine(volumes[Alpha], "smbdir")));
            Assert.Equal($"{DockerHost.RemapBase}:100 600", await OwnerAsync(Path.Combine(volumes[Beta], "smb.txt")));
            var app = await RunAsync("setpriv", $"--reuid={AppUid}", $"--regid={AppUid}", "--clear-groups", "--",
                "sh", "-c", $"cat '{Path.Combine(volumes[Alpha], "smb.txt")}' > /dev/null && echo app >> '{Path.Combine(volumes[Alpha], "smb.txt")}' && echo from-app > '{Path.Combine(volumes[Alpha], "app.txt")}'");
            Assert.True(app.ExitCode == 0, app.StandardError);
            var local = Path.Combine(work, "fetched.txt");
            var fetched = await SmbAsync("pxit-tom", $"get {Alpha}/data/app.txt {local}");
            Assert.Equal("from-app\n", File.Exists(local) ? await File.ReadAllTextAsync(local, Token) : fetched);

            // The top level takes nothing, and no link, .. or folder change leads out of an app's folder.
            var top = await SmbAsync("pxit-ada", $"put {configPath} top.txt; mkdir mine");
            Assert.Empty(Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Except([Alpha, Beta]));
            Assert.Contains("NT_STATUS_", top, StringComparison.Ordinal);
            var escapes = await SmbAsync("pxit-ada",
                $"get {Alpha}/data/passwd {Path.Combine(work, "passwd")}; ls {Alpha}/data/etc/*; ls {Alpha}/data/other/*; ls {Alpha}/data/up/*; "
                + $"ls {Alpha}/../../*; cd {Alpha}/data; cd ../../..; ls; cd /; cd ..; ls");
            Assert.False(File.Exists(Path.Combine(work, "passwd")), escapes);
            Assert.DoesNotContain("beta-marker", escapes, StringComparison.Ordinal);
            Assert.DoesNotContain("parent-marker", escapes, StringComparison.Ordinal);
            Assert.DoesNotContain("hosts", escapes, StringComparison.Ordinal);

            // tom reads and cannot write, rename or delete, although Samba works as root in Apps.
            var tom = await SmbAsync("pxit-tom", $"ls {Alpha}/data/*; put {configPath} {Alpha}/data/tom.txt; mkdir {Alpha}/data/tomdir; rename {Alpha}/data/smb.txt {Alpha}/data/moved.txt; del {Alpha}/data/app.txt");
            Assert.Contains("smb.txt", tom, StringComparison.Ordinal);
            Assert.Contains("NT_STATUS_ACCESS_DENIED", tom, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(volumes[Alpha], "tom.txt")));
            Assert.False(Directory.Exists(Path.Combine(volumes[Alpha], "tomdir")));
            Assert.True(File.Exists(Path.Combine(volumes[Alpha], "smb.txt")));
            Assert.True(File.Exists(Path.Combine(volumes[Alpha], "app.txt")));

            // A move into another app's folder crosses a mount: Samba refuses it, and a client that copies instead
            // creates a new file, which belongs to the other app.
            await SmbAsync("pxit-ada", $"rename {Alpha}/data/app.txt {Beta}/data/moved.txt");
            Assert.True(File.Exists(Path.Combine(volumes[Alpha], "app.txt")));
            Assert.False(File.Exists(Path.Combine(volumes[Beta], "moved.txt")));

            // chmod, DOS attributes, ACLs and owners from the client change nothing on the box.
            var before = await OwnerAsync(Path.Combine(volumes[Alpha], "smb.txt"));
            await SmbAsync("pxit-ada", $"chmod 777 {Alpha}/data/smb.txt; chown 0 0 {Alpha}/data/smb.txt; setmode {Alpha}/data/smb.txt +r");
            string[][] changes = [["-a", "ACL:PXIT\\pxit-tom:ALLOWED/0x0/FULL"], ["-C", "pxit-ada"], ["-G", "root"], ["-S", "REVISION:1,OWNER:PXIT\\pxit-tom,ACL:Everyone:ALLOWED/0x0/FULL"]];
            foreach (var change in changes)
            {
                await RunAsync("smbcacls", ["//127.0.0.1/Apps", $"{Alpha}/data/smb.txt", "--port", Port, "-U", $"pxit-ada%{Password}", .. change]);
            }
            Assert.Equal(before, await OwnerAsync(Path.Combine(volumes[Alpha], "smb.txt")));
            Assert.DoesNotMatch(@"(?m)^(user|group):[^:\n]+:", (await RunAsync("getfacl", "--numeric", "--omit-header", Path.Combine(volumes[Alpha], "smb.txt"))).StandardOutput);

            // A file held open over SMB: once Samba let go of the share, the app's dataset can go.
            var holderStart = new ProcessStartInfo("smbclient") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in (string[])["//127.0.0.1/Apps", "-p", Port, "-U", $"pxit-ada%{Password}", "-m", "SMB3"])
            {
                holderStart.ArgumentList.Add(argument);
            }
            holder = Process.Start(holderStart)!;
            await holder.StandardInput.WriteLineAsync($"open {Beta}/data/smb.txt");
            await holder.StandardInput.FlushAsync(Token);
            var opened = "";
            for (var attempt = 0; attempt < 50 && !opened.Contains("smb.txt", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(100, Token);
                opened = (await RunAsync("smbstatus", "-s", configPath, "-L")).StandardOutput;
            }
            Assert.Contains("smb.txt", opened, StringComparison.Ordinal);
            var betaMount = Path.GetDirectoryName(Path.GetDirectoryName(volumes[Beta]))!;
            // On a box AppsShare sends close-share to the box's smbd; this Samba has its own state folders.
            Assert.Equal(0, (await RunAsync("smbcontrol", "-s", configPath, "smbd", "close-share", AppsShare.Name)).ExitCode);
            using (await apps.ReleaseBelowAsync([betaMount], Token))
            {
                Assert.False(Directory.Exists(Path.Combine(root, Beta)));
                await vdevs.RunAsync("zfs", ["destroy", ZfsPlans.AppDataset(pool, Beta)]);
                await state.RefreshPoolsAsync(Token);
            }
            await apps.ReconcileAsync(Token);
            Assert.Equal([Alpha], Directory.EnumerateDirectories(root).Select(Path.GetFileName));
            Assert.Contains("app", await File.ReadAllTextAsync(Path.Combine(root, Alpha, "data", "smb.txt"), Token), StringComparison.Ordinal);
        }
        finally
        {
            Stop(holder);
            Stop(smbd);
            if (Directory.Exists(runRoot))
            {
                foreach (var mount in MountTable.ParseMountInfo(await File.ReadAllTextAsync("/proc/self/mountinfo", CancellationToken.None))
                    .Where(mount => mount.Target.StartsWith(runRoot + "/", StringComparison.Ordinal)).OrderByDescending(mount => mount.Target.Length))
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

/// <summary>Tests that run alone, so no other test holds the lock of <c>/etc/passwd</c> while they add accounts.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LinuxAccounts
{
    public const string Name = "linux_accounts";
}
