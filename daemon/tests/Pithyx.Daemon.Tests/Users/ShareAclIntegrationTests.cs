using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Users;

/// <summary>Real <c>setfacl</c> on a folder tree in a temporary folder, as root on the dev VM (D281).</summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ShareAclIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<string> AclAsync(string path) =>
        (await _commands.RunAsync(new CommandRequest("getfacl", ["--omit-header", "--numeric", "--", path]), Token)).StandardOutput;

    [Fact]
    public async Task ASharedFolderGetsTheAclOfItsRightsOnlyWhenTheyChange()
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
            long rita;
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync("INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (20000, 'user', 'ada', 'x'), (20001, 'user', 'rita', 'x'), (20002, 'user', 'tom', 'x')");
                await connection.ExecuteAsync(
                    """
                    INSERT INTO users (id, username, password_hash, is_admin, uid, created_at, updated_at) VALUES
                        (20000, 'ada', 'x', 1, 20000, 'x', 'x'), (20001, 'rita', 'x', 0, 20001, 'x', 'x'), (20002, 'tom', 'x', 0, 20002, 'x', 'x')
                    """);
                rita = 20001;
                var shareId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES ('Media', @share, 'tank/pithyx/shares/media', 'x', 'x') RETURNING id",
                    new { share });
                await connection.ExecuteAsync("INSERT INTO share_access (share_id, user_id, level) VALUES (@shareId, @rita, 'read')", new { shareId, rita });
            }
            using var acls = new ShareAcls(connections, _commands, new AccountOptions("/etc/passwd", Apply: true), NullLogger<ShareAcls>.Instance);

            await acls.ReconcileAsync(Token);

            var folder = await AclAsync(Path.Combine(share, "albums"));
            Assert.Contains("user:20000:rwx", folder, StringComparison.Ordinal);
            Assert.Contains("user:20001:r-x", folder, StringComparison.Ordinal);
            Assert.Contains("default:user:20001:r-x", folder, StringComparison.Ordinal);
            Assert.DoesNotContain("20002", folder, StringComparison.Ordinal);
            Assert.Contains("other::---", folder, StringComparison.Ordinal);
            var file = await AclAsync(Path.Combine(share, "albums", "song.txt"));
            Assert.Contains("user:20001:r--", file, StringComparison.Ordinal);
            Assert.DoesNotContain("default:", file, StringComparison.Ordinal);
            Assert.Equal(passwdBefore, await AclAsync("/etc/passwd"));

            // Something created later inherits the default ACL.
            await File.WriteAllTextAsync(Path.Combine(share, "late.txt"), "", Token);
            Assert.Matches(@"user:20001:r-[x-]\s+#effective:r--", await AclAsync(Path.Combine(share, "late.txt")));

            // Unchanged rights are not walked again; changed ones are.
            var song = Path.Combine(share, "albums", "song.txt");
            Assert.Equal(0, (await _commands.RunAsync(new CommandRequest("setfacl", ["-b", "--", song]), Token)).ExitCode);
            await acls.ReconcileAsync(Token);
            Assert.DoesNotContain("user:20001", await AclAsync(song), StringComparison.Ordinal);
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync("UPDATE share_access SET level = 'readWrite'");
                await connection.ExecuteAsync("UPDATE users SET disabled_at = 'x' WHERE id = 20000");
            }
            await acls.ReconcileAsync(Token);
            var changed = await AclAsync(song);
            Assert.Contains("user:20001:rw-", changed, StringComparison.Ordinal);
            Assert.DoesNotContain("20000", changed, StringComparison.Ordinal);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
