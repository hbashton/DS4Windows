using DS4Windows;
using DS4Windows.Switch2;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConIdlePolicyTests
{
    [TestMethod]
    public void NintendoOffAndUsbNeverDisconnectEvenAfterDeadline()
    {
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.Off, 100_000));
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.Absolute, 100_000, wireless: false));
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.Inactive, 100_000, wireless: false));
    }

    [TestMethod]
    public void AbsoluteUsesConnectionStartWhileInactiveUsesLatestEitherHandActivity()
    {
        Assert.IsTrue(Check(Switch2AutoDisconnectMode.Absolute, 11_000));
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.Inactive, 11_000));
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.Inactive, 14_999));
        Assert.IsTrue(Check(Switch2AutoDisconnectMode.Inactive, 15_000));
    }

    [TestMethod]
    public void LegacySettingAndInvalidClockRemainConservative()
    {
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.LegacyProfile, 100_000, legacy: 0));
        Assert.IsTrue(Check(Switch2AutoDisconnectMode.LegacyProfile, 15_000, legacy: 10));
        Assert.IsFalse(Check(Switch2AutoDisconnectMode.Inactive, 4_999));
        Assert.IsFalse(LegacyJoyConIdlePolicy.ShouldDisconnect(Switch2AutoDisconnectMode.Absolute,
            10, 10, true, 0, 5_000, 100_000, 1_000));
        Assert.IsFalse(LegacyJoyConIdlePolicy.ShouldDisconnect(Switch2AutoDisconnectMode.Absolute,
            10, 10, true, 1_000, 5_000, 100_000, 0));
        Assert.IsFalse(LegacyJoyConIdlePolicy.ShouldDisconnect(Switch2AutoDisconnectMode.Absolute,
            long.MaxValue, 10, true, 1_000, 5_000, 100_000, 1_000));
    }

    [TestMethod]
    public void NintendoCaptureRailsAndTriggersCountAsActivity()
    {
        Assert.IsTrue(LegacyJoyConIdlePolicy.IsIdle(new DS4State()));
        foreach (Action<DS4State> press in new Action<DS4State>[]
        {
            s => s.Capture = true, s => s.SideL = true, s => s.SideR = true,
            s => s.L2Btn = true, s => s.R2Btn = true, s => s.L2 = 1,
            s => s.PS = true, s => s.Options = true, s => s.LX = 63, s => s.RY = 192
        })
        {
            var state = new DS4State();
            press(state);
            Assert.IsFalse(LegacyJoyConIdlePolicy.IsIdle(state));
        }
    }

    [TestMethod]
    public void ChargingKeepsLegacyIdleExemptionButDoesNotOverrideExplicitNintendoTimers()
    {
        foreach (var mode in new[] { Switch2AutoDisconnectMode.LegacyProfile,
                     Switch2AutoDisconnectMode.Inactive, Switch2AutoDisconnectMode.Absolute })
        {
            Assert.AreEqual(mode != Switch2AutoDisconnectMode.LegacyProfile,
                LegacyJoyConIdlePolicy.ShouldDisconnect(mode, 10, 10, true,
                    1_000, 5_000, 100_000, 1_000, charging: true), mode.ToString());
            Assert.IsTrue(LegacyJoyConIdlePolicy.ShouldDisconnect(mode, 10, 10, true,
                1_000, 5_000, 100_000, 1_000, charging: false), mode.ToString());
        }
    }

    [TestMethod]
    public void EitherReaderCanRefreshActivityWithoutAnOlderSampleMovingItBack()
    {
        var group = (LegacyJoyConGroup)RuntimeHelpers.GetUninitializedObject(typeof(LegacyJoyConGroup));
        LegacyJoyConIdlePolicy.RecordActivity(group, 2_000);
        LegacyJoyConIdlePolicy.RecordActivity(group, 1_000);
        Assert.AreEqual(2_000L, group.LastActivityTimestampQpc);
        Parallel.For(2_001, 10_001, timestamp => LegacyJoyConIdlePolicy.RecordActivity(group, timestamp));
        Assert.AreEqual(10_000L, group.LastActivityTimestampQpc);
    }

    private static bool Check(Switch2AutoDisconnectMode mode, long now, bool wireless = true, int legacy = 10) =>
        LegacyJoyConIdlePolicy.ShouldDisconnect(mode, 10, legacy, wireless, 1_000, 5_000, now, 1_000);
}
