using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class DualShock4EffectTransportTests
{
    [TestMethod]
    public void OrdinaryBluetoothEffectsUseControlPipe()
    {
        Assert.IsTrue(DS4Device.UsesControlPipeForEffectOutput(ConnectionType.BT, false));
    }

    [TestMethod]
    public void CopyCatBluetoothRetainsInterruptOutput()
    {
        Assert.IsFalse(DS4Device.UsesControlPipeForEffectOutput(ConnectionType.BT, true));
    }

    [TestMethod]
    public void NonBluetoothEffectsRetainInterruptOutput()
    {
        foreach (var connection in Enum.GetValues<ConnectionType>())
        {
            if (connection == ConnectionType.BT) continue;
            Assert.IsFalse(DS4Device.UsesControlPipeForEffectOutput(connection, false));
            Assert.IsFalse(DS4Device.UsesControlPipeForEffectOutput(connection, true));
        }
    }

    [TestMethod]
    public void BothEffectEntryPointsSharePolicyWithoutUsingAudioWriter()
    {
        // Native HID is not invoked in the test process. This wiring contract
        // complements the reporter's physical two-controller A/B reproduction.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string path = null;
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName,
                "DS4Windows", "DS4Library", "DS4Device.cs");
            if (File.Exists(candidate)) { path = candidate; break; }
            directory = directory.Parent;
        }
        Assert.IsNotNull(path);
        string source = File.ReadAllText(path);
        int start = source.IndexOf("protected bool WriteOutput(byte[]", StringComparison.Ordinal);
        int end = source.IndexOf("public bool SetBluetoothAudioStreaming(", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        string effects = source[start..end];
        StringAssert.Contains(effects, "return WriteEffectOutput(outputBuffer);");
        StringAssert.Contains(effects, "return WriteEffectOutput(conType == ConnectionType.BT");
        StringAssert.Contains(effects, "? outputReport : outReportBuffer);");
        StringAssert.Contains(effects, "WriteOutputReportViaControl(report)");
        StringAssert.Contains(effects, "WriteOutputReportViaInterrupt(report, READ_STREAM_TIMEOUT)");
        Assert.IsFalse(effects.Contains("WriteOutputReportViaSharedOverlapped", StringComparison.Ordinal));
        Assert.AreEqual(2, effects.Split("lock (bluetoothOutputWriteLock)").Length - 1);
        StringAssert.Contains(source[end..], "WriteOutputReportViaSharedOverlapped(");
    }
}
