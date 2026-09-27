namespace Pithyx.Daemon.Tests.Support;

public static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pnpm-workspace.yaml")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
