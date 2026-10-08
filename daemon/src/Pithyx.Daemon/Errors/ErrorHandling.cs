using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Errors;

public static partial class ErrorHandling
{
    public const string CodeKey = "code";
    public const string ParamsKey = "params";
    public const string RequestIdKey = "requestId";

    /// <summary>Every failing request, including framework errors such as 404, answers with <see cref="ApiProblem"/>.</summary>
    public static IServiceCollection AddPithyxErrors(this IServiceCollection services)
    {
        services.AddExceptionHandler<ExceptionHandler>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = Customize);
        return services;
    }

    /// <summary>Answers with the problem of a code outside the exception flow (authentication handlers, middleware).</summary>
    public static ValueTask<bool> WriteProblemAsync(this IProblemDetailsService problemDetails, HttpContext context, string code)
    {
        ArgumentNullException.ThrowIfNull(problemDetails);
        ArgumentNullException.ThrowIfNull(context);
        var status = (int)ErrorCodes.StatusOf(code);
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status };
        problem.Extensions[CodeKey] = code;
        return problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    private static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        var code = problem.Extensions.TryGetValue(CodeKey, out var value) && value is string existing ? existing : ProblemMapping.FromStatus(status);
        var parameters = problem.Extensions.TryGetValue(ParamsKey, out var p) && p is IReadOnlyDictionary<string, object?> given
            ? given
            : new Dictionary<string, object?>();

        if (problem is HttpValidationProblemDetails validation)
        {
            code = ErrorCodes.RequestBodyInvalid;
            parameters = new Dictionary<string, object?> { ["fields"] = validation.Errors.Keys.Order(StringComparer.Ordinal).ToArray() };
            validation.Errors.Clear();
        }

        if (!ErrorCodes.All.ContainsKey(code))
        {
            code = ErrorCodes.SystemInternalUnexpected;
        }

        // RFC 9457: "about:blank" when no more specific type applies, for example 429.
        problem.Type ??= "about:blank";
        problem.Detail = ErrorMessages.Detail(code, parameters);
        problem.Extensions.Remove("traceId");
        problem.Extensions[CodeKey] = code;
        problem.Extensions[ParamsKey] = parameters;
        problem.Extensions[RequestIdKey] = Tracing.CurrentRequestId;
    }

    private sealed partial class ExceptionHandler(IProblemDetailsService problemDetails, ILogger<ExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            var (code, parameters) = ProblemMapping.FromException(exception);
            var status = (int)ErrorCodes.StatusOf(code);
            if (code == ErrorCodes.SystemInternalUnexpected)
            {
                LogUnexpected(exception, httpContext.Request.Method, httpContext.Request.Path);
            }
            else
            {
                LogExpected(code, httpContext.Request.Method, httpContext.Request.Path);
            }

            httpContext.Response.StatusCode = status;
            if (parameters.TryGetValue("retryAfterSeconds", out var retryAfter) && retryAfter is not null)
            {
                httpContext.Response.Headers.RetryAfter = Convert.ToString(retryAfter, System.Globalization.CultureInfo.InvariantCulture);
            }
            var problem = new ProblemDetails { Status = status };
            problem.Extensions[CodeKey] = code;
            problem.Extensions[ParamsKey] = parameters;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
                Exception = exception,
            });
        }

        [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error in {Method} {Path}")]
        private partial void LogUnexpected(Exception exception, string method, PathString path);

        [LoggerMessage(Level = LogLevel.Information, Message = "{Method} {Path} failed with {ErrorCode}")]
        private partial void LogExpected(string errorCode, string method, PathString path);
    }
}
