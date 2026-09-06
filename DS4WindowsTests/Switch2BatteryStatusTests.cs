using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BatteryStatusTests
{
    [DataTestMethod]
    [DataRow(2500, Switch2BatteryBand.Low,
        Switch2BatteryStatus.LowCompatibilityPercentage)]
    [DataRow(3125, Switch2BatteryBand.Low,
        Switch2BatteryStatus.LowCompatibilityPercentage)]
    [DataRow(3126, Switch2BatteryBand.Medium,
        Switch2BatteryStatus.MediumCompatibilityPercentage)]
    [DataRow(3250, Switch2BatteryBand.Medium,
        Switch2BatteryStatus.MediumCompatibilityPercentage)]
    [DataRow(3251, Switch2BatteryBand.High,
        Switch2BatteryStatus.HighCompatibilityPercentage)]
    [DataRow(5000, Switch2BatteryBand.High,
        Switch2BatteryStatus.HighCompatibilityPercentage)]
    public void ValidVoltageUsesPinnedThreeBandProjection(int millivolts,
        Switch2BatteryBand expectedBand, byte expectedPercentage)
    {
        Switch2CommonInputReport report = Decode((ushort)millivolts,
            currentRaw: 0x5634, opaque23Raw: 0x78);

        Assert.IsTrue(Switch2BatteryStatus.TryCreate(report,
            out var status));
        Assert.IsTrue(status.IsValid);
        Assert.AreEqual((ushort)millivolts, status.VoltageMillivolts);
        Assert.AreEqual((ushort)0x5634, status.CurrentRaw);
        Assert.AreEqual((byte)0x78, status.Opaque23Raw);
        Assert.AreEqual(expectedBand, status.Band);
        Assert.AreEqual(expectedPercentage,
            status.CompatibilityPercentage);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2499)]
    [DataRow(5001)]
    [DataRow(65535)]
    public void InvalidVoltageFailsClosed(int millivolts)
    {
        Switch2CommonInputReport report = Decode((ushort)millivolts,
            currentRaw: ushort.MaxValue, opaque23Raw: byte.MaxValue);

        Assert.IsFalse(Switch2BatteryStatus.TryCreate(report,
            out var status));
        Assert.IsFalse(status.IsValid);
        Assert.AreEqual(Switch2BatteryBand.Unknown, status.Band);
    }

    private static Switch2CommonInputReport Decode(ushort millivolts,
        ushort currentRaw, byte opaque23Raw)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x1F, 2),
            millivolts);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x21, 2),
            currentRaw);
        body[0x23] = opaque23Raw;
        Assert.IsTrue(Switch2InputCodec.TryDecodeCommon05(body,
            out var report));
        return report;
    }
}
