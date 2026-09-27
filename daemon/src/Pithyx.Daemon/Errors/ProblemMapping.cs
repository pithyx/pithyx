namespace Pithyx.Daemon.Errors;

/// <summary>Decides which error code a failure is reported with.</summary>
public static class ProblemMapping
{
    private static readonly IReadOnlyDictionary<string, object?> _noParameters = new Dictionary<string, object?>();

    public static (string Code, IReadOnlyDictionary<string, object?> Parameters) FromException(Exception exception) => exception switch
    {
        PithyxException pithyx => (pithyx.Code, pithyx.Parameters),
        BadHttpRequestException { StatusCode: StatusCodes.Status415UnsupportedMediaType } => (ErrorCodes.RequestMediaTypeUnsupported, _noParameters),
        BadHttpRequestException => (ErrorCodes.RequestBodyInvalid, _noParameters),
        _ => (ErrorCodes.SystemInternalUnexpected, _noParameters),
    };

    /// <summary>The code for a problem response that was produced by the framework from a status code alone.</summary>
    public static string FromStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.RequestBodyInvalid,
        StatusCodes.Status404NotFound => ErrorCodes.RequestRouteNotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.RequestMethodNotAllowed,
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.RequestMediaTypeUnsupported,
        StatusCodes.Status416RangeNotSatisfiable => ErrorCodes.FsRangeNotSatisfiable,
        _ => ErrorCodes.SystemInternalUnexpected,
    };
}
