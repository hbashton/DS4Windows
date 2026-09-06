using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RumbleDelayTests
{
    [TestMethod]
    public void BoundsNormalizeAndProfileRoundTrips()
    {
        Assert.AreEqual(0, Switch2RumbleDelay.Normalize(-1));
        Assert.AreEqual(0, Switch2RumbleDelay.Normalize(0));
        Assert.AreEqual(9_999, Switch2RumbleDelay.Normalize(9_999));
        Assert.AreEqual(0, Switch2RumbleDelay.Normalize(10_000));

        BackingStore store = new();
        ProfileDTO profile = new() { DeviceIndex = 0 };
        Assert.AreEqual(0, profile.Switch2RumbleDelayMilliseconds);
        profile.Switch2RumbleDelayMilliseconds = 175;
        profile.MapTo(store);
        Assert.AreEqual(175, store.switch2RumbleDelayMilliseconds[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(175, output.Switch2RumbleDelayMilliseconds);
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
            "<Switch2RumbleDelayMilliseconds>175</Switch2RumbleDelayMilliseconds>");

        using var reader = new StringReader(
            "<DS4Windows config_version=\"5\" />");
        var legacy = (ProfileDTO)serializer.Deserialize(reader);
        Assert.AreEqual(0, legacy.Switch2RumbleDelayMilliseconds);

        profile.Switch2RumbleDelayMilliseconds = 10_000;
        profile.MapTo(store);
        Assert.AreEqual(0, store.switch2RumbleDelayMilliseconds[0]);
    }
}
