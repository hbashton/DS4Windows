using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;
using GyroMouse = DS4Windows.Mouse;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2GyroActivationTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;
    private delegate bool Evaluate(string tokens, bool and,
        out int active, out int first, out ulong mask);
    private delegate bool IsActive(GyroOutMode mode, out int tuningIndex);

    [DataTestMethod]
    [DataRow(GyroOutMode.Mouse, false, 32)]
    [DataRow(GyroOutMode.Mouse, true, 32)]
    [DataRow(GyroOutMode.MouseJoystick, false, 32)]
    [DataRow(GyroOutMode.MouseJoystick, true, 32)]
    [DataRow(GyroOutMode.Mouse, false, 33)]
    [DataRow(GyroOutMode.Mouse, true, 33)]
    [DataRow(GyroOutMode.MouseJoystick, false, 33)]
    [DataRow(GyroOutMode.MouseJoystick, true, 33)]
    [DataRow(GyroOutMode.Mouse, false, 34)]
    [DataRow(GyroOutMode.Mouse, true, 34)]
    [DataRow(GyroOutMode.MouseJoystick, false, 34)]
    [DataRow(GyroOutMode.MouseJoystick, true, 34)]
    public void HighTriggerHoldToggleAndTuningEdgesUseRealMouseActivation(
        GyroOutMode mode, bool toggle, int token)
    {
        string previousMouse = Global.SATriggers[Slot];
        string previousJoystick = Global.SAMousestickTriggers[Slot];
        bool previousMouseAnd = Global.SATriggerCond[Slot];
        bool previousJoystickAnd = Global.SAMouseStickTriggerCond[Slot];
        try
        {
            Global.SATriggers[Slot] = Global.SAMousestickTriggers[Slot] = $"2,{token}";
            Global.SATriggerCond[Slot] = Global.SAMouseStickTriggerCond[Slot] = false;
            var state = State(token);
            SetRailPressed(false);
            var mouse = CreateMouse(state);
            mouse.ToggleGyroMouse = toggle;
            mouse.ToggleGyroStick = toggle;
            var active = typeof(GyroMouse).GetMethod("IsGyroTriggerActive",
                BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<IsActive>(mouse);
            Assert.IsFalse(active(mode, out _));
            state.Square = true;
            Assert.IsTrue(active(mode, out int tuning));
            Assert.AreEqual(2, tuning);
            SetRailPressed(true);
            Assert.IsTrue(active(mode, out tuning));
            Assert.AreEqual(2, tuning, "An overlapping hold keeps its activation's tuning.");
            state.Square = false;
            Assert.IsTrue(active(mode, out _));
            SetRailPressed(false);
            Assert.AreEqual(toggle, active(mode, out _));
            SetRailPressed(true);
            Assert.AreEqual(!toggle, active(mode, out tuning));
            Assert.AreEqual(token, tuning, "A fresh high-bit edge selects its own tuning slot.");
            SetRailPressed(false);
            Assert.IsFalse(active(mode, out _));
            SetRailPressed(true);
            Assert.IsTrue(active(mode, out tuning));
            Assert.AreEqual(token, tuning);

            void SetRailPressed(bool pressed)
            {
                state.Switch2JoyConRawInputStatus.LeftRailSR = pressed && token == 32;
                state.Switch2JoyConRawInputStatus.RightRailSL = pressed && token == 33;
                state.Switch2JoyConRawInputStatus.RightRailSR = pressed && token == 34;
                state.Switch2JoyConRawInputStatus.CompletionTimestampQpc++;
            }
        }
        finally
        {
            Global.SATriggers[Slot] = previousMouse;
            Global.SAMousestickTriggers[Slot] = previousJoystick;
            Global.SATriggerCond[Slot] = previousMouseAnd;
            Global.SAMouseStickTriggerCond[Slot] = previousJoystickAnd;
        }
    }

    [DataTestMethod]
    [DataRow(30)]
    [DataRow(31)]
    [DataRow(32)]
    [DataRow(33)]
    [DataRow(34)]
    public void PhysicalSourcesHaveIndependentGyroTokensAndRelease(int token)
    {
        var state = State(token);
        Evaluate evaluate = Evaluator(state);
        string selected = token.ToString();
        Assert.IsTrue(evaluate(selected, false, out int active, out int first,
            out ulong mask));
        Assert.AreEqual(token, active);
        Assert.AreEqual(token, first);
        Assert.AreEqual(1UL << token, mask);
        // A high bit must not alias Cross, Circle or Square (IDs 0..2).
        Assert.IsFalse(evaluate("0,1,2", false, out _, out _, out mask));
        Assert.AreEqual(0UL, mask);
        state.Switch2JoyConRawInputStatus = default;
        Assert.IsFalse(evaluate(selected, false, out _, out _, out mask));
        Assert.AreEqual(0UL, mask);
    }

    [TestMethod]
    public void HighTriggerEdgesSurviveLegacyButtonOverlapWithoutMaskAliasing()
    {
        var state = State(34);
        state.Square = true;
        Evaluate evaluate = Evaluator(state);
        Assert.IsTrue(evaluate("2,34", true, out _, out _, out ulong both));
        Assert.AreEqual((1UL << 2) | (1UL << 34), both);
        state.Square = false;
        Assert.IsTrue(evaluate("2,34", false, out _, out _, out ulong rail));
        Assert.AreEqual(1UL << 34, rail);
        Assert.AreEqual(1UL << 2, both & ~rail);
        Assert.IsFalse(evaluate("2,34", true, out _, out _, out _));
        Assert.IsTrue(evaluate("-1", false, out int active, out _, out ulong always));
        Assert.AreEqual(29, active);
        Assert.AreEqual(1UL << 29, always);
        Assert.IsFalse(evaluate("29,35,64,garbage", false, out _, out _, out _),
            "The tuning-only ID 29 must not become an activation token.");
    }

    [TestMethod]
    public void RailsRequireExclusiveCurrentJoyConSourceAndPresentHalf()
    {
        foreach (int token in new[] { 31, 32, 33, 34 })
        {
            var state = State(token);
            Evaluate evaluate = Evaluator(state);
            state.Switch2JoyConRawInputStatus.ContractVersion = 2;
            Assert.IsFalse(evaluate(token.ToString(), false, out _, out _, out _));
            state.Switch2JoyConRawInputStatus.ContractVersion =
                Switch2JoyConProfileInputFrame.CurrentVersion;
            state.Switch2JoyConRawInputStatus.LeftPresent = false;
            state.Switch2JoyConRawInputStatus.RightPresent = false;
            Assert.IsFalse(evaluate(token.ToString(), false, out _, out _, out _));
            state.Switch2JoyConRawInputStatus = State(token).Switch2JoyConRawInputStatus;
            state.Switch2RawInputStatus = new() { IsValid = true,
                ContractVersion = Switch2ProProfileInputFrame.CurrentVersion };
            Assert.IsFalse(evaluate(token.ToString(), false, out _, out _, out _));
            Assert.IsFalse(Switch2GyroTriggerModifier.TryReadInput(state, 1, 1,
                true, out _));
        }
    }

    [TestMethod]
    public void ProCAndPaddlesIgnoreAnObsoleteJoyConSidecar()
    {
        var state = State(31);
        state.Switch2JoyConRawInputStatus.ContractVersion = 2;
        state.BRP = true;
        state.Switch2RawInputStatus = new() { IsValid = true,
            ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
            DeviceGeneration = 2, TransportGeneration = 3,
            CompletionTimestampQpc = 100, QpcFrequency = 1_000_000,
            CButton = true };
        Assert.IsTrue(Evaluator(state)("30", false, out _, out _, out _));
        Assert.IsTrue(Switch2GyroTriggerModifier.TryReadInput(state, 1, 1,
            true, out var input));
        Assert.IsFalse(input.Identity.JoyCon);
        Assert.AreEqual(Switch2JoyConProfileButton.C |
            Switch2JoyConProfileButton.RightPaddle1, input.Buttons);
    }

    [TestMethod]
    public void WarmActivationParserAllocatesNothingWithHighSourceBits()
    {
        Evaluate evaluate = Evaluator(State(34));
        bool result = true;
        for (int i = 0; i < 2_000; i++)
            result &= evaluate("0,30,31,32,33,34", false, out _, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
            result &= evaluate("0,30,31,32,33,34", false, out _, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(result);
        Assert.AreEqual(0L, allocated);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void AllGyroMenusPersistTokensIndependentlyOfDisplayOrder(int mode)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            string previous = Read();
            var previousOutput = Global.outDevTypeTemp[Slot];
            try
            {
                // Initialize WPF before the view model loads pack resources;
                // this test must also run independently of the other UI tests.
                var menu = new ContextMenu();
                var vm = new ProfileSettingsViewModel(Slot);
                vm.CreateGyroTriggerMenuItems(menu, (_, _) => { });
                var items = menu.Items.Cast<MenuItem>().ToArray();
                CollectionAssert.AreEqual(Enumerable.Range(0, 29)
                    .Concat(Enumerable.Range(30, 5)).Append(-1).ToArray(),
                    items.Select(item => (int)item.Tag).ToArray());
                Assert.AreEqual("Always On", items[^1].Header);
                for (int token = 30; token <= 34; token++)
                {
                    Write(token.ToString());
                    Populate();
                    Assert.AreEqual(token, (int)items.Single(i => i.IsChecked).Tag);
                    Update(false);
                    Assert.AreEqual(token.ToString(), Read());
                }
                Write("0,28,32,34");
                Populate();
                Update(false);
                Assert.AreEqual("0,28,32,34", Read());
                Update(true);
                Assert.AreEqual("-1", Read());
                Assert.AreEqual(-1, (int)items.Single(i => i.IsChecked).Tag);
                Write("31");
                Populate();
                Write("32");
                Populate();
                Assert.AreEqual(32, (int)items.Single(i => i.IsChecked).Tag,
                    "Reload must clear the previous profile's checked items.");
                foreach (string invalid in new[] { "29", "35", "bad", "0,35", "-1,35" })
                {
                    Write(invalid);
                    Populate();
                    Assert.AreEqual(invalid, Read(), "Reading must not rewrite profile tokens.");
                    string display = mode switch {
                        0 => vm.GyroMouseTrigDisplay, 1 => vm.GyroMouseStickTrigDisplay,
                        2 => vm.GyroSwipeTrigDisplay, _ => vm.GyroControlsTrigDisplay };
                    StringAssert.Contains(display, "Unsupported trigger");
                    Assert.AreEqual(invalid.Contains("-1"), items[^1].IsChecked);
                }
                Write(string.Empty);
                Populate();
                Assert.IsFalse(items.Any(i => i.IsChecked));

                void Populate()
                {
                    switch (mode)
                    {
                        case 0: vm.PopulateGyroMouseTrig(menu); break;
                        case 1: vm.PopulateGyroMouseStickTrig(menu); break;
                        case 2: vm.PopulateGyroSwipeTrig(menu); break;
                        case 3: vm.PopulateGyroControlsTrig(menu); break;
                    }
                }
                void Update(bool always)
                {
                    switch (mode)
                    {
                        case 0: vm.UpdateGyroMouseTrig(menu, always); break;
                        case 1: vm.UpdateGyroMouseStickTrig(menu, always); break;
                        case 2: vm.UpdateGyroSwipeTrig(menu, always); break;
                        case 3: vm.UpdateGyroControlsTrig(menu, always); break;
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Write(previous); Global.outDevTypeTemp[Slot] = previousOutput; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) Assert.Fail(failure.ToString());

        string Read() => mode switch {
            0 => Global.SATriggers[Slot], 1 => Global.SAMousestickTriggers[Slot],
            2 => Global.GyroSwipeInf[Slot].triggers, _ => Global.GyroControlsInf[Slot].triggers };
        void Write(string value)
        {
            switch (mode)
            {
                case 0: Global.SATriggers[Slot] = value; break;
                case 1: Global.SAMousestickTriggers[Slot] = value; break;
                case 2: Global.GyroSwipeInf[Slot].triggers = value; break;
                case 3: Global.GyroControlsInf[Slot].triggers = value; break;
            }
        }
    }

    private static DS4State State(int token) => new() {
        Switch2JoyConRawInputStatus = new() {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            Mode = Switch2JoyConProfileMode.Joined, PairEpoch = 1,
            LeftDeviceGeneration = 1, LeftTransportGeneration = 1,
            RightDeviceGeneration = 2, RightTransportGeneration = 2,
            CompletionTimestampQpc = 1_000_000, QpcFrequency = 1_000_000,
            LeftPresent = true, RightPresent = true, CButton = token == 30,
            LeftRailSL = token == 31, LeftRailSR = token == 32,
            RightRailSL = token == 33, RightRailSR = token == 34,
        } };

    private static Evaluate Evaluator(DS4State state)
    {
        return typeof(GyroMouse).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "EvaluateGyroTriggers" && m.GetParameters().Length == 5)
            .CreateDelegate<Evaluate>(CreateMouse(state));
    }

    private static GyroMouse CreateMouse(DS4State state)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1,
            Switch2Transport.Usb, out var device, out _));
        // In-memory runtime only: never start input/output or open a transport.
        var mouse = new GyroMouse(Slot, device);
        typeof(GyroMouse).GetField("s", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(mouse, state);
        return mouse;
    }
}
