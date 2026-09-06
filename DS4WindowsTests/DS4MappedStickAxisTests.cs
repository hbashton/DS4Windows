using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class DS4MappedStickAxisTests
{
    [TestMethod]
    [DoNotParallelize]
    public void ExistingDriftCalibrationPreservesFractionalValuesAndLegacyClamping()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        sbyte leftX = Global.LeftStickDriftXAxis[slot], leftY = Global.LeftStickDriftYAxis[slot];
        sbyte rightX = Global.RightStickDriftXAxis[slot], rightY = Global.RightStickDriftYAxis[slot];
        try
        {
            foreach (sbyte drift in new sbyte[] { 0, 1, -1, 127, -127 })
            {
                Global.LeftStickDriftXAxis[slot] = Global.RightStickDriftXAxis[slot] = drift;
                Global.LeftStickDriftYAxis[slot] = Global.RightStickDriftYAxis[slot] = (sbyte)-drift;
                var precise = new DS4State {
                    LXAxis = DS4MappedStickAxis.FromSigned(16), LYAxis = DS4MappedStickAxis.FromSigned(-32),
                    RXAxis = DS4MappedStickAxis.FromSigned(48), RYAxis = DS4MappedStickAxis.FromSigned(-64) };
                double lx = precise.LXAxis.ProfileCoordinate, ly = precise.LYAxis.ProfileCoordinate;
                double rx = precise.RXAxis.ProfileCoordinate, ry = precise.RYAxis.ProfileCoordinate;
                Assert.AreSame(precise, Mapping.ApplyStickCalibration(slot, precise));
                Assert.AreEqual(Math.Clamp(lx - drift, 0, 255), precise.LXAxis.ProfileCoordinate);
                Assert.AreEqual(Math.Clamp(ly + drift, 0, 255), precise.LYAxis.ProfileCoordinate);
                Assert.AreEqual(Math.Clamp(rx - drift, 0, 255), precise.RXAxis.ProfileCoordinate);
                Assert.AreEqual(Math.Clamp(ry + drift, 0, 255), precise.RYAxis.ProfileCoordinate);
                Assert.IsTrue(precise.LXAxis.IsHighResolution);
                Assert.IsTrue(precise.RYAxis.IsHighResolution);
                for (int value = 0; value < 256; value++)
                {
                    var legacy = new DS4State { LX = (byte)value, LY = (byte)value, RX = (byte)value, RY = (byte)value };
                    Mapping.ApplyStickCalibration(slot, legacy);
                    Assert.AreEqual((byte)Math.Clamp(value - drift, 0, 255), legacy.LX);
                    Assert.AreEqual((byte)Math.Clamp(value + drift, 0, 255), legacy.LY);
                    Assert.AreEqual(legacy.LX, legacy.RX);
                    Assert.AreEqual(legacy.LY, legacy.RY);
                    Assert.IsFalse(legacy.LXAxis.IsHighResolution);
                }
            }
        }
        finally
        {
            Global.LeftStickDriftXAxis[slot] = leftX;
            Global.LeftStickDriftYAxis[slot] = leftY;
            Global.RightStickDriftXAxis[slot] = rightX;
            Global.RightStickDriftYAxis[slot] = rightY;
        }
    }

    [TestMethod]
    public void SharedRotationKeepsFractionalInputAndLegacyResults()
    {
        var precise = new DS4State {
            LXAxis = DS4MappedStickAxis.FromSigned(16), LYAxis = DS4MappedStickAxis.FromSigned(-32),
            RXAxis = DS4MappedStickAxis.FromSigned(48), RYAxis = DS4MappedStickAxis.FromSigned(-64) };
        double x = precise.LXAxis.ProfileCoordinate - 128;
        double y = precise.LYAxis.ProfileCoordinate - 128;
        double rx = precise.RXAxis.ProfileCoordinate - 128;
        double ry = precise.RYAxis.ProfileCoordinate - 128;
        precise.rotateLSCoordinates(Math.PI / 2);
        precise.rotateRSCoordinates(-Math.PI / 2);
        Assert.AreEqual(-y + 128, precise.LXAxis.ProfileCoordinate, 1e-10);
        Assert.AreEqual(x + 128, precise.LYAxis.ProfileCoordinate, 1e-10);
        Assert.AreEqual(ry + 128, precise.RXAxis.ProfileCoordinate, 1e-10);
        Assert.AreEqual(-rx + 128, precise.RYAxis.ProfileCoordinate, 1e-10);
        Assert.IsTrue(precise.LXAxis.IsHighResolution);
        Assert.IsTrue(precise.RXAxis.IsHighResolution);

        foreach (double angle in new[] { 0.0, -2.1, Math.PI / 2, 0.17, Math.PI })
        for (int sample = 0; sample < 256; sample++)
        {
            var legacy = new DS4State { LX = (byte)sample, LY = (byte)(255 - sample),
                RX = (byte)(255 - sample), RY = (byte)sample };
            byte expectedX = (byte)(Global.Clamp(-128, (sample - 128) * Math.Cos(angle) -
                (127 - sample) * Math.Sin(angle), 127) + 128);
            byte expectedY = (byte)(Global.Clamp(-128, (sample - 128) * Math.Sin(angle) +
                (127 - sample) * Math.Cos(angle), 127) + 128);
            legacy.rotateLSCoordinates(angle);
            legacy.rotateRSCoordinates(angle);
            Assert.AreEqual(expectedX, legacy.LX);
            Assert.AreEqual(expectedY, legacy.LY);
            Assert.IsFalse(legacy.LXAxis.IsHighResolution);
            Assert.IsFalse(legacy.RXAxis.IsHighResolution);
        }
    }

    [TestMethod]
    public void EverySignedValueSurvivesWithoutChangingLegacyProjection()
    {
        for (int value = short.MinValue; value <= short.MaxValue; value++)
        {
            var axis = DS4MappedStickAxis.FromSigned((short)value);
            Assert.AreEqual((short)value, axis.ToSigned16());
            Assert.AreEqual(Switch2ProfileAxisProjection.QuantizeLegacy((short)value), axis.LegacyValue);
            Assert.IsTrue(axis.IsHighResolution);
            Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(axis.ProfileCoordinate, out var copy));
            Assert.AreEqual((short)value, copy.ToSigned16());
        }
        Assert.AreEqual((short)32767, DS4MappedStickAxis.FromSigned(short.MinValue).ToSigned16(true));
        Assert.AreEqual((short)-32768, DS4MappedStickAxis.FromSigned(short.MaxValue).ToSigned16(true));
    }

    [TestMethod]
    public void DefaultAndInvalidCoordinatesAreNeutralAndLegacyWritesAreExact()
    {
        Assert.AreEqual(128, default(DS4MappedStickAxis).LegacyValue);
        Assert.AreEqual((short)0, default(DS4MappedStickAxis).ToSigned16());
        Assert.AreEqual((ushort)2048, default(DS4MappedStickAxis).ToUnsigned12());
        foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -0.01, 255.01 })
        {
            Assert.IsFalse(DS4MappedStickAxis.TryFromProfileCoordinate(invalid, out var axis));
            Assert.AreEqual(default, axis);
        }
        for (int value = 0; value <= 255; value++)
        {
            var axis = DS4MappedStickAxis.FromLegacy((byte)value);
            Assert.AreEqual((byte)value, axis.LegacyValue);
            Assert.AreEqual((double)value, axis.ProfileCoordinate);
            Assert.IsFalse(axis.IsHighResolution);
        }
        Assert.AreEqual((ushort)0, DS4MappedStickAxis.FromSigned(short.MinValue).ToUnsigned12());
        Assert.AreEqual((ushort)4095, DS4MappedStickAxis.FromSigned(short.MaxValue).ToUnsigned12());
    }

    [TestMethod]
    public void SameByteOverrideReplacesPrecisionAndExtrasCannotRestoreRawInput()
    {
        var source = new DS4State { LXAxis = DS4MappedStickAxis.FromSigned(16),
            LYAxis = DS4MappedStickAxis.FromSigned(-16), RXAxis = DS4MappedStickAxis.FromSigned(32),
            RYAxis = DS4MappedStickAxis.FromSigned(-32),
            Switch2RawInputStatus = new() { IsValid = true, LeftStickX = 16 } };
        var mapped = new DS4State(source);
        Assert.AreEqual((byte)128, source.LX);
        mapped.LX = source.LX;
        Assert.AreEqual((short)0, mapped.LXAxis.ToSigned16(), "Even an equal byte is a new owner's complete value.");
        Assert.IsFalse(mapped.LXAxis.IsHighResolution);
        Assert.AreEqual(source.LYAxis, mapped.LYAxis, "Replacing one axis must not replace another.");
        mapped.LYAxis = DS4MappedStickAxis.FromSigned(400);
        source.CopyExtrasTo(mapped);
        Assert.AreEqual(source.Switch2RawInputStatus, mapped.Switch2RawInputStatus);
        Assert.AreEqual((short)0, mapped.LXAxis.ToSigned16());
        Assert.AreEqual((short)400, mapped.LYAxis.ToSigned16());
        var copy = new DS4State();
        source.CopyTo(copy);
        Assert.AreEqual(source.LXAxis, copy.LXAxis);
        Assert.AreEqual(source.LYAxis, copy.LYAxis);
        Assert.AreEqual(source.RXAxis, copy.RXAxis);
        Assert.AreEqual(source.RYAxis, copy.RYAxis);
        copy.LX = 0;
        Assert.AreEqual((short)16, source.LXAxis.ToSigned16(), "Copies must not share mutable axis storage.");
    }

    [TestMethod]
    public void WarmMappedAxisCopyConversionAndOverrideAllocateNothing()
    {
        var source = new DS4State();
        var destination = new DS4State();
        long checksum = 0;
        for (int i = 0; i < 2_000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++) Step(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(checksum > 0);
        Assert.AreEqual(0L, allocated);

        void Step(int value)
        {
            source.LXAxis = DS4MappedStickAxis.FromSigned((short)value);
            source.CopyTo(destination);
            checksum += destination.LXAxis.ToSigned16() + destination.LXAxis.ToUnsigned12();
            destination.LX = 128;
            source.CopyExtrasTo(destination);
            checksum += destination.LXAxis.ToSigned16();
        }
    }
}
