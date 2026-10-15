using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ViiperStartupDiagnosticsTests
    {
        private static ViiperPrerequisiteStatus Ready() => new()
        {
            UsbipVersion = ViiperSetupManager.RequiredUsbipVersion.ToString(), UsbipInstalled = true, UsbipExecutableSafe = true,
            UsbipDriverFilesSafe = true, UsbipRuntimeReady = true,
            ViiperInstalled = true, ViiperPackageCurrent = true, ServerRunning = true
        };

        [TestMethod]
        public void RunningServerWithBrokenUsbipCannotBeReportedReady()
        {
            var status = Ready();
            status.UsbipRuntimeReady = false;
            status.UsbipProbeMessage = "Driver could not be opened";
            var lines = ViiperStartupDiagnostics.Describe(status).ToArray();
            Assert.IsFalse(lines.Contains("VIIPER virtual-controller backend ready"));
            Assert.IsTrue(lines.Any(line => line.Contains(status.UsbipProbeMessage)));
            Assert.IsTrue(lines.Any(line => line.Contains("runtime: not ready")));
        }

        [TestMethod]
        public void UnsupportedInstalledVersionIsReportedAlongsideRequirement()
        {
            var status = Ready();
            status.UsbipVersion = "0.9.7.6";
            status.UsbipInstalled = false;
            status.UsbipExecutableSafe = false;
            var lines = ViiperStartupDiagnostics.Describe(status).ToArray();
            Assert.IsTrue(lines[0].Contains($"installed version 0.9.7.6; required {ViiperSetupManager.RequiredUsbipVersion}"));
            Assert.IsFalse(lines.Contains("VIIPER virtual-controller backend ready"));
        }

        [TestMethod]
        public void MissingVersionDoesNotPretendTheBundledVersionIsInstalled()
        {
            var status = new ViiperPrerequisiteStatus();
            Assert.IsTrue(ViiperStartupDiagnostics.Describe(status).First().Contains("unknown or missing"));
        }

        [TestMethod]
        public void ReadyRequiresVerifiedPackageAndReachableServer()
        {
            var status = Ready();
            Assert.IsTrue(ViiperStartupDiagnostics.Describe(status).Contains("VIIPER virtual-controller backend ready"));
            status.ServerRunning = false;
            Assert.IsFalse(ViiperStartupDiagnostics.Describe(status).Contains("VIIPER virtual-controller backend ready"));
        }
    }
}
