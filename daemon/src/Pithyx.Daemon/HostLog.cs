namespace Pithyx.Daemon;

internal static partial class HostLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Serving plain HTTP on port {Port} in {Mode} mode. Do not expose this to a network.")]
    public static partial void PlainHttp(ILogger logger, int port, DaemonMode mode);
}
