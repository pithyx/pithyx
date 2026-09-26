using Microsoft.AspNetCore.Http;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Tests.Errors;

public sealed class ProblemMappingTests
{
    [Fact]
    public void VyxisExceptionKeepsCodeAndParameters()
    {
        var parameters = new Dictionary<string, object?> { ["fields"] = new[] { "name" } };

        var (code, mapped) = ProblemMapping.FromException(new VyxisException(ErrorCodes.RequestBodyInvalid, parameters));

        Assert.Equal(ErrorCodes.RequestBodyInvalid, code);
        Assert.Same(parameters, mapped);
    }

    [Fact]
    public void MalformedBodyIsInvalidRequest() =>
        Assert.Equal(ErrorCodes.RequestBodyInvalid, ProblemMapping.FromException(new BadHttpRequestException("bad json")).Code);

    [Fact]
    public void UnknownExceptionIsUnexpected() =>
        Assert.Equal(ErrorCodes.SystemInternalUnexpected, ProblemMapping.FromException(new InvalidOperationException("boom")).Code);

    [Theory]
    [InlineData(404, ErrorCodes.RequestRouteNotFound)]
    [InlineData(405, ErrorCodes.RequestMethodNotAllowed)]
    [InlineData(415, ErrorCodes.RequestMediaTypeUnsupported)]
    [InlineData(503, ErrorCodes.SystemInternalUnexpected)]
    public void StatusOnlyProblemsGetACode(int status, string expected) => Assert.Equal(expected, ProblemMapping.FromStatus(status));
}
