using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// The install and update steps of an app's files without ZFS (simulation): a failure at any step, the registry
/// commit last, leaves the installed version exactly as it was. The snapshot path runs against real ZFS in
/// <see cref="Storage.Integration.AppDatasetIntegrationTests"/>.
/// </summary>
public sealed class AppVersionsTests : IDisposable
{
    private const string Pool = "tank";
    private const string AppId = "com.example.probe";
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-apps-").FullName;
    private readonly AppStorage _storage;

    public AppVersionsTests()
    {
        var pools = new PoolInventory();
        pools.Replace(new PoolList([new Pool(Pool, "1", "ONLINE", 0, 0, 0, 0, null, false, 0, null, [])], []));
        // Simulation never refreshes the pools or runs a command.
        _storage = new AppStorage(new ScriptedCommandRunner(), pools, null!, new AppStorageOptions(UseZfs: false, Path.Combine(_root, "pools")), TimeProvider.System);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string App => Path.Combine(_root, "pools", Pool, ".pithyx", "apps", AppId);

    private async Task<UnpackedPackage> PackageAsync(string version, string content)
    {
        var bytes = TarBuilder.MinimalPackage(AppId, version).File("frontend/marker.txt", content).End().Gzip();
        return await PackageReader.UnpackAsync(new MemoryStream(bytes), Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N")), PackageLimits.Default, Token);
    }

    private Task InstallAsync(UnpackedPackage package, string version, bool update, Func<CancellationToken, Task>? commit = null) =>
        AppVersions.InstallAsync(_storage, Pool, AppId, package, version, update, commit ?? (_ => Task.CompletedTask), Token);

    private string Marker(string version) => File.ReadAllText(Path.Combine(App, "versions", version, "frontend", "marker.txt"));

    [Fact]
    public async Task AFailedUpdateLeavesTheInstalledVersionAsItWas()
    {
        await InstallAsync(await PackageAsync("1.0.0", "one"), "1.0.0", update: false);
        await File.WriteAllTextAsync(Path.Combine(App, "volumes", "data.txt"), "kept", Token);

        var next = await PackageAsync("2.0.0", "two");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstallAsync(next, "2.0.0", update: true, _ => throw new InvalidOperationException("commit failed")));

        Assert.Equal(["1.0.0"], Directory.GetFileSystemEntries(Path.Combine(App, "versions")).Select(Path.GetFileName));
        Assert.Equal("versions/1.0.0", new FileInfo(Path.Combine(App, "current")).LinkTarget);
        Assert.Equal("one", Marker("1.0.0"));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(App, "volumes", "data.txt"), Token));
    }

    [Fact]
    public async Task AFailedReinstallBringsTheSameVersionsOldFilesBack()
    {
        await InstallAsync(await PackageAsync("1.0.0", "old"), "1.0.0", update: false);

        var next = await PackageAsync("1.0.0", "new");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstallAsync(next, "1.0.0", update: true, _ => throw new InvalidOperationException("commit failed")));

        Assert.Equal("old", Marker("1.0.0"));
        Assert.Equal(["1.0.0"], Directory.GetFileSystemEntries(Path.Combine(App, "versions")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFailedFirstInstallLeavesNoFrontend()
    {
        var next = await PackageAsync("1.0.0", "one");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstallAsync(next, "1.0.0", update: false, _ => throw new InvalidOperationException("commit failed")));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(App, "versions")));
        Assert.False(File.Exists(Path.Combine(App, "current")) || new FileInfo(Path.Combine(App, "current")).LinkTarget is not null);
    }

    [Fact]
    public async Task ASuccessfulUpdateKeepsOnlyTheNewVersionAndTheVolumes()
    {
        await InstallAsync(await PackageAsync("1.0.0", "one"), "1.0.0", update: false);
        await File.WriteAllTextAsync(Path.Combine(App, "volumes", "data.txt"), "kept", Token);

        var committed = false;
        await InstallAsync(await PackageAsync("1.1.0", "two"), "1.1.0", update: true, _ =>
        {
            // The new files are in place when the registry changes.
            Assert.Equal("two", Marker("1.1.0"));
            committed = true;
            return Task.CompletedTask;
        });

        Assert.True(committed);
        Assert.Equal(["1.1.0"], Directory.GetFileSystemEntries(Path.Combine(App, "versions")).Select(Path.GetFileName));
        Assert.Equal("versions/1.1.0", new FileInfo(Path.Combine(App, "current")).LinkTarget);
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(App, "volumes", "data.txt"), Token));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("99999999999999999999.0.0", "1.0.0", 1)]
    [InlineData("1.0.0-99999999999999999999", "1.0.0-100000000000000000000", -1)]
    public void VersionsCompareBySemVer(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(AppService.CompareVersions(left, right)));
}
