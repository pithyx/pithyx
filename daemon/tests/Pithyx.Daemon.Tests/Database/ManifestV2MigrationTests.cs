using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Database;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Database;

/// <summary>
/// Migration 0038 (D388): the stored manifests of installed apps, and of the version before their last update, become
/// manifest version 2 with one rights map, so the apps of a box keep working after the upgrade.
/// </summary>
public sealed class ManifestV2MigrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-manifest-v2-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private const string Chat =
        """{"$schema":"./node_modules/@pithyx/sdk/schema/manifest.v1.schema.json","manifestVersion":1,"id":"com.example.chat","version":"1.1.0","name":{"en":"Chat"},"author":{"name":"Example"},"homepage":"https://example.org/chat","entry":"frontend/index.html","sdk":"^1.0.0","permissions":["notifications","network.internet"],"window":{"instances":"single","defaultSize":{"width":800,"height":600},"minSize":{"width":320,"height":240}},"backend":{"compose":"backend/compose.yaml","endpoints":{"api":{"service":"web","port":8080}},"lifecycle":"onDemand","elevated":["device:/dev/dri","docker.socket.raw"],"optionalDevices":["/dev/dri"]}}""";

    private const string ChatBefore =
        """{"manifestVersion":1,"id":"com.example.chat","version":"1.0.0","name":{"en":"Chat"},"author":{"name":"Example"},"entry":"frontend/index.html","sdk":"^1.0.0","permissions":["notifications"],"window":{"instances":"single","defaultSize":{"width":800,"height":600},"minSize":{"width":320,"height":240}}}""";

    private async Task<SqliteConnection> UpgradeAsync()
    {
        var connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        var runner = new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version <= 37)], Token);
        await using (var connection = await connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (40000, 'app', 'com.example.chat', '2026-10-01T00:00:00Z');
                INSERT INTO apps (id, version, manifest, source, icon, package_sha256, uid, installed_at, updated_at, previous)
                VALUES ('com.example.chat', '1.1.0', @manifest, 'sideload', X'3C7376672F3E', @sha, 40000, '2026-10-01T00:00:00Z', '2026-10-02T00:00:00Z', @previous);
                """,
                new
                {
                    manifest = Chat,
                    sha = new string('a', 64),
                    previous = JsonSerializer.Serialize(new { version = "1.0.0", manifest = JsonDocument.Parse(ChatBefore).RootElement, icon = "PHN2Zy8+" }),
                });
        }
        await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version <= 38)], Token);
        return await connections.OpenAsync(Token);
    }

    [Fact]
    public async Task PermissionsAndElevatedRightsBecomeOneMapWithTheOptionalDevice()
    {
        await using var connection = await UpgradeAsync();
        using var manifest = JsonDocument.Parse(await connection.ExecuteScalarAsync<string>("SELECT manifest FROM apps") ?? "");
        var root = manifest.RootElement;

        Assert.Equal(ManifestValidator.SupportedVersion, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(["notifications", "network.internet", "device:/dev/dri", "docker.socket.raw"], root.GetProperty("permissions").EnumerateObject().Select(entry => entry.Name));
        Assert.Equal(["notifications", "network.internet"], ElevatedRights.PermissionsOf(root));
        Assert.Equal(["device:/dev/dri", "docker.socket.raw"], ElevatedRights.Of(root));
        Assert.Equal(["/dev/dri"], ElevatedRights.OptionalDevices(root));
        Assert.True(ElevatedRights.IsCritical(root));
        Assert.False(root.GetProperty("backend").TryGetProperty("elevated", out _));
        Assert.False(root.GetProperty("backend").TryGetProperty("optionalDevices", out _));
        Assert.Equal(["amd64", "arm64"], root.GetProperty("arch").EnumerateArray().Select(arch => arch.GetString()));
        Assert.Equal("https://example.org/chat", root.GetProperty("source").GetString());
        Assert.EndsWith("manifest.v2.schema.json", root.GetProperty("$schema").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVersionBeforeTheLastUpdateIsConvertedForItsRollback()
    {
        await using var connection = await UpgradeAsync();
        using var previous = JsonDocument.Parse(await connection.ExecuteScalarAsync<string>("SELECT previous FROM apps") ?? "");
        var manifest = previous.RootElement.GetProperty("manifest");

        Assert.Equal("1.0.0", previous.RootElement.GetProperty("version").GetString());
        Assert.Equal(ManifestValidator.SupportedVersion, manifest.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(["notifications"], ElevatedRights.PermissionsOf(manifest));
        Assert.Equal("private", manifest.GetProperty("source").GetString());
    }
}
