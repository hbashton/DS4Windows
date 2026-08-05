using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ViiperSetupManagerTests
    {
        [TestMethod]
        public void UsbipVersionRequiresPinnedSafe0977()
        {
            Assert.IsFalse(ViiperSetupManager.IsSupportedUsbipVersion(null));
            Assert.IsFalse(ViiperSetupManager.IsSupportedUsbipVersion(
                new Version(0, 9, 7, 6)));
            Assert.IsTrue(ViiperSetupManager.IsSupportedUsbipVersion(
                new Version(0, 9, 7, 7)));
            Assert.IsFalse(ViiperSetupManager.IsSupportedUsbipVersion(
                new Version(0, 9, 7, 8)));
            Assert.IsFalse(ViiperSetupManager.IsSupportedUsbipVersion(
                new Version(0, 9, 8, 0)));
        }

        [TestMethod]
        public void InstallerFailureMessageExpandsTheLogPath()
        {
            const string logPath =
                @"C:\Program Files\DS4Windows\VIIPER\install.log";
            string message = ViiperSetupManager.
                BuildInstallerFailureMessage(1, logPath);

            StringAssert.Contains(message, logPath);
            Assert.IsFalse(message.Contains("{logPath}",
                StringComparison.Ordinal));
        }

        [TestMethod]
        public void UsbipPortProbeRequiresZeroExitCode()
        {
            Assert.IsTrue(ViiperSetupManager.IsSuccessfulUsbipPortProbe(
                0, "Port 00: <Port in Use>"));
            Assert.IsFalse(ViiperSetupManager.IsSuccessfulUsbipPortProbe(
                1, "Port 00: <Port in Use>"));
        }

        [DataTestMethod]
        [DataRow("error: ABI mismatch, unexpected size of the input structure")]
        [DataRow("ABI MISMATCH")]
        [DataRow("unexpected size of output structure")]
        [DataRow("The specified conversion is not valid.")]
        [DataRow("invalid structure size")]
        public void UsbipPortProbeRejectsAbiMismatchDiagnostics(string output)
        {
            Assert.IsFalse(ViiperSetupManager.IsSuccessfulUsbipPortProbe(
                0, output));
        }

        [TestMethod]
        public void ReadyRequiresRuntimeProbeButNotStartupTaskMaintenance()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ViiperStartupTaskReady = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = false,
            };

            Assert.IsFalse(status.Ready,
                "A listening VIIPER server must not mask a failed usbip ABI probe.");

            status.UsbipRuntimeReady = true;
            Assert.IsTrue(status.Ready);

            status.ViiperStartupTaskReady = false;
            Assert.IsTrue(status.Ready,
                "A stale startup task must not block an already healthy portable VIIPER runtime.");
            StringAssert.Contains(status.DisplayText,
                "startup task needs repair");
            status.ViiperStartupTaskReady = true;

            status.ViiperPackageCurrent = false;
            Assert.IsFalse(status.Ready,
                "An older same-version VIIPER binary must not survive a DS4Windows update.");
        }

        [TestMethod]
        public void HealthyPortableRuntimeRemainsUsableWhileMigrationIsOffered()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = true,
                UsingExternalViiper = true,
            };

            Assert.IsTrue(ViiperSetupManager.IsReadyPortableRuntime(status));
            status.UsingExternalViiper = false;
            Assert.IsFalse(ViiperSetupManager.IsReadyPortableRuntime(status));
            status.UsingExternalViiper = true;
            status.ServerRunning = false;
            Assert.IsFalse(ViiperSetupManager.IsReadyPortableRuntime(status));
        }

        [TestMethod]
        public void ExplicitPortablePathWinsOverAStaleStartupTask()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                $"viiper-portable-{Guid.NewGuid():N}");
            string preferred = Path.Combine(directory, "viiper.exe");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(preferred, new byte[] { 0x4D, 0x5A });

                string selected = ViiperSetupManager.ResolveRuntimeViiperPath(
                    Path.Combine(directory, "canonical", "viiper.exe"),
                    preferred);

                Assert.IsTrue(ViiperSetupManager.IsExactViiperExecutablePath(
                    preferred, selected));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [TestMethod]
        public void PortableInstallerViiperPathUsesLocalAppData()
        {
            string localApplicationData = Path.Combine("C:\\Users", "Tester",
                "AppData", "Local");

            string path = ViiperSetupManager.GetPortableViiperExePath(
                localApplicationData);

            Assert.AreEqual(Path.Combine(localApplicationData, "VIIPER",
                "viiper.exe"), path);
        }

        [TestMethod]
        public void ViiperBinaryRequiresExactBundledHash()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                $"viiper-hash-{Guid.NewGuid():N}");
            string bundled = Path.Combine(directory, "bundled.exe");
            string matching = Path.Combine(directory, "matching.exe");
            string different = Path.Combine(directory, "different.exe");
            try
            {
                Directory.CreateDirectory(directory);
                byte[] expected = { 0x56, 0x49, 0x49, 0x50, 0x45, 0x52 };
                File.WriteAllBytes(bundled, expected);
                File.WriteAllBytes(matching, expected);
                File.WriteAllBytes(different,
                    new byte[] { 0x56, 0x49, 0x49, 0x50, 0x45, 0x53 });

                Assert.IsTrue(ViiperSetupManager.FilesHaveSameSha256(
                    matching, bundled));
                Assert.IsFalse(ViiperSetupManager.FilesHaveSameSha256(
                    different, bundled));
                Assert.IsFalse(ViiperSetupManager.FilesHaveSameSha256(
                    Path.Combine(directory, "missing.exe"), bundled));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [TestMethod]
        public void InstalledHashMismatchRequiresVerifiedUpdate()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = false,
            };

            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));
            status.ViiperPackageCurrent = true;
            Assert.IsFalse(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));
            status.ViiperInstalled = false;
            status.ViiperPackageCurrent = false;
            Assert.IsFalse(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));
        }

        [TestMethod]
        public void UnsupportedUsbipVersionForcesGuidedRepair()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                UsbipVersion = "0.9.7.8",
                UsbipInstalled = false,
            };

            Assert.IsTrue(ViiperSetupManager.RequiresUsbipReplacement(status));
            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));

            status.UsbipVersion = "0.9.7.7";
            status.UsbipInstalled = true;
            status.UsbipExecutableSafe = true;
            status.UsbipDriverFilesSafe = true;
            Assert.IsFalse(ViiperSetupManager.RequiresUsbipReplacement(status));
            Assert.IsFalse(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));
        }

        [TestMethod]
        public void UnsafeUsbipRuntimeAndCitrixBypassPromptSuppression()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                UsbipVersion = "0.9.7.7",
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = false,
            };
            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));

            status.UsbipDriverFilesSafe = true;
            status.UsbipExecutableSafe = false;
            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));

            status.UsbipExecutableSafe = true;
            status.UsbipRebootOrRepairRequired = true;
            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));

            status.UsbipRebootOrRepairRequired = false;
            status.CitrixUsbMonitorConflict = true;
            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));
        }

        [TestMethod]
        public void ReadyRejectsUnsafeCitrixUsbMonitor()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = true,
                CitrixUsbMonitorConflict = true,
                CitrixUsbMonitorConflictMessage =
                    "Citrix USB Monitor is unsafe",
            };

            Assert.IsFalse(status.Ready);
            StringAssert.Contains(status.DisplayText, "unsafe");
        }

        [DataTestMethod]
        [DataRow(false, null, null, false)]
        [DataRow(true, "Stopped", 4, false)]
        [DataRow(true, "Running", 4, true)]
        [DataRow(true, "Stopped", 2, true)]
        [DataRow(true, null, 3, true)]
        [DataRow(true, null, null, true)]
        public void CitrixUsbMonitorGuardFailsClosed(bool installed,
            string state, int? startValue, bool expected)
        {
            Assert.AreEqual(expected, ViiperSetupManager.
                IsUnsafeCitrixUsbMonitorState(installed, state,
                    startValue));
        }

        [TestMethod]
        public void ViiperExecutableOwnershipRequiresExactCanonicalPath()
        {
            string canonical = Path.Combine("C:\\Users", "Tester",
                "AppData", "Local", "VIIPER", "viiper.exe");
            string equivalent = Path.Combine("c:\\users", "tester",
                "AppData", "Local", "VIIPER", ".", "viiper.exe");
            string foreign = Path.Combine("C:\\Tools", "VIIPER",
                "viiper.exe");

            Assert.IsTrue(ViiperSetupManager.IsExactViiperExecutablePath(
                equivalent, canonical));
            Assert.IsFalse(ViiperSetupManager.IsExactViiperExecutablePath(
                foreign, canonical));
            Assert.IsFalse(ViiperSetupManager.IsExactViiperExecutablePath(
                null, canonical));
        }

        [TestMethod]
        public void ForeignViiperConflictAlwaysBlocksReadyStatus()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = true,
                ViiperProcessConflict = true,
                ViiperProcessConflictMessage =
                    "VIIPER startup blocked: foreign process",
            };

            Assert.IsFalse(status.Ready);
            StringAssert.Contains(status.DisplayText, "startup blocked");
        }

        [TestMethod]
        public void ReadyRejectsMixedUsbipDriverFiles()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = true,
                UsbipDriverFilesSafe = false,
                UsbipDriverIntegrityMessage =
                    "Unsafe or mixed usbip-win2 driver files detected",
                UsbipRuntimeReady = true,
            };

            Assert.IsFalse(status.Ready);
            StringAssert.Contains(status.DisplayText, "mixed");
        }

        [TestMethod]
        public void ReadyRejectsModifiedUsbipExecutableEvenWithMatchingVersion()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ViiperPackageCurrent = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipExecutableSafe = false,
                UsbipDriverFilesSafe = true,
                UsbipRuntimeReady = true,
            };

            Assert.IsFalse(status.Ready);
            StringAssert.Contains(status.DisplayText,
                "executable verification failed");
            Assert.IsTrue(ViiperSetupManager.
                RequiresVerifiedViiperUpdate(status));
        }

        [TestMethod]
        public void UsbipDriverHashesMustMatchBothPinnedFiles()
        {
            Assert.AreEqual(64,
                ViiperSetupManager.SupportedUsbipExecutableSha256.Length);
            Assert.AreEqual(64,
                ViiperSetupManager.SupportedUsbipUdeSha256.Length);
            Assert.AreEqual(64,
                ViiperSetupManager.SupportedUsbipFilterSha256.Length);
            Assert.IsTrue(ViiperSetupManager.
                AreSupportedUsbipDriverHashes(
                    ViiperSetupManager.SupportedUsbipUdeSha256,
                    ViiperSetupManager.SupportedUsbipFilterSha256));
            Assert.IsFalse(ViiperSetupManager.
                AreSupportedUsbipDriverHashes(
                    new string('0', 64),
                    ViiperSetupManager.SupportedUsbipFilterSha256));
            Assert.IsFalse(ViiperSetupManager.
                AreSupportedUsbipDriverHashes(
                    ViiperSetupManager.SupportedUsbipUdeSha256,
                    new string('0', 64)));
        }

        [TestMethod]
        public void ElevatedTerminationHelperRejectsMalformedRequest()
        {
            Assert.IsFalse(ViiperSetupManager.
                TryRunForeignViiperTerminationHelper(
                    new[] { "--not-the-helper" }, out _));
            Assert.IsTrue(ViiperSetupManager.
                TryRunForeignViiperTerminationHelper(
                    new[] { "--terminate-foreign-viiper", "not-base64" },
                    out int exitCode));
            Assert.AreEqual(87, exitCode);
        }

        [TestMethod]
        public void StartupTaskHelperRecognizesRemovalAndRejectsBadSid()
        {
            Assert.IsFalse(ViiperSetupManager.
                TryRunStartupTaskRegistrationHelper(
                    new[] { "--not-the-helper" }, out _));
            Assert.IsTrue(ViiperSetupManager.
                TryRunStartupTaskRegistrationHelper(
                    new[]
                    {
                        "--remove-viiper-startup-task",
                        "not-base64",
                    }, out int exitCode));
            Assert.AreEqual(87, exitCode);
        }
    }
}
