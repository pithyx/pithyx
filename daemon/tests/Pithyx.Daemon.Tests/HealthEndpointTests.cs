using System.Net;
using System.Net.Http.Json;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests;

public sealed class HealthEndpointTests(DaemonFactory factory) : IClassFixture<DaemonFactory>
{
    [Theory]
    [InlineData("0", "real")]
    [InlineData("1", "simulated")]
    public async Task HealthReportsStatusAndMode(string simulate, string expectedMode)
    {
        using var client = factory.WithWebHostBuilder(builder => builder.UseSetting("PITHYX_SIMULATE", simulate)).CreateClient();

        using var response = await client.GetAsync(new Uri("/api/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HealthBody("ok", expectedMode), body);
    }

    private sealed record HealthBody(string Status, string Mode);
}
