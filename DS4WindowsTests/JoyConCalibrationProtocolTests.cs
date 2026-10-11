using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class JoyConCalibrationProtocolTests
{
    [TestMethod]
    public void FailedWriteReturnsNoReplyWithoutReading()
    {
        var device = new FakeJoyCon(false) { AcceptWrite = false };
        Assert.IsNull(device.Subcommand(0x10, Request(0x8010, 2), 5, true));
        Assert.AreEqual(1, device.Writes.Count);
        Assert.AreEqual(0, device.ReadCalls);
    }

    [TestMethod]
    public void InvalidPayloadIsRejectedBeforeWriting()
    {
        var device = new FakeJoyCon(false);
        Assert.ThrowsException<ArgumentException>(() => device.Subcommand(0x10, null, 5, true));
        Assert.ThrowsException<ArgumentException>(() => device.Subcommand(0x10, new byte[4], 5, true));
        Assert.ThrowsException<ArgumentException>(() => device.Subcommand(0x10, new byte[64], 64, true));
        Assert.AreEqual(0, device.Writes.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReplyMustMatchReportAckSubcommandAddressCountAndActualLength(bool usb)
    {
        var device = new FakeJoyCon(false, usb);
        byte[] valid = SpiReply(0x603D, Stick(false));
        foreach (int offset in new[] { 0, 13, 14, 15, 19 })
        {
            byte[] wrong = (byte[])valid.Clone();
            wrong[offset] = 0;
            device.Replies.Enqueue(wrong);
        }
        device.Replies.Enqueue(valid[..^1]);
        device.Replies.Enqueue(valid);
        CollectionAssert.AreEqual(valid, device.Subcommand(0x10, Request(0x603D, 9), 5, true));
        Assert.AreEqual(7, device.ReadCalls);
    }

    [TestMethod]
    public void OrdinaryAckRequiresBothReportIdAndSubcommand()
    {
        var device = new FakeJoyCon(false);
        byte[] wrongReport = Ack(0x40);
        wrongReport[0] = 0x30;
        device.Replies.Enqueue(wrongReport);
        device.Replies.Enqueue(Ack(0x30));
        device.Replies.Enqueue(Ack(0x40));
        CollectionAssert.AreEqual(Ack(0x40), device.Subcommand(0x40, new byte[] { 1 }, 1, true));
        Assert.AreEqual(3, device.ReadCalls);
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(363)]
    public void FailedOrImpossibleReadCannotAdmitBufferContents(int count)
    {
        var device = new FakeJoyCon(false) { ForcedReadResult = count };
        device.Replies.Enqueue(SpiReply(0x603D, Stick(false)));
        Assert.IsNull(device.Subcommand(0x10, Request(0x603D, 9), 5, true));
        Assert.AreEqual(1, device.ReadCalls);
    }

    [TestMethod]
    public void UnrelatedReportsShareOneDeadlineAndHaveAFiniteFloodBound()
    {
        var delayed = new FakeJoyCon(false) { MillisecondsPerRead = 180, RepeatReply = Ack(0x30) };
        Assert.IsNull(delayed.Subcommand(0x40, new byte[] { 1 }, 1, true));
        CollectionAssert.AreEqual(new uint[] { 500, 320, 140 }, delayed.ReadTimeouts);
        var immediate = new FakeJoyCon(false) { RepeatReply = Ack(0x30) };
        Assert.IsNull(immediate.Subcommand(0x40, new byte[] { 1 }, 1, true));
        Assert.AreEqual(64, immediate.ReadCalls);
    }

    [TestMethod]
    public void FireAndForgetPreservesPacketCounterWithoutReading()
    {
        var device = new FakeJoyCon(false);
        SetField(device, "frameCount", (byte)15);
        device.Subcommand(0x30, new byte[] { 1 }, 1);
        device.Subcommand(0x30, new byte[] { 2 }, 1);
        CollectionAssert.AreEqual(new byte[] { 15, 0 }, device.Writes.Select(report => report[1]).ToArray());
        Assert.AreEqual(0, device.ReadCalls);
    }

    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void ValidCalibrationPreservesEachSidesLayoutAndFactoryCutoff(bool right, bool user, bool usb)
    {
        var device = CalibratedDevice(right, user, usb);
        device.CalibrationData();
        AssertAxis(device, right, user);
        Assert.AreEqual(user, Field<bool>(device, right ? "foundRightStickCalib" : "foundLeftStickCalib"));
        Assert.IsTrue(Field<double[]>(device, "accelCoeff").All(double.IsFinite));
        Assert.IsTrue(Field<double[]>(device, "gyroCoeff").All(double.IsFinite));
        Assert.IsFalse(device.SpiAddresses.Contains(right ? (ushort)0x603D : (ushort)0x6046),
            "A Joy-Con must not require calibration for its absent stick.");
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void MissingOrInvalidUserCalibrationFallsBackToRealFactoryData(bool right, bool missing)
    {
        var device = CalibratedDevice(right, true);
        device.TransformSpiReply = (address, reply) => address >= 0x8000 ?
            missing ? null : SpiReply(address, new byte[reply[19]]) : reply;
        device.CalibrationData();
        AssertAxis(device, right, false);
        Assert.IsTrue(device.SpiAddresses.Contains((ushort)0x6020));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TransientFactoryFailureRecoversWithinBoundedRetries(bool right)
    {
        var device = CalibratedDevice(right, false);
        ushort factory = right ? (ushort)0x6046 : (ushort)0x603D;
        int reads = 0;
        device.TransformSpiReply = (address, reply) => address == factory && reads++ == 0 ? null : reply;
        device.CalibrationData();
        Assert.AreEqual(2, device.SpiAddresses.Count(address => address == factory));
        AssertAxis(device, right, false);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ErasedOrMissingFactoryDataFailsAfterThreeAttempts(bool right, bool erased)
    {
        var device = CalibratedDevice(right, false);
        ushort factory = right ? (ushort)0x6046 : (ushort)0x603D;
        device.TransformSpiReply = (address, reply) => address == factory ?
            erased ? SpiReply(address, Enumerable.Repeat((byte)0xFF, 9).ToArray()) : null : reply;
        IOException failure = Assert.ThrowsException<IOException>(device.CalibrationData);
        StringAssert.Contains(failure.Message, "Joy-Con");
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == factory));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidImuFailsWithoutInstallingInfiniteCoefficients(bool right)
    {
        var device = CalibratedDevice(right, false);
        device.TransformSpiReply = (address, reply) => address == 0x6020 ? SpiReply(address, new byte[24]) : reply;
        Assert.ThrowsException<IOException>(device.CalibrationData);
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == 0x6020));
        Assert.IsTrue(Field<double[]>(device, "accelCoeff").All(value => value == 0));
        Assert.IsTrue(Field<double[]>(device, "gyroCoeff").All(value => value == 0));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void JoyConSpecificYCutoffCannotInstallAnInvertedFactoryRange(bool right)
    {
        var device = CalibratedDevice(right, false);
        ushort factory = right ? (ushort)0x6046 : (ushort)0x603D;
        byte[] narrow = Stick(right);
        PutPair(narrow, right ? 3 : 6, 100);
        PutPair(narrow, right ? 6 : 0, 150);
        Assert.IsTrue(SwitchProCalibrationProtocol.IsValidStick(SpiReply(factory, narrow), right, false));
        device.TransformSpiReply = (address, reply) => address == factory ? SpiReply(address, narrow) : reply;
        Assert.ThrowsException<IOException>(device.CalibrationData);
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == factory));
    }

    [TestMethod]
    public void ProgrammingErrorsRemainVisibleInsteadOfBeingReportedAsDisconnects()
    {
        var device = new FakeJoyCon(false) { WriteFailure = new ArgumentException("Synthetic programming error") };
        int removals = 0;
        device.Removal += (_, _) => removals++;
        Assert.ThrowsException<ArgumentException>(device.StartUpdate);
        Assert.AreEqual(0, removals);
    }

    [DataTestMethod]
    [DataRow(0x603D, 0)]
    [DataRow(0x603D, 1)]
    [DataRow(0x603D, 2)]
    [DataRow(0x603D, 3)]
    [DataRow(0x6020, 0)]
    [DataRow(0x6020, 1)]
    [DataRow(0x6020, 2)]
    [DataRow(0x6020, 3)]
    public void FailedInitializationRemovesDeviceWithoutStartingWorkersOrPublishingIt(int factory, int kind)
    {
        var device = CalibratedDevice(false, false);
        SetField(device, "connectionOpened", true);
        device.TransformSpiReply = (address, reply) =>
        {
            if (address != factory) return reply;
            if (kind == 0) return null;
            if (kind == 1) return reply[..19];
            reply[kind == 2 ? 13 : 15] = 0;
            return reply;
        };
        // No service constructor, HID IO, driver or virtual-output creation.
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        service.DS4Controllers[0] = device;
        int removals = 0, publications = 0;
        device.Removal += (_, _) => { removals++; service.DS4Controllers[0] = null; };
        service.HotplugController += (_, _, _) => publications++;
        device.StartUpdate();
        service.PublishPreparedHotplug(device, 0);
        Assert.AreEqual(1, removals);
        Assert.AreEqual(0, publications);
        Assert.IsFalse(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        Assert.IsNull(Field<object>(device, "rumbleOutput"));
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == factory));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ValidBluetoothInitializationKeepsEachSideOperational(bool right)
    {
        var device = CalibratedDevice(right, false);
        device.SetOperational();
        Assert.IsTrue(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        AssertAxis(device, right, false);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClosedNativeOwnerUsesCleanRemovalBoundary(bool read)
    {
        var device = CalibratedDevice(false, false);
        if (read) device.ReadFailure = new ObjectDisposedException("Test HID owner");
        else device.WriteFailure = new InvalidOperationException("Test HID owner unavailable");
        int removals = 0;
        device.Removal += (_, _) => removals++;
        device.StartUpdate();
        Assert.AreEqual(1, removals);
        Assert.IsFalse(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
    }

    private static FakeJoyCon CalibratedDevice(bool right, bool user, bool usb = false) => new(right, usb)
    {
        AutoAcknowledgeCommands = true,
        SpiData = (address, count) => address switch
        {
            0x8010 or 0x801B or 0x8026 => user ? new byte[] { 0xB2, 0xA1 } : new byte[2],
            0x8012 or 0x603D => Stick(false),
            0x801D or 0x6046 => Stick(true),
            0x8028 or 0x6020 => Imu(),
            _ => throw new AssertFailedException($"Unexpected SPI address {address:X4}/{count}")
        }
    };

    private static byte[] Request(ushort address, byte count) => SwitchProCalibrationProtocol.CreateSpiRequest(address, count);
    private static byte[] Ack(byte command)
    {
        byte[] reply = new byte[15];
        reply[0] = 0x21; reply[13] = 0x80; reply[14] = command;
        return reply;
    }
    private static byte[] SpiReply(ushort address, byte[] data)
    {
        byte[] reply = new byte[20 + data.Length];
        Ack(0x10).CopyTo(reply, 0);
        reply[13] = 0x90;
        Request(address, (byte)data.Length).CopyTo(reply, 15);
        data.CopyTo(reply, 20);
        return reply;
    }
    private static byte[] Stick(bool right)
    {
        byte[] data = new byte[9];
        PutPair(data, right ? 0 : 3, right ? 2100 : 2000);
        PutPair(data, right ? 3 : 6, 1000);
        PutPair(data, right ? 6 : 0, 1000);
        return data;
    }
    private static void PutPair(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)((value >> 8) | ((value & 0x0F) << 4));
        data[offset + 2] = (byte)(value >> 4);
    }
    private static byte[] Imu()
    {
        byte[] data = new byte[24];
        for (int axis = 0; axis < 3; axis++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(axis * 2, 2), (short)(10 + axis));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(6 + axis * 2, 2), 16400);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12 + axis * 2, 2), (short)(20 + axis));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(18 + axis * 2, 2), 13400);
        }
        return data;
    }
    private static void SetField(JoyConDevice device, string name, object value) =>
        typeof(JoyConDevice).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(device, value);
    private static T Field<T>(JoyConDevice device, string name) =>
        (T)typeof(JoyConDevice).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(device)!;
    private static void AssertAxis(JoyConDevice device, bool right, bool user)
    {
        JoyConDevice.StickAxisData axis = Field<JoyConDevice.StickAxisData>(device, right ? "rightStickXData" : "leftStickXData");
        Assert.AreEqual(user ? (right ? 1100 : 1000) : (right ? 1144 : 1040), (int)axis.min);
        Assert.AreEqual(user ? (right ? 2100 : 2000) : (right ? 2060 : 1960), (int)axis.mid);
        Assert.AreEqual(user ? (right ? 3100 : 3000) : (right ? 2976 : 2880), (int)axis.max);
        JoyConDevice.StickAxisData y = Field<JoyConDevice.StickAxisData>(device, right ? "rightStickYData" : "leftStickYData");
        Assert.AreEqual(user ? (right ? 1100 : 1000) : (right ? 1254 : 1140), (int)y.min);
        Assert.AreEqual(user ? (right ? 2100 : 2000) : (right ? 2115 : 2010), (int)y.mid);
        Assert.AreEqual((int)axis.max, (int)y.max);
    }

    private sealed class FakeJoyCon : JoyConDevice
    {
        internal bool AcceptWrite = true, AutoAcknowledgeCommands;
        internal readonly List<byte[]> Writes = new();
        internal readonly Queue<byte[]> Replies = new();
        internal readonly List<uint> ReadTimeouts = new();
        internal readonly List<ushort> SpiAddresses = new();
        internal Func<ushort, byte, byte[]> SpiData;
        internal Func<ushort, byte[], byte[]> TransformSpiReply;
        internal byte[] RepeatReply;
        internal int? ForcedReadResult;
        internal int MillisecondsPerRead, ReadCalls;
        internal Exception WriteFailure, ReadFailure;
        private long now;
        internal bool HasInputWorker => ds4Input != null;

        internal FakeJoyCon(bool right, bool usb = false) : base(
            (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)), "Test Joy-Con")
        {
            conType = usb ? ConnectionType.USB : ConnectionType.BT;
            SetField(this, "sideType", right ? JoyConSide.Right : JoyConSide.Left);
            SetField(this, "inputReportLen", usb ? INPUT_REPORT_LEN_USB : INPUT_REPORT_LEN_BT);
            SetField(this, "rumbleReportLen", usb ? RUMBLE_REPORT_LEN_USB : RUMBLE_REPORT_LEN_BT);
        }
        protected override bool SubmitSubcommandReport(byte[] report)
        {
            if (WriteFailure != null) throw WriteFailure;
            Writes.Add((byte[])report.Clone());
            if (!AcceptWrite) return false;
            if (report[0] == 0x01 && report[10] == 0x10)
            {
                ushort address = BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(11, 2));
                SpiAddresses.Add(address);
                if (SpiData != null)
                {
                    byte[] reply = SpiReply(address, SpiData(address, report[15]));
                    if (TransformSpiReply != null) reply = TransformSpiReply(address, reply);
                    if (reply != null) Replies.Enqueue(reply);
                }
            }
            else if (report[0] == 0x01 && AutoAcknowledgeCommands) Replies.Enqueue(Ack(report[10]));
            return true;
        }
        protected override int ReadSubcommandReport(byte[] report, uint timeout)
        {
            if (ReadFailure != null) throw ReadFailure;
            ReadCalls++;
            ReadTimeouts.Add(timeout);
            now += MillisecondsPerRead;
            byte[] reply = Replies.Count > 0 ? Replies.Dequeue() : RepeatReply;
            reply?.CopyTo(report, 0);
            return ForcedReadResult ?? reply?.Length ?? -1;
        }
        protected override long SubcommandTimestampMilliseconds => now;
    }
}
