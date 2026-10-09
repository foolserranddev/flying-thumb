namespace FlyingThumbManager;

public sealed record DiagnosticResult(string Test, string Status, string Evidence);

public static class DiagnosticReport
{
    public static void ValidateSecurity(string output)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(output, @"Secure Boot:\s*Disabled", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || !System.Text.RegularExpressions.Regex.IsMatch(output, @"Flash Encryption:\s*Disabled", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Active diagnostics cannot replace firmware while secure boot or flash encryption is enabled, or when the security state is unknown. The read-only checks remain available.");
    }
    public static IReadOnlyList<DiagnosticResult> AssessActive(string report)
    {
        var results = Parse(report).ToList();
        var required = new[] { "CHIP", "HEAP_BEFORE", "INTERNAL_RAM", "PSRAM", "TIMER", "RNG", "DIE_TEMPERATURE", "CPU_CORE0", "CPU_CORE1", "SHA256_KNOWN_VECTOR", "FLASH_SCRATCH", "BLE_CONTROLLER", "GPIO_LEVELS / LED_DATA_GPIO40", "GPIO_LEVELS / LED_CLOCK_GPIO39", "GPIO_CROSS_SHORT / DATA40_TO_CLOCK39", "GPIO_CROSS_SHORT / CLOCK39_TO_DATA40", "LED_INIT_COMPLETE", "WIFI_SCAN", "WIFI_SAVED_CONNECTION", "WIFI_AP", "SD_INIT", "SD_ONE_BIT", "HEAP_AFTER" };
        foreach (var name in required)
            if (!results.Any(result => result.Test == name)) results.Add(new(name, "INCOMPLETE", "No result was returned for this required stage."));
        if (!report.Split('\n').Any(line => line.TrimEnd().EndsWith("|SCHEMA|2", StringComparison.Ordinal))) results.Add(new("REPORT_SCHEMA", "INCOMPLETE", "Expected schema 2."));
        if (!report.Contains("|DIAGNOSTIC_COMPLETE|", StringComparison.Ordinal)) results.Add(new("EXECUTION", "INCOMPLETE", "Completion marker missing; last checkpoint may locate the stopped test."));
        if (!report.Split('\n').Any(line => line.TrimEnd().EndsWith("|TRANSPORT|HWCDC_V1", StringComparison.Ordinal))) results.Add(new("TRANSPORT", "INCOMPLETE", "Current hardware-serial transport marker missing."));
        if (!results.Any(result => result.Test == "NVS" && result.Status is "FAIL" or "SKIP"))
            foreach (var name in new[] { "NVS_PERSISTENCE", "NVS_CLEANUP" })
                if (!results.Any(result => result.Test == name)) results.Add(new(name, "INCOMPLETE", "Settings storage check did not return a result."));
        foreach (var color in new[] { "RED", "GREEN", "BLUE", "WHITE", "OFF" })
            if (!report.Split('\n').Any(line => line.TrimEnd().EndsWith("|LED_COMMAND_COMPLETE|" + color, StringComparison.Ordinal)))
                results.Add(new("LED_COLOR / " + color, "INCOMPLETE", "Color command completion missing; visible light still requires observation."));
        if (results.Any(result => result.Test == "SD_INIT" && result.Status == "PASS"))
            foreach (var name in new[] { "SD_FAT_GEOMETRY", "SD_SECTOR0", "SD_RAW_SAMPLES", "SD_FILE_ROUNDTRIP", "SD_RENAME", "SD_CLEANUP" })
                if (!results.Any(result => result.Test == name)) results.Add(new(name, "INCOMPLETE", "Mounted-card check did not return a result."));
        return results;
    }
    public static string ExtractMac(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"MAC:\s*([0-9a-f]{2}(?::[0-9a-f]{2}){5})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("The recovery response did not contain a device MAC identity.");
        return match.Groups[1].Value.ToLowerInvariant();
    }
    public static void ValidatePartitionLayout(byte[] table)
    {
        var found = new HashSet<string>();
        for (var offset = 0; offset + 32 <= table.Length; offset += 32)
        {
            if (BitConverter.ToUInt16(table, offset) != 0x50aa) break;
            var address = BitConverter.ToUInt32(table, offset + 4);
            var size = BitConverter.ToUInt32(table, offset + 8);
            var label = System.Text.Encoding.ASCII.GetString(table, offset + 12, 16).TrimEnd('\0');
            if (label == "app0" && table[offset + 2] == 0 && table[offset + 3] == 0x10 && address == 0x10000 && size == 0x640000) found.Add(label);
            if (label == "otadata" && table[offset + 2] == 1 && table[offset + 3] == 0 && address == 0xe000 && size == 0x2000) found.Add(label);
            if (label == "coredump" && table[offset + 2] == 1 && table[offset + 3] == 3 && address == 0xff0000 && size == 0x10000) found.Add(label);
        }
        if (found.Count != 3) throw new InvalidOperationException("The installed flash layout is not compatible with this diagnostic. No firmware or settings were changed. Use the correct recovery firmware first.");
    }
    public static IReadOnlyList<DiagnosticResult> Parse(string report)
    {
        var results = new List<DiagnosticResult>();
        foreach (var line in report.Split('\n'))
        {
            var fields = line.Trim().Split('|');
            if (fields.Length < 4 || fields[0] != "FTDIAG") continue;
            var statusIndex = Array.FindIndex(fields, 3, field => field is "PASS" or "FAIL" or "SKIP" or "UNVERIFIED" or "INCOMPLETE" || field.StartsWith("UNVERIFIED_", StringComparison.Ordinal));
            if (statusIndex < 0) continue;
            var test = fields[2];
            if (statusIndex > 3) test += " / " + string.Join(" / ", fields.Skip(3).Take(statusIndex - 3));
            var status = fields[statusIndex].StartsWith("UNVERIFIED", StringComparison.Ordinal) ? "UNVERIFIED" : fields[statusIndex];
            results.Add(new(test, status, string.Join("; ", fields.Skip(statusIndex + 1))));
        }
        return results;
    }
}
