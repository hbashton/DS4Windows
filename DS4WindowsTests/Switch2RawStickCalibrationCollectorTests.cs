using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RawStickCalibrationCollectorTests
{
    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2, Switch2StickSide.Left)]
    [DataRow(Switch2ControllerModel.ProController2, Switch2StickSide.Right)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right)]
    public void BasicBluetoothReportsRetainPhysicalStickAndEightBitCounterWrap(
        Switch2ControllerModel model, Switch2StickSide side)
    {
        var f = new Fixture(model, false, side, basic: true);
        Rotate(f);
        FinishCenter(f, 2100, 2000);
        Assert.IsTrue(f.Collector.TryGetResult(out var result));
        Assert.AreEqual((ushort)2100, result.NeutralX);
        Assert.AreEqual((ushort)2000, result.NeutralY);
        Assert.AreEqual((ushort)1800, result.NegativeRangeX);
        Assert.AreEqual((ushort)1450, result.PositiveRangeY);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Left)]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Right)]
    [DataRow(Switch2ControllerModel.ProController2, false, Switch2StickSide.Left)]
    [DataRow(Switch2ControllerModel.ProController2, false, Switch2StickSide.Right)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right)]
    public void RealDecoderKeepsPhysicalSideAndAsymmetricRawCenterTravel(
        Switch2ControllerModel model, bool usb, Switch2StickSide side)
    {
        var f = new Fixture(model, usb, side);
        Rotate(f);
        FinishCenter(f, 2100, 2000);
        Assert.IsTrue(f.Collector.TryGetResult(out var calibration));
        Assert.AreEqual(f.Peer, f.Collector.Peer);
        Assert.AreEqual(side, f.Collector.Side);
        Assert.AreEqual((ushort)2100, calibration.NeutralX);
        Assert.AreEqual((ushort)2000, calibration.NeutralY);
        Assert.AreEqual((ushort)1800, calibration.NegativeRangeX);
        Assert.AreEqual((ushort)1600, calibration.PositiveRangeX);
        Assert.AreEqual((ushort)1550, calibration.NegativeRangeY);
        Assert.AreEqual((ushort)1450, calibration.PositiveRangeY);
        Assert.IsTrue(Switch2ProfileAxisProjection.TryMap(2100,
            2100 - calibration.NeutralX, calibration.NegativeRangeX,
            calibration.PositiveRangeX, false, out var center));
        Assert.AreEqual((short)0, center.SignedValue);
        Assert.AreEqual((byte)128, center.LegacyValue);
        Assert.IsTrue(Switch2ProfileAxisProjection.TryMap(3700,
            3700 - calibration.NeutralX, calibration.NegativeRangeX,
            calibration.PositiveRangeX, false, out var maximum));
        Assert.AreEqual(short.MaxValue, maximum.SignedValue);
        // Factory/user-SPI calibration is immutable; this is a separate result.
        Assert.IsTrue(f.Frame(2100, 2000).TryGetLeftStick(out var original) ||
            f.Descriptor.Identity.Model == Switch2ControllerModel.JoyCon2Right);
        if (side == Switch2StickSide.Left)
            Assert.AreNotEqual(0, original.OffsetX);
    }

    [DataTestMethod]
    [DataRow(2000)]
    [DataRow(4000)]
    [DataRow(50000)]
    public void MovementComparisonIsIndependentOfInputReportRate(int intervalMicroseconds)
    {
        var f = new Fixture();
        for (int time = 0; time <= 12_000_000 &&
            f.Collector.Stage == Switch2RawStickCalibrationStage.Rotate; time += intervalMicroseconds)
        {
            // Slow motion changes by fewer than ten raw counts on individual
            // high-rate reports, but is visible at the reference UI cadence.
            double phase = (time % 2_000_000) / 2_000_000.0;
            ushort x = (ushort)(800 + 2200 * (phase < .5 ? phase * 2 : 2 - phase * 2));
            Assert.IsTrue(f.Observe(x, 2048, intervalMicroseconds));
        }
        Assert.AreEqual(Switch2RawStickCalibrationStage.Settle, f.Collector.Stage);
        Assert.AreEqual(1.0, f.Collector.RotationProgress, 1e-9);
    }

    [TestMethod]
    public void StationaryStickCannotEarnRotationTimeAndIncompleteTravelCannotBeSaved()
    {
        var stationary = new Fixture();
        for (int i = 0; i < 400; i++) Assert.IsTrue(stationary.Observe(2048, 2048));
        Assert.AreEqual(0.0, stationary.Collector.RotationProgress);
        Assert.IsFalse(stationary.Collector.TryGetResult(out _));

        var partial = new Fixture();
        for (int i = 0; i < 250 && partial.Collector.Stage == Switch2RawStickCalibrationStage.Rotate; i++)
            Assert.IsTrue(partial.Observe((ushort)(i % 2 == 0 ? 2040 : 2060), 2048));
        FinishCenter(partial, 2048, 2048);
        Assert.AreEqual(Switch2RawStickCalibrationStage.InsufficientTravel, partial.Collector.Stage);
        Assert.IsFalse(partial.Collector.TryGetResult(out _));
    }

    [TestMethod]
    public void LongInputPauseAndTouchBetweenUiSamplesResetContinuousCenterEvidence()
    {
        var f = new Fixture();
        Rotate(f);
        EnterCenter(f);
        for (int i = 0; i < 20; i++) Assert.IsTrue(f.Observe(2100, 2000));
        Assert.IsTrue(f.Collector.StationaryProgress > .3);
        Assert.IsTrue(f.Observe(2100, 2000, 4_000_000));
        Assert.AreEqual(Switch2RawStickCalibrationStage.Settle, f.Collector.Stage);
        Assert.AreEqual(0.0, f.Collector.StationaryProgress);
        EnterCenter(f);
        for (int i = 0; i < 20; i++) Assert.IsTrue(f.Observe(2100, 2000));
        Assert.IsTrue(f.Observe(2200, 2000, 2000));
        Assert.IsTrue(f.Observe(2100, 2000, 2000));
        Assert.AreEqual(Switch2RawStickCalibrationStage.Settle, f.Collector.Stage);
        Assert.AreEqual(0.0, f.Collector.StationaryProgress);
        Assert.IsFalse(f.Collector.TryGetResult(out _));
        FinishCenter(f, 2100, 2000);
        Assert.IsTrue(f.Collector.TryGetResult(out var calibration));
        Assert.AreEqual((ushort)2100, calibration.NeutralX);
    }

    [TestMethod]
    public void DuplicateTimeWrongLifetimeAndCancelledSamplesCannotChangeProgress()
    {
        var f = new Fixture();
        var frame = f.Frame(300, 450);
        Assert.IsTrue(f.Collector.TryObserve(frame));
        Assert.IsFalse(f.Collector.TryObserve(frame));
        var successor = new Fixture(generation: 2);
        Assert.IsFalse(f.Collector.TryObserve(successor.Frame(3700, 3450)));
        Assert.IsFalse(f.Collector.TryObserve(default));
        Assert.AreEqual(0.0, f.Collector.RotationProgress);
        f.Collector.Cancel();
        Assert.IsFalse(f.Collector.TryObserve(f.Frame(3700, 3450)));
        Assert.IsFalse(f.Collector.TryGetResult(out _));
        Assert.AreEqual(Switch2RawStickCalibrationStage.Cancelled, f.Collector.Stage);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ConfirmedProCounterDiscontinuityDoesNotDiscardCalibrationSamples(bool usb)
    {
        var pro = new Fixture(usb: usb);
        Assert.IsTrue(pro.Collector.TryObserve(pro.Frame(300, 450, counter: 1_431_640)));
        var discontinuity = pro.Frame(3700, 3450, counter: 1);
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder, discontinuity.CounterSequence);
        Assert.IsTrue(pro.Collector.TryObserve(discontinuity));
        Assert.IsTrue(pro.Collector.RotationProgress > 0);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right)]
    public void ProCounterCorrectionDoesNotBroadenJoyConCalibrationAdmission(
        Switch2ControllerModel model, Switch2StickSide side)
    {
        var ble = new Fixture(model, usb: false, side);
        Assert.IsTrue(ble.Collector.TryObserve(ble.Frame(300, 450, counter: 20)));
        Assert.IsFalse(ble.Collector.TryObserve(ble.Frame(3700, 3450, counter: 19)));
        Assert.AreEqual(0.0, ble.Collector.RotationProgress);
    }

    [TestMethod]
    public void ConstructorRequiresPhysicalPeerAndSidePresentOnThatModel()
    {
        var f = new Fixture(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left);
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2RawStickCalibrationCollector(f.Descriptor, default, Switch2StickSide.Left));
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2RawStickCalibrationCollector(f.Descriptor, f.Peer, Switch2StickSide.Right));
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2RawStickCalibrationCollector(default, f.Peer, Switch2StickSide.Left));
    }

    [TestMethod]
    public void AdmittedSamplingUsesNoManagedAllocationAfterWarmup()
    {
        var f = new Fixture();
        for (int i = 0; i < 2000; i++) f.Collector.TryObserve(f.Frame(2048, 2048));
        bool accepted = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++) accepted &= f.Collector.TryObserve(f.Frame(2048, 2048));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(accepted);
        Assert.AreEqual(0L, allocated);
    }

    private static void Rotate(Fixture f)
    {
        for (int i = 0; i < 250 && f.Collector.Stage == Switch2RawStickCalibrationStage.Rotate; i++)
            Assert.IsTrue(f.Observe((ushort)(i % 2 == 0 ? 300 : 3700), (ushort)(i % 2 == 0 ? 450 : 3450)));
        Assert.AreEqual(Switch2RawStickCalibrationStage.Settle, f.Collector.Stage);
    }

    private static void EnterCenter(Fixture f)
    {
        for (int i = 0; i < 50 && f.Collector.Stage != Switch2RawStickCalibrationStage.Center; i++)
            Assert.IsTrue(f.Observe(2100, 2000));
        Assert.AreEqual(Switch2RawStickCalibrationStage.Center, f.Collector.Stage);
    }

    private static void FinishCenter(Fixture f, ushort x, ushort y)
    {
        for (int i = 0; i < 125 && f.Collector.Stage is Switch2RawStickCalibrationStage.Settle or
            Switch2RawStickCalibrationStage.Center; i++) Assert.IsTrue(f.Observe(x, y));
    }

    internal sealed class Fixture
    {
        internal readonly Switch2InputSessionDescriptor Descriptor;
        internal readonly Switch2PersistentPeerId Peer;
        internal readonly Switch2RawStickCalibrationCollector Collector;
        private readonly Switch2InputSession session;
        private readonly byte[] packet;
        private readonly bool usb, basic;
        private readonly Switch2StickSide side;
        private uint counter;
        private long timestamp;

        internal Fixture(Switch2ControllerModel model = Switch2ControllerModel.ProController2,
            bool usb = true, Switch2StickSide side = Switch2StickSide.Left, ulong generation = 1, bool basic = false)
        {
            this.usb = usb; this.side = side; this.basic = basic;
            Guid characteristic = !basic ? Switch2InputCodec.Common05CharacteristicUuid : model switch
            {
                Switch2ControllerModel.JoyCon2Left => Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
                Switch2ControllerModel.JoyCon2Right => Switch2InputCodec.JoyCon2Right08CharacteristicUuid,
                _ => Switch2InputCodec.ProController2_09CharacteristicUuid,
            };
            Switch2InputProtocolIdentity identity;
            bool valid = usb ? Switch2InputProtocolIdentity.TryCreateProController2Usb(0x057E, 0x2069, 0x0201, out identity) :
                Switch2InputProtocolIdentity.TryCreateBluetoothLe(Switch2InputCodec.ServiceUuid,
                    characteristic, Switch2GattProperty.Read | Switch2GattProperty.Notify,
                    model, out identity);
            Assert.IsTrue(valid);
            Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, generation, generation, 1_000_000, out Descriptor));
            Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model, generation, out var calibration));
            session = new Switch2InputSession(Descriptor, calibration);
            ushort product = model switch
            {
                Switch2ControllerModel.JoyCon2Left => Switch2AdvertisementCodec.JoyCon2LeftProductId,
                Switch2ControllerModel.JoyCon2Right => Switch2AdvertisementCodec.JoyCon2RightProductId,
                _ => Switch2AdvertisementCodec.ProController2ProductId,
            };
            Assert.IsTrue(Switch2PersistentPeerId.TryDerive(Enumerable.Repeat((byte)1, 32).ToArray(),
                new byte[] { 1, 2, 3 }, model, product, out Peer));
            Collector = new Switch2RawStickCalibrationCollector(Descriptor, Peer, side);
            packet = new byte[usb ? Switch2InputCodec.UsbPacketLength : Switch2InputCodec.BluetoothLeBodyLength];
            if (usb) packet[0] = (byte)Switch2InputReportKind.Common05;
        }

        internal bool Observe(ushort x, ushort y, int interval = 50_000) => Collector.TryObserve(Frame(x, y, interval));

        internal Switch2CanonicalInputFrame Frame(ushort x, ushort y, int interval = 50_000, uint? counter = null)
        {
            int offset = usb ? 1 : 0;
            uint nextCounter = counter ?? ++this.counter;
            if (basic)
            {
                packet[0] = (byte)nextCounter;
                bool singleStick = Descriptor.Identity.Model != Switch2ControllerModel.ProController2;
                Pack(5, singleStick || side == Switch2StickSide.Left ? x : (ushort)2048,
                    singleStick || side == Switch2StickSide.Left ? y : (ushort)2048);
                if (!singleStick)
                    Pack(8, side == Switch2StickSide.Right ? x : (ushort)2048, side == Switch2StickSide.Right ? y : (ushort)2048);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), nextCounter);
                Pack(offset + 10, side == Switch2StickSide.Left ? x : (ushort)2048, side == Switch2StickSide.Left ? y : (ushort)2048);
                Pack(offset + 13, side == Switch2StickSide.Right ? x : (ushort)2048, side == Switch2StickSide.Right ? y : (ushort)2048);
            }
            timestamp += interval;
            if (!session.TryProcess(Descriptor, packet, timestamp, out var frame, out var failure))
                throw new InvalidOperationException(failure.ToString());
            return frame;
        }

        private void Pack(int offset, ushort x, ushort y)
        {
            packet[offset] = (byte)x;
            packet[offset + 1] = (byte)((x >> 8) | ((y & 15) << 4));
            packet[offset + 2] = (byte)(y >> 4);
        }
    }
}
