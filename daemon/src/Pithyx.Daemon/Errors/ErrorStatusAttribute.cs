using System.Net;

namespace Pithyx.Daemon.Errors;

/// <summary>The HTTP status an error code is returned with.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ErrorStatusAttribute(HttpStatusCode status) : Attribute
{
    public HttpStatusCode Status { get; } = status;
}
