namespace Vyxis.Daemon.Realtime;

/// <summary>
/// Every close code the daemon sends on <c>/ws</c>. The desktop maps each one to an action
/// (<c>closeActions</c> in packages/api-client), so adding a code here fails the TypeScript build until the client
/// knows what to do with it.
/// </summary>
public enum WebSocketCloseCode
{
    /// <summary>The client closed the connection. Nothing to do.</summary>
    NormalClosure = 1000,

    /// <summary>The daemon is stopping or restarting. Reconnect with backoff.</summary>
    DaemonRestart = 1001,

    /// <summary>The client did not read fast enough and its outbox overflowed. Reconnect and restore subscriptions.</summary>
    ClientTooSlow = 1008,

    /// <summary>A client message was larger than 16 KB. Reconnect.</summary>
    MessageTooBig = 1009,

    /// <summary>The session ended: logout, password reset or expiry. Go to the login screen; do not reconnect.</summary>
    SessionEnded = 4401,

    /// <summary>The origin or a permission was rejected. Do not reconnect.</summary>
    Forbidden = 4403,
}
