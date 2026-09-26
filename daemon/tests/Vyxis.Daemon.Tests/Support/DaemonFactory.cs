using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Vyxis.Daemon.Tests.Support;

/// <summary>Runs the daemon in memory with its files in a temporary directory.</summary>
public class DaemonFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("vyxis-test-").FullName;

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
        builder.UseSetting("VYXIS_LOG_DIR", Path.Combine(DataDirectory, "logs"));
        builder.UseSetting("VYXIS_DB", Path.Combine(DataDirectory, "vyxis.db"));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
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
