using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Encryption;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// Encrypted pools (D384) on the recorded pool <c>pithyxteste</c>: locked after an import (<c>zfs-encrypted-locked</c>)
/// and unlocked (<c>zfs-encrypted</c>). ZFS is scripted: the right key on stdin loads, any other is refused.
/// </summary>
public sealed partial class PoolEncryptionTests : IDisposable
{
    private const string PoolName = "pithyxteste";
    private const string Root = "pithyxteste/pithyx";
    private const string Guid = "5031077736857206243";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _disks = new();
    private readonly PoolInventory _pools = new();
    private readonly SystemStateService _state;
    private readonly StorageFacts _facts;
    private readonly KeyVault _vault;
    private readonly PoolEncryptionService _encryption;
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-encryption-").FullName;
    private readonly string _key = PoolKey.New();
    private string _loadedKey = "";
    private bool _keyCommandsHang;
    private readonly HashSet<string> _failingMounts = new(StringComparer.Ordinal);

    public PoolEncryptionTests()
    {
        File.WriteAllText(Path.Combine(_directory, "mountinfo"), Proc("mountinfo"));
        File.WriteAllText(Path.Combine(_directory, "swaps"), Proc("swaps"));
        var environment = new DeploymentEnvironment(DeploymentKind.Dev, new HashSet<string> { "VYXTEST01", "VYXTEST02", "VYXTEST03", "VYXTEST04" }, new HashSet<string>());
        _facts = new StorageFacts(_disks, _pools, environment, new SystemFiles(Path.Combine(_directory, "mountinfo"), Path.Combine(_directory, "swaps")));
        _vault = new KeyVault(Path.Combine(_directory, "pool-keys"));
        var hub = new TopicHub(new TopicRegistry([_disks.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _state = new SystemStateService(_commands, _disks, _pools, new ZfsBackend(_commands), new DiskProbe(_commands), _facts, hub, _time, NullLogger<SystemStateService>.Instance, vault: _vault);
        _encryption = new PoolEncryptionService(_commands, _pools, _state, _facts, _vault, NullLogger<PoolEncryptionService>.Instance);
        _commands.Results["lsblk"] = Ok(Read("default", "lsblk"));
        _commands.Results["blkid"] = new CommandResult(2, "", "", TimeSpan.Zero);
        _commands.Results["wipefs"] = Ok("");
        _commands.Results["zpool"] = Ok("");
        _commands.Results["zfs"] = Ok("");
        _commands.Handler = request => request switch
        {
            { FileName: "zfs", Arguments: ["load-key" or "change-key", ..] } when _keyCommandsHang => throw new TimeoutException(),
            { FileName: "zfs", Arguments: ["load-key", Root] } => LoadKey(request.StandardInput),
            { FileName: "zfs", Arguments: ["change-key", .., Root] } => ChangeKey(request.StandardInput),
            { FileName: "zfs", Arguments: ["mount", var dataset] } when _failingMounts.Contains(dataset) =>
                new CommandResult(1, "", $"cannot mount '{dataset}': directory is not empty\n", TimeSpan.Zero),
            _ => null,
        };
        _loadedKey = PoolKey.ToHex(_key);
        Pools("zfs-encrypted-locked");
    }

    public void Dispose()
    {
        _encryption.Dispose();
        _state.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static CommandResult Ok(string output) => new(0, output, "", TimeSpan.Zero);

    private CommandResult LoadKey(string? input)
    {
        if (input != EncryptionCommands.Input(_loadedKey))
        {
            return new CommandResult(255, "", $"Key load error: Incorrect key provided for '{Root}'.\n", TimeSpan.Zero);
        }
        Pools("zfs-encrypted");
        return Ok("");
    }

    private CommandResult ChangeKey(string? input)
    {
        _loadedKey = input!.Trim();
        return Ok("");
    }

    private void Pools(string scenario)
    {
        _commands.Results["zpool status"] = Ok(Read(scenario, "zpool-status"));
        _commands.Results["zpool list"] = Ok(Read(scenario, "zpool-list"));
        _commands.Results["zfs list"] = Ok(Read(scenario, "zfs-list"));
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", scenario, "zfs-get.stdout")))
        {
            _commands.Results["zfs get"] = Ok(Read(scenario, "zfs-get"));
        }
    }

    private async Task<Pool> PoolAsync()
    {
        await _state.RefreshPoolsAsync(Token);
        return _pools.Current.Pools.Single(pool => pool.Name == PoolName);
    }

    /// <summary>Puts a folder where the pool's vault file belongs, so writing and deleting the key fails as on a broken disk.</summary>
    private void BreakVault()
    {
        Directory.CreateDirectory(Path.Combine(_vault.Directory, $"{Guid}.key"));
    }

    /// <summary>The unlocked pool as recorded, with <paramref name="unmounted"/> not mounted (a mount that failed before).</summary>
    private void UnlockedWithout(params string[] unmounted)
    {
        Pools("zfs-encrypted");
        var list = System.Text.Json.Nodes.JsonNode.Parse(Read("zfs-encrypted", "zfs-list"))!;
        foreach (var dataset in unmounted)
        {
            list["datasets"]![dataset]!["properties"]!["mounted"]!["value"] = "no";
        }
        _commands.Results["zfs list"] = Ok(list.ToJsonString());
    }

    private string[] Mounted() => [.. _commands.Calls.Where(call => call is { FileName: "zfs", Arguments: ["mount", _] }).Select(call => call.Arguments[1])];

    private static async Task<string> ErrorAsync(Func<Task> action) => (await Assert.ThrowsAsync<PithyxException>(action)).Code;

    [Fact]
    public void AKeyReadsBackWithTyposCaughtAndTheUsualSlipsForgiven()
    {
        Assert.Matches(KeyShape(), _key);
        var hex = PoolKey.ToHex(_key);
        Assert.Matches("^[0-9a-f]{64}$", hex);
        Assert.Equal(_key, PoolKey.Format(Convert.FromHexString(hex)));

        var plain = _key.Replace("-", "", StringComparison.Ordinal);
        Assert.Equal(hex, PoolKey.ToHex(plain.ToLowerInvariant()));
        Assert.Equal(hex, PoolKey.ToHex(plain[PoolKey.Prefix.Length..]));
        Assert.Equal(hex, PoolKey.ToHex(" " + string.Join(' ', _key.Split('-')) + " "));
        Assert.Equal(hex, PoolKey.ToHex(_key.Replace('0', 'O').Replace('1', 'I')));

        var last = _key[^1];
        var typo = _key[..^1] + (last == 'Z' ? 'Y' : 'Z');
        Assert.Equal("storage.encryption.key_invalid", Assert.Throws<PithyxException>(() => PoolKey.ToHex(typo)).Code);
        Assert.Equal("storage.encryption.key_invalid", Assert.Throws<PithyxException>(() => PoolKey.ToHex(_key[..^1])).Code);
        Assert.Equal("storage.encryption.key_invalid", Assert.Throws<PithyxException>(() => PoolKey.ToHex(_key + "0")).Code);
        Assert.Equal("storage.encryption.key_invalid", Assert.Throws<PithyxException>(() => PoolKey.ToHex(_key.Replace(_key[10], 'U'))).Code);
        Assert.Equal("storage.encryption.key_invalid", Assert.Throws<PithyxException>(() => PoolKey.ToHex(null)).Code);
        Assert.NotEqual(_key, PoolKey.New());
    }

    [Fact]
    public void TheVaultKeepsOneKeyPerPoolForRootAlone()
    {
        var hex = PoolKey.ToHex(_key);
        Assert.False(_vault.Has(Guid));
        Assert.Null(_vault.Read(Guid));
        // A box that never stored a key has no vault folder yet.
        _vault.Remove(Guid);
        Assert.False(Directory.Exists(_vault.Directory));

        _vault.Save(Guid, hex);
        Assert.Equal(hex, _vault.Read(Guid));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_vault.Directory, $"{Guid}.key")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_vault.Directory));
        }
        _vault.Save(Guid, new string('a', 64));
        Assert.Equal(new string('a', 64), _vault.Read(Guid));
        Assert.Equal([$"{Guid}.key"], Directory.GetFiles(_vault.Directory).Select(Path.GetFileName));

        _vault.Remove(Guid);
        _vault.Remove(Guid);
        Assert.False(_vault.Has(Guid));
        Assert.Throws<ArgumentException>(() => _vault.Has("../etc/passwd"));
        Assert.Throws<ArgumentException>(() => _vault.Save(Guid, "not hex"));
    }

