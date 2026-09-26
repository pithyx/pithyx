using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Vyxis.Daemon.Tests.Support;

/// <summary>Runs the daemon in memory with its files in a temporary directory.</summary>
public class DaemonFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("vyxis-test-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("VYXIS_LOG_DIR", Path.Combine(DataDirectory, "logs"));
        builder.UseSetting("VYXIS_DB", Path.Combine(DataDirectory, "vyxis.db"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}
