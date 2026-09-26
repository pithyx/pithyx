namespace Vyxis.Daemon.Hosting;

/// <summary>Where the daemon keeps its files. Development and simulation use <c>.vyxis-dev/</c> in the repository.</summary>
public sealed record DaemonPaths(string LogDirectory, string DatabasePath)
{
    public static DaemonPaths Resolve(IConfiguration configuration, IHostEnvironment environment, DaemonMode mode)
    {
        var development = environment.IsDevelopment() || mode == DaemonMode.Simulated;
        var devRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", ".vyxis-dev"));
        return new DaemonPaths(
            configuration["VYXIS_LOG_DIR"] ?? (development ? Path.Combine(devRoot, "logs") : "/var/log/vyxis"),
            configuration["VYXIS_DB"] ?? (development ? Path.Combine(devRoot, "vyxis.db") : "/var/lib/vyxis/vyxis.db"));
    }
}
