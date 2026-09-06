using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbCounterRolloverTests
{
    // Only these counter values are derived from the passive capture. All
    // buttons/sticks and timestamps below are synthetic test data. No opaque
    // hardware bytes or device identifiers are included in this fixture.
    private static readonly uint[] CapturedCounters = { 1_431_649, 1_431_653, 2, 6, 10 };

    [TestMethod]
    public void UsbCounterDiscontinuityPreservesButtonReleaseAndNextBaseline()
    {
        var session = CreateSession(usb: true);
        var state = new DS4State();
        for (int index = 0; index < CapturedCounters.Length; index++)
        {
            byte[] packet = Packet(CapturedCounters[index], index < 2);
            Assert.IsTrue(session.TryProcess(session.Descriptor, packet,
                100_000 + index * 40_000, out var frame, out var failure), failure.ToString());
            Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(frame, out var mapped,
                out var mappingFailure), mappingFailure.ToString());
            Assert.IsTrue(mapped.TryWriteLegacyState(state));
            Assert.AreEqual(index < 2, state.Square, "The release at counter rollover must be published.");
            Assert.AreEqual(CapturedCounters[index], frame.DeviceCounterRaw);
            Assert.AreEqual(7UL, frame.DeviceGeneration);
            Assert.AreEqual(9UL, frame.TransportGeneration);
            if (index == 2)
            {
                Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder, frame.CounterSequence);
                Assert.AreEqual(unchecked(2u - 1_431_653u), frame.CounterDeltaRaw,
                    "Preserve raw diagnostic evidence; do not invent a counter modulus.");
            }
            else if (index > 0)
            {
                Assert.AreEqual(Switch2CounterSequenceKind.Forward, frame.CounterSequence);
                Assert.AreEqual(4u, frame.CounterDeltaRaw);
            }
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReplayAndLiveSessionAgreeAcrossCounterDiscontinuity(bool usb)
    {
        var session = CreateSession(usb);
        var expected = new List<(Switch2CounterSequenceKind Sequence, uint Delta)>();
        var fixtures = new List<Switch2FixtureEnvelope>();
        var source = Switch2FixtureSource.Synthetic("fact-13afc0c695e04b70aa4465d063b2e1fe");
        const string stream = "stream-cfd898785684447d958d520911f9ec05";
        const string clock = "clock-abab6ae3235b4bcb89b1f2ae1fa8fd59";
        for (int index = 0; index < CapturedCounters.Length; index++)
        {
            byte[] packet = Packet(CapturedCounters[index], false);
            ReadOnlySpan<byte> observation = usb ? packet : packet.AsSpan(1);
            Assert.IsTrue(session.TryProcess(session.Descriptor, observation, index,
                out var frame, out _));
            expected.Add((frame.CounterSequence, frame.CounterDeltaRaw));
            fixtures.Add(usb ? Switch2FixtureEnvelope.CreateUsb(stream, source,
                Switch2ControllerModel.ProController2, "unknown", 7, 0, clock,
                10_000_000, index, packet) : Switch2FixtureEnvelope.CreateBluetoothLe(stream,
                source, Switch2ControllerModel.ProController2, "unknown", 7, 0, clock,
                10_000_000, index, Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid,
                Switch2GattProperty.Read | Switch2GattProperty.Notify, packet.AsSpan(1)));
        }
        var actual = new List<(Switch2CounterSequenceKind Sequence, uint Delta)>();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            (in Switch2ReplayEvent replay) => actual.Add((replay.CounterSequence, replay.CounterDelta)),
            out var failure), failure.Kind.ToString());
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(Switch2CounterSequenceKind.Forward, actual[3].Sequence);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CounterDiscontinuityCannotBypassHostClockOrLifetimeFences(bool usb)
    {
        var session = CreateSession(usb);
        byte[] high = Packet(1_431_653, true);
        byte[] reset = Packet(2, false);
        int offset = usb ? 0 : 1;
        Assert.IsTrue(session.TryProcess(session.Descriptor, high.AsSpan(offset),
            100, out _, out _));
        Assert.IsFalse(session.TryProcess(session.Descriptor, reset.AsSpan(offset),
            99, out _, out var clockFailure));
        Assert.AreEqual(Switch2InputSessionFailure.TimestampRegression, clockFailure);
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(session.Descriptor.Identity,
            7, 8, 10_000_000, out var staleTransport));
        Assert.IsFalse(session.TryProcess(staleTransport, reset.AsSpan(offset),
            101, out _, out var lifetimeFailure));
        Assert.AreEqual(Switch2InputSessionFailure.DescriptorMismatch, lifetimeFailure);
        Assert.IsTrue(session.TryProcess(session.Descriptor, high.AsSpan(offset),
            102, out var unchanged, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.Duplicate, unchanged.CounterSequence,
            "Rejected observations must not mutate the accepted baseline.");
    }

    [TestMethod]
    public void BluetoothObservedCounterResetPublishesReleaseAndAdvancesBaseline()
    {
        var session = CreateSession(usb: false);
        // Only this counter pair and host completion times come from the
        // private b65 snapshot. Buttons and the successor report are synthetic.
        Assert.IsTrue(session.TryProcess(session.Descriptor, Packet(1_431_640, true).AsSpan(1),
            2_200_529_540_394, out var held, out _));
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(held, out var mappedHeld, out _));
        var state = new DS4State();
        Assert.IsTrue(mappedHeld.TryWriteLegacyState(state));
        Assert.IsTrue(state.Square);
        Assert.IsTrue(session.TryProcess(session.Descriptor, Packet(1, false).AsSpan(1),
            2_200_529_689_392, out var backward, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder, backward.CounterSequence);
        Assert.AreEqual(unchecked(1u - 1_431_640u), backward.CounterDeltaRaw);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(backward, out var mappedRelease,
            out var failure), failure.ToString());
        Assert.IsTrue(mappedRelease.TryWriteLegacyState(state));
        Assert.IsFalse(state.Square);
        Assert.IsTrue(session.TryProcess(session.Descriptor, Packet(16, false).AsSpan(1),
            2_200_529_839_392, out var forward, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.Forward, forward.CounterSequence);
        Assert.AreEqual(15u, forward.CounterDeltaRaw);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void ProCounterPolicyDoesNotBroadenJoyConAdmission(Switch2ControllerModel model)
    {
        Assert.IsFalse(Switch2CounterSequence.UsesArrivalOrdering(model,
            Switch2Transport.BluetoothLe, Switch2InputReportKind.Common05));
        Assert.IsFalse(Switch2CounterSequence.UsesArrivalOrdering(model,
            Switch2Transport.Usb, Switch2InputReportKind.Common05));
    }

    private static Switch2InputSession CreateSession(bool usb)
    {
        Switch2InputProtocolIdentity identity;
        bool created = usb ? Switch2InputProtocolIdentity.TryCreateProController2Usb(
            0x057E, 0x2069, 0x0201, out identity) : Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid, Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify,
            Switch2ControllerModel.ProController2, out identity);
        Assert.IsTrue(created);
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, 7, 9,
            10_000_000, out var descriptor));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, 7, out var calibration));
        return new Switch2InputSession(descriptor, calibration);
    }

    private static byte[] Packet(uint counter, bool pressed)
    {
        byte[] packet = new byte[64];
        packet[0] = 0x05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5),
            pressed ? (uint)Switch2ProButton.FaceWest : 0u);
        packet[12] = packet[15] = 0x08;
        packet[13] = packet[16] = 0x80;
        return packet;
    }
}
