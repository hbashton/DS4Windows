using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProUsbInputProjectionTests
{
    private const string FactId =
        "fact-765fc847b31d4385b65d52bd94a337ac";
    private const string StreamId =
        "stream-bb8d477c0d8743f4bfbd55833d43ad37";
    private const string ClockId =
        "clock-68d0cb24bfa2445ba33ed67b44827976";

    [TestMethod]
    public void Common05RetainsAndRoundTripsEveryBodyByte()
    {
        var random = new Random(0x057E2069);
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        var encoded = new byte[Switch2InputCodec.BluetoothLeBodyLength];

        for (int iteration = 0; iteration < 20000; iteration++)
        {
            random.NextBytes(body);

            Assert.IsTrue(Switch2InputCodec.TryDecodeCommon05(body,
                out var report));
            Assert.IsTrue(Switch2InputCodec.TryEncodeCommon05(report,
                encoded));
            CollectionAssert.AreEqual(body, encoded,
                $"Lossless common-report round trip failed at {iteration}.");
        }
    }

    [TestMethod]
    public void Common05ExposesEveryOpaqueRegionWithoutAssigningSemantics()
    {
        byte[] body = BuildCommonBody(counter: 0x40302010,
            buttons: 0xA5C35A3C, leftX: 0x123, leftY: 0x456,
            rightX: 0x789, rightY: 0xABC);
        WriteUInt16(body, 0x08, 0xBBAA);
        body[0x18] = 0xCC;
        new byte[] { 0x10, 0x21, 0x32, 0x43, 0x54, 0x65 }
            .CopyTo(body, 0x24);
        body[0x3C] = 0x76;
        body[0x3D] = 0x87;
        body[0x3E] = 0x98;

        Assert.IsTrue(Switch2InputCodec.TryDecodeCommon05(body,
            out var report));
        Assert.AreEqual((ushort)0xBBAA, report.Opaque08Raw);
        Assert.AreEqual((byte)0xCC, report.Opaque18Raw);
        Assert.AreEqual(0x0000655443322110UL,
            report.Opaque24To29Raw);
        Assert.AreEqual(0x00988776u, report.Opaque3CTo3ERaw);
    }

    [TestMethod]
    public void UsbDecoderAcceptsOnlyCorroboratedCommon05Report()
    {
        var packet = new byte[Switch2InputCodec.UsbPacketLength];

        for (int reportId = 0; reportId <= byte.MaxValue; reportId++)
        {
            packet[0] = (byte)reportId;
            bool decoded = Switch2InputCodec.TryDecodeUsb(packet,
                Switch2ControllerModel.ProController2, out _);
            Assert.AreEqual(reportId ==
                (byte)Switch2InputReportKind.Common05, decoded,
                $"Unexpected Pro USB report-ID policy for 0x{reportId:X2}.");
        }

        packet[0] = (byte)Switch2InputReportKind.Common05;
        Assert.IsFalse(Switch2InputCodec.TryDecodeUsb(packet,
            Switch2ControllerModel.Unknown, out _));
        Assert.IsFalse(Switch2InputCodec.TryDecodeUsb(packet,
            Switch2ControllerModel.JoyCon2Left, out _),
            "Wired/Charging-Grip Joy-Con 2 is not yet an audited USB model.");
    }

    [TestMethod]
    public void CanonicalProjectionMapsOnlyPinnedProButtonsAndRetainsUnknownBits()
    {
        const uint rawButtons = uint.MaxValue;
        const Switch2ProButton allKnownButtons =
            Switch2ProButton.FaceWest | Switch2ProButton.FaceNorth |
            Switch2ProButton.FaceSouth | Switch2ProButton.FaceEast |
            Switch2ProButton.RightShoulder | Switch2ProButton.RightTrigger |
            Switch2ProButton.Back | Switch2ProButton.Start |
            Switch2ProButton.RightStick | Switch2ProButton.LeftStick |
            Switch2ProButton.Guide | Switch2ProButton.Capture |
            Switch2ProButton.C | Switch2ProButton.DpadDown |
            Switch2ProButton.DpadUp | Switch2ProButton.DpadRight |
            Switch2ProButton.DpadLeft | Switch2ProButton.LeftShoulder |
            Switch2ProButton.LeftTrigger | Switch2ProButton.RightPaddle |
            Switch2ProButton.LeftPaddle;
        Assert.AreEqual((uint)allKnownButtons,
            Switch2ProUsbInputProjection.KnownButtonMask);
        Switch2ReplayEvent replayEvent = ReplayOne(CreateUsbFixture(
            BuildUsbPacket(counter: 7, buttons: rawButtons,
                leftX: 0, leftY: 0xFFF, rightX: 0x321, rightY: 0xCBA),
            firmware: "unknown", ticks: 123456));
        Switch2ProUsbProtocolIdentity identity = ResolveIdentity();

        Assert.IsTrue(Switch2ProUsbInputProjection.TryProject(replayEvent,
            identity, out var frame, out var failure), failure.ToString());
        Assert.AreEqual(Switch2ProUsbProtocolRevision.Common05Bcd0201,
            frame.ProtocolRevision);
        Assert.AreEqual((ushort)0x057E, frame.UsbVendorId);
        Assert.AreEqual((ushort)0x2069, frame.UsbProductId);
        Assert.AreEqual((ushort)0x0201, frame.UsbBcdDevice);
        Assert.AreEqual(Switch2FirmwareEvidence.UnknownNotQueried,
            frame.FirmwareEvidence);
        Assert.AreEqual(rawButtons, frame.RawButtonBits);
        Assert.AreEqual(rawButtons &
            ~Switch2ProUsbInputProjection.KnownButtonMask,
            frame.UnknownButtonBits);
        Assert.AreEqual(allKnownButtons, frame.Buttons);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.FaceWest) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.FaceNorth) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.FaceSouth) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.FaceEast) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.Capture) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.C) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.LeftPaddle) != 0);
        Assert.IsTrue((frame.Buttons & Switch2ProButton.RightPaddle) != 0);
        Assert.AreEqual((ushort)0, frame.LeftStickRaw.X);
        Assert.AreEqual((ushort)0xFFF, frame.LeftStickRaw.Y);
        Assert.AreEqual((ushort)0x321, frame.RightStickRaw.X);
        Assert.AreEqual((ushort)0xCBA, frame.RightStickRaw.Y);
    }

    [TestMethod]
    public void ProjectionRequiresExactIdentityModelTransportAndFirmwareEvidence()
    {
        Assert.IsFalse(Switch2ProUsbInputProjection.TryResolveIdentity(
            0x057F, 0x2069, 0x0201, out _));
        Assert.IsFalse(Switch2ProUsbInputProjection.TryResolveIdentity(
            0x057E, 0x2068, 0x0201, out _));
        Assert.IsFalse(Switch2ProUsbInputProjection.TryResolveIdentity(
            0x057E, 0x2069, 0x0200, out _));
        Switch2ProUsbProtocolIdentity identity = ResolveIdentity();

        Switch2ReplayEvent firmwareSpecific = ReplayOne(CreateUsbFixture(
            BuildUsbPacket(1, 0, 1, 2, 3, 4), firmware: "fw-2.0"));
        Assert.IsFalse(Switch2ProUsbInputProjection.TryProject(
            firmwareSpecific, identity, out _, out var firmwareFailure));
        Assert.AreEqual(
            Switch2ProUsbProjectionFailure.UnsupportedFirmwareEvidence,
            firmwareFailure);

        byte[] wrongModelPacket = BuildUsbPacket(1, 0, 1, 2, 3, 4);
        byte[] wrongModelBody = wrongModelPacket.AsSpan(1).ToArray();
        Assert.IsTrue(Switch2InputCodec.TryDecodeBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify,
            wrongModelBody, Switch2ControllerModel.JoyCon2Left,
            out var wrongModelReport));
        Switch2FixtureEnvelope wrongModelFixture = CreateUsbFixture(
            wrongModelPacket, firmware: "unknown",
            model: Switch2ControllerModel.JoyCon2Left);
        var wrongModel = new Switch2ReplayEvent(0, wrongModelFixture,
            wrongModelReport, false, 0, Switch2CounterSequenceKind.First);
        Assert.IsFalse(Switch2ProUsbInputProjection.TryProject(wrongModel,
            identity, out _, out var modelFailure));
        Assert.AreEqual(Switch2ProUsbProjectionFailure.UnsupportedModel,
            modelFailure);

        Switch2ReplayEvent ble = ReplayOne(CreateBleFixture(
            BuildCommonBody(1, 0, 1, 2, 3, 4)));
        Assert.IsFalse(Switch2ProUsbInputProjection.TryProject(ble,
            identity, out _, out var transportFailure));
        Assert.AreEqual(Switch2ProUsbProjectionFailure.UnsupportedTransport,
            transportFailure);

        Assert.IsFalse(Switch2ProUsbInputProjection.TryProject(default,
            identity, out _, out var missingFailure));
        Assert.AreEqual(Switch2ProUsbProjectionFailure.MissingReplayFixture,
            missingFailure);
    }

    [TestMethod]
    public void ProjectionKeepsHostAndDeviceTimeSemanticsSeparate()
    {
        byte[] first = BuildUsbPacket(0xFFFFFFFC, 0, 1, 2, 3, 4);
        byte[] second = BuildUsbPacket(0, 0, 5, 6, 7, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(1 + 0x2A, 4),
            0xA1B2C3D4);
        var fixtures = new[]
        {
            CreateUsbFixture(first, "unknown", ticks: 100),
            CreateUsbFixture(second, "unknown", ticks: 140),
        };
        var collector = new ReplayCollector();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            collector.OnEvent, out var replayFailure), replayFailure.Kind.ToString());
        Switch2ReplayEvent replayEvent = collector.Events[1];

        Assert.IsTrue(Switch2ProUsbInputProjection.TryProject(replayEvent,
            ResolveIdentity(), out var frame, out var projectionFailure),
            projectionFailure.ToString());
        Assert.AreEqual(140L, frame.HostTimestampTicks);
        Assert.AreEqual(TimeSpan.TicksPerSecond,
            frame.HostTimestampFrequency);
        Assert.AreEqual(0u, frame.DeviceCounterRaw);
        Assert.AreEqual(4u, frame.DeviceCounterDeltaRaw);
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            frame.CounterSequence);
        Assert.AreEqual(0xA1B2C3D4u, frame.RawReport.MotionTimestamp,
            "Motion timestamp stays raw and is not used as the host clock.");
    }

    [TestMethod]
    public void CommonEncodeAndCanonicalProjectionAllocateNoManagedMemory()
    {
        byte[] packet = BuildUsbPacket(1, 0x02084081,
            0x111, 0x222, 0xDDD, 0xEEE);
        byte[] encoded = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        Switch2ReplayEvent replayEvent = ReplayOne(CreateUsbFixture(packet,
            "unknown"));
        Switch2ProUsbProtocolIdentity identity = ResolveIdentity();

        for (int warmup = 0; warmup < 2000; warmup++)
        {
            Switch2InputCodec.TryDecodeUsb(packet,
                Switch2ControllerModel.ProController2, out var report);
            Switch2InputCodec.TryEncodeCommon05(report.Common, encoded);
            Switch2ProUsbInputProjection.TryProject(replayEvent, identity,
                out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            succeeded &= Switch2InputCodec.TryDecodeUsb(packet,
                Switch2ControllerModel.ProController2, out var report);
            succeeded &= Switch2InputCodec.TryEncodeCommon05(report.Common,
                encoded);
            succeeded &= Switch2ProUsbInputProjection.TryProject(replayEvent,
                identity, out _, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void CommonEncoderRejectsEveryNonExactDestinationLength()
    {
        Assert.IsTrue(Switch2InputCodec.TryDecodeCommon05(
            new byte[Switch2InputCodec.BluetoothLeBodyLength],
            out var report));

        for (int length = 0; length <= 80; length++)
        {
            bool encoded = Switch2InputCodec.TryEncodeCommon05(report,
                new byte[length]);
            Assert.AreEqual(length ==
                Switch2InputCodec.BluetoothLeBodyLength, encoded,
                $"Unexpected common encode result at length {length}.");
        }
    }

    private static Switch2ProUsbProtocolIdentity ResolveIdentity()
    {
        Assert.IsTrue(Switch2ProUsbInputProjection.TryResolveIdentity(
            Switch2ProUsbInputProjection.NintendoUsbVendorId,
            Switch2ProUsbInputProjection.ProController2ProductId,
            Switch2ProUsbInputProjection.AuditedBcdDevice,
            out var identity));
        return identity;
    }

    private static Switch2ReplayEvent ReplayOne(
        Switch2FixtureEnvelope fixture)
    {
        var collector = new ReplayCollector();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(new[] { fixture },
            collector.OnEvent, out var failure), failure.Kind.ToString());
        Assert.AreEqual(1, collector.Events.Count);
        return collector.Events[0];
    }

    private static Switch2FixtureEnvelope CreateUsbFixture(byte[] packet,
        string firmware, long ticks = 100,
        Switch2ControllerModel model =
            Switch2ControllerModel.ProController2) =>
        Switch2FixtureEnvelope.CreateUsb(StreamId,
            Switch2FixtureSource.Synthetic(FactId), model, firmware,
            generation: 1, pairEpoch: 0, ClockId,
            TimeSpan.TicksPerSecond, ticks, packet);

    private static Switch2FixtureEnvelope CreateBleFixture(byte[] body) =>
        Switch2FixtureEnvelope.CreateBluetoothLe(StreamId,
            Switch2FixtureSource.Synthetic(FactId),
            Switch2ControllerModel.ProController2, "unknown", generation: 1,
            pairEpoch: 0, ClockId, TimeSpan.TicksPerSecond, 100,
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify, body);

    private static byte[] BuildUsbPacket(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY)
    {
        var packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BuildCommonBody(counter, buttons, leftX, leftY, rightX, rightY)
            .CopyTo(packet, 1);
        return packet;
    }

    private static byte[] BuildCommonBody(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x00, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x04, 4), buttons);
        PackStick(body, 0x0A, leftX, leftY);
        PackStick(body, 0x0D, rightX, rightY);
        return body;
    }

    private static void PackStick(byte[] destination, int offset,
        ushort x, ushort y)
    {
        Assert.IsTrue(x <= 0x0FFF && y <= 0x0FFF);
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private static void WriteUInt16(byte[] destination, int offset,
        ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(
        destination.AsSpan(offset, 2), value);

    private sealed class ReplayCollector
    {
        public List<Switch2ReplayEvent> Events { get; } = new();

        public void OnEvent(in Switch2ReplayEvent replayEvent) =>
            Events.Add(replayEvent);
    }
}
