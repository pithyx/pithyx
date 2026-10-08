using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Tests.Apps;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Logs;

/// <summary>Which journal entries of app containers reach the Log Center (A14, D305).</summary>
public sealed class JournalReaderTests : IDisposable
{
    private const string AppId = "com.example.chat";

    private readonly DaemonFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static PendingLogEntry Entry(LogLevelName level, string source = "app/" + AppId) =>
        new(DateTimeOffset.UnixEpoch, level, source, "listening");

    [Fact]
    public async Task AnAppsOutputIsKeptWhileItIsInstalledOrBeingInstalled()
    {
        var registry = _factory.Services.GetRequiredService<AppRegistry>();

        Assert.True(JournalReader.Keeps(Entry(LogLevelName.Info, "system/docker"), registry, null));
        Assert.False(JournalReader.Keeps(Entry(LogLevelName.Info), registry, null));

        // The backend of a first install starts before the registry names the app; its output must not get lost.
        Assert.True(registry.TryBegin(AppId));
        Assert.True(JournalReader.Keeps(Entry(LogLevelName.Warn), registry, null));
        Assert.False(JournalReader.Keeps(Entry(LogLevelName.Debug), registry, null));
        registry.End(AppId);
        Assert.False(JournalReader.Keeps(Entry(LogLevelName.Warn), registry, null));

        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteInsertAppAsync(AppId);
        }
        await registry.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(JournalReader.Keeps(Entry(LogLevelName.Info), registry, null));
        Assert.False(JournalReader.Keeps(Entry(LogLevelName.Debug), registry, null));
    }
}
