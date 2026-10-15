using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class LogDiagnosticsTests
    {
        [TestMethod]
        public void InfVersionUsesTheVersionSectionAndIgnoresComments()
        {
            Assert.AreEqual("12.1.30.403", DriverDiagnostics.ReadInfVersion(new[]
            {
                "; DriverVer=01/01/2020,9.9.9.9", "[Other]", "DriverVer=01/01/2020,8.8.8.8",
                "[vErSiOn]", " DriverVer = 09/22/2026,12.1.30.403 ; installed package"
            }));
            Assert.IsNull(DriverDiagnostics.ReadInfVersion(new[] { "[Version]", "DriverVer=invalid" }));
            Assert.IsNull(DriverDiagnostics.ReadInfVersion(new[] { "[Version]", "DriverVer=09/22/2026,%VERSION%" }));
        }

        [TestMethod]
        public void CopiedTextPreservesTimestampsOrderAndMultilineMessages()
        {
            var when = new DateTime(2026, 10, 11, 23, 10, 32);
            var entries = new[]
            {
                new DS4WinWPF.LogItem { Datetime = when, Message = "USB/IP ready" },
                new DS4WinWPF.LogItem { Datetime = when.AddSeconds(1), Message = "First line\nSecond line" }
            };
            Assert.AreEqual($"{when}: USB/IP ready{Environment.NewLine}{when.AddSeconds(1)}: First line\nSecond line{Environment.NewLine}", DS4WinWPF.LogWriter.Format(entries));
            Assert.AreEqual(string.Empty, DS4WinWPF.LogWriter.Format(Array.Empty<DS4WinWPF.LogItem>()));
        }
    }
}
