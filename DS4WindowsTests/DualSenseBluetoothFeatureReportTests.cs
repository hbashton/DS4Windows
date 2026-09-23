using System.Buffers.Binary;
using System.Reflection;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseBluetoothFeatureReportTests
{
    [DataTestMethod]
    [DataRow(0x20, 0x20, true, true)]
    [DataRow(0x20, 0x05, true, false)]
    [DataRow(0x20, 0x20, false, false)]
    [DataRow(0x05, 0x05, true, true)]
    [DataRow(0x05, 0x20, true, false)]
    public void UsbFeatureReadRequiresSuccessAndTheRequestedReportId(
        int requestedId, int returnedId, bool hidSuccess, bool expected)
    {
        byte[] buffer = new byte[requestedId == 0x20 ? 64 : 41];
        buffer[0] = (byte)requestedId;
        int reads = 0;
        Assert.AreEqual(expected, DualSenseDevice.TryReadUsbFeatureReport(buffer, target =>
        {
            reads++;
            Assert.AreEqual((byte)requestedId, target[0]);
            target[0] = (byte)returnedId;
            return hidSuccess;
        }));
        Assert.AreEqual(1, reads, "USB must not introduce Bluetooth CRC retries or delays.");
    }

    [TestMethod]
    public void FailedHidReadsCannotAuthenticateStaleFirmwareBytes()
    {
        byte[] buffer = ValidReport(0x20);
        int reads = 0;
        Assert.IsFalse(DualSenseDevice.TryReadBluetoothFeatureReport(buffer, buffer.Length,
            _ => { reads++; return false; }));
        Assert.AreEqual(5, reads);
    }

    [TestMethod]
    public void ValidFirmwareReadStopsImmediately()
    {
        byte[] buffer = new byte[64];
        buffer[0] = 0x20;
        int reads = 0;
        Assert.IsTrue(DualSenseDevice.TryReadBluetoothFeatureReport(buffer, buffer.Length,
            target => { reads++; ValidReport(0x20).CopyTo(target, 0); return true; }));
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public void BadCrcAndWrongReportIdAreRetriedWithOriginalRequest()
    {
        byte[] buffer = new byte[64];
        buffer[0] = 0x20;
        int reads = 0;
        Assert.IsTrue(DualSenseDevice.TryReadBluetoothFeatureReport(buffer, buffer.Length, target =>
        {
            Assert.AreEqual((byte)0x20, target[0]);
            reads++;
            ValidReport(reads == 2 ? (byte)0x05 : (byte)0x20).CopyTo(target, 0);
            if (reads == 1) target[63] ^= 1;
            return true;
        }));
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public void CorruptFeatureReadsHaveBoundedRetries()
    {
        byte[] buffer = new byte[64];
        buffer[0] = 0x20;
        int reads = 0;
        Assert.IsFalse(DualSenseDevice.TryReadBluetoothFeatureReport(buffer, buffer.Length,
            _ => { reads++; return true; }));
        Assert.AreEqual(5, reads);
    }

    private static byte[] ValidReport(byte id)
    {
        byte[] report = new byte[64];
        report[0] = id;
        report[44] = 0x17;
        report[45] = 0x02;
        byte[] prefixed = new byte[61];
        prefixed[0] = 0xA3;
        report.AsSpan(0, 60).CopyTo(prefixed.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(60), Crc32Algorithm.Compute(prefixed));
        return report;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedCalibrationReadPreservesAcceptedCoefficients(bool bluetooth)
    {
        var motion = new DS4SixAxis();
        Assert.IsTrue(DualSenseDevice.TryRefreshCalibration(motion, bluetooth,
            target => { Calibration(13).CopyTo(target, 0); return true; }));
        Assert.AreEqual(13, PitchBias(motion));
        Assert.IsFalse(DualSenseDevice.TryRefreshCalibration(motion, bluetooth,
            target => { Calibration(99).CopyTo(target, 0); return false; }));
        Assert.AreEqual(13, PitchBias(motion));
        Assert.IsTrue(DualSenseDevice.TryRefreshCalibration(motion, bluetooth,
            target => { Calibration(23).CopyTo(target, 0); return true; }));
        Assert.AreEqual(23, PitchBias(motion));
    }

    [TestMethod]
    public void CorruptBluetoothCalibrationCannotReplaceGoodCoefficients()
    {
        var motion = new DS4SixAxis();
        Assert.IsTrue(DualSenseDevice.TryRefreshCalibration(motion, true,
            target => { Calibration(13).CopyTo(target, 0); return true; }));
        Assert.IsFalse(DualSenseDevice.TryRefreshCalibration(motion, true, target =>
        {
            Calibration(99).CopyTo(target, 0);
            target[40] ^= 1;
            return true;
        }));
        Assert.AreEqual(13, PitchBias(motion));
    }

    [DataTestMethod]
    [DataRow(7)] [DataRow(11)] [DataRow(15)]
    [DataRow(23)] [DataRow(27)] [DataRow(31)] [DataRow(19)]
    public void InvalidAxisScaleCannotReplaceGoodCoefficients(int offset)
    {
        var motion = new DS4SixAxis();
        Assert.IsTrue(DualSenseDevice.TryRefreshCalibration(motion, false,
            target => { Calibration(13).CopyTo(target, 0); return true; }));
        Assert.IsFalse(DualSenseDevice.TryRefreshCalibration(motion, false, target =>
        {
            Calibration(99).CopyTo(target, 0);
            target.AsSpan(offset, 4).Clear();
            return true;
        }));
        Assert.AreEqual(13, PitchBias(motion));
    }

    private static byte[] Calibration(short bias)
    {
        byte[] report = new byte[41];
        report[0] = 5;
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(1), bias);
        foreach (int offset in new[] { 7, 11, 15, 23, 27, 31 })
        {
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset), 8192);
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset + 2), -8192);
        }
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(19), 512);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(21), 512);
        byte[] prefixed = new byte[38];
        prefixed[0] = 0xA3;
        report.AsSpan(0, 37).CopyTo(prefixed.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(37), Crc32Algorithm.Compute(prefixed));
        return report;
    }

    private static int PitchBias(DS4SixAxis motion)
    {
        var coefficients = (Array)typeof(DS4SixAxis).GetField("calibrationData",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(motion)!;
        object pitch = coefficients.GetValue(0)!;
        return (int)pitch.GetType().GetField("bias")!.GetValue(pitch)!;
    }
}