    [Fact]
    public async Task APoolShowsWhetherItIsLockedAndWhetherTheVaultHoldsItsKey()
    {
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false), (await PoolAsync()).Encryption);

        _vault.Save(Guid, PoolKey.ToHex(_key));
        _state.PublishVault();
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: true), _pools.Current.Pools.Single().Encryption);

        Pools("zfs-encrypted");
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), (await PoolAsync()).Encryption);
        Pools("zfs-mirror-single");
        await _state.RefreshPoolsAsync(Token);
        Assert.All(_pools.Current.Pools, pool => Assert.Null(pool.Encryption));
    }

    [Fact]
    public async Task AKeyInTheVaultUnlocksThePoolAtStartAndMountsWhatAnImportWould()
    {
        _vault.Save(Guid, PoolKey.ToHex(_key));
        await PoolAsync();

        await _encryption.UnlockFromVaultAsync(Token);

        var load = Assert.Single(_commands.Calls, call => call.Arguments is ["load-key", ..]);
        Assert.Equal(["load-key", Root], load.Arguments);
        Assert.DoesNotContain(load.Arguments, argument => argument.Contains(PoolKey.ToHex(_key), StringComparison.Ordinal));
        Assert.Equal([$"{Root}/cache", $"{Root}/system", $"{Root}/homes", $"{Root}/homes/alice"], Mounted());
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), _pools.Current.Pools.Single().Encryption);
    }

    [Fact]
    public async Task AWrongKeyInTheVaultIsTriedOnceUntilThePoolAppearsAgain()
    {
        _vault.Save(Guid, PoolKey.ToHex(PoolKey.New()));
        await PoolAsync();

        await _encryption.UnlockFromVaultAsync(Token);
        await _encryption.UnlockFromVaultAsync(Token);
        Assert.Single(_commands.Calls, call => call.Arguments is ["load-key", ..]);
        Assert.True(_pools.Current.Pools.Single().Encryption!.Locked);

        Pools("zfs-empty");
        await _state.RefreshPoolsAsync(Token);
        await _encryption.UnlockFromVaultAsync(Token);
        Pools("zfs-encrypted-locked");
        await _state.RefreshPoolsAsync(Token);
        await _encryption.UnlockFromVaultAsync(Token);
        Assert.Equal(2, _commands.Calls.Count(call => call.Arguments is ["load-key", ..]));
    }

    [Fact]
    public async Task ATypedKeyUnlocksOnlyWhenRightAndGoesIntoTheVaultOnlyWhenAsked()
    {
        await PoolAsync();
        Assert.Equal("storage.encryption.key_required", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, null, false, Token)));
        Assert.Equal("storage.encryption.key_invalid", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, "PXPK-1234", true, Token)));
        Assert.Equal("storage.encryption.key_wrong", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, PoolKey.New(), true, Token)));
        Assert.False(_vault.Has(Guid));
        Assert.Empty(Mounted());

        await _encryption.UnlockAsync(PoolName, _key.ToLowerInvariant(), true, Token);

        Assert.Equal(PoolKey.ToHex(_key), _vault.Read(Guid));
        Assert.Equal(4, Mounted().Length);
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), _pools.Current.Pools.Single().Encryption);
    }

    [Fact]
    public async Task AnUnlockedPoolKeepsNoUncheckedKeyButMountsWhatIsMissing()
    {
        Pools("zfs-encrypted");
        await PoolAsync();

        Assert.Equal("storage.pool.not_locked", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, _key, true, Token)));
        Assert.Equal("storage.pool.not_locked", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, null, true, Token)));
        Assert.False(_vault.Has(Guid));
        await _encryption.UnlockAsync(PoolName, null, false, Token);
        await _encryption.UnlockAsync(PoolName, PoolKey.New(), false, Token);

        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["load-key", ..]);
        // Everything of the recorded pool is mounted already.
        Assert.Empty(Mounted());
        Assert.Equal("storage.pool.not_found", await ErrorAsync(() => _encryption.UnlockAsync("nope", null, false, Token)));
    }

    [Fact]
    public async Task APoolWithoutItsOwnKeyHasNothingToUnlockOrChange()
    {
        Pools("zfs-mirror-single");
        await _state.RefreshPoolsAsync(Token);

        Assert.Equal("storage.pool.not_encrypted", await ErrorAsync(() => _encryption.UnlockAsync("pithyxtestm", _key, false, Token)));
        Assert.Equal("storage.pool.not_encrypted", await ErrorAsync(() => _encryption.ChangeKeyAsync("pithyxtestm", _key, false, Token)));
        Assert.Equal("storage.pool.not_encrypted", await ErrorAsync(() => _encryption.ForgetKeyAsync("pithyxtestm", Token)));
    }

    [Fact]
    public async Task AChangedKeyReplacesTheOldOneInZfsAndInTheVault()
    {
        await PoolAsync();
        var next = PoolKey.New();
        Assert.Equal("storage.pool.locked", await ErrorAsync(() => _encryption.ChangeKeyAsync(PoolName, next, true, Token)));

        await _encryption.UnlockAsync(PoolName, _key, true, Token);
        Assert.Equal("storage.encryption.key_invalid", await ErrorAsync(() => _encryption.ChangeKeyAsync(PoolName, "PXPK", true, Token)));
        await _encryption.ChangeKeyAsync(PoolName, next, true, Token);

        var change = Assert.Single(_commands.Calls, call => call.Arguments is ["change-key", ..]);
        Assert.Equal(["change-key", "-o", "keyformat=hex", "-o", "keylocation=prompt", Root], change.Arguments);
        Assert.Equal(PoolKey.ToHex(next), _loadedKey);
        Assert.Equal(PoolKey.ToHex(next), _vault.Read(Guid));

        await _encryption.ChangeKeyAsync(PoolName, PoolKey.New(), false, Token);
        Assert.False(_vault.Has(Guid));
        Assert.False(_pools.Current.Pools.Single().Encryption!.KeyInVault);
    }

    [Fact]
    public async Task ForgettingTheKeyEmptiesTheVaultAndLeavesThePoolAsItIs()
    {
        _vault.Save(Guid, PoolKey.ToHex(_key));
        Pools("zfs-encrypted");
        await PoolAsync();

        await _encryption.ForgetKeyAsync(PoolName, Token);

        Assert.False(_vault.Has(Guid));
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: false), _pools.Current.Pools.Single().Encryption);
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["unload-key" or "unmount", ..]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADestroyedPoolTakesItsKeyOutOfTheVault(bool keyStored)
    {
        if (keyStored)
        {
            _vault.Save(Guid, PoolKey.ToHex(_key));
        }
        Pools("zfs-encrypted");
        await PoolAsync();
        await _state.RefreshDisksAsync([], Token);
        var planner = new StoragePlanner(_state, _facts, _pools, new PoolImportScanner(_commands, _state, _facts, _pools), Datasets(), _commands, _time, _vault);
        var plan = (await planner.PlanAsync(new StoragePlanRequest("pool.destroy", PoolName, ConfirmName: PoolName), Token)).Plan!;

        Pools("zfs-empty");
        await new CommandPlanRunner(_commands, _state, _facts, _time).RunAsync(plan, null, Token);

        Assert.False(_vault.Has(Guid));
    }

    [Fact]
    public async Task AnEncryptedPoolIsCreatedWithTheKeyOnStdinAndKeepsItInTheVaultWhenAsked()
    {
        Pools("zfs-empty");
        await _state.RefreshPoolsAsync(Token);
        await _state.RefreshDisksAsync([], Token);
        var planner = new StoragePlanner(_state, _facts, _pools, new PoolImportScanner(_commands, _state, _facts, _pools), Datasets(), _commands, _time, _vault);
        var request = new StoragePlanRequest("pool.create", PoolName, "single", ["serial-VYXTEST03"], Encryption: "key", EncryptionKey: _key, KeepKeyInVault: true);

        Assert.Equal("storage.encryption.key_required", (await Assert.ThrowsAsync<PithyxException>(() => planner.PlanAsync(request with { EncryptionKey = null }, Token))).Code);
        Assert.Equal("storage.encryption.key_invalid", (await Assert.ThrowsAsync<PithyxException>(() => planner.PlanAsync(request with { EncryptionKey = "PXPK-0000" }, Token))).Code);
        Assert.Equal("storage.pool.encryption_required", (await Assert.ThrowsAsync<PithyxException>(() => planner.PlanAsync(request with { Encryption = null }, Token))).Code);
        var plan = (await planner.PlanAsync(request, Token)).Plan!;

        var creates = plan.Steps.Where(step => step.Command.Arguments is ["create", ..] && step.Command.FileName == "zfs").ToList();
        Assert.Equal(["create", "-o", "encryption=aes-256-gcm", "-o", "keyformat=hex", "-o", "keylocation=prompt", "-o", "canmount=off", "-o", "mountpoint=none", Root], creates[0].Command.Arguments);
        Assert.Equal(EncryptionCommands.Input(PoolKey.ToHex(_key)), creates[0].Command.StandardInput);
        Assert.All(creates.Skip(1), step => Assert.Null(step.Command.StandardInput));
        Assert.DoesNotContain(plan.Steps.SelectMany(step => step.Command.Arguments), argument => argument.Contains(PoolKey.ToHex(_key), StringComparison.Ordinal) || argument.Contains(_key, StringComparison.Ordinal));
        Assert.DoesNotContain(PoolKey.ToHex(_key), creates[0].Command.ToString(), StringComparison.Ordinal);

        Pools("zfs-encrypted");
        await new CommandPlanRunner(_commands, _state, _facts, _time).RunAsync(plan, null, Token);
        Assert.Equal(PoolKey.ToHex(_key), _vault.Read(Guid));
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), _pools.Current.Pools.Single().Encryption);

        var manual = (await planner.PlanAsync(request with { Pool = "tank", KeepKeyInVault = false }, Token)).Plan!;
        Assert.Null(manual.AfterRun);
        var plain = (await planner.PlanAsync(request with { Pool = "tank", Encryption = "none", EncryptionKey = null }, Token)).Plan!;
        Assert.All(plain.Steps, step => Assert.Null(step.Command.StandardInput));
        Assert.DoesNotContain(plain.Steps.SelectMany(step => step.Command.Arguments), argument => argument.StartsWith("encryption=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADamagedVaultFileLeavesThePoolLockedAndSaysSoUntilTheTypedKeyRepairsIt()
    {
        Directory.CreateDirectory(_vault.Directory);
        await File.WriteAllTextAsync(Path.Combine(_vault.Directory, $"{Guid}.key"), "zz-not-a-key\n", Token);
        await PoolAsync();

        await _encryption.UnlockFromVaultAsync(Token);

        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["load-key", ..]);
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: true, VaultFailed: true), _pools.Current.Pools.Single().Encryption);
        var alert = Assert.Single(Pithyx.Daemon.Storage.Health.StorageAlerts.Evaluate(_pools.Current, [], [], _time.GetUtcNow()));
        Assert.Equal(($"pool.locked:{PoolName}", "critical", "storage.alert.poolLockedVault"), (alert.Key, alert.Severity, alert.Code));

        await _encryption.UnlockAsync(PoolName, _key, true, Token);

        Assert.Equal(PoolKey.ToHex(_key), _vault.Read(Guid));
        Assert.False(_vault.Failed(Guid));
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), _pools.Current.Pools.Single().Encryption);
        Assert.Empty(Pithyx.Daemon.Storage.Health.StorageAlerts.Evaluate(_pools.Current, [], [], _time.GetUtcNow()));
    }

    [Fact]
    public async Task AWrongVaultKeyMarksTheFailureWithoutTouchingThePool()
    {
        _vault.Save(Guid, PoolKey.ToHex(PoolKey.New()));
        await PoolAsync();
        Assert.False(_pools.Current.Pools.Single().Encryption!.VaultFailed);

        await _encryption.UnlockFromVaultAsync(Token);

        Assert.True(_pools.Current.Pools.Single().Encryption!.VaultFailed);
        Assert.Empty(Mounted());
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["change-key" or "destroy" or "unload-key", ..]);
    }

    [Fact]
    public async Task AKeyCommandThatHangsEndsWithTheTimeoutCodeAndChangesNothing()
    {
        _keyCommandsHang = true;
        await PoolAsync();

        Assert.Equal("storage.command.timed_out", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, _key, true, Token)));
        Assert.False(_vault.Has(Guid));

        _keyCommandsHang = false;
        await _encryption.UnlockAsync(PoolName, _key, false, Token);
        _keyCommandsHang = true;
        _vault.Save(Guid, PoolKey.ToHex(_key));
        Assert.Equal("storage.command.timed_out", await ErrorAsync(() => _encryption.ChangeKeyAsync(PoolName, PoolKey.New(), true, Token)));
        Assert.Equal(PoolKey.ToHex(_key), _vault.Read(Guid));
    }

    [Fact]
    public async Task AFailedMountLeavesThePoolUnlockedAndASecondUnlockMountsWhatIsMissing()
    {
        await PoolAsync();
        _failingMounts.Add($"{Root}/homes/alice");
        _commands.Handler = WithLoadedPool(_commands.Handler!, () => UnlockedWithout($"{Root}/homes/alice"));

        var failed = await Assert.ThrowsAsync<PithyxException>(() => _encryption.UnlockAsync(PoolName, _key, false, Token));

        Assert.Equal("storage.command.failed", failed.Code);
        Assert.Contains($"{Root}/homes/alice", failed.Parameters["stderr"]!.ToString(), StringComparison.Ordinal);
        Assert.False(_pools.Current.Pools.Single().Encryption!.Locked);
        _failingMounts.Clear();
        _commands.ClearCalls();

        // The dialog sends the key again; on an unlocked pool it is not checked, only what is missing is mounted.
        await _encryption.UnlockAsync(PoolName, _key, false, Token);
        Assert.Equal([$"{Root}/homes/alice"], Mounted());
        _commands.ClearCalls();
        _vault.Save(Guid, PoolKey.ToHex(_key));
        await _encryption.UnlockAsync(PoolName, _key, true, Token);
        Assert.Equal("storage.pool.not_locked", await ErrorAsync(() => _encryption.UnlockAsync(PoolName, PoolKey.New(), true, Token)));

        Assert.Equal([$"{Root}/homes/alice"], Mounted());
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["load-key", ..]);
    }

    [Fact]
    public async Task AVaultThatCannotBeWrittenNeverCostsTheUnlockAndSaysSo()
    {
        await PoolAsync();
        BreakVault();

        var error = await ErrorAsync(() => _encryption.UnlockAsync(PoolName, _key, true, Token));

        Assert.Equal("storage.encryption.vault_failed", error);
        Assert.Equal(4, Mounted().Length);
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: false), _pools.Current.Pools.Single().Encryption);
    }

    [Fact]
    public async Task AChangedKeyThatTheVaultCannotTakeLeavesNoOldKeyBehindThatWouldFail()
    {
        _vault.Save(Guid, PoolKey.ToHex(_key));
        Pools("zfs-encrypted");
        await PoolAsync();
        var next = PoolKey.New();
        _loadedKey = PoolKey.ToHex(_key);
        File.Delete(Path.Combine(_vault.Directory, $"{Guid}.key"));
        BreakVault();

        Assert.Equal("storage.encryption.vault_failed", await ErrorAsync(() => _encryption.ChangeKeyAsync(PoolName, next, true, Token)));

        Assert.Equal(PoolKey.ToHex(next), _loadedKey);
        Assert.Null(_vault.Read(Guid));
        Assert.False(_pools.Current.Pools.Single().Encryption!.KeyInVault);
    }

    [Fact]
    public async Task AKeyFromTheVaultCanBeShownAgainButOnlyOneThatIsThere()
    {
        Pools("zfs-encrypted");
        await PoolAsync();
        Assert.Equal("storage.encryption.not_in_vault", Assert.Throws<PithyxException>(() => _encryption.RevealKey(PoolName, "admin")).Code);

        _vault.Save(Guid, PoolKey.ToHex(_key));

        var hex = _encryption.RevealKey(PoolName, "admin");
        Assert.Equal(PoolKey.ToHex(_key), hex);
        Assert.Equal(_key, PoolKey.FromHex(hex));
        Assert.Equal("storage.pool.not_found", Assert.Throws<PithyxException>(() => _encryption.RevealKey("nope", "admin")).Code);
        Pools("zfs-mirror-single");
        await _state.RefreshPoolsAsync(Token);
        Assert.Equal("storage.pool.not_encrypted", Assert.Throws<PithyxException>(() => _encryption.RevealKey("pithyxtestm", "admin")).Code);
    }

    [Fact]
    public async Task ANewPoolWhoseKeyTheVaultCannotTakeExistsAndThePlanSaysSo()
    {
        Pools("zfs-empty");
        await _state.RefreshPoolsAsync(Token);
        await _state.RefreshDisksAsync([], Token);
        var planner = new StoragePlanner(_state, _facts, _pools, new PoolImportScanner(_commands, _state, _facts, _pools), Datasets(), _commands, _time, _vault);
        var plan = (await planner.PlanAsync(new StoragePlanRequest("pool.create", PoolName, "single", ["serial-VYXTEST03"], Encryption: "key", EncryptionKey: _key, KeepKeyInVault: true), Token)).Plan!;
        BreakVault();

        Pools("zfs-encrypted");
        var error = await Assert.ThrowsAsync<PithyxException>(() => new CommandPlanRunner(_commands, _state, _facts, _time).RunAsync(plan, null, Token));

        Assert.Equal("storage.encryption.vault_failed", error.Code);
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: false), _pools.Current.Pools.Single().Encryption);
    }

    [Fact]
    public void OnlyPithyxsOwnEncryptionRootMakesAPoolEncrypted()
    {
        Dataset Dataset(string name, string encryption, string? keyStatus, string? root) =>
            new(name, "tank", "filesystem", 0, 0, 0, null, false, encryption, keyStatus, root, null, null, null, null, null, null, 0);
        var pool = Pithyx.Daemon.Tests.Storage.Health.StorageAlertsTests.Pool();
        PoolEncryption? Of(params Dataset[] datasets) => EncryptionCommands.Apply(new PoolList([pool], datasets), _ => true, _ => true).Pools.Single().Encryption;

        Assert.Null(Of(Dataset("tank/data", "aes-256-gcm", "unavailable", "tank/data")));
        Assert.Null(Of(Dataset("tank/pithyx", "off", null, null), Dataset("tank/pithyx/shares/x", "aes-256-gcm", "unavailable", "tank/pithyx/shares/x")));
        Assert.Null(Of(Dataset("tank/pithyx", "aes-256-gcm", "unavailable", "tank")));
        Assert.Equal(new PoolEncryption(true, true, true), Of(Dataset("tank/pithyx", "aes-256-gcm", "unavailable", "tank/pithyx")));
        Assert.Equal(new PoolEncryption(false, true, false), Of(Dataset("tank/pithyx", "aes-256-gcm", "available", "tank/pithyx")));
    }

    /// <summary>After a successful load-key the pools read as <paramref name="unlocked"/> says, not as the recorded unlocked pool.</summary>
    private static Func<CommandRequest, CommandResult?> WithLoadedPool(Func<CommandRequest, CommandResult?> handler, Action unlocked) => request =>
    {
        var result = handler(request);
        if (request.Arguments is ["load-key", ..] && result is { ExitCode: 0 })
        {
            unlocked();
        }
        return result;
    };

    private DatasetPlanner Datasets()
    {
        var connections = new SqliteConnectionFactory(Path.Combine(_directory, "unused.db"));
        var snapshots = new SnapshotInventory();
        return new DatasetPlanner(
            _state,
            _pools,
            snapshots,
            new SnapshotService(_commands, snapshots, _pools, connections, _time, NullLogger<SnapshotService>.Instance),
            new ShareService(connections, _time),
            _time,
            NullLogger<DatasetPlanner>.Instance);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^PXPK(-[0-9A-HJKMNP-TV-Z]{4}){14}$")]
    private static partial System.Text.RegularExpressions.Regex KeyShape();
}
