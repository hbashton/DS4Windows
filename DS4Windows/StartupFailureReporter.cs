using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DS4WinWPF
{
    internal static class StartupFailureReporter
    {
        private static readonly object writeLock = new();

        internal static string Write(Exception exception, string phase,
            string configuredDataPath = null)
        {
            Exception failure = exception ??
                new InvalidOperationException("Unknown startup failure.");
            string payload = BuildPayload(failure, phase);

            foreach (string directory in CandidateDirectories(
                configuredDataPath))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory,
                        "startup_failure.log");
                    lock (writeLock)
                    {
                        File.AppendAllText(path, payload, Encoding.UTF8);
                    }

                    return path;
                }
                catch
                {
                    // Keep trying progressively safer per-user locations.
                }
            }

            return string.Empty;
        }

        internal static string BuildUserMessage(string logPath)
        {
            const string introduction =
                "DS4Windows could not finish starting. Your profiles and settings were not removed.";
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return introduction +
                    "\n\nWindows prevented DS4Windows from writing a diagnostic log.";
            }

            return introduction +
                $"\n\nDiagnostic details were saved to:\n{logPath}";
        }

        private static IEnumerable<string> CandidateDirectories(
            string configuredDataPath)
        {
            if (DS4Windows.PortableLabContext.Requested || DS4Windows.PortableLabContext.IsActive)
            {
                // Invalid lab arguments/path must not cause a shared-log write.
                if (DS4Windows.PortableLabContext.Current is { } lab)
                {
                    string directory = null;
                    try
                    {
                        lab.ValidateDataTree();
                        directory = Path.Combine(lab.DataPath, "Logs");
                    }
                    catch { }
                    if (directory != null) yield return directory;
                }
                yield break;
            }
            if (!string.IsNullOrWhiteSpace(configuredDataPath))
            {
                yield return Path.Combine(configuredDataPath, "Logs");
            }

            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "DS4Windows",
                    "Logs");
            }

            yield return Path.Combine(Path.GetTempPath(), "DS4Windows");
        }

        private static string BuildPayload(Exception exception,
            string phase)
        {
            Process process = Process.GetCurrentProcess();
            StringBuilder builder = new();
            builder.AppendLine(new string('=', 72));
            builder.AppendLine($"UTC: {DateTime.UtcNow:O}");
            builder.AppendLine($"Phase: {phase ?? "unknown"}");
            builder.AppendLine($"Process: {process.ProcessName} ({process.Id})");
            builder.AppendLine($"Executable: {Environment.ProcessPath}");
            builder.AppendLine($"Runtime: {Environment.Version}");
            builder.AppendLine($"OS: {Environment.OSVersion}");
            builder.AppendLine(exception.ToString());
            builder.AppendLine();
            return builder.ToString();
        }
    }
}
