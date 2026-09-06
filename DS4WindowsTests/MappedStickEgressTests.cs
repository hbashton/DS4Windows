using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class MappedStickEgressTests
{
    [TestMethod]
    public void EveryLegacyAxisKeepsItsExactHistoricalPacketValue()
    {
        var state = new DS4State();
        for (int raw = 0; raw < 256; raw++)
        {
            state.LX = state.LY = state.RX = state.RY = (byte)raw;
            var xbox = ViiperStatePacketBuilder.BuildXbox360State(state, -1);
            var one = XboxOneEgressState.FromLegacyMappedState(state, -1);
            var sw = ViiperStatePacketBuilder.BuildSwitch2State(state, -1);
            Assert.AreEqual(LegacyXbox(raw, false), xbox.LeftStickX);
            Assert.AreEqual(LegacyXbox(raw, true), xbox.LeftStickY);
            Assert.AreEqual(xbox.LeftStickX, xbox.RightStickX);
            Assert.AreEqual(xbox.LeftStickY, xbox.RightStickY);
            Assert.AreEqual(xbox.LeftStickX, one.LeftStickX);
            Assert.AreEqual(xbox.LeftStickY, one.LeftStickY);
            Assert.AreEqual(xbox.RightStickX, one.RightStickX);
            Assert.AreEqual(xbox.RightStickY, one.RightStickY);
            ushort expected = (ushort)(raw <= 128 ? raw * 16 : 2048 + ((raw - 128) * 2047 + 63) / 127);
            Assert.AreEqual(expected, sw.LeftStickX);
            Assert.AreEqual(expected, sw.LeftStickY);
            Assert.AreEqual(expected, sw.RightStickX);
            Assert.AreEqual(expected, sw.RightStickY);
        }
    }

    [TestMethod]
    public void EverySignedPositionReachesBothXboxWireFormats()
    {
        var state = new DS4State();
        Span<byte> xboxBytes = stackalloc byte[Xbox360EgressState.WireSize];
        Span<byte> oneBytes = stackalloc byte[XboxOneEgressState.WireSize];
        for (int raw = short.MinValue; raw <= short.MaxValue; raw++)
        {
            state.LXAxis = state.LYAxis = state.RXAxis = state.RYAxis = DS4MappedStickAxis.FromSigned((short)raw);
            ViiperStatePacketBuilder.BuildXbox360State(state, -1).BuildInto(xboxBytes);
            XboxOneEgressState.FromLegacyMappedState(state, -1).BuildInto(oneBytes);
            short flipped = (short)(raw < 0 ? ((-(long)raw * 32767 + 16384) / 32768) :
                -(((long)raw * 32768 + 16383) / 32767));
            Assert.AreEqual((short)raw, BinaryPrimitives.ReadInt16LittleEndian(xboxBytes[6..]));
            Assert.AreEqual(flipped, BinaryPrimitives.ReadInt16LittleEndian(xboxBytes[8..]));
            Assert.AreEqual((short)raw, BinaryPrimitives.ReadInt16LittleEndian(xboxBytes[10..]));
            Assert.AreEqual(flipped, BinaryPrimitives.ReadInt16LittleEndian(xboxBytes[12..]));
            Assert.AreEqual((short)raw, BinaryPrimitives.ReadInt16LittleEndian(oneBytes[12..]));
            Assert.AreEqual(flipped, BinaryPrimitives.ReadInt16LittleEndian(oneBytes[14..]));
            Assert.AreEqual((short)raw, BinaryPrimitives.ReadInt16LittleEndian(oneBytes[16..]));
            Assert.AreEqual(flipped, BinaryPrimitives.ReadInt16LittleEndian(oneBytes[18..]));
        }
    }

    [TestMethod]
    public void AllTwelveBitPositionsSurviveCanonicalProfileAndFieldMappingIntoWire()
    {
        var source = new DS4State();
        var mapped = new DS4State();
        var fields = new DS4StateFieldMapping();
        var exposed = new DS4StateExposed(mapped);
        var neutralOsc = new DS4State();
        var debouncer = new DS4WinWPF.DS4Control.Debouncer(TimeSpan.FromMilliseconds(1));
        debouncer.AddDebouncer(nameof(DS4State.Cross));
        var settings = DS4StickProfileTransformTests.NoopSettings(true);
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0, 0, 1, 1, BezierCurve.AxisType.LSRS));
        Span<byte> packet = stackalloc byte[Switch2EgressState.WireSize];
        for (ushort raw = 0; raw < 4096; raw++)
        {
            Assert.IsTrue(Switch2ProfileAxisProjection.TryMapSigned(raw, raw - 2048, 2048, 2047, false, out short signed));
            source.LXAxis = source.LYAxis = source.RXAxis = source.RYAxis = DS4MappedStickAxis.FromSigned(signed);
            source.CopyTo(mapped);
            debouncer.ProcessInput(mapped).CopyTo(mapped);
            DS4StickProfileTransform.ApplyDeadzoneAndOuter(settings, ref mapped.LXAxis, ref mapped.LYAxis, ref mapped.OutputLSOuter);
            DS4StickProfileTransform.ApplyOutputCurve(settings, 6, curve, ref mapped.LXAxis, ref mapped.LYAxis);
            fields.PopulateFieldMapping(mapped, exposed, null);
            fields.PopulateState(mapped);
            ControlService.OSCPostMappingStep(mapped, neutralOsc);
            ViiperStatePacketBuilder.BuildSwitch2State(mapped, -1).BuildInto(packet);
            for (int offset = 4; offset <= 10; offset += 2)
                Assert.AreEqual(raw, BinaryPrimitives.ReadUInt16LittleEndian(packet[offset..]));
            Assert.AreEqual(signed, XboxOneEgressState.FromLegacyMappedState(mapped, -1).LeftStickX);
        }
    }

    [TestMethod]
    public void SameByteFreshMotionIsDistinctAndExplicitOverrideCannotBeRestoredFromRawExtras()
    {
        var source = new DS4State { LXAxis = DS4MappedStickAxis.FromSigned(16),
            Switch2RawInputStatus = new() {
                IsValid = true, ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
                Transport = Switch2Transport.Usb,
                ProtocolRevision = Switch2InputProtocolRevision.ProUsbCommon05Bcd0201,
                DeviceGeneration = 1, TransportGeneration = 1,
                CompletionTimestampQpc = 100, QpcFrequency = 10000000, DeviceCounterRaw = 1,
                LeftStickXRaw = 2049, LeftStickYRaw = 2048,
                RightStickXRaw = 2048, RightStickYRaw = 2048, LeftStickX = 16 } };
        var first = XboxOneEgressState.FromLegacyMappedState(source, -1);
        source.LXAxis = DS4MappedStickAxis.FromSigned(32);
        source.Switch2RawInputStatus.LeftStickX = 32;
        source.Switch2RawInputStatus.LeftStickXRaw = 2050;
        source.Switch2RawInputStatus.DeviceCounterRaw++;
        source.Switch2RawInputStatus.CompletionTimestampQpc++;
        Assert.AreEqual((byte)128, source.LX);
        var second = XboxOneEgressState.FromLegacyMappedState(source, -1);
        Assert.AreNotEqual(first, second, "Change-only presentation must see motion within one legacy byte.");
        Assert.AreEqual((short)16, first.LeftStickX);
        Assert.AreEqual((short)32, second.LeftStickX);
        var mapped = new DS4State(source);
        mapped.LX = 128;
        source.CopyExtrasTo(mapped);
        Assert.AreEqual((short)0, XboxOneEgressState.FromLegacyMappedState(mapped, -1).LeftStickX);
        Assert.AreEqual((ushort)2048, ViiperStatePacketBuilder.BuildSwitch2State(mapped, -1).LeftStickX);
        mapped.LX = 255; // Existing macro/button output is deliberately byte-owned.
        source.CopyExtrasTo(mapped);
        Assert.AreEqual(short.MaxValue, XboxOneEgressState.FromLegacyMappedState(mapped, -1).LeftStickX);
    }

    [TestMethod]
    public void SteeringOverridesFinalTypedCoordinatesForEveryStickAxis()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        var prior = Global.SASteeringWheelEmulationAxis[slot];
        var state = new DS4State { LXAxis = DS4MappedStickAxis.FromSigned(16), LYAxis = DS4MappedStickAxis.FromSigned(32),
            RXAxis = DS4MappedStickAxis.FromSigned(48), RYAxis = DS4MappedStickAxis.FromSigned(64), SASteeringWheelEmulationUnit = 1234 };
        try
        {
            Global.SASteeringWheelEmulationAxis[slot] = SASteeringWheelEmulationAxisType.None;
            var oneBaseline = XboxOneEgressState.FromLegacyMappedState(state, slot);
            var switchBaseline = ViiperStatePacketBuilder.BuildSwitch2State(state, slot);
            var baselineXboxAxes = new[] { oneBaseline.LeftStickX, oneBaseline.LeftStickY, oneBaseline.RightStickX, oneBaseline.RightStickY };
            var baselineSwitchAxes = new[] { switchBaseline.LeftStickX, switchBaseline.LeftStickY, switchBaseline.RightStickX, switchBaseline.RightStickY };
            for (int axis = 1; axis <= 4; axis++)
            {
                Global.SASteeringWheelEmulationAxis[slot] = (SASteeringWheelEmulationAxisType)axis;
                var one = XboxOneEgressState.FromLegacyMappedState(state, slot);
                var sw = ViiperStatePacketBuilder.BuildSwitch2State(state, slot);
                var xboxAxes = new[] { one.LeftStickX, one.LeftStickY, one.RightStickX, one.RightStickY };
                var switchAxes = new[] { sw.LeftStickX, sw.LeftStickY, sw.RightStickX, sw.RightStickY };
                for (int check = 0; check < 4; check++)
                {
                    Assert.AreEqual(check == axis - 1 ? (short)1234 : baselineXboxAxes[check], xboxAxes[check]);
                    Assert.AreEqual(check == axis - 1 ? (ushort)1234 : baselineSwitchAxes[check], switchAxes[check]);
                }
            }
        }
        finally { Global.SASteeringWheelEmulationAxis[slot] = prior; }
    }

    [TestMethod]
    public void WarmTypedEgressBuildAndEncodingAllocateNothing()
    {
        var state = new DS4State();
        long checksum = 0;
        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.IsTrue(checksum > 0);
        void Step(int value)
        {
            state.LXAxis = DS4MappedStickAxis.FromSigned((short)value);
            Span<byte> packet = stackalloc byte[24];
            XboxOneEgressState.FromLegacyMappedState(state, -1).BuildInto(packet);
            checksum += packet[12];
            ViiperStatePacketBuilder.BuildSwitch2State(state, -1).BuildInto(packet);
            checksum += packet[4];
            ViiperStatePacketBuilder.BuildXbox360State(state, -1).BuildInto(packet[..20]);
            checksum += packet[6];
        }
    }

    // Frozen pre-migration output conversion, including historical float rounding.
    private static short LegacyXbox(int value, bool flip)
    {
        value -= 128;
        float temp = value * (value >= 0 ? 1.0f / 127 : 1.0f / 128);
        if (flip) temp = -temp;
        return unchecked((short)(((temp + 1.0f) * 0.5f) * 65535 + -32768));
    }
}
