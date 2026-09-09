using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class SwitchProCalibrationProtocolTests
{
    [TestMethod]
    public void FailedWriteReturnsNoReplyWithoutReading()
    {
        var device = new FakePro { AcceptWrite = false };
        Assert.IsNull(device.Subcommand(0x10, Request(0x8010, 2), 5, true));
        Assert.AreEqual(1, device.Writes.Count);
        Assert.AreEqual(0, device.ReadCalls);
    }

    [TestMethod]
    public void InvalidPayloadIsRejectedBeforeWriting()
    {
        var device = new FakePro();
        Assert.ThrowsException<ArgumentException>(() => device.Subcommand(0x10, null, 5, true));
        Assert.ThrowsException<ArgumentException>(() => device.Subcommand(0x10, new byte[4], 5, true));
        Assert.ThrowsException<ArgumentException>(() => device.Subcommand(0x10, new byte[64], 64, true));
        Assert.AreEqual(0, device.Writes.Count);
    }

    [TestMethod]
    public void ReplyMustMatchReportAckSubcommandAddressCountAndActualLength()
    {
        var device = new FakePro();
        byte[] valid = SpiReply(0x603D, Stick(false));
        byte[] wrongReport = (byte[])valid.Clone();
        wrongReport[0] = 0x30;
        byte[] wrongSubcommand = (byte[])valid.Clone();
        wrongSubcommand[14] = 0x30;
        byte[] nack = (byte[])valid.Clone();
        nack[13] = 0;
        byte[] wrongCount = (byte[])valid.Clone();
        wrongCount[19] = 8;
        device.Replies.Enqueue(wrongReport);
        device.Replies.Enqueue(wrongSubcommand);
        device.Replies.Enqueue(nack);
        device.Replies.Enqueue(SpiReply(0x6046, Stick(true)));
        device.Replies.Enqueue(wrongCount);
        device.Replies.Enqueue(valid[..^1]);
        device.Replies.Enqueue(valid);

        CollectionAssert.AreEqual(valid, device.Subcommand(0x10, Request(0x603D, 9), 5, true));
        Assert.AreEqual(7, device.ReadCalls);
    }

    [TestMethod]
    public void OrdinaryAckStillRequiresBothReportIdAndSubcommand()
    {
        var device = new FakePro();
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
    public void FailedOrImpossibleNativeReadCannotAdmitBufferContents(int readResult)
    {
        var device = new FakePro { ForcedReadResult = readResult };
        device.Replies.Enqueue(SpiReply(0x603D, Stick(false)));
        Assert.IsNull(device.Subcommand(0x10, Request(0x603D, 9), 5, true));
        Assert.AreEqual(1, device.ReadCalls);
    }

    [TestMethod]
    public void ReceiveDeadlineCoversAllUnrelatedReports()
    {
        var device = new FakePro { MillisecondsPerRead = 180, RepeatReply = Ack(0x30) };
        Assert.IsNull(device.Subcommand(0x40, new byte[] { 1 }, 1, true));
        CollectionAssert.AreEqual(new uint[] { 500, 320, 140 }, device.ReadTimeouts);
        Assert.AreEqual(3, device.ReadCalls);
    }

    [TestMethod]
    public void ImmediateUnrelatedReportFloodAlsoHasAFiniteBound()
    {
        var device = new FakePro { RepeatReply = Ack(0x30) };
        Assert.IsNull(device.Subcommand(0x40, new byte[] { 1 }, 1, true));
        Assert.AreEqual(64, device.ReadCalls);
        Assert.AreEqual(1, device.Writes.Count);
    }

    [TestMethod]
    public void FireAndForgetKeepsPacketCounterAndDoesNotRead()
    {
        var device = new FakePro { FrameCount = 15 };
        device.Subcommand(0x30, new byte[] { 1 }, 1);
        device.Subcommand(0x30, new byte[] { 2 }, 1);
        Assert.AreEqual((byte)15, device.Writes[0][1]);
        Assert.AreEqual((byte)0, device.Writes[1][1]);
        Assert.AreEqual((byte)1, device.Writes[0][11]);
        Assert.AreEqual(0, device.ReadCalls);
    }

    [TestMethod]
    public void UnreadableUserMagicFallsBackToRealFactoryCalibration()
    {
        var device = CalibratedDevice(user: false);
        device.RejectAddress = address => address >= 0x8000;
        device.CalibrationData();
        AssertAxis(device, "leftStickXData", 1040, 1960, 2880);
        AssertAxis(device, "rightStickXData", 1144, 2060, 2976);
        CollectionAssert.AreEqual(new ushort[] { 1000, 1000, 2000, 2000, 1000, 1000 },
            Field<ushort[]>(device, "leftStickCalib"));
        Assert.IsTrue(Field<double[]>(device, "accelCoeff").All(double.IsFinite));
        Assert.IsTrue(Field<double[]>(device, "gyroCoeff").All(double.IsFinite));
    }

    [TestMethod]
    public void ValidUserCalibrationPreservesExistingAxisOrderingAndNoFactoryCutoff()
    {
        var device = CalibratedDevice(user: true);
        device.CalibrationData();
        AssertAxis(device, "leftStickXData", 1000, 2000, 3000);
        AssertAxis(device, "rightStickXData", 1100, 2100, 3100);
        Assert.IsFalse(device.SpiAddresses.Any(address => address < 0x8000));
    }

    [TestMethod]
    public void InvalidUserBlockFallsBackToFactoryRatherThanInstallingZeros()
    {
        var device = CalibratedDevice(user: true);
        Func<ushort, byte, byte[]> healthy = device.SpiData;
        device.SpiData = (address, count) => address == 0x8012 ? new byte[count] : healthy(address, count);
        device.CalibrationData();
        AssertAxis(device, "leftStickXData", 1040, 1960, 2880);
        Assert.IsTrue(device.SpiAddresses.Contains(0x603D));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrInvalidFactoryCalibrationThrowsIOExceptionAfterThreeAttempts(bool zeroData)
    {
        var device = CalibratedDevice(user: false);
        if (zeroData)
            device.SpiData = (_, count) => new byte[count];
        else
            device.AcceptWrite = false;

        Assert.ThrowsException<IOException>(device.CalibrationData);
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == 0x603D));
        Assert.AreEqual(4, device.Writes.Count);
    }

    [TestMethod]
    public void TransientZeroFactoryBlockCanRecoverOnBoundedRetry()
    {
        var device = CalibratedDevice(user: false);
        Func<ushort, byte, byte[]> healthy = device.SpiData;
        int reads = 0;
        device.SpiData = (address, count) => address == 0x603D && reads++ == 0 ?
            new byte[count] : healthy(address, count);
        device.CalibrationData();
        Assert.AreEqual(2, device.SpiAddresses.Count(address => address == 0x603D));
        AssertAxis(device, "leftStickXData", 1040, 1960, 2880);
    }

    [TestMethod]
    public void InvalidImuDenominatorsFailInsteadOfInstallingInfiniteCoefficients()
    {
        var device = CalibratedDevice(user: false);
        Func<ushort, byte, byte[]> healthy = device.SpiData;
        device.SpiData = (address, count) => address == 0x6020 ? new byte[count] : healthy(address, count);
        Assert.ThrowsException<IOException>(device.CalibrationData);
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == 0x6020));
        Assert.IsTrue(Field<double[]>(device, "accelCoeff").All(value => value == 0));
    }

    [TestMethod]
    public void FailedInitializationRaisesRemovalAndStartsNoWorkersEvenAfterPreviousOpen()
    {
        var device = new FakePro { AcceptWrite = false };
        typeof(SwitchProDevice).GetField("connectionOpened", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(device, true);
        int removals = 0;
        device.Removal += (_, _) => removals++;
        device.StartUpdate();
        Assert.AreEqual(1, removals);
        Assert.IsFalse(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        Assert.IsNull(Field<object>(device, "rumbleOutput"));
    }

    [DataTestMethod]
    [DataRow(0x603D, 0)]
    [DataRow(0x603D, 1)]
    [DataRow(0x603D, 2)]
    [DataRow(0x603D, 3)]
    [DataRow(0x6046, 0)]
    [DataRow(0x6046, 1)]
    [DataRow(0x6046, 2)]
    [DataRow(0x6046, 3)]
    [DataRow(0x6020, 0)]
    [DataRow(0x6020, 1)]
    [DataRow(0x6020, 2)]
    [DataRow(0x6020, 3)]
    public void BluetoothCalibrationFailureCannotReannounceRemovedController(
        int factoryAddress, int failureKind)
    {
        // #68 reports a Bluetooth Switch Pro null reference immediately after
        // virtual-output association. Exercise actual StartUpdate through each
        // calibration stage, not just an isolated calibration decoder call.
        var device = CalibratedDevice(user: false);
        device.AutoAcknowledgeCommands = true;
        device.TransformSpiReply = (address, reply) =>
        {
            if (address != factoryAddress) return reply;
            switch (failureKind)
            {
                case 0: return null; // no reply
                case 1: return reply[..19]; // missing SPI payload
                case 2: reply[13] = 0; return reply; // negative acknowledgement
                case 3: reply[15] ^= 1; return reply; // different SPI address
                default: throw new AssertFailedException("Unknown calibration failure fixture.");
            }
        };

        // Use the production post-preparation publication guard, but fake its
        // removal subscriber: no service constructor, virtual pad, or HID IO.
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        service.DS4Controllers[0] = device;
        int removals = 0;
        int publications = 0;
        device.Removal += (_, _) =>
        {
            removals++;
            service.DS4Controllers[0] = null;
        };
        service.HotplugController += (_, _, _) => publications++;

        device.StartUpdate();
        service.PublishPreparedHotplug(device, 0);

        Assert.AreEqual(1, removals);
        Assert.AreEqual(0, publications);
        Assert.IsFalse(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        Assert.IsNull(Field<object>(device, "rumbleOutput"));
        Assert.AreEqual(3, device.SpiAddresses.Count(address => address == factoryAddress));
        Assert.IsFalse(device.Writes.Any(report => report[0] == 0x80),
            "Bluetooth initialization must not depend on USB setup commands.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BluetoothInitializationPreservesValidUserOrFactoryCalibration(bool user)
    {
        var device = CalibratedDevice(user);
        device.AutoAcknowledgeCommands = true;
        device.SetOperational();

        Assert.IsTrue(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        Assert.IsFalse(device.Writes.Any(report => report[0] == 0x80));
        Assert.AreEqual(user ? 2000 : 1960,
            (int)Field<SwitchProDevice.StickAxisData>(device, "leftStickXData").mid);
        Assert.AreEqual(user ? 2100 : 2060,
            (int)Field<SwitchProDevice.StickAxisData>(device, "rightStickXData").mid);
        Assert.IsTrue(Field<double[]>(device, "accelCoeff").All(double.IsFinite));
        Assert.IsTrue(Field<double[]>(device, "gyroCoeff").All(double.IsFinite));
    }

    [DataTestMethod]
    [DataRow(0x603D)]
    [DataRow(0x6046)]
    [DataRow(0x6020)]
    public void BluetoothInitializationRecoversTransientFactoryReadFailure(int factoryAddress)
    {
        var device = CalibratedDevice(user: false);
        device.AutoAcknowledgeCommands = true;
        int failedStageReads = 0;
        device.TransformSpiReply = (address, reply) =>
            address == factoryAddress && failedStageReads++ == 0 ? null : reply;

        device.SetOperational();

        Assert.IsTrue(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        Assert.AreEqual(2, device.SpiAddresses.Count(address => address == factoryAddress));
        Assert.IsTrue(Field<double[]>(device, "accelCoeff").All(double.IsFinite));
        Assert.IsTrue(Field<double[]>(device, "gyroCoeff").All(double.IsFinite));
    }

    [TestMethod]
    public void UsbInitializationUsesBoundedTransportSeamAndKeepsHandshakeOrder()
    {
        var device = CalibratedDevice(user: false);
        device.UseUsb();
        device.AutoAcknowledgeCommands = true;
        device.SetOperational();
        Assert.IsTrue(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        byte[][] usbCommands = device.Writes.Where(report => report[0] == 0x80).ToArray();
        Assert.AreEqual(5, usbCommands.Length);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 2, 4 },
            usbCommands.Select(report => report[1]).ToArray());
        Assert.AreEqual((byte)0x03, device.Writes[0][10]);
        Assert.AreEqual((byte)0x3F, device.Writes[0][11]);
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public void ClosedNativeOwnerUsesCleanRemovalBoundary(int operation, bool disposed)
    {
        var device = new FakePro { AutoAcknowledgeCommands = true };
        Exception failure = disposed ? new ObjectDisposedException("HID owner") :
            new InvalidOperationException("The HID transfer handle could not be opened.");
        SetNativeFailure(device, operation, failure);
        int removals = 0;
        device.Removal += (_, _) => removals++;
        device.StartUpdate();
        Assert.AreEqual(1, removals);
        Assert.IsFalse(device.IsAlive());
        Assert.IsFalse(device.HasInputWorker);
        Assert.IsNull(Field<object>(device, "rumbleOutput"));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void NativeOwnerNormalizationPreservesOriginalException(int operation)
    {
        var device = new FakePro { AutoAcknowledgeCommands = true };
        var failure = new ObjectDisposedException("HID owner");
        SetNativeFailure(device, operation, failure);
        IOException observed = operation == 2 ?
            Assert.ThrowsException<IOException>(device.SetInitRumble) :
            operation == 3 ? Assert.ThrowsException<IOException>(device.SetOperational) :
            Assert.ThrowsException<IOException>(() => device.Subcommand(0x30, new byte[] { 1 }, 1, true));
        Assert.AreSame(failure, observed.InnerException);
    }

    [TestMethod]
    public void UnrelatedProgrammingFailureIsNotSuppressedAsDisconnect()
    {
        var device = new FakePro { WriteException = new ArgumentException("Unexpected programming failure") };
        int removals = 0;
        device.Removal += (_, _) => removals++;
        Assert.ThrowsException<ArgumentException>(device.StartUpdate);
        Assert.AreEqual(0, removals);
    }

    private static void SetNativeFailure(FakePro device, int operation, Exception failure)
    {
        switch (operation)
        {
            case 0: device.WriteException = failure; break;
            case 1: device.ReadException = failure; break;
            case 2: device.RumbleException = failure; break;
            case 3: device.UseUsb(); device.UsbWriteException = failure; break;
            default: throw new AssertFailedException("Unknown native operation.");
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ErasedOrOutOfRangeStickCalibrationIsRejected(bool right)
    {
        Assert.IsFalse(SwitchProCalibrationProtocol.IsValidStick(
            SpiReply(0x603D, Enumerable.Repeat((byte)0xFF, 9).ToArray()), right, false));
        byte[] data = Stick(right);
        PutPair(data, right ? 3 : 6, 2500, 2500); // below-center delta would underflow
        Assert.IsFalse(SwitchProCalibrationProtocol.IsValidStick(SpiReply(0x603D, data), right, true));
        Assert.IsFalse(SwitchProCalibrationProtocol.IsValidStick(new byte[28], right, true));
    }

    private static FakePro CalibratedDevice(bool user)
    {
        return new FakePro
        {
            SpiData = (address, count) => address switch
            {
                0x8010 or 0x801B or 0x8026 => user ? new byte[] { 0xB2, 0xA1 } : new byte[2],
                0x8012 or 0x603D => Stick(false),
                0x801D or 0x6046 => Stick(true),
                0x8028 or 0x6020 => Imu(),
                _ => throw new AssertFailedException($"Unexpected SPI address {address:X4}/{count}")
            }
        };
    }

    private static byte[] Request(ushort address, byte count) =>
        SwitchProCalibrationProtocol.CreateSpiRequest(address, count);

    private static byte[] Ack(byte command)
    {
        byte[] result = new byte[15];
        result[0] = 0x21;
        result[13] = 0x80;
        result[14] = command;
        return result;
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
        PutPair(data, right ? 0 : 3, right ? 2100 : 2000, right ? 2100 : 2000);
        PutPair(data, right ? 3 : 6, 1000, 1000);
        PutPair(data, right ? 6 : 0, 1000, 1000);
        return data;
    }

    private static void PutPair(byte[] data, int offset, int x, int y)
    {
        data[offset] = (byte)x;
        data[offset + 1] = (byte)((x >> 8) | ((y & 0x0F) << 4));
        data[offset + 2] = (byte)(y >> 4);
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

    private static T Field<T>(SwitchProDevice device, string field) =>
        (T)typeof(SwitchProDevice).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(device)!;

    private static void AssertAxis(FakePro device, string field, int min, int mid, int max)
    {
        SwitchProDevice.StickAxisData axis = Field<SwitchProDevice.StickAxisData>(device, field);
        Assert.AreEqual(min, (int)axis.min);
        Assert.AreEqual(mid, (int)axis.mid);
        Assert.AreEqual(max, (int)axis.max);
    }

    private sealed class FakePro : SwitchProDevice
    {
        internal bool AcceptWrite = true;
        internal readonly List<byte[]> Writes = new();
        internal readonly Queue<byte[]> Replies = new();
        internal readonly List<uint> ReadTimeouts = new();
        internal readonly List<ushort> SpiAddresses = new();
        internal Func<ushort, byte, byte[]> SpiData;
        internal Func<ushort, byte[], byte[]> TransformSpiReply;
        internal Func<ushort, bool> RejectAddress;
        internal byte[] RepeatReply;
        internal int? ForcedReadResult;
        internal int MillisecondsPerRead;
        internal int ReadCalls;
        internal bool AutoAcknowledgeCommands;
        internal Exception WriteException;
        internal Exception ReadException;
        internal Exception RumbleException;
        internal Exception UsbWriteException;
        private long now;
        internal bool HasInputWorker => ds4Input != null;

        internal FakePro() : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)), "Test Pro")
        {
            conType = ConnectionType.BT;
        }

        internal void UseUsb() => conType = ConnectionType.USB;

        protected override bool SubmitSubcommandReport(byte[] report)
        {
            if (WriteException != null) throw WriteException;
            if (report[0] == 0x80 && UsbWriteException != null) throw UsbWriteException;
            Writes.Add((byte[])report.Clone());
            if (report[10] == 0x10)
            {
                ushort address = BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(11, 2));
                SpiAddresses.Add(address);
                if (!AcceptWrite || RejectAddress?.Invoke(address) == true)
                    return false;
                if (SpiData != null)
                {
                    byte[] reply = SpiReply(address, SpiData(address, report[15]));
                    if (TransformSpiReply != null) reply = TransformSpiReply(address, reply);
                    if (reply != null) Replies.Enqueue(reply);
                }
            }
            else if (AutoAcknowledgeCommands && report[0] == 0x01 && AcceptWrite)
            {
                Replies.Enqueue(Ack(report[10]));
            }
            return AcceptWrite;
        }

        protected override int ReadSubcommandReport(byte[] report, uint timeout)
        {
            if (ReadException != null) throw ReadException;
            ReadCalls++;
            ReadTimeouts.Add(timeout);
            now += MillisecondsPerRead;
            byte[] reply = Replies.Count > 0 ? Replies.Dequeue() : RepeatReply;
            reply?.CopyTo(report, 0);
            return ForcedReadResult ?? reply?.Length ?? -1;
        }

        protected override long SubcommandTimestampMilliseconds => now;
        protected override bool WriteRumbleReport(byte[] report)
        {
            if (RumbleException != null) throw RumbleException;
            return true;
        }
    }
}
