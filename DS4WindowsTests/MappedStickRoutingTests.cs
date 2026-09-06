using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class MappedStickRoutingTests
{
    [TestMethod]
    public void DirectionMappingPreservesEveryLegacyCombination()
    {
        var field = new DS4StateFieldMapping();
        for (int source = (int)DS4Controls.LXNeg; source <= (int)DS4Controls.RYPos; source++)
        foreach (bool destinationPositive in new[] { false, true })
        for (int raw = 0; raw < 256; raw++)
        {
            bool sourcePositive = (source & 1) == 0;
            field.axisdirs[source] = (byte)raw;
            bool active = sourcePositive ? raw > 128 : raw < 128;
            byte expected = !active ? (byte)128 : sourcePositive == destinationPositive ? (byte)raw : (byte)(255 - raw);
            var result = Mapping.GetXYAxisMapping(Global.TEST_PROFILE_INDEX, (DS4Controls)source,
                null, null, null, field, destinationPositive);
            Assert.AreEqual(expected, result.LegacyValue);
            Assert.IsFalse(result.IsHighResolution);
        }
    }

    [TestMethod]
    public void PreciseDirectionInversionUsesSignedMagnitudeAndRetainsAllValues()
    {
        foreach (bool sourcePositive in new[] { false, true })
        for (int raw = short.MinValue; raw <= short.MaxValue; raw++)
        {
            var original = DS4MappedStickAxis.FromSigned((short)raw);
            var reversed = original.MapDirection(sourcePositive, !sourcePositive);
            bool active = sourcePositive ? raw > 0 : raw < 0;
            Assert.IsTrue(reversed.IsHighResolution);
            if (!active)
            {
                Assert.AreEqual(128.0, reversed.ProfileCoordinate);
                continue;
            }
            double unit = (original.ProfileCoordinate - 128) / (sourcePositive ? 127 : 128);
            double expected = 128 - unit * (sourcePositive ? 128 : 127);
            Assert.AreEqual(expected, reversed.ProfileCoordinate, 1e-12);
            var restored = reversed.MapDirection(!sourcePositive, sourcePositive);
            Assert.AreEqual(original.ProfileCoordinate, restored.ProfileCoordinate, 1e-12);
        }
    }

    [TestMethod]
    public void SubByteRemapReachesBothDestinationSlotsAndNeutralDoesNotOverwrite()
    {
        var field = new DS4StateFieldMapping();
        var output = new DS4StateFieldMapping();
        var original = Axis(127.9);
        field.axisdirs.SetMappedAxis((int)DS4Controls.LXNeg, original);
        var value = Mapping.GetXYAxisMapping(0, DS4Controls.LXNeg, null, null, null, field, true);
        Assert.AreEqual(128.09921875, value.ProfileCoordinate, 1e-12);
        Assert.AreEqual((byte)128, value.LegacyValue);
        Assert.IsTrue(Mapping.ApplyMappedAxisBinding(output, (int)DS4Controls.RYPos, value));
        Assert.AreEqual(value, output.axisdirs.GetMappedAxis((int)DS4Controls.RYNeg));
        Assert.AreEqual(value, output.axisdirs.GetMappedAxis((int)DS4Controls.RYPos));
        var state = new DS4State();
        output.PopulateState(state);
        Assert.AreEqual(value, state.RYAxis);
        Assert.IsFalse(Mapping.ApplyMappedAxisBinding(output, (int)DS4Controls.RYPos, default));
        Assert.AreEqual(value, output.axisdirs.GetMappedAxis((int)DS4Controls.RYPos));
        Assert.IsFalse(Mapping.ApplyMappedAxisBinding(output, (int)DS4Controls.Cross, original));
        // A deliberate consume/reset retires the mapped value; extras cannot restore it.
        output.axisdirs[(int)DS4Controls.RYNeg] = output.axisdirs[(int)DS4Controls.RYPos] = 128;
        output.PopulateState(state);
        Assert.IsFalse(state.RYAxis.IsHighResolution);
        Assert.AreEqual((byte)128, state.RY);
    }

    [TestMethod]
    public void NeutralOscKeepsPrecisionAndNonNeutralOscReplacesOnlyItsAxis()
    {
        foreach (bool post in new[] { false, true })
        {
            var state = new DS4State { LXAxis = Axis(128.1), LYAxis = Axis(127.9),
                RXAxis = Axis(201.25), RYAxis = Axis(83.75) };
            var before = new DS4State(state);
            var osc = new DS4State();
            Apply();
            AssertAxes(before, state);
            osc.LX = 201; osc.R2 = 90; osc.Cross = true;
            Apply();
            Assert.AreEqual((byte)201, state.LX);
            Assert.IsFalse(state.LXAxis.IsHighResolution);
            Assert.AreEqual(before.LYAxis, state.LYAxis);
            Assert.AreEqual(before.RXAxis, state.RXAxis);
            Assert.AreEqual(before.RYAxis, state.RYAxis);
            Assert.AreEqual((byte)90, state.R2);
            Assert.IsTrue(state.Cross);
            // A typed future producer is still an explicit mapped replacement.
            osc.LXAxis = Axis(128.05);
            Apply();
            Assert.AreEqual(osc.LXAxis, state.LXAxis);
            void Apply()
            {
                if (post) ControlService.OSCPostMappingStep(state, osc);
                else ControlService.OSCPreMappingStep(state, osc);
            }
        }
    }

    [TestMethod]
    public void StrongestContributorPreservesLegacyTiesAndPreciseWinners()
    {
        for (int current = 0; current < 256; current++)
        for (int candidate = 0; candidate < 256; candidate++)
        {
            byte expected = candidate != 128 && Math.Abs(candidate - 128) > Math.Abs(current - 128) ?
                (byte)candidate : (byte)current;
            Assert.AreEqual(expected, DS4MappedStickAxis.SelectStronger(
                DS4MappedStickAxis.FromLegacy((byte)current),
                DS4MappedStickAxis.FromLegacy((byte)candidate)).LegacyValue);
        }
        var precise = Axis(129.25);
        Assert.AreEqual(precise, DS4MappedStickAxis.SelectStronger(precise, DS4MappedStickAxis.FromLegacy(129)));
        Assert.AreEqual(precise, DS4MappedStickAxis.SelectStronger(precise, Axis(126.75)));
        var state = new DS4State { LXAxis = Axis(129.1), LYAxis = Axis(126.3) };
        var data = new Mapping.PostMapStickData { LXAxis = precise, LY = 127, dirty = true };
        Mapping.ApplyPostMapStickData(state, data);
        Assert.AreEqual(precise, state.LXAxis);
        Assert.AreEqual(126.3, state.LYAxis.ProfileCoordinate, 1e-12);
        Assert.IsFalse(data.dirty);
        Assert.AreEqual((byte)128, data.LX);
        Assert.IsFalse(data.LXAxis.IsHighResolution);
    }

    [TestMethod]
    public void GyroLosingToPhysicalInputDoesNotQuantizeItsWinner()
    {
        const int slot = 0;
        var mode = Global.GetGyroOutMode(slot);
        var info = Global.GetGyroMouseStickInfo(slot);
        var priorOutput = info.outputStick;
        var priorAxis = info.outputStickDir;
        byte priorX = Mapping.gyroStickX[slot], priorY = Mapping.gyroStickY[slot];
        var priorData = Mapping.mapStickActionData[slot];
        try
        {
            Global.GyroOutputMode[slot] = GyroOutMode.MouseJoystick;
            info.outputStick = GyroMouseStickInfo.OutputStick.RightStick;
            info.outputStickDir = GyroMouseStickInfo.OutputStickAxes.XY;
            Mapping.mapStickActionData[slot] = new Mapping.PostMapStickData();
            var accumulator = Mapping.mapStickActionData[slot];
            Assert.IsTrue(accumulator.TrySubmit(accumulator.CaptureEpoch(),
                GyroMouseStickInfo.OutputStick.RightStick, true, true, 129, 127, true, slot));
            accumulator.Reset(); // Retain current gyro, not an already-pending contribution.
            var state = new DS4State { RXAxis = Axis(129.2), RYAxis = Axis(126.7) };
            Mapping.TempMouseJoystick(slot, state);
            Assert.AreEqual(129.2, state.RXAxis.ProfileCoordinate, 1e-12);
            Assert.AreEqual(126.7, state.RYAxis.ProfileCoordinate, 1e-12);
            var retained = Mapping.mapStickActionData[slot];
            Assert.AreEqual((byte)129, retained.RX);
            Assert.AreEqual((byte)127, retained.RY);
            var released = new DS4State();
            Mapping.ApplyPostMapStickData(released, retained);
            Assert.AreEqual((byte)129, released.RX, "The prior physical winner must not be replayed after release.");
            Assert.AreEqual((byte)127, released.RY);
            Assert.IsFalse(released.RXAxis.IsHighResolution || released.RYAxis.IsHighResolution);
            Assert.IsTrue(accumulator.TrySubmit(accumulator.CaptureEpoch(),
                GyroMouseStickInfo.OutputStick.RightStick, true, true, 134, 127, true, slot));
            Mapping.TempMouseJoystick(slot, state);
            Assert.AreEqual((byte)134, state.RX);
            Assert.IsFalse(state.RXAxis.IsHighResolution);
        }
        finally
        {
            Global.GyroOutputMode[slot] = mode;
            info.outputStick = priorOutput; info.outputStickDir = priorAxis;
            Mapping.gyroStickX[slot] = priorX; Mapping.gyroStickY[slot] = priorY;
            Mapping.mapStickActionData[slot] = priorData;
        }
    }

    [TestMethod]
    public void AnglesUseFractionalMotionWhileEveryLegacyAngleRemainsExact()
    {
        var state = new DS4State();
        for (int x = 0; x < 256; x++)
        for (int y = 0; y < 256; y++)
        {
            state.LX = state.RX = (byte)x; state.LY = state.RY = (byte)y;
            state.calculateStickAngles();
            double expected = Math.Atan2(-(y - 128), x - 128);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(state.LSAngleRad));
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(state.RSAngleRad));
        }
        state.LXAxis = Axis(128.1); state.LYAxis = Axis(128.2);
        state.RXAxis = Axis(127.9); state.RYAxis = Axis(127.8);
        state.calculateStickAngles();
        Assert.AreEqual(Math.Atan2(-(state.LYAxis.ProfileCoordinate - 128), state.LXAxis.ProfileCoordinate - 128),
            state.LSAngleRad, 1e-12);
        Assert.AreEqual(Math.Atan2(-(state.RYAxis.ProfileCoordinate - 128), state.RXAxis.ProfileCoordinate - 128),
            state.RSAngleRad, 1e-12);
        Assert.AreNotEqual(0.0, state.LSAngleRad);
    }

    private static DS4MappedStickAxis Axis(double coordinate)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(coordinate, out var result));
        return result;
    }

    private static void AssertAxes(DS4State expected, DS4State actual)
    {
        Assert.AreEqual(expected.LXAxis, actual.LXAxis); Assert.AreEqual(expected.LYAxis, actual.LYAxis);
        Assert.AreEqual(expected.RXAxis, actual.RXAxis); Assert.AreEqual(expected.RYAxis, actual.RYAxis);
    }
}
