using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2IdentificationHapticTests
{
    [TestMethod]
    public void PatternMatchesPinnedSwitch2ConnectPingExactly()
    {
        Assert.AreEqual(100,
            Switch2IdentificationHaptic.PulseDurationMilliseconds);
        Assert.AreEqual(100,
            Switch2IdentificationHaptic.PulseGapMilliseconds);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 800, 0x1e1, 800),
            Switch2IdentificationHaptic.SourcePulseSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 1_023, 0x1e1, 800),
            Switch2IdentificationHaptic.ProPulseSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 696, 0x1e1, 327),
            Switch2IdentificationHaptic.JoyConPulseSubframe);
        Assert.AreEqual(Switch2IdentificationHaptic.ProPulseSubframe,
            Switch2IdentificationHaptic.ProPulseGroup.First);
        Assert.AreEqual(Switch2IdentificationHaptic.JoyConPulseSubframe,
            Switch2IdentificationHaptic.JoyConPulseGroup.Second);
        Assert.AreEqual(Switch2IdentificationHaptic.JoyConPulseSubframe,
            Switch2IdentificationHaptic.JoyConPulseGroup.Third);
        Assert.AreEqual(Scale(1_023),
            Switch2IdentificationHaptic.ProMarker.BodyLow);
        Assert.AreEqual(Scale(800),
            Switch2IdentificationHaptic.ProMarker.BodyHigh);
        Assert.AreEqual(Scale(696),
            Switch2IdentificationHaptic.JoyConMarker.BodyLow);
        Assert.AreEqual(Scale(327),
            Switch2IdentificationHaptic.JoyConMarker.BodyHigh);
        Assert.IsFalse(Switch2IdentificationHaptic.ProMarker.IsNeutral);
        Assert.IsFalse(Switch2IdentificationHaptic.JoyConMarker.IsNeutral);
    }

    private static ushort Scale(ushort value) => checked((ushort)(
        ((uint)value * ushort.MaxValue +
            Switch2HdRumbleSubframe.MaximumCode / 2U) /
        Switch2HdRumbleSubframe.MaximumCode));
}
