using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Homes on real OpenZFS (D348): a user's home is created on the home pool, owned by the user with mode 0700 and the
/// quota it should have, and a home moves to another pool with its files, owners and ACLs through
/// <c>zfs send | zfs receive</c>, while a failed move leaves it where it was, writable.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class HomeIntegrationTests
{
    private const long Uid = 29998;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (SystemStateService State, PoolInventory Pools) State(FileVdevs vdevs)
    {
        var pools = new PoolInventory();
        var disks = new DiskInventory();
        var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        return (new SystemStateService(vdevs.Commands, disks, pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance), pools);
    }

    [Fact]
    public async Task AUsersHomeIsCreatedOwnedByTheUserWithItsQuota()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var (state, pools) = State(vdevs);
        using var _ = state;
        await state.RefreshPoolsAsync(Token);
        var connections = new SqliteConnectionFactory(Path.Combine(vdevs.Directory, "pithyx.db"));
        await new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
        await using (var connection = await connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                "INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (@uid, 'user', 'rita', '2026-10-13T00:00:00Z')",
                new { uid = Uid });
            await connection.ExecuteAsync(
                "INSERT INTO users (id, username, password_hash, uid, created_at, updated_at) VALUES (@uid, 'rita', 'x', @uid, '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z')",
                new { uid = Uid });
        }
        using var homes = new Homes(connections, vdevs.Commands, pools, state, new HomeStorageOptions(UseZfs: true, "/"), new PermissionService(connections), new ShareChanges(), TimeProvider.System, NullLogger<Homes>.Instance);

        await homes.ReconcileAsync(Token);

        var home = Path.Combine(vdevs.AltRoot, vdevs.PoolName, "homes", "rita");
        Assert.Equal(vdevs.PoolName, await homes.PoolAsync(Token));
        Assert.Contains(HomePlans.Dataset(vdevs.PoolName, "rita"), await vdevs.RunAsync("zfs", ["list", "-H", "-o", "name", "-r", vdevs.PoolName]), StringComparison.Ordinal);
        Assert.Equal($"{Uid}:100 700", (await vdevs.RunAsync("stat", ["-c", "%u:%g %a", home])).Trim());
        var row = Assert.Single(await homes.RowsAsync(Token));
        Assert.Equal((home, (long?)Uid), (row.Path, row.UserId));

        await using (var connection = await connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync("UPDATE users SET home_quota_bytes = 10485760 WHERE id = @uid", new { uid = Uid });
        }
        await homes.ReconcileAsync(Token);
        Assert.Equal("10485760", (await vdevs.RunAsync("zfs", ["get", "-Hp", "-o", "value", "quota", HomePlans.Dataset(vdevs.PoolName, "rita")])).Trim());

        var file = Path.Combine(home, "upload.txt");
        await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
        {
            new FileOwner((uint)Uid, FileOwner.UsersGroup).Apply(stream.SafeFileHandle);
        }
        Assert.Equal($"{Uid}:100", (await vdevs.RunAsync("stat", ["-c", "%u:%g", file])).Trim());
    }

    [Fact]
    public async Task AHomeMovesToAnotherPoolWithItsFilesOwnersAndAclsAndAFailedMoveLeavesIt()
    {
        FileVdevs.RequireEnabled();
        await using var from = FileVdevs.Create(1);
        await using var to = FileVdevs.Create(1);
        await from.CreateLayoutPoolAsync();
        await to.CreateLayoutPoolAsync();
        var source = HomePlans.Dataset(from.PoolName, "rita");
        var target = HomePlans.Dataset(to.PoolName, "rita");
        await from.RunAsync("zfs", ["create", source]);
        var home = Path.Combine(from.AltRoot, from.PoolName, "homes", "rita");
        await File.WriteAllTextAsync(Path.Combine(home, "notes.txt"), "kept", Token);
        await from.RunAsync("chown", ["-R", $"{Uid}:100", home]);
        await from.RunAsync("setfacl", ["-m", "g:30001:rwx", Path.Combine(home, "notes.txt")]);
        var snapshot = SnapshotNames.For(SnapshotKinds.Move, DateTimeOffset.UtcNow);

        await HomeService.CopyAsync(from.Commands, source, target, snapshot, Token);

        var moved = Path.Combine(to.AltRoot, to.PoolName, "homes", "rita");
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(moved, "notes.txt"), Token));
        Assert.Equal($"{Uid}:100", (await to.RunAsync("stat", ["-c", "%u:%g", Path.Combine(moved, "notes.txt")])).Trim());
        Assert.Contains("group:30001:rwx", await to.RunAsync("getfacl", ["--omit-header", "--numeric", Path.Combine(moved, "notes.txt")]), StringComparison.Ordinal);
        Assert.Equal("on", (await from.RunAsync("zfs", ["get", "-H", "-o", "value", "readonly", source])).Trim());
        Assert.Equal("off", (await to.RunAsync("zfs", ["get", "-H", "-o", "value", "readonly", target])).Trim());

        // A second copy finds the home there and touches nothing.
        var again = await Assert.ThrowsAsync<PithyxException>(() => HomeService.CopyAsync(from.Commands, source, target, SnapshotNames.For(SnapshotKinds.Move, DateTimeOffset.UtcNow.AddSeconds(1)), Token));
        Assert.Equal(ErrorCodes.HomesPoolTaken, again.Code);
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(moved, "notes.txt"), Token));

        // A copy that fails halfway (no such pool) leaves the home writable and without its snapshot.
        await from.RunAsync("zfs", ["inherit", "readonly", source]);
        await from.RunAsync("zfs", ["destroy", $"{source}@{snapshot}"]);
        var failed = SnapshotNames.For(SnapshotKinds.Move, DateTimeOffset.UtcNow.AddSeconds(2));
        await Assert.ThrowsAsync<PithyxException>(() => HomeService.CopyAsync(from.Commands, source, HomePlans.Dataset("pithyxci-missing", "rita"), failed, Token));
        Assert.Equal("off", (await from.RunAsync("zfs", ["get", "-H", "-o", "value", "readonly", source])).Trim());
        Assert.Equal("", (await from.RunAsync("zfs", ["list", "-H", "-t", "snapshot", "-o", "name", "-r", source])).Trim());
    }
}
