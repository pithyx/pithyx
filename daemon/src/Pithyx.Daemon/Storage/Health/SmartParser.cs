using System.Text.Json;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>Reads the JSON of <c>smartctl -n standby -j -a</c> (smartmontools 7.4) for ATA, SCSI and NVMe disks.</summary>
public static class SmartParser
{
    // smartctl exit status bits: 0 = the command line was not understood, 1 = the device could not be opened or was in a low-power mode (-n), 2 = a SMART
    // command failed, 3 = the drive reports "failing", 4 and 5 = attributes at or below their threshold.
    private const int CommandLineError = 1 << 0;
    private const int OpenFailedOrAsleep = 1 << 1;
    private const int DiskFailing = 1 << 3;
    private const int PrefailBelowThreshold = 1 << 4;

    private static readonly string[] _virtualVendors = ["QEMU", "VBOX", "VMware", "Virtual", "Msft", "BHYVE", "Xen"];
    private static readonly string[] _lowPowerModes = ["STANDBY", "SLEEP"];

    public static SmartReading Parse(string json, int exitCode)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new SmartReading(SmartOutcome.Failed);
        }

        var exitStatus = root.TryGetProperty("smartctl", out var smartctl) && smartctl.TryGetProperty("exit_status", out var status) && status.TryGetInt32(out var value)
            ? value
            : exitCode;
        if ((exitStatus & CommandLineError) != 0)
        {
            return new SmartReading(SmartOutcome.Failed);
        }
        if ((exitStatus & OpenFailedOrAsleep) != 0)
        {
            return IsAsleep(root, smartctl) ? new SmartReading(SmartOutcome.Asleep) : new SmartReading(SmartOutcome.Failed);
        }
        if (IsVirtual(root))
        {
            return new SmartReading(SmartOutcome.Unsupported, Reason: "virtual");
        }
        var hasHealth = root.TryGetProperty("smart_status", out var smartStatus) && smartStatus.TryGetProperty("passed", out _);
        if (!hasHealth && (!root.TryGetProperty("smart_support", out var support) || support.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False))
        {
            return new SmartReading(SmartOutcome.Unsupported, Reason: "not_available");
        }

        var attributes = AtaAttributes(root);
        var nvme = root.TryGetProperty("nvme_smart_health_information_log", out var log) ? log : (JsonElement?)null;
        var reading = new SmartReading(
            SmartOutcome.Read,
            TemperatureCelsius: Number(root, "temperature", "current") is { } temperature ? (int)temperature : null,
            PowerOnHours: Number(root, "power_on_time", "hours"),
            ReallocatedSectors: attributes.GetValueOrDefault(5),
            PendingSectors: attributes.GetValueOrDefault(197),
            UncorrectableSectors: attributes.GetValueOrDefault(198),
            MediaErrors: nvme is { } health ? Number(health, "media_errors") : null,
            CriticalWarning: nvme is { } warning && Number(warning, "critical_warning") is { } bits ? (int)bits : null,
            LastSelfTest: LastSelfTest(root));
        var passed = !hasHealth || smartStatus.GetProperty("passed").ValueKind != JsonValueKind.False;
        var failed = !passed || (exitStatus & (DiskFailing | PrefailBelowThreshold)) != 0 || reading.CriticalWarning is > 0;
        var warn = reading.ReallocatedSectors > 0 || reading.PendingSectors > 0 || reading.UncorrectableSectors > 0 || reading.MediaErrors > 0
            || reading.LastSelfTest == "failed";
        return reading with { State = failed ? "failed" : warn ? "warning" : "passed" };
    }

    /// <summary>With <c>-n standby</c> smartctl stops before reading anything and says which power mode it found.</summary>
    private static bool IsAsleep(JsonElement root, JsonElement smartctl)
    {
        if (Text(root, "power_mode") is { } mode && _lowPowerModes.Any(low => mode.Contains(low, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return smartctl.ValueKind == JsonValueKind.Object
            && smartctl.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array
            && messages.EnumerateArray().Any(message =>
                Text(message, "string") is { } text
                && text.Contains(" mode", StringComparison.OrdinalIgnoreCase)
                && _lowPowerModes.Any(low => text.Contains(low, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsVirtual(JsonElement root)
    {
        string?[] names = [Text(root, "scsi_vendor"), Text(root, "scsi_model_name"), Text(root, "model_name"), Text(root, "model_family")];
        return names.Any(name => name is not null && _virtualVendors.Any(vendor => name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<int, long> AtaAttributes(JsonElement root)
    {
        var attributes = new Dictionary<int, long>();
        if (!root.TryGetProperty("ata_smart_attributes", out var table) || !table.TryGetProperty("table", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return attributes;
        }
        foreach (var row in rows.EnumerateArray())
        {
            if (Number(row, "id") is { } id && Number(row, "raw", "value") is { } raw)
            {
                // Some drives pack more than the count into the raw value; the low 32 bits hold it.
                attributes[(int)id] = raw & 0xFFFF_FFFF;
            }
        }
        return attributes;
    }

    private static string? LastSelfTest(JsonElement root)
    {
        // ATA: status values 0xF0 to 0xFF mean a test is running now.
        if (Number(root, "ata_smart_data", "self_test", "status", "value") is { } current && current >> 4 == 0xF)
        {
            return "running";
        }
        if (root.TryGetProperty("ata_smart_self_test_log", out var ata)
            && ata.TryGetProperty("standard", out var standard)
            && standard.TryGetProperty("table", out var tests)
            && tests.ValueKind == JsonValueKind.Array
            && tests.GetArrayLength() > 0)
        {
            // The newest test comes first.
            var newest = tests[0];
            if (!newest.TryGetProperty("status", out var status))
            {
                return null;
            }
            return status.TryGetProperty("passed", out var passed)
                ? passed.ValueKind == JsonValueKind.True ? "passed" : "failed"
                : "aborted";
        }
        if (root.TryGetProperty("nvme_self_test_log", out var nvme))
        {
            if (Number(nvme, "current_self_test_operation", "value") is > 0)
            {
                return "running";
            }
            if (nvme.TryGetProperty("table", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            {
                // NVMe result codes: 0 no error, 5 to 7 failed, the others aborted.
                return Number(entries[0], "self_test_result", "value") switch
                {
                    0 => "passed",
                    5 or 6 or 7 => "failed",
                    null => null,
                    _ => "aborted",
                };
            }
        }
        return null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) ? number : null;
    }
}
