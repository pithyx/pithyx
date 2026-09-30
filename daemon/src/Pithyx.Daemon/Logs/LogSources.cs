namespace Pithyx.Daemon.Logs;

/// <summary>Source names: the daemon, <c>system/&lt;service&gt;</c> from the journal and <c>app/&lt;id&gt;</c> from the SDK.</summary>
public static class LogSources
{
    public const string Daemon = "daemon";
    public const string SystemPrefix = "system/";
    public const string AppPrefix = "app/";
}
