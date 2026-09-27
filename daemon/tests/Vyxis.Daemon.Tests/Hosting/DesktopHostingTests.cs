using System.Net;
using Vyxis.Daemon.Tests.Support;

namespace Vyxis.Daemon.Tests.Hosting;

public sealed class DesktopHostingTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;

    public DesktopHostingTests()
    {
        var desktop = Path.Combine(_factory.DataDirectory, "desktop");
        Directory.CreateDirectory(Path.Combine(desktop, "assets"));
        File.WriteAllText(Path.Combine(desktop, "index.html"), "<!doctype html><title>Vyxis</title>");
        File.WriteAllText(Path.Combine(desktop, "assets", "index-abc123.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(desktop, "assets", "index-abc123.js.br"), "brotli");
        File.WriteAllText(Path.Combine(desktop, "assets", "index-abc123.js.gz"), "gzip");
        _client = _factory.WithWebHostBuilder(builder => builder.UseSetting("VYXIS_DESKTOP_DIR", desktop)).CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/files/documents")]
    [InlineData("/settings/users/admin")]
    public async Task DesktopPathsServeIndexHtml(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task HashedAssetsAreCachedForever()
    {
        using var response = await _client.GetAsync("/assets/index-abc123.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", string.Join(",", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("br, gzip, deflate", "br", "brotli")]
    [InlineData("gzip, deflate", "gzip", "gzip")]
    public async Task PrecompressedAssetsAreServedWhenAccepted(string acceptEncoding, string encoding, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/index-abc123.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Contains("immutable", string.Join(",", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
        Assert.Equal(body, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssetsAreServedPlainWithoutAcceptEncoding()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/index-abc123.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("console.log(1);", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api")]
    public async Task UnknownApiPathsStayProblemDetails(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MissingAssetsAreNotAnsweredWithIndexHtml()
    {
        using var response = await _client.GetAsync("/assets/missing-123.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
