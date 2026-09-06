using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2CemuhookYawSensitivityTests
{
    [TestMethod]
    public void SourceEstablishedFiveLevelsHaveExactMultipliers()
    {
        double[] expected =
        [
            1.0,
            13.0 / 12.0,
            7.0 / 6.0,
            5.0 / 4.0,
            4.0 / 3.0,
        ];

        for (int level = Switch2CemuhookYawSensitivity.MinimumLevel;
             level <= Switch2CemuhookYawSensitivity.MaximumLevel; level++)
        {
            Assert.AreEqual(expected[level - 1],
                Switch2CemuhookYawSensitivity.MultiplierForLevel(level),
                1.0e-15);
        }
    }

    [TestMethod]
    public void LevelOnePreservesCanonicalYawBitForBit()
    {
        double yaw = -123.456789;

        double output = Switch2CemuhookYawSensitivity.ApplyYaw(yaw, 1);

        Assert.AreEqual(BitConverter.DoubleToInt64Bits(yaw),
            BitConverter.DoubleToInt64Bits(output));
    }

    [TestMethod]
    public void InvalidLevelsAndNonFiniteValuesFailSafe()
    {
        Assert.AreEqual(Switch2CemuhookYawSensitivity.DefaultLevel,
            Switch2CemuhookYawSensitivity.NormalizeLevel(0));
        Assert.AreEqual(Switch2CemuhookYawSensitivity.DefaultLevel,
            Switch2CemuhookYawSensitivity.NormalizeLevel(6));
        Assert.AreEqual(90.0,
            Switch2CemuhookYawSensitivity.ApplyYaw(90.0, -1));
        Assert.IsTrue(double.IsNaN(
            Switch2CemuhookYawSensitivity.ApplyYaw(double.NaN, 5)));
        Assert.AreEqual(double.PositiveInfinity,
            Switch2CemuhookYawSensitivity.ApplyYaw(
                double.PositiveInfinity, 5));
        Assert.AreEqual(double.MaxValue,
            Switch2CemuhookYawSensitivity.ApplyYaw(double.MaxValue, 5));
    }

    [TestMethod]
    public void WarmProjectionDoesNotAllocate()
    {
        _ = Switch2CemuhookYawSensitivity.ApplyYaw(90.0, 5);
        long before = GC.GetAllocatedBytesForCurrentThread();

        double value = 0.0;
        for (int i = 0; i < 10_000; i++)
        {
            value += Switch2CemuhookYawSensitivity.ApplyYaw(i, 5);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreNotEqual(0.0, value);
        Assert.AreEqual(0L, after - before);
    }

    [TestMethod]
    public void ProfileDefaultsToOneAndRoundTripsLevelFive()
    {
        BackingStore store = new();
        ProfileDTO profile = new() { DeviceIndex = 0 };
        Assert.AreEqual(1, profile.Switch2CemuhookYawSensitivity);

        profile.MapTo(store);
        Assert.AreEqual(1, store.switch2CemuhookYawSensitivity[0]);

        profile.Switch2CemuhookYawSensitivity = 5;
        profile.MapTo(store);
        Assert.AreEqual(5, store.switch2CemuhookYawSensitivity[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(5, output.Switch2CemuhookYawSensitivity);

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
            "<Switch2CemuhookYawSensitivity>5</Switch2CemuhookYawSensitivity>");

        using var reader = new StringReader(
            "<DS4Windows config_version=\"5\" />");
        var legacy = (ProfileDTO)serializer.Deserialize(reader);
        Assert.AreEqual(1, legacy.Switch2CemuhookYawSensitivity);
    }

    [TestMethod]
    public void ProfileRejectsOutOfRangeLevelsAtStoreBoundary()
    {
        BackingStore store = new();
        ProfileDTO profile = new()
        {
            DeviceIndex = 0,
            Switch2CemuhookYawSensitivity = int.MaxValue,
        };

        profile.MapTo(store);

        Assert.AreEqual(1, store.switch2CemuhookYawSensitivity[0]);
    }
}
