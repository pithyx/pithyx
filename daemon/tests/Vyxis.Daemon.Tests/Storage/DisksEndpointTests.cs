using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vyxis.Daemon.Tests.Auth;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Storage;

public sealed class DisksEndpointTests(DaemonFactory factory) : IClassFixture<DaemonFactory>
{
    [Fact]
    public async Task ListsTheDisksFromMemory()
    {
        using var client = factory.CreateApiClient();
        await client.SetupAdminAsync();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/disks", Token);

        var disks = body.GetProperty("disks").EnumerateArray().ToList();
        Assert.Equal(7, disks.Count);
        Assert.Contains(disks, d => d.GetProperty("serial").GetString() == "VYXTEST06");
    }

    [Fact]
    public async Task RequiresASession()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync("/api/disks", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
