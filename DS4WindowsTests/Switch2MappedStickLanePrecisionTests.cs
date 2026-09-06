using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MappedStickLanePrecisionTests
{
    private static readonly Switch2StickDirectionActivationModes AllTap = new(
        Switch2StickDirectionActivationMode.Tap, Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap, Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap, Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap, Switch2StickDirectionActivationMode.Tap);

    [TestMethod]
    public void SectorGeometryIsIdenticalForEveryLegacyBytePair()
    {
        for (int x = 0; x <= byte.MaxValue; x++)
        for (int y = 0; y <= byte.MaxValue; y++)
            Assert.AreEqual(LegacySector((byte)x, (byte)y),
                Switch2StickScrollTapLane.ResolveSector((byte)x, (byte)y));
    }

    [TestMethod]
    public void AssistIsIdenticalForEveryLegacyByteCoordinate()
    {
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            Switch2StickAssistProfileLaneState state = default;
            var pro = Pro(1_000);
            Assert.IsFalse(Switch2StickAssistProfileLane.TryAdvance(pro, default,
                (byte)128, (byte)128, (byte)value, (byte)(255 - value), true,
                5, 1, ref state, out _));
            pro.CompletionTimestampQpc = 1_010;
            bool accepted = Switch2StickAssistProfileLane.TryAdvance(pro, default,
                (byte)128, (byte)128, (byte)value, (byte)(255 - value), true,
                5, 1, ref state, out var result);
            double x = LegacyNormalized((byte)value);
            double y = LegacyNormalized((byte)(255 - value));
            Assert.AreEqual(x != 0 || y != 0, accepted);
            Assert.AreEqual(x * 240, result.VelocityX);
            Assert.AreEqual(y * 240, result.VelocityY);
            Assert.AreEqual(x * 240 * 0.01, result.DeltaX);
            Assert.AreEqual(y * 240 * 0.01, result.DeltaY);
        }
    }

    [TestMethod]
    public void ScrollCenterAndAngularBoundariesUseFractionsBeforeByteRounding()
    {
        Assert.AreEqual(Switch2StickScrollSector.None,
            Switch2StickScrollTapLane.ResolveSector(128.0, 128.0));
        Assert.AreEqual(Switch2StickScrollSector.None,
            Switch2StickScrollTapLane.ResolveSector(131.80, 128.0));
        Assert.AreEqual(Switch2StickScrollSector.Right,
            Switch2StickScrollTapLane.ResolveSector(131.82, 128.0));
        Assert.AreEqual(Switch2StickScrollSector.Left,
            Switch2StickScrollTapLane.ResolveSector(0.0, 128.0));
        Assert.AreEqual(Switch2StickScrollSector.Right,
            Switch2StickScrollTapLane.ResolveSector(255.0, 128.0));

        double x = 200.25;
        double boundaryY = 128 - (x - 128) * Math.Tan(Math.PI / 8.0);
        Assert.AreEqual((byte)(boundaryY - 0.001), (byte)(boundaryY + 0.001));
        Assert.AreEqual(Switch2StickScrollSector.Right,
            Switch2StickScrollTapLane.ResolveSector(x, boundaryY + 0.001));
        Assert.AreEqual(Switch2StickScrollSector.Right | Switch2StickScrollSector.Up,
            Switch2StickScrollTapLane.ResolveSector(x, boundaryY - 0.001));

        Switch2StickScrollTapLaneState state = default;
        var pro = Pro(1_000);
        Assert.IsTrue(Scroll(pro, 131.80, 128, ref state, out _));
        pro.CompletionTimestampQpc = 1_030;
        Assert.IsTrue(Scroll(pro, 131.82, 128, ref state, out var frame));
        Assert.IsTrue(frame.TryHandle(DS4Controls.LXPos, out bool emit, out int step));
        Assert.IsTrue(emit);
        Assert.AreEqual(Switch2StickScrollTapLane.MinimumTapStep, step);
    }

    [TestMethod]
    public void ScrollStepRetainsFractionsUntilTheExistingIntegerWheelStep()
    {
        double threshold = 128 + 127 * (0.03 + 0.97 * (50.0 / 150.0));
        Assert.AreEqual((byte)(threshold - 0.01), (byte)(threshold + 0.01));
        foreach (bool above in new[] { false, true })
        {
            Switch2StickScrollTapLaneState state = default;
            var pro = Pro(1_000);
            Assert.IsTrue(Scroll(pro, 128, 128, ref state, out _));
            pro.CompletionTimestampQpc = 1_030;
            Assert.IsTrue(Scroll(pro, threshold + (above ? 0.01 : -0.01), 128,
                ref state, out var frame));
            Assert.IsTrue(frame.TryHandle(DS4Controls.LXPos, out bool emit, out int step));
            Assert.IsTrue(emit);
            Assert.AreEqual(above ? 50 : 49, step);
        }
    }

    [TestMethod]
    public void DirectionPulseStartsAtSubByteEdgeAndKeepsExactEightyMillisecondExpiry()
    {
        Switch2StickDirectionTapLaneState state = default;
        var pro = Pro(1_000);
        Assert.IsTrue(Direction(pro, 131.80, 128, ref state, out _));
        pro.CompletionTimestampQpc = 1_010;
        Assert.IsTrue(Direction(pro, 131.82, 128, ref state, out var edge));
        Assert.IsTrue(edge.TryOverride(DS4Controls.LXPos, out bool active));
        Assert.IsTrue(active);
        pro.CompletionTimestampQpc = 1_089;
        Assert.IsTrue(Direction(pro, 131.82, 128, ref state, out var before));
        Assert.IsTrue(before.TryOverride(DS4Controls.LXPos, out active));
        Assert.IsTrue(active);
        pro.CompletionTimestampQpc = 1_090;
        Assert.IsTrue(Direction(pro, 131.82, 128, ref state, out var expired));
        Assert.IsTrue(expired.TryOverride(DS4Controls.LXPos, out active));
        Assert.IsFalse(active);
    }

    [TestMethod]
    public void AssistPreservesSubByteVelocityAndItsExistingInclusiveNeutralBand()
    {
        Switch2StickAssistProfileLaneState state = default;
        var pro = Pro(1_000);
        Assert.IsFalse(Assist(pro, 128, 128, ref state, out _));
        foreach (double neutral in new[] { 127.0, 127.5, 128.0, 128.5, 129.0 })
        {
            pro.CompletionTimestampQpc += 10;
            Assert.IsFalse(Assist(pro, neutral, 128, ref state, out var result));
            Assert.AreEqual(0.0, result.DeltaX);
        }
        pro.CompletionTimestampQpc += 10;
        Assert.IsTrue(Assist(pro, 129.0001, 128, ref state, out var boundary));
        Assert.IsTrue(boundary.DeltaX > 0);
        pro.CompletionTimestampQpc += 10;
        Assert.IsTrue(Assist(pro, 180.1, 128, ref state, out var first));
        pro.CompletionTimestampQpc += 10;
        Assert.IsTrue(Assist(pro, 180.2, 128, ref state, out var second));
        Assert.IsTrue(second.VelocityX > first.VelocityX);
        Assert.AreEqual((180.1 - 128) / 127 * 240, first.VelocityX);
        Assert.AreEqual((180.2 - 128) / 127 * 240, second.VelocityX);
    }

    [TestMethod]
    public void StandaloneRightAssistUsesPreciseLogicalLeftCoordinates()
    {
        var source = new Switch2JoyConRawInputStatus
        {
            IsValid = true,
            ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            Mode = Switch2JoyConProfileMode.StandaloneVerticalRight,
            RightPresent = true,
            RightDeviceGeneration = 5,
            RightTransportGeneration = 6,
            CompletionTimestampQpc = 1_000,
            QpcFrequency = 1_000,
        };
        Switch2StickAssistProfileLaneState state = default;
        Assert.IsFalse(Switch2StickAssistProfileLane.TryAdvance(default, source,
            129.25, 126.75, 0, 255, true, 5, 1, ref state, out _));
        source.CompletionTimestampQpc = 1_010;
        Assert.IsTrue(Switch2StickAssistProfileLane.TryAdvance(default, source,
            129.25, 126.75, 0, 255, true, 5, 1, ref state, out var result));
        Assert.AreEqual(1.25 / 127 * 240, result.VelocityX);
        Assert.AreEqual(-1.25 / 128 * 240, result.VelocityY);
    }

    [TestMethod]
    public void InvalidCoordinateInAnySlotResetsAllLanesAndRequiresFreshBaseline()
    {
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity,
                     double.NegativeInfinity, -0.001, 255.001 })
        for (int slot = 0; slot < 4; slot++)
        {
            var pro = Pro(1_000);
            Switch2StickScrollTapLaneState scroll = default;
            Switch2StickDirectionTapLaneState direction = default;
            Switch2StickAssistProfileLaneState assist = default;
            Scroll(pro, 128, 128, ref scroll, out _);
            Direction(pro, 128, 128, ref direction, out _);
            Assist(pro, 128, 128, ref assist, out _);
            double[] coordinates = { 255, 128, 255, 128 };
            coordinates[slot] = invalid;
            pro.CompletionTimestampQpc = 1_010;
            Assert.IsFalse(Switch2StickScrollTapLane.TryAdvance(pro, default,
                coordinates[0], coordinates[1], coordinates[2], coordinates[3],
                Switch2StickScrollActivationMode.Tap, Switch2StickScrollActivationMode.Tap,
                1, ref scroll, out var invalidScroll));
            Assert.IsFalse(invalidScroll.IsValid);
            Assert.IsFalse(scroll.HasBaseline);
            Assert.IsFalse(Switch2StickDirectionTapLane.TryAdvance(pro, default,
                coordinates[0], coordinates[1], coordinates[2], coordinates[3],
                AllTap, 1, ref direction, out var invalidDirection));
            Assert.IsFalse(invalidDirection.IsValid);
            Assert.IsFalse(direction.HasBaseline);
            Assert.IsFalse(Switch2StickAssistProfileLane.TryAdvance(pro, default,
                coordinates[0], coordinates[1], coordinates[2], coordinates[3],
                true, 5, 1, ref assist, out var invalidAssist));
            Assert.IsFalse(assist.HasBaseline);
            Assert.AreEqual(0.0, invalidAssist.DeltaX);
            Assert.AreEqual(0.0, invalidAssist.DeltaY);

            pro.CompletionTimestampQpc = 1_040;
            Assert.IsTrue(Scroll(pro, 255, 128, ref scroll, out var rearmedScroll));
            Assert.IsTrue(rearmedScroll.TryHandle(DS4Controls.LXPos, out bool emit, out _));
            Assert.IsFalse(emit);
            Assert.IsTrue(Direction(pro, 255, 128, ref direction, out var rearmedDirection));
            Assert.IsTrue(rearmedDirection.TryOverride(DS4Controls.LXPos, out bool active));
            Assert.IsFalse(active);
            Assert.IsFalse(Assist(pro, 255, 128, ref assist, out _));
        }
    }

    [TestMethod]
    public void FractionalWarmPathAllocatesNoManagedMemory()
    {
        var pro = Pro(1_000);
        Switch2StickScrollTapLaneState scroll = default;
        Switch2StickDirectionTapLaneState direction = default;
        Switch2StickAssistProfileLaneState assist = default;
        AdvanceBatch(ref pro, ref scroll, ref direction, ref assist, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = AdvanceBatch(ref pro, ref scroll, ref direction, ref assist, 20_000);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.IsTrue(double.IsFinite(sum) && sum > 0);
    }

    private static double AdvanceBatch(ref Switch2RawInputStatus pro,
        ref Switch2StickScrollTapLaneState scroll,
        ref Switch2StickDirectionTapLaneState direction,
        ref Switch2StickAssistProfileLaneState assist, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            pro.CompletionTimestampQpc += 30;
            double x = (i & 1) == 0 ? 180.1 : 180.2;
            Scroll(pro, x, 128.1, ref scroll, out _);
            Direction(pro, x, 128.1, ref direction, out _);
            Assist(pro, x, 126.9, ref assist, out var result);
            sum += result.VelocityX;
        }
        return sum;
    }

    private static bool Scroll(in Switch2RawInputStatus pro, double x, double y,
        ref Switch2StickScrollTapLaneState state, out Switch2StickScrollTapFrame frame) =>
        Switch2StickScrollTapLane.TryAdvance(pro, default, x, y, 128, 128,
            Switch2StickScrollActivationMode.Tap, Switch2StickScrollActivationMode.Tap,
            1, ref state, out frame);

    private static bool Direction(in Switch2RawInputStatus pro, double x, double y,
        ref Switch2StickDirectionTapLaneState state, out Switch2StickDirectionTapFrame frame) =>
        Switch2StickDirectionTapLane.TryAdvance(pro, default, x, y, 128, 128,
            AllTap, 1, ref state, out frame);

    private static bool Assist(in Switch2RawInputStatus pro, double x, double y,
        ref Switch2StickAssistProfileLaneState state, out Switch2StickAssistResult result) =>
        Switch2StickAssistProfileLane.TryAdvance(pro, default, 128, 128, x, y,
            true, 5, 1, ref state, out result);

    private static Switch2RawInputStatus Pro(long timestamp) => new()
    {
        IsValid = true,
        ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
        DeviceGeneration = 11,
        TransportGeneration = 12,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = 1_000,
    };

    private static double LegacyNormalized(byte value)
    {
        int centered = value - 128;
        if (centered is >= -1 and <= 1) return 0;
        return centered < 0 ? centered / 128.0 : centered / 127.0;
    }

    private static Switch2StickScrollSector LegacySector(byte x, byte y)
    {
        double dx = x - 128;
        double dy = y - 128;
        if (Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / 127) <= 0.03)
            return Switch2StickScrollSector.None;
        double radians = Math.Atan2(-(y - 128), x - 128);
        double angle = (radians >= 0 ? radians : 2 * Math.PI + radians) * 180 / Math.PI;
        Switch2StickScrollSector sector = Switch2StickScrollSector.None;
        if (x < 128 && angle is >= 112.5 and <= 247.5) sector |= Switch2StickScrollSector.Left;
        else if (x > 128 && (angle <= 67.5 || angle >= 292.5)) sector |= Switch2StickScrollSector.Right;
        if (y < 128 && angle is >= 22.5 and <= 157.5) sector |= Switch2StickScrollSector.Up;
        else if (y > 128 && angle is >= 202.5 and <= 337.5) sector |= Switch2StickScrollSector.Down;
        return sector;
    }
}
