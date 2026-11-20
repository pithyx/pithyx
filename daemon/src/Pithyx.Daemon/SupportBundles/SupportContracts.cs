namespace Pithyx.Daemon.SupportBundles;

/// <summary>A part of a support bundle (D412); the administrator leaves out any of them.</summary>
public enum SupportBundlePart
{
    /// <summary>Version, operating system, kernel, hardware, packages and failed services.</summary>
    System,

    /// <summary>Pools, datasets, disks and their SMART data.</summary>
    Storage,

    /// <summary>Addresses, routes, the firewall's rules and the name servers.</summary>
    Network,

    /// <summary>The box's settings from its database and the configuration files it writes, without any secret.</summary>
    Configuration,

    /// <summary>The Log Center's entries of the last days and the kernel's messages since boot.</summary>
    Logs,

    /// <summary>The state of the apps' containers.</summary>
    Apps,

    /// <summary>A memory dump of the daemon. It holds secrets, so it is never chosen by default and needs a step-up.</summary>
    MemoryDump,
}

/// <param name="Parts">What the bundle holds; at least one part.</param>
/// <param name="Password">The administrator's password for the step-up a memory dump needs; empty while a step-up holds.</param>
/// <param name="TotpCode">The current authenticator code for the step-up, with two-factor sign-in on.</param>
public sealed record CreateSupportBundleRequest(IReadOnlyList<SupportBundlePart> Parts, string? Password = null, string? TotpCode = null);

/// <summary>The bundle on the box, ready to download until <paramref name="ExpiresAt"/>.</summary>
/// <param name="Id">Names the bundle in the download URL.</param>
/// <param name="FileName">The name the download gets.</param>
/// <param name="SizeBytes">The size of the file.</param>
/// <param name="Parts">The parts it holds.</param>
/// <param name="Failures">Commands or files that could not be read; the bundle names each in <c>failures.json</c>.</param>
/// <param name="CreatedAt">When it was made, UTC.</param>
/// <param name="ExpiresAt">When the box deletes it, UTC.</param>
public sealed record SupportBundleInfo(
    string Id,
    string FileName,
    long SizeBytes,
    IReadOnlyList<SupportBundlePart> Parts,
    int Failures,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>The bundle the box keeps, if any, and the job making a new one.</summary>
/// <param name="Bundle">The bundle ready to download, or none.</param>
/// <param name="JobId">The job making a bundle right now, or none.</param>
public sealed record SupportBundleState(SupportBundleInfo? Bundle, long? JobId);
