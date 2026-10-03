using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Users;

/// <summary>Real <c>setfacl</c> on a folder tree in a temporary folder, as root on the dev VM (D286, D290).</summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ShareAclIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<string> AclAsync(string path) =>
        (await _commands.RunAsync(new CommandRequest("getfacl", ["--omit-header", "--numeric", "--", path]), Token)).StandardOutput;

    [Fact]
    public async Task ASharedFolderNamesItsTwoGroupsAndIsWalkedOnlyWhenTheyChange()
    {
        FileVdevs.RequireEnabled();
        var directory = Directory.CreateTempSubdirectory("pithyx-acl-").FullName;
        try
        {
            var share = Path.Combine(directory, "media");
            Directory.CreateDirectory(Path.Combine(share, "albums"));
            await File.WriteAllTextAsync(Path.Combine(share, "albums", "song.txt"), "la", Token);
            File.CreateSymbolicLink(Path.Combine(share, "passwd"), "/etc/passwd");
            var passwdBefore = await AclAsync("/etc/passwd");

            var connections = new SqliteConnectionFactory(Path.Combine(directory, "pithyx.db"));
            await new MigrationRunner(connections, new FakeTimeProvider(), NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
            long readGid, writeGid;
            await using (var connection = await connections.OpenAsync(Token))
            {
                await TestUsers.InsertAsync(connection, "ada", admin: true, id: 20000, uid: 20000);
                await TestUsers.InsertAsync(connection, "rita", id: 20001, uid: 20001);
                var shareId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES ('Media', @share, 'tank/pithyx/shares/media', 'x', 'x') RETURNING id",
                    new { share });
                await TestUsers.GrantAsync(connection, 20001, $"share:{shareId}", "read");
            }
            await ShareGroups.AllocateAsync(connections, TimeProvider.System, Token);
            await using (var connection = await connections.OpenAsync(Token))
            {
                (readGid, writeGid) = await connection.QuerySingleAsync<(long, long)>("SELECT read_gid, write_gid FROM shares");
            }
            using var acls = new ShareAcls(connections, _commands, new AccountOptions("/etc/passwd", Apply: true), NullLogger<ShareAcls>.Instance);

            await acls.ReconcileAsync(Token);

            // Only the share's two groups are named (D102); who is in them decides, not the files.
            var folder = await AclAsync(Path.Combine(share, "albums"));
            Assert.Contains($"group:{readGid}:r-x", folder, StringComparison.Ordinal);
            Assert.Contains($"group:{writeGid}:rwx", folder, StringComparison.Ordinal);
            Assert.Contains($"default:group:{readGid}:r-x", folder, StringComparison.Ordinal);
            Assert.DoesNotContain("user:2000", folder, StringComparison.Ordinal);
            Assert.Contains("other::---", folder, StringComparison.Ordinal);
            var file = await AclAsync(Path.Combine(share, "albums", "song.txt"));
            Assert.Contains($"group:{readGid}:r--", file, StringComparison.Ordinal);
            Assert.DoesNotContain("default:", file, StringComparison.Ordinal);
            Assert.Equal(passwdBefore, await AclAsync("/etc/passwd"));

            // Something created later inherits the default ACL.
            await File.WriteAllTextAsync(Path.Combine(share, "late.txt"), "", Token);
            Assert.Matches($@"group:{readGid}:r-[x-]\s+#effective:r--", await AclAsync(Path.Combine(share, "late.txt")));

            // A change of rights changes group members only: the files are not walked again.
            var song = Path.Combine(share, "albums", "song.txt");
            Assert.Equal(0, (await _commands.RunAsync(new CommandRequest("setfacl", ["-b", "--", song]), Token)).ExitCode);
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync("UPDATE grants SET level = 'readWrite' WHERE resource GLOB 'share:*'");
                await connection.ExecuteAsync("UPDATE users SET disabled_at = 'x' WHERE id = 20000");
            }
            await acls.ReconcileAsync(Token);
            Assert.DoesNotContain($"group:{readGid}", await AclAsync(song), StringComparison.Ordinal);

            // An entry added behind Pithyx's back on the share is found and set right again.
            Assert.Equal(0, (await _commands.RunAsync(new CommandRequest("setfacl", ["-m", "u:20002:rwx", "--", share]), Token)).ExitCode);
            await acls.ReconcileAsync(Token);
            Assert.DoesNotContain("user:20002", await AclAsync(share), StringComparison.Ordinal);

            // New entries (another pair of groups) walk again.
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync("UPDATE shares SET read_gid = NULL, write_gid = NULL");
            }
            await ShareGroups.AllocateAsync(connections, TimeProvider.System, Token);
            await acls.ReconcileAsync(Token);
            long newWrite;
            await using (var connection = await connections.OpenAsync(Token))
            {
                newWrite = await connection.ExecuteScalarAsync<long>("SELECT write_gid FROM shares");
            }
            Assert.Contains($"group:{newWrite}:rw-", await AclAsync(song), StringComparison.Ordinal);
            Assert.DoesNotContain($"group:{readGid}:", await AclAsync(song), StringComparison.Ordinal);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
