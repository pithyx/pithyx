using System.Net;
using System.Net.Http.Json;
using Vyxis.Daemon.Hosting;
using Vyxis.Daemon.Tests.Support;

namespace Vyxis.Daemon.Tests.Errors;

public sealed class ProblemResponseTests(DaemonFactory factory) : IClassFixture<DaemonFactory>
{
    [Fact]
    public async Task UnknownRouteReturnsProblemWithCodeAndRequestId()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(problem);
        Assert.Equal("request.route.not_found", problem.Code);
        Assert.Equal("This API endpoint does not exist.", problem.Detail);
        Assert.Equal(32, problem.RequestId.Length);
        Assert.Equal(problem.RequestId, Assert.Single(response.Headers.GetValues(RequestIdMiddleware.HeaderName)));
    }

    [Fact]
    public async Task WrongMethodReturnsMethodNotAllowed()
    {
        using var client = factory.CreateClient();

        using var response = await client.DeleteAsync(new Uri("/api/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("request.method.not_allowed", problem?.Code);
    }

    [Fact]
    public async Task IncomingTraceparentBecomesTheRequestId()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/does-not-exist");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken);

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", problem?.RequestId);
    }

    private sealed record Problem(int Status, string Detail, string Code, Dictionary<string, object> Params, string RequestId);
}
