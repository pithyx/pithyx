using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Vyxis.Daemon.Tests.Support;

/// <summary>Runs the daemon in memory with its files in a temporary directory.</summary>
public class DaemonFactory : WebApplicationFactory<Program>
{
    private string? _dataDirectory;

    /// <summary>A new temporary directory, or the one of an earlier factory to play a daemon restart.</summary>
    public string DataDirectory
    {
        get => _dataDirectory ??= Directory.CreateTempSubdirectory("vyxis-test-").FullName;
        init => _dataDirectory = value;
    }

    /// <summary>Leaves the files behind on dispose, for a factory that plays the restart.</summary>
    public bool KeepDataDirectory { get; init; }

    /// <summary>The daemon's clock. Starts at the current time and only moves when a test advances it.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>A client that keeps cookies and sends the CSRF header like the desktop does.</summary>
    public HttpClient CreateApiClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Vyxis-Request", "1");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Simulation by default, so tests never read the host's disks; a test can still switch to real mode.
        builder.UseSetting("VYXIS_SIMULATE", "1");
        builder.UseSetting("VYXIS_LOG_DIR", Path.Combine(DataDirectory, "logs"));
        builder.UseSetting("VYXIS_DB", Path.Combine(DataDirectory, "vyxis.db"));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !KeepDataDirectory && _dataDirectory is not null && Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
