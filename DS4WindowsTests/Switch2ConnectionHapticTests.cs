using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ConnectionHapticTests
{
    [TestMethod]
    public void PatternMatchesPinnedSwitch2ConnectSignatureExactly()
    {
        Assert.AreEqual(1_200,
            Switch2ConnectionHaptic.UsbInitialDelayMilliseconds);
        Assert.AreEqual(200,
            Switch2ConnectionHaptic.BassDurationMilliseconds);
        Assert.AreEqual(10,
            Switch2ConnectionHaptic.NeutralGapMilliseconds);
        Assert.AreEqual(1_000,
            Switch2ConnectionHaptic.SharpClickDurationMilliseconds);

        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 0, 0x1e1, 0),
            Switch2ConnectionHaptic.NeutralSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x060, 0x350, 0x0c0, 0x250),
            Switch2ConnectionHaptic.SourceBassSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 0x030, 0x1e2, 0x300),
            Switch2ConnectionHaptic.SourceSharpClickSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x060, 1_023, 0x0c0, 592),
            Switch2ConnectionHaptic.ProBassSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x060, 759, 0x0c0, 264),
            Switch2ConnectionHaptic.JoyConBassSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 62, 0x1e2, 768),
            Switch2ConnectionHaptic.ProSharpClickSubframe);
        Assert.AreEqual(new Switch2HdRumbleSubframe(
            0x0e1, 62, 0x1e2, 460),
            Switch2ConnectionHaptic.JoyConSharpClickSubframe);
        Assert.AreEqual(Switch2ConnectionHaptic.NeutralSubframe,
            Switch2ConnectionHaptic.ProBassGroup.Second);
        Assert.AreEqual(Switch2ConnectionHaptic.NeutralSubframe,
            Switch2ConnectionHaptic.JoyConBassGroup.Third);
        Assert.AreEqual(Switch2ConnectionHaptic.NeutralSubframe,
            Switch2ConnectionHaptic.ProSharpClickGroup.Second);
        Assert.AreEqual(Switch2ConnectionHaptic.NeutralSubframe,
            Switch2ConnectionHaptic.JoyConSharpClickGroup.Third);
    }

    [TestMethod]
    public void CanonicalMarkersPreserveBandOrderingAndRemainNonNeutral()
    {
        Assert.AreEqual(Scale(1_023),
            Switch2ConnectionHaptic.ProBassMarker.BodyLow);
        Assert.AreEqual(Scale(592),
            Switch2ConnectionHaptic.ProBassMarker.BodyHigh);
        Assert.AreEqual(Scale(759),
            Switch2ConnectionHaptic.JoyConBassMarker.BodyLow);
        Assert.AreEqual(Scale(264),
            Switch2ConnectionHaptic.JoyConBassMarker.BodyHigh);
        Assert.AreEqual(Scale(62),
            Switch2ConnectionHaptic.ProSharpClickMarker.BodyLow);
        Assert.AreEqual(Scale(768),
            Switch2ConnectionHaptic.ProSharpClickMarker.BodyHigh);
        Assert.AreEqual(Scale(460),
            Switch2ConnectionHaptic.JoyConSharpClickMarker.BodyHigh);
        Assert.IsFalse(Switch2ConnectionHaptic.ProBassMarker.IsNeutral);
        Assert.IsFalse(Switch2ConnectionHaptic.
            JoyConSharpClickMarker.IsNeutral);
    }

    [TestMethod]
    public void ProfileDefaultsOnAndRoundTripsExplicitOptOut()
    {
        BackingStore store = new();
        ProfileDTO profile = new() { DeviceIndex = 0 };
        Assert.IsTrue(profile.Switch2ConnectionHapticEnabled);
        profile.MapTo(store);
        Assert.IsTrue(store.switch2ConnectionHapticEnabled[0]);

        profile.Switch2ConnectionHapticEnabled = false;
        profile.MapTo(store);
        Assert.IsFalse(store.switch2ConnectionHapticEnabled[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsFalse(output.Switch2ConnectionHapticEnabled);
        output.SerializeAppAttrs = false;
        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2ConnectionHapticEnabled>false</Switch2ConnectionHapticEnabled>");

        using var reader = new StringReader(
            "<DS4Windows config_version=\"5\" />");
        var legacy = (ProfileDTO)serializer.Deserialize(reader);
        Assert.IsTrue(legacy.Switch2ConnectionHapticEnabled);
    }

    private static ushort Scale(ushort value) => checked((ushort)(
        ((uint)value * ushort.MaxValue +
            Switch2HdRumbleSubframe.MaximumCode / 2U) /
        Switch2HdRumbleSubframe.MaximumCode));
}
