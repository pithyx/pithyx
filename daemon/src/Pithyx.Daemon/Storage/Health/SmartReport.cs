namespace Pithyx.Daemon.Storage.Health;

/// <summary>SMART health of one disk as last read. The API serves it from memory; reading it never runs a command.</summary>
/// <param name="DiskId">The disk's id.</param>
/// <param name="State">
/// <c>passed</c>, <c>warning</c> (reallocated, pending or uncorrectable sectors, media errors or a failed self-test),
/// <c>failed</c> (the drive predicts its failure), <c>unsupported</c> (no SMART, see <paramref name="Reason"/>) or
/// <c>unknown</c> (not read yet, asleep since the daemon started, or the read failed).
/// </param>
/// <param name="Reason">
/// For <c>unsupported</c>: <c>virtual</c> (a disk of a virtual machine) or <c>not_available</c>. For <c>unknown</c>:
/// <c>asleep</c>, <c>not_read</c> or <c>read_failed</c>. Otherwise null.
/// </param>
/// <param name="Asleep">True when the last check found the disk asleep; the values are then from <paramref name="ReadAt"/>.</param>
/// <param name="TemperatureCelsius">Current temperature, if reported.</param>
/// <param name="PowerOnHours">Power-on time, if reported.</param>
/// <param name="ReallocatedSectors">ATA attribute 5, raw value.</param>
/// <param name="PendingSectors">ATA attribute 197, raw value.</param>
/// <param name="UncorrectableSectors">ATA attribute 198, raw value.</param>
/// <param name="MediaErrors">NVMe media and data integrity errors.</param>
/// <param name="CriticalWarning">NVMe critical warning bits; not 0 means the drive reports a critical condition.</param>
/// <param name="LastSelfTest">Result of the newest self-test: <c>passed</c>, <c>failed</c>, <c>aborted</c> or <c>running</c>; null without one.</param>
/// <param name="ReadAt">When SMART data was last read (UTC); null if never.</param>
/// <param name="CheckedAt">When the disk was last checked (UTC), including checks that found it asleep.</param>
public sealed record SmartReport(
    string DiskId,
    string State,
    string? Reason,
    bool Asleep,
    int? TemperatureCelsius,
    long? PowerOnHours,
    long? ReallocatedSectors,
    long? PendingSectors,
    long? UncorrectableSectors,
    long? MediaErrors,
    int? CriticalWarning,
    string? LastSelfTest,
    DateTimeOffset? ReadAt,
    DateTimeOffset CheckedAt);

/// <summary>What one <c>smartctl -n standby -j -a</c> call found.</summary>
public enum SmartOutcome
{
    /// <summary>SMART data was read.</summary>
    Read,

    /// <summary>The disk was asleep; nothing was read and it was not woken.</summary>
    Asleep,

    /// <summary>The disk has no SMART.</summary>
    Unsupported,

    /// <summary>smartctl could not read the disk.</summary>
    Failed,
}

/// <summary>
/// The parsed output of one smartctl call; <see cref="SmartParser"/> builds it. <c>State</c> is set for
/// <see cref="SmartOutcome.Read"/> (<c>passed</c>, <c>warning</c> or <c>failed</c>), <c>Reason</c> for
/// <see cref="SmartOutcome.Unsupported"/> (<c>virtual</c> or <c>not_available</c>). The values mean the same as in
/// <see cref="SmartReport"/>.
/// </summary>
public sealed record SmartReading(
    SmartOutcome Outcome,
    string? State = null,
    string? Reason = null,
    int? TemperatureCelsius = null,
    long? PowerOnHours = null,
    long? ReallocatedSectors = null,
    long? PendingSectors = null,
    long? UncorrectableSectors = null,
    long? MediaErrors = null,
    int? CriticalWarning = null,
    string? LastSelfTest = null);
