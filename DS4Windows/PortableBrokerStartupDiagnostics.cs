namespace DS4Windows;

internal static class PortableBrokerStartupDiagnostics
{
    // VIIPER startup exit codes are a bounded, credential-free protocol. Never
    // substitute arbitrary child stderr, command lines, or driver output here.
    internal static string DescribeExit(int? code)
    {
        string reason = code switch
        {
            70 => "USB/IP could not be found or its version could not be checked.",
            71 => "The installed USB/IP version does not match the required version (0.9.7.7).",
            72 => "The USB/IP startup check timed out before VIIPER could open its connection.",
            73 => "The USB/IP driver did not pass its compatibility check.",
            74 => "VIIPER could not open or create its local connection key.",
            75 => "VIIPER could not start its USB/IP listener. Check for another program using its port.",
            76 => "VIIPER could not start its API listener. Check for another program using its port.",
            _ => "VIIPER exited before it was ready" + (code.HasValue
                ? " (exit code " + code.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")."
                : "."),
        };
        return reason + "\n\nUse Install / Repair VIIPER in Settings to retry while DS4Windows stays open. " +
            "Include this startup check in a bug report if it happens again. Your key and profiles were not replaced.";
    }
}
