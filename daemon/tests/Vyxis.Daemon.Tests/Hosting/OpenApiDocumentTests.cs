using System.Text.Json;
using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Tests.Support;

namespace Vyxis.Daemon.Tests.Hosting;

public sealed class OpenApiDocumentTests(DaemonFactory factory) : IClassFixture<DaemonFactory>
{
    [Fact]
    public async Task DocumentListsEveryErrorCodeAndTheProblemSchema()
    {
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/openapi.json", UriKind.Relative), TestContext.Current.CancellationToken));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var codes = schemas.GetProperty("ErrorCode").GetProperty("enum").EnumerateArray().Select(code => code.GetString() ?? "").ToHashSet();
        Assert.Equal(ErrorCodes.All.Keys.ToHashSet(), codes);
        Assert.Equal("#/components/schemas/ErrorCode", schemas.GetProperty("ApiProblem").GetProperty("properties").GetProperty("code").GetProperty("$ref").GetString());
    }
}
