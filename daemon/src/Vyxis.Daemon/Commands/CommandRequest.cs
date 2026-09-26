namespace Vyxis.Daemon.Commands;

public sealed record CommandRequest(string FileName, IReadOnlyList<string> Arguments, TimeSpan? Timeout = null);
