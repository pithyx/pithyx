namespace Pithyx.Daemon.Hosting;

/// <summary>Where the daemon keeps its files. Development and simulation use <c>.pithyx-dev/</c> in the repository.</summary>
/// <param name="LogDirectory">Structured logs.</param>
/// <param name="DatabasePath">The SQLite database.</param>
/// <param name="DesktopDirectory">The built desktop.</param>
/// <param name="CacheDirectory">Rebuildable data on the system disk (thumbnails); safe to delete.</param>
public sealed record DaemonPaths(string LogDirectory, string DatabasePath, string DesktopDirectory, string CacheDirectory)
{
    public static DaemonPaths Resolve(IConfiguration configuration, IHostEnvironment environment, DaemonMode mode)
    {
        var development = environment.IsDevelopment() || mode == DaemonMode.Simulated;
        var devRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", ".pithyx-dev"));
        return new DaemonPaths(
            configuration["PITHYX_LOG_DIR"] ?? (development ? Path.Combine(devRoot, "logs") : "/var/log/pithyx"),
            configuration["PITHYX_DB"] ?? (development ? Path.Combine(devRoot, "pithyx.db") : "/var/lib/pithyx/pithyx.db"),
            configuration["PITHYX_DESKTOP_DIR"] ?? (development
                ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", "desktop", "dist"))
                : "/usr/lib/pithyx/desktop"),
            configuration["PITHYX_CACHE_DIR"] ?? (development ? Path.Combine(devRoot, "cache") : "/var/cache/pithyx"));
    }
}
