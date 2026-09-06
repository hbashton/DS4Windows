using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2GyroActivationOrientationTests
{
    private delegate bool IsActive(GyroOutMode mode, out int tuningIndex);

    [DataTestMethod]
    [DataRow(GyroOutMode.Mouse, true, true)]
    [DataRow(GyroOutMode.Mouse, true, false)]
    [DataRow(GyroOutMode.Mouse, false, true)]
    [DataRow(GyroOutMode.Mouse, false, false)]
    [DataRow(GyroOutMode.MouseJoystick, true, true)]
    [DataRow(GyroOutMode.MouseJoystick, true, false)]
    [DataRow(GyroOutMode.MouseJoystick, false, true)]
    [DataRow(GyroOutMode.MouseJoystick, false, false)]
    [DoNotParallelize]
    public void OrientationRefreshesActiveTriggerTuningWithoutChangingToggle(GyroOutMode mode, bool left, bool sl)
    {
        var previousStore = Global.store;
        FieldInfo storeField = typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            storeField.SetValue(null, new BackingStore());
            const int slot = Global.TEST_PROFILE_INDEX;
            int shoulder = sl ? 4 : 6;
            int rail = left ? (sl ? 31 : 32) : (sl ? 33 : 34);
            Global.SATriggers[slot] = Global.SAMousestickTriggers[slot] = $"{shoulder},{rail}";
            Global.SATriggerCond[slot] = Global.SAMouseStickTriggerCond[slot] = false;
            foreach (bool toggle in new[] { false, true })
            {
                Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1,
                    Switch2Transport.Usb, out var runtime, out _));
                var mouse = new Mouse(slot, runtime) { ToggleGyroMouse = toggle, ToggleGyroStick = toggle };
                var source = Source(left);
                typeof(Mouse).GetField("s", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mouse, source);
                var active = typeof(Mouse).GetMethod("IsGyroTriggerActive", BindingFlags.Instance | BindingFlags.NonPublic)
                    .CreateDelegate<IsActive>(mouse);
                Read(false, false, false, shoulder);
                Read(false, true, true, rail);
                Read(true, true, true, shoulder); // unchanged physical SL/SR, new logical alias
                Read(false, true, true, rail);
                Read(false, false, toggle, rail);
                Read(false, true, !toggle, rail);
                Read(true, true, !toggle, shoulder); // keep an OFF toggle off, too

                void Read(bool horizontal, bool pressed, bool expected, int expectedTuning)
                {
                    source.Switch2JoyConRawInputStatus.Mode = left ?
                        (horizontal ? Switch2JoyConProfileMode.StandaloneHorizontalLeft : Switch2JoyConProfileMode.StandaloneVerticalLeft) :
                        (horizontal ? Switch2JoyConProfileMode.StandaloneHorizontalRight : Switch2JoyConProfileMode.StandaloneVerticalRight);
                    source.L1 = sl && horizontal && pressed;
                    source.R1 = !sl && horizontal && pressed;
                    source.Switch2JoyConRawInputStatus.LeftRailSL = pressed && left && sl;
                    source.Switch2JoyConRawInputStatus.LeftRailSR = pressed && left && !sl;
                    source.Switch2JoyConRawInputStatus.RightRailSL = pressed && !left && sl;
                    source.Switch2JoyConRawInputStatus.RightRailSR = pressed && !left && !sl;
                    source.Switch2JoyConRawInputStatus.CompletionTimestampQpc++;
                    Assert.AreEqual(expected, active(mode, out int tuning));
                    Assert.AreEqual(expectedTuning, tuning);
                }
            }
        }
        finally { storeField.SetValue(null, previousStore); }
    }

    [TestMethod]
    public void ObserverRequiresSameLifetimeAndExactStandaloneSource()
    {
        foreach (bool left in new[] { false, true })
        {
            var source = Source(left);
            var observer = new Switch2GyroActivationOrientation();
            Assert.IsFalse(observer.Observe(source));
            Horizontal(source, left);
            Assert.IsTrue(observer.Observe(source));
            Assert.IsFalse(observer.Observe(source));
            Assert.IsFalse(observer.Observe(null));
            Assert.IsFalse(observer.Observe(Source(left)), "Unknown gaps are not orientation transitions.");

            // Neither a new physical lifetime nor malformed topology is a
            // reason to intercept the ordinary gyro policy's activation edge.
            for (int variation = 0; variation < 12; variation++)
            {
                source = Source(left);
                observer = default;
                Assert.IsFalse(observer.Observe(source));
                Horizontal(source, left);
                switch (variation)
                {
                    case 0: source.Switch2JoyConRawInputStatus.IsValid = false; break;
                    case 1: source.Switch2JoyConRawInputStatus.ContractVersion--; break;
                    case 2: source.Switch2JoyConRawInputStatus.PairEpoch = 1; break;
                    case 3: source.Switch2JoyConRawInputStatus.Mode = Switch2JoyConProfileMode.Invalid; break;
                    case 4: source.Switch2JoyConRawInputStatus.Mode = Switch2JoyConProfileMode.Joined; break;
                    case 5:
                        source.Switch2JoyConRawInputStatus.LeftPresent = false;
                        source.Switch2JoyConRawInputStatus.RightPresent = false; break;
                    case 6:
                        source.Switch2JoyConRawInputStatus.LeftPresent = true;
                        source.Switch2JoyConRawInputStatus.RightPresent = true; break;
                    case 7:
                        if (left) source.Switch2JoyConRawInputStatus.LeftDeviceGeneration++;
                        else source.Switch2JoyConRawInputStatus.RightDeviceGeneration++; break;
                    case 8:
                        if (left) source.Switch2JoyConRawInputStatus.LeftTransportGeneration = 0;
                        else source.Switch2JoyConRawInputStatus.RightTransportGeneration = 0; break;
                    case 9:
                        source.Switch2RawInputStatus = new() { IsValid = true,
                            ContractVersion = Switch2ProProfileInputFrame.CurrentVersion }; break;
                    case 10:
                        if (left) source.Switch2JoyConRawInputStatus.RightDeviceGeneration = 9;
                        else source.Switch2JoyConRawInputStatus.LeftDeviceGeneration = 9; break;
                    case 11:
                        if (left) source.Switch2JoyConRawInputStatus.RightTransportGeneration = 9;
                        else source.Switch2JoyConRawInputStatus.LeftTransportGeneration = 9; break;
                }
                Assert.IsFalse(observer.Observe(source), $"variation {variation}, left={left}");
            }
            source = Source(left);
            source.Switch2RawInputStatus = new() { IsValid = true,
                ContractVersion = Switch2ProProfileInputFrame.CurrentVersion - 1 };
            observer = default;
            Assert.IsFalse(observer.Observe(source));
            Horizontal(source, left);
            Assert.IsTrue(observer.Observe(source), "An obsolete Pro sidecar does not override current Joy-Con input.");
        }
    }

    [DataTestMethod]
    [DataRow(GyroOutMode.Controls)]
    [DataRow(GyroOutMode.Mouse)]
    [DataRow(GyroOutMode.MouseJoystick)]
    [DoNotParallelize]
    public void WarmOrdinaryActivationWithOrientationChangesAllocatesNothing(GyroOutMode mode)
    {
        var previousStore = Global.store;
        FieldInfo storeField = typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            storeField.SetValue(null, new BackingStore());
            const int slot = Global.TEST_PROFILE_INDEX;
            Global.SATriggers[slot] = Global.SAMousestickTriggers[slot] =
                Global.GyroControlsInf[slot].triggers = "4,31";
            Global.SATriggerCond[slot] = Global.SAMouseStickTriggerCond[slot] =
                Global.GyroControlsInf[slot].triggerCond = false;
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1,
                Switch2Transport.Usb, out var runtime, out _));
            var source = Source(true);
            source.Switch2JoyConRawInputStatus.LeftRailSL = true;
            var mouse = new Mouse(slot, runtime) {
                ToggleGyroControls = false, ToggleGyroMouse = false, ToggleGyroStick = false };
            typeof(Mouse).GetField("s", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mouse, source);
            bool active = true;
            for (int i = 0; i < 2_000; i++) Step(i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20_000; i++) Step(i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(active);
            Assert.AreEqual(0L, allocated);

            void Step(int i)
            {
                source.L1 = (i & 1) == 0;
                source.Switch2JoyConRawInputStatus.Mode = source.L1 ?
                    Switch2JoyConProfileMode.StandaloneHorizontalLeft : Switch2JoyConProfileMode.StandaloneVerticalLeft;
                source.Switch2JoyConRawInputStatus.CompletionTimestampQpc++;
                active &= mouse.IsGyroTriggerActive(mode);
            }
        }
        finally { storeField.SetValue(null, previousStore); }
    }

    private static DS4State Source(bool left) => new() {
        Switch2JoyConRawInputStatus = new() {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            Mode = left ? Switch2JoyConProfileMode.StandaloneVerticalLeft : Switch2JoyConProfileMode.StandaloneVerticalRight,
            LeftPresent = left, RightPresent = !left,
            LeftDeviceGeneration = left ? 1UL : 0, LeftTransportGeneration = left ? 2UL : 0,
            RightDeviceGeneration = left ? 0 : 1UL, RightTransportGeneration = left ? 0 : 2UL,
            CompletionTimestampQpc = 1_000_000, QpcFrequency = 1_000_000 } };

    private static void Horizontal(DS4State source, bool left) =>
        source.Switch2JoyConRawInputStatus.Mode = left ?
            Switch2JoyConProfileMode.StandaloneHorizontalLeft : Switch2JoyConProfileMode.StandaloneHorizontalRight;
}
