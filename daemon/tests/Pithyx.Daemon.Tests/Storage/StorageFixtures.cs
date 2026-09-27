namespace Pithyx.Daemon.Tests.Storage;

/// <summary>Command output recorded on the dev VM (<c>daemon/tests/fixtures/commands</c>).</summary>
internal static class StorageFixtures
{
    public static string Read(string scenario, string id) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", scenario, $"{id}.stdout"));

    public static string Proc(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "proc", name));
}
