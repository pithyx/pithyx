using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Hosting;

/// <summary>Starts the built daemon as a process, like systemd does, and checks why it exits.</summary>
public sealed class StartupExitTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-exit-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ADatabaseFromANewerDaemonEndsWithItsOwnExitCodeAndReason()
    {
        var database = Path.Combine(_directory, "pithyx.db");
        var newer = Migration.Embedded().Count + 1;
        await using (var connection = await new SqliteConnectionFactory(database).OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("CREATE TABLE schema_version (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL) STRICT");
            await connection.ExecuteAsync("INSERT INTO schema_version VALUES (@newer, 'from_a_newer_daemon', '2026-09-28T00:00:00Z')", new { newer });
        }

        var (exitCode, output) = await RunDaemonAsync(database);

        Assert.Equal(DaemonExitCode.DatabaseNewerThanDaemon, exitCode);
        Assert.Contains("a newer daemon wrote it", output, StringComparison.Ordinal);
        Assert.Contains($"[{newer}]", output, StringComparison.Ordinal);
        var logFile = await File.ReadAllTextAsync(Path.Combine(_directory, "logs", "daemon.json"), TestContext.Current.CancellationToken);
        Assert.Contains("a newer daemon wrote it", logFile, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunDaemonAsync(string database)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _directory,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pithyxd.dll"));
        start.Environment["PITHYX_SIMULATE"] = "1";
        start.Environment["PITHYX_FIXTURES"] = Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures");
        start.Environment["PITHYX_DB"] = database;
        start.Environment["PITHYX_LOG_DIR"] = Path.Combine(_directory, "logs");
        start.Environment["PITHYX_DESKTOP_DIR"] = _directory;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
