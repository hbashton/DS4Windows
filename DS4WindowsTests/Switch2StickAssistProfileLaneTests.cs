using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2StickAssistProfileLaneTests
{
    [TestMethod]
    public void ProRightStickUsesElapsedTimeAndPreservesAxisSigns()
    {
        Switch2StickAssistProfileLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 1_000);

        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 0, true,
            5.0, 1, ref state, out _));

        pro.CompletionTimestampQpc = 1_010;
        Assert.IsTrue(Advance(pro, default, 128, 128, 255, 0, true,
            5.0, 1, ref state, out Switch2StickAssistResult result));
        Assert.AreEqual(2.4, result.DeltaX, 0.000001);
        Assert.AreEqual(-2.4, result.DeltaY, 0.000001);
        Assert.AreEqual(240.0, result.VelocityX, 0.000001);
        Assert.AreEqual(-240.0, result.VelocityY, 0.000001);
    }

    [TestMethod]
    public void JoinedUsesRightAndStandaloneLeftUsesLogicalLeft()
    {
        Switch2StickAssistProfileLaneState state = default;
        Switch2JoyConRawInputStatus joined = JoyCon(
            Switch2JoyConProfileMode.Joined, timestamp: 2_000,
            leftPresent: true, rightPresent: true, pairEpoch: 7);
        Assert.IsFalse(Advance(default, joined, 0, 255, 255, 0, true,
            2.0, 1, ref state, out _));
        joined.CompletionTimestampQpc = 2_010;
        Assert.IsTrue(Advance(default, joined, 0, 255, 255, 0, true,
            2.0, 1, ref state, out Switch2StickAssistResult joinedResult));
        Assert.IsTrue(joinedResult.DeltaX > 0.0);
        Assert.IsTrue(joinedResult.DeltaY < 0.0);

        Switch2JoyConRawInputStatus standalone = JoyCon(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft,
            timestamp: 3_000, leftPresent: true, rightPresent: false,
            pairEpoch: 0);
        Assert.IsFalse(Advance(default, standalone, 0, 255, 255, 0, true,
            2.0, 1, ref state, out _));
        standalone.CompletionTimestampQpc = 3_010;
        Assert.IsTrue(Advance(default, standalone, 0, 255, 255, 0, true,
            2.0, 1, ref state,
            out Switch2StickAssistResult standaloneResult));
        Assert.IsTrue(standaloneResult.DeltaX < 0.0);
        Assert.IsTrue(standaloneResult.DeltaY > 0.0);

        Switch2JoyConRawInputStatus standaloneRight = JoyCon(
            Switch2JoyConProfileMode.StandaloneVerticalRight,
            timestamp: 4_000, leftPresent: false, rightPresent: true,
            pairEpoch: 0);
        Assert.IsFalse(Advance(default, standaloneRight, 255, 0, 0, 255,
            true, 2.0, 1, ref state, out _));
        standaloneRight.CompletionTimestampQpc = 4_010;
        Assert.IsTrue(Advance(default, standaloneRight, 255, 0, 0, 255,
            true, 2.0, 1, ref state,
            out Switch2StickAssistResult standaloneRightResult));
        Assert.IsTrue(standaloneRightResult.DeltaX > 0.0);
        Assert.IsTrue(standaloneRightResult.DeltaY < 0.0);
    }

    [TestMethod]
    public void GyroInactiveProfileChangeAndLongGapFenceOutput()
    {
        Switch2StickAssistProfileLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 1_000);
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 1, ref state, out _));

        pro.CompletionTimestampQpc = 1_010;
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, false,
            5.0, 1, ref state, out _));
        pro.CompletionTimestampQpc = 1_020;
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 1, ref state, out _));
        pro.CompletionTimestampQpc = 1_030;
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 2, ref state, out _));
        pro.CompletionTimestampQpc = 1_100;
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 2, ref state, out _));
        pro.CompletionTimestampQpc = 1_110;
        Assert.IsTrue(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 2, ref state, out _));
    }

    [TestMethod]
    public void InvalidSensitivityAndDuplicateTimestampFailClosed()
    {
        Assert.AreEqual(0.0,
            Switch2StickAssistProfileLane.NormalizeSensitivity(double.NaN));
        Assert.AreEqual(0.0,
            Switch2StickAssistProfileLane.NormalizeSensitivity(-0.1));
        Assert.AreEqual(0.0,
            Switch2StickAssistProfileLane.NormalizeSensitivity(10.1));

        Switch2StickAssistProfileLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 1_000);
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 1, ref state, out _));
        Assert.IsFalse(Advance(pro, default, 128, 128, 255, 128, true,
            5.0, 1, ref state, out _));
    }

    [TestMethod]
    public void WarmPathDoesNotAllocate()
    {
        Switch2StickAssistProfileLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 1_000);
        Advance(pro, default, 128, 128, 255, 0, true, 5.0, 1,
            ref state, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 20_000; index++)
        {
            pro.CompletionTimestampQpc++;
            succeeded &= Advance(pro, default, 128, 128, 255, 0, true,
                5.0, 1, ref state, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, after - before);
    }

    private static bool Advance(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, byte leftX, byte leftY,
        byte rightX, byte rightY, bool active, double sensitivity,
        long profileRevision, ref Switch2StickAssistProfileLaneState state,
        out Switch2StickAssistResult result) =>
        Switch2StickAssistProfileLane.TryAdvance(pro, joyCon, leftX, leftY,
            rightX, rightY, active, sensitivity, profileRevision, ref state,
            out result);

    private static Switch2RawInputStatus Pro(long timestamp) => new()
    {
        IsValid = true,
        ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
        DeviceGeneration = 11,
        TransportGeneration = 12,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = 1_000,
    };

    private static Switch2JoyConRawInputStatus JoyCon(
        Switch2JoyConProfileMode mode, long timestamp, bool leftPresent,
        bool rightPresent, ulong pairEpoch) => new()
    {
        IsValid = true,
        ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
        Mode = mode,
        PairEpoch = pairEpoch,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = 1_000,
        LeftPresent = leftPresent,
        LeftDeviceGeneration = leftPresent ? 21UL : 0UL,
        LeftTransportGeneration = leftPresent ? 22UL : 0UL,
        RightPresent = rightPresent,
        RightDeviceGeneration = rightPresent ? 31UL : 0UL,
        RightTransportGeneration = rightPresent ? 32UL : 0UL,
    };
}
