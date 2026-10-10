using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Updates;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Updates;

/// <summary>What the daemon of a restored snapshot takes over from <c>result.json</c> into its older database.</summary>
public sealed class UpdateOutcomeTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _start = new(2026, 10, 10, 7, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-outcome-").FullName;
    private readonly SqliteConnectionFactory _connections;

    public UpdateOutcomeTests() => _connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));

    public async ValueTask InitializeAsync() =>
        await new MigrationRunner(_connections, new FakeTimeProvider(_start), NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheCarriedHistoryWinsOverTheSnapshotsRunningEntry()
    {
        // The snapshot was taken while update 2 still ran; the rollback to it (entry 3) carries the history that followed.
        await using (var connection = await _connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                "INSERT INTO system_updates (id, kind, source, from_version, to_version, state, started_at) VALUES (2, 'update', 'online', '0.1.0-dev.2', '0.1.0-dev.3', 'running', @at)",
                new { at = _start.ToString("O") });
        }
        var history = new List<UpdateHistoryEntry>
        {
            new(2, "update", "online", "0.1.0-dev.2", "0.1.0-dev.3", "a", "succeeded", null, null, _start, _start.AddMinutes(3)),
            new(3, "rollback", "recovery", "0.1.0-dev.3", "0.1.0-dev.2", "a", "restarting", null, null, _start.AddMinutes(10), null),
        };

        await UpdateService.MergeOutcomeAsync(_connections, new UpdateOutcome(3, "succeeded", null, null, _start.AddMinutes(11), history), Token);

        await using var check = await _connections.OpenAsync(Token);
        var states = (await check.QueryAsync<(long Id, string State)>("SELECT id, state FROM system_updates ORDER BY id")).ToList();
        Assert.Equal([(2L, "succeeded"), (3L, "succeeded")], states);
    }
}
