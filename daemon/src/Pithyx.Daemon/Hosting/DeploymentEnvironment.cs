namespace Pithyx.Daemon.Hosting;

public enum DeploymentKind
{
    Prod,
    Dev,
}

/// <summary>
/// What kind of machine this is, from <c>/etc/pithyx/environment</c> (<c>dev</c> or <c>prod</c>; missing or unknown
/// means <c>prod</c>), and on a dev machine which disks tests may write to (<c>/etc/pithyx/test-disks</c>, one serial per
/// line).
/// </summary>
public sealed record DeploymentEnvironment(DeploymentKind Kind, IReadOnlySet<string> TestDiskSerials)
{
    public const string DefaultDirectory = "/etc/pithyx";

    public static DeploymentEnvironment Prod { get; } = new(DeploymentKind.Prod, new HashSet<string>());

    public static DeploymentEnvironment Load(string directory)
    {
        var kind = ReadLines(Path.Combine(directory, "environment")).FirstOrDefault() == "dev" ? DeploymentKind.Dev : DeploymentKind.Prod;
        var serials = kind == DeploymentKind.Dev
            ? ReadLines(Path.Combine(directory, "test-disks")).Where(line => !line.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
            : [];
        return new DeploymentEnvironment(kind, serials);
    }

    private static List<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
