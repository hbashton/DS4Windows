using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class PortableBrokerStartupDiagnosticsTests
{
    [DataTestMethod]
    [DataRow(70, "could not be found")]
    [DataRow(71, "does not match")]
    [DataRow(72, "timed out")]
    [DataRow(73, "compatibility check")]
    [DataRow(74, "connection key")]
    [DataRow(75, "USB/IP listener")]
    [DataRow(76, "API listener")]
    public void KnownStartupPhaseReachesTheUserWithoutChildOutput(int code, string expected)
    {
        string message = PortableBrokerStartupDiagnostics.DescribeExit(code);
        StringAssert.Contains(message, expected);
        StringAssert.Contains(message, "Settings to retry");
        StringAssert.Contains(message, "Your key and profiles were not replaced");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(69)]
    [DataRow(77)]
    [DataRow(-1073741819)]
    public void UnknownExitDoesNotInventDriverFailure(int code)
    {
        string message = PortableBrokerStartupDiagnostics.DescribeExit(code);
        StringAssert.Contains(message, "exit code " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(message.Contains("compatibility check"));
    }

    [TestMethod]
    public void MissingExitCodeDoesNotImplyZeroOrCorruptInstallation()
    {
        string message = PortableBrokerStartupDiagnostics.DescribeExit(null);
        StringAssert.Contains(message, "exited before it was ready.");
        Assert.IsFalse(message.Contains("exit code"));
    }
}
