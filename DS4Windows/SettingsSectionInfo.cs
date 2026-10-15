using System;

namespace DS4Windows;

internal static class SettingsSectionInfo
{
    internal static string Normalize(string section) => section switch
    {
        "Appearance" or "Controllers" or "Tools" or "Integrations" or "Advanced" => section,
        _ => "General"
    };
}

internal static class SettingsRestartRequirements
{
    internal static bool Language(string applied, string requested) =>
        !string.Equals(applied ?? "", requested ?? "", StringComparison.OrdinalIgnoreCase);

    internal static string RequestedExecutable(string alias) =>
        string.IsNullOrEmpty(alias) ? "DS4Windows.exe" : alias + ".exe";

    internal static bool Executable(string runningFile, string alias) =>
        !string.Equals(System.IO.Path.GetFileName(runningFile), RequestedExecutable(alias), StringComparison.OrdinalIgnoreCase);
}
