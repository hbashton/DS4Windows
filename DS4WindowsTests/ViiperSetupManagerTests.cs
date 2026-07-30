using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

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
        public void UsbipPortProbeRejectsAbiMismatchDiagnostics(string output)
        {
            Assert.IsFalse(ViiperSetupManager.IsSuccessfulUsbipPortProbe(
                0, output));
        }

        [TestMethod]
        public void ReadyRequiresCanonicalRuntimeProbeInAdditionToServer()
        {
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperInstalled = true,
                ServerRunning = true,
                UsbipInstalled = true,
                UsbipRuntimeReady = false,
            };

            Assert.IsFalse(status.Ready,
                "A listening VIIPER server must not mask a failed usbip ABI probe.");

            status.UsbipRuntimeReady = true;
            Assert.IsTrue(status.Ready);
        }
    }
}
