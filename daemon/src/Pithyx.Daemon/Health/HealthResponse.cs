namespace Pithyx.Daemon.Health;

public sealed record HealthResponse(string Status, DaemonMode Mode);
