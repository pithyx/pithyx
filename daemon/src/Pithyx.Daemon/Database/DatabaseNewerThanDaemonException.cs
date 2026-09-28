namespace Pithyx.Daemon.Database;

/// <summary>The database holds schema versions this daemon does not know, so a newer daemon wrote it.</summary>
public sealed class DatabaseNewerThanDaemonException(string databasePath, IReadOnlyList<int> versions)
    : InvalidOperationException($"The database {databasePath} has schema versions this daemon does not know ({string.Join(", ", versions)}). A newer daemon wrote it.")
{
    public string DatabasePath { get; } = databasePath;

    public IReadOnlyList<int> Versions { get; } = versions;
}
