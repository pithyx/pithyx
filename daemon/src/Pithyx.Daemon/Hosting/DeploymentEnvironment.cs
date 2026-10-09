namespace Pithyx.Daemon.Hosting;

public enum DeploymentKind
{
    Prod,
    Dev,
}

/// <summary>
/// What kind of machine this is, from <c>/etc/pithyx/environment</c> (<c>dev</c> or <c>prod</c>; missing or unknown
/// means <c>prod</c>), and on a dev machine which disks tests may write to (<c>/etc/pithyx/test-disks</c>, one serial per
/// line), and which network cards Pithyx may change there (<c>/etc/pithyx/test-nics</c>, one name per line; none without
/// the file, D292, D337).
/// </summary>
public sealed record DeploymentEnvironment(DeploymentKind Kind, IReadOnlySet<string> TestDiskSerials, IReadOnlySet<string> TestNics)
{
    public const string DefaultDirectory = "/etc/pithyx";

    public static DeploymentEnvironment Prod { get; } = new(DeploymentKind.Prod, new HashSet<string>(), new HashSet<string>());

    public static DeploymentEnvironment Load(string directory)
    {
        var kind = ReadLines(Path.Combine(directory, "environment")).FirstOrDefault() == "dev" ? DeploymentKind.Dev : DeploymentKind.Prod;
        var serials = kind == DeploymentKind.Dev
            ? ReadLines(Path.Combine(directory, "test-disks")).Where(line => !line.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
            : [];
        var nics = kind == DeploymentKind.Dev
            ? ReadLines(Path.Combine(directory, "test-nics")).Where(line => !line.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
            : [];
        return new DeploymentEnvironment(kind, serials, nics);
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
