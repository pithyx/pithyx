using System.Reflection;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// The build runs the app briefly to write <c>packages/api-client/openapi.json</c>. Startup work with side effects
/// (logging to files, migrations, background services) must be skipped in that run.
/// </summary>
public static class BuildTimeDocument
{
    public static bool IsGenerating { get; } = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
}
