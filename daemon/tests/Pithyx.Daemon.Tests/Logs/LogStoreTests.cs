using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Logs;

public sealed class LogStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-logs-").FullName;
    private readonly LogStore _store;

    public LogStoreTests() => _store = new LogStore(Path.Combine(_directory, "system"));

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static LogQuery Query(int limit = 100) => new(null, null, null, null, null, null, limit);

    private static PendingLogEntry Entry(int second, string message, LogLevelName level = LogLevelName.Info, string source = "daemon", string? request = null) =>
        new(Start.AddSeconds(second), level, source, message, request);

    private async Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query)
    {
        await using var connection = await _store.OpenAsync(Token);
        return await LogDatabase.QueryAsync(connection, query, null, archived: false);
    }

    [Fact]
    public async Task FiltersByLevelSourceTimeAndText()
    {
        await _store.WriteAsync(
            [
                Entry(1, "pool tank is degraded", LogLevelName.Warn),
                Entry(2, "share media created", source: "app/files"),
                Entry(3, "100% done_now", request: "abc123"),
                Entry(4, "disk sdb removed", LogLevelName.Error, "system/kernel"),
            ],
            Token);

        Assert.Equal(["disk sdb removed", "pool tank is degraded"], (await QueryAsync(Query() with { Levels = new HashSet<LogLevelName> { LogLevelName.Warn, LogLevelName.Error } })).Select(entry => entry.Message));
        Assert.Equal(["share media created"], (await QueryAsync(Query() with { Sources = new HashSet<string> { "app/files" } })).Select(entry => entry.Message));
        Assert.Equal(["100% done_now", "share media created"], (await QueryAsync(Query() with { From = Start.AddSeconds(2), To = Start.AddSeconds(3) })).Select(entry => entry.Message));
        Assert.Equal(["pool tank is degraded"], (await QueryAsync(Query() with { Search = "TANK" })).Select(entry => entry.Message));
        Assert.Equal(["100% done_now"], (await QueryAsync(Query() with { Search = "0% d" })).Select(entry => entry.Message));
        Assert.Empty(await QueryAsync(Query() with { Search = "%" + "x" }));
        Assert.Equal(["100% done_now"], (await QueryAsync(Query() with { Search = "abc123" })).Select(entry => entry.Message));
        await using var connection = await _store.OpenAsync(Token);
        Assert.Equal(["app/files", "daemon", "system/kernel"], await LogDatabase.SourcesAsync(connection));
    }

    [Fact]
    public async Task PagesGoFromNewestToOldestWithoutGapsOrRepeats()
    {
        await _store.WriteAsync([.. Enumerable.Range(0, 25).Select(second => Entry(second / 2, $"line {second}"))], Token);
        var seen = new List<string>();
        LogCursor? cursor = null;
        while (true)
        {
            var page = await QueryAsync(Query(7) with { Before = cursor });
            seen.AddRange(page.Select(entry => entry.Message));
            if (page.Count < 7)
            {
                break;
            }
            cursor = new LogCursor(LogDatabase.Micros(page[^1].Time), page[^1].Id);
        }

        Assert.Equal(Enumerable.Range(0, 25).Reverse().Select(number => $"line {number}"), seen);
    }

    [Fact]
    public async Task PruningDropsOldEntriesAndKeepsTheFileBelowItsLimit()
    {
        var text = new string('x', 2000);
        for (var batch = 0; batch < 10; batch++)
        {
            await _store.WriteAsync([.. Enumerable.Range(batch * 100, 100).Select(second => Entry(second, text))], Token);
        }
        await using var connection = await _store.OpenAsync(Token);
        Assert.True(await LogDatabase.UsedBytesAsync(connection) > 2_000_000);

        var removed = await LogDatabase.PruneAsync(connection, Start.AddSeconds(100), 1_000_000);

        Assert.True(await LogDatabase.UsedBytesAsync(connection) <= 1_000_000);
        var (bytes, oldest) = await LogDatabase.StatusAsync(connection);
        Assert.True(bytes <= 1_000_000);
        Assert.True(oldest > Start.AddSeconds(100));
        Assert.True(removed > 100);
        var newest = await QueryAsync(Query(1));
        Assert.Equal(Start.AddSeconds(999), newest[0].Time);
    }

    [Fact]
    public async Task TheJournalCursorIsStoredWithItsBatch()
    {
        Assert.Null(await _store.JournalCursorAsync(Token));

        await _store.WriteAsync([Entry(1, "a") with { JournalCursor = "c1" }, Entry(2, "b") with { JournalCursor = "c2" }, Entry(3, "daemon line")], Token);

        Assert.Equal("c2", await _store.JournalCursorAsync(Token));
    }

    [Fact]
    public async Task TheArchiveHoldsWhatTheCurrentLogsDroppedAndNothingTwice()
    {
        var time = new FakeTimeProvider(Start);
        var mount = Path.Combine(_directory, "tank-system");
        var pools = new PoolInventory();
        pools.Replace(new PoolList(
            [new Pool("tank", "1", "ONLINE", 1, 0, 1, 0, 0, false, 0, null, [])],
            [new Dataset("tank/pithyx/system", "tank", "filesystem", 1, 1, 1, mount, true, "off", null, null, null, null, "lz4", 131072, null, null, 0)]));
        var archive = new LogArchive(pools, _store, time);
        var settings = LogSettings.Default with { ArchiveEnabled = true };
        await _store.WriteAsync([.. Enumerable.Range(0, 10).Select(second => Entry(second, $"line {second}"))], Token);

        Assert.True(await archive.SyncAsync(settings, force: false, Token));
        await using (var connection = await _store.OpenAsync(Token))
        {
            await LogDatabase.PruneAsync(connection, Start.AddSeconds(6), LogSettings.MaxMaxBytes);
        }
        await _store.WriteAsync([Entry(10, "line 10")], Token);
        Assert.True(await archive.SyncAsync(settings, force: true, Token));
        Assert.True(File.Exists(Path.Combine(mount, LogArchive.RelativePath)));

        await using var current = await _store.OpenAsync(Token);
        var fromCurrent = await LogDatabase.QueryAsync(current, Query(), null, archived: false);
        var oldest = LogDatabase.Micros(fromCurrent[^1].Time);
        var fromArchive = await archive.QueryAsync(settings, Query(), oldest, Token);
        Assert.Equal(Enumerable.Range(0, 11).Reverse().Select(number => $"line {number}"), fromCurrent.Concat(fromArchive).Select(entry => entry.Message));
        Assert.All(fromArchive, entry => Assert.True(entry.Archived));
        Assert.Equal(11, await archive.CopiedUntilAsync(settings, Token));
    }

    [Theory]
    [InlineData(false, "tank", LogArchiveState.Off)]
    [InlineData(true, null, LogArchiveState.Ready)]
    [InlineData(true, "other", LogArchiveState.Unavailable)]
    public void TheArchiveIsOnTheChosenOrFirstUsablePool(bool enabled, string? pool, LogArchiveState state)
    {
        var current = new PoolList(
            [new Pool("tank", "1", "ONLINE", 1, 0, 1, 0, 0, false, 0, null, []), new Pool("other", "2", "ONLINE", 1, 0, 1, 0, 0, true, 0, null, [])],
            [
                new Dataset("tank/pithyx/system", "tank", "filesystem", 1, 1, 1, "/tank/.pithyx/system", true, "off", null, null, null, null, "lz4", 131072, null, null, 0),
                new Dataset("other/pithyx/system", "other", "filesystem", 1, 1, 1, "/other/.pithyx/system", true, "off", null, null, null, null, "lz4", 131072, null, null, 0),
            ]);

        var located = LogArchive.Locate(current, LogSettings.Default with { ArchiveEnabled = enabled, ArchivePool = pool });

        Assert.Equal(state, located.State);
        Assert.Equal(["tank"], LogArchive.Candidates(current));
        Assert.Equal(state == LogArchiveState.Ready ? "/tank/.pithyx/system/logs/archive.db" : null, located.File);
    }
}
