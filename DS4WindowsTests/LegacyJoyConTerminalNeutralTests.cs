using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class LegacyJoyConTerminalNeutralTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void UnlinkOrSecondaryRemovalReleasesRetainedPadAndHeldKbmWithoutAnotherReport(bool rightOwns, bool remove)
    {
        using var input = new SyntheticFixture();
        var output = new RecordingPad();
        var companion = new RecordingPad();
        var links = new LegacyJoyConLinkCoordinator(() => new Projection(),
            (owner, state, _) => output.ConvertandSendReport(state, owner.Slot));
        var left = links.Register(new FakeDevice(InputDeviceType.JoyConL), 0);
        var right = links.Register(new FakeDevice(InputDeviceType.JoyConR), 1);
        var owner = rightOwns ? right : left;
        var secondary = rightOwns ? left : right;
        var controllers = new[] { left.Device, right.Device };
        Assert.IsTrue(links.TryLink(owner, secondary, (_, _) => { }, out var pair));
        links.TryPublish(left, new DS4State { Cross = true }, 1_000, 1_000);
        links.TryPublish(right, new DS4State { Square = true }, 1_001, 1_000);
        companion.ConvertandSendReport(new DS4State { Circle = true }, owner.Slot);
        input.Hold(owner.Slot);
        Assert.IsTrue(output.Held && companion.Held);
        Assert.AreEqual(0, input.Handler.KeyReleases);
        int publications = output.Publications;
        int releases = 0;
        void Release(LegacyJoyConConnection retained)
        {
            Assert.AreSame(owner, retained);
            Assert.IsTrue(retained.Paused);
            Assert.IsFalse(Monitor.IsEntered(pair.Gate), "Output and mapper release must run outside the report gate.");
            Assert.IsTrue(LegacyJoyConTerminalNeutral.TryRelease(retained, retained.Group, controllers,
                output, companion, () => Mapping.Commit(retained.Slot)));
            releases++;
        }
        if (remove) Assert.AreSame(owner, links.Remove(secondary.Device, Release));
        else Assert.IsTrue(links.TryUnlink(pair, _ => Release(owner)));
        // Deliberately no TryPublish here: the missing follow-up report is the regression.
        Assert.AreEqual(1, releases);
        Assert.AreEqual(publications, output.Publications);
        Assert.AreEqual(1, output.Resets);
        Assert.AreEqual(1, companion.Resets);
        Assert.IsFalse(output.Held || companion.Held);
        Assert.AreEqual(1, input.Handler.KeyReleases);
        Assert.AreEqual(1, input.Handler.MouseReleases);
        Assert.IsFalse(owner.Paused);
        Assert.AreEqual(0, output.Disconnections + companion.Disconnections);
    }

    [TestMethod]
    public void ReusedSlotUnpausedOwnerAndOldGroupCannotResetAnyOutput()
    {
        var device = new FakeDevice(InputDeviceType.JoyConL);
        var owner = new LegacyJoyConConnection(device, 0, 1);
        var group = new LegacyJoyConGroup(owner, null, 0, new Projection());
        owner.Group = group;
        var controllers = new DS4Device[] { device };
        var output = new RecordingPad();
        bool Try() => LegacyJoyConTerminalNeutral.TryRelease(owner, group, controllers,
            output, null, () => Assert.Fail("A stale credential must not release mapper state."));
        Assert.IsFalse(Try(), "Live/unpaused reports are not a terminal-release boundary.");
        owner.Paused = true;
        controllers[0] = new FakeDevice(InputDeviceType.JoyConL);
        Assert.IsFalse(Try());
        controllers[0] = device;
        owner.Group = new LegacyJoyConGroup(owner, null, 0, new Projection());
        Assert.IsFalse(Try());
        Assert.AreEqual(0, output.Resets);
    }

    [TestMethod]
    public void MappingFailureStillResetsNativeAndCompanionWithoutDestroyingThem()
    {
        var device = new FakeDevice(InputDeviceType.JoyConL);
        var owner = new LegacyJoyConConnection(device, 0, 1) { Paused = true };
        owner.Group = new LegacyJoyConGroup(owner, null, 0, new Projection());
        var native = new RecordingPad();
        var companion = new RecordingPad();
        Assert.ThrowsException<InvalidOperationException>(() => LegacyJoyConTerminalNeutral.TryRelease(
            owner, owner.Group, new DS4Device[] { device }, native, companion,
            () => throw new InvalidOperationException("Injected mapper failure")));
        Assert.AreEqual(1, native.Resets);
        Assert.AreEqual(1, companion.Resets);
        Assert.AreEqual(0, native.Disconnections + companion.Disconnections);
    }

    [TestMethod]
    public void RemovingOwnerDoesNotReleaseUnrelatedSurvivorSlotAndReleaseFailureDoesNotStrandPause()
    {
        var links = new LegacyJoyConLinkCoordinator(() => new Projection(), (_, _, _) => { });
        var left = links.Register(new FakeDevice(InputDeviceType.JoyConL), 0);
        var right = links.Register(new FakeDevice(InputDeviceType.JoyConR), 1);
        links.TryLink(left, right, (_, _) => { }, out _);
        Assert.AreSame(right, links.Remove(left.Device, _ => Assert.Fail("Removed owner's normal retirement owns its output.")));
        Assert.IsFalse(right.Paused);
        var newLeft = links.Register(new FakeDevice(InputDeviceType.JoyConL), 0);
        links.TryLink(right, newLeft, (_, _) => { }, out _);
        Assert.ThrowsException<InvalidOperationException>(() => links.Remove(newLeft.Device,
            _ => throw new InvalidOperationException("Injected neutral callback failure")));
        Assert.IsFalse(right.Paused);
        Assert.IsTrue(right.Group.Active);
    }

    private sealed class Projection : ILegacyJoyConProfileProjection
    {
        public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination)
        { destination.Cross = input.Left?.Cross ?? false; destination.Square = input.Right?.Square ?? false; return true; }
    }
    private sealed class FakeDevice : DS4Device
    {
        internal FakeDevice(InputDeviceType type) : base("No hardware neutral test", type, ConnectionType.BT) { }
    }
    private sealed class RecordingPad : OutputDevice
    {
        internal bool Held;
        internal int Resets, Publications, Disconnections;
        public override void ConvertandSendReport(DS4State state, int device)
        { Publications++; Held = state.Cross || state.Square || state.Circle; }
        public override void ResetState(bool submit = true) { Resets++; Held = false; }
        public override void Connect() => Assert.Fail("No output recreation allowed.");
        public override void Disconnect() => Disconnections++;
        public override string GetDeviceType() => "No hardware";
        public override void RemoveFeedbacks() { }
        public override void RemoveFeedback(int index) { }
    }
    private sealed class SyntheticFixture : IDisposable
    {
        private readonly VirtualKBMBase oldHandler = Global.outputKBMHandler;
        private readonly VirtualKBMMapping oldMapping = Global.outputKBMMapping;
        private readonly Mapping.SyntheticState oldGlobal = Mapping.globalState;
        private readonly Mapping.SyntheticState[] oldDevices = Mapping.deviceState;
        internal readonly RecordingHandler Handler = new();
        internal SyntheticFixture()
        {
            var mapping = new SendInputMapping(); mapping.PopulateConstants(); mapping.PopulateMappings();
            Global.outputKBMHandler = Handler; Global.outputKBMMapping = mapping;
            Mapping.globalState = new();
            Mapping.deviceState = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new Mapping.SyntheticState()).ToArray();
        }
        internal void Hold(int slot)
        {
            Mapping.deviceState[slot].nativeKeyAlias[65] = 65;
            Mapping.deviceState[slot].keyPresses[65] = new Mapping.SyntheticState.KeyPresses
                { current = new Mapping.SyntheticState.KeyPress { vkCount = 1 } };
            Mapping.MapClick(slot, Mapping.Click.Left);
            Mapping.Commit(slot);
        }
        public void Dispose()
        {
            Global.outputKBMHandler = oldHandler; Global.outputKBMMapping = oldMapping;
            Mapping.globalState = oldGlobal; Mapping.deviceState = oldDevices;
        }
    }
    private sealed class RecordingHandler : VirtualKBMBase
    {
        internal int KeyReleases, MouseReleases;
        public override bool Connect() => true;
        public override bool Disconnect() => true;
        public override void MoveRelativeMouse(int x, int y) { }
        public override void MoveAbsoluteMouse(double x, double y) { }
        public override void PerformMouseWheelEvent(int vertical, int horizontal) { }
        public override void PerformMouseButtonEvent(uint button)
        { if (button == Global.outputKBMMapping.MOUSEEVENTF_LEFTUP) MouseReleases++; }
        public override void PerformMouseButtonPress(uint button) { }
        public override void PerformMouseButtonRelease(uint button) { }
        public override void PerformKeyPress(uint key) { }
        public override void PerformKeyPressAlt(uint key) { }
        public override void PerformKeyRelease(uint key) => KeyReleases++;
        public override void PerformKeyReleaseAlt(uint key) => KeyReleases++;
        public override string GetDisplayName() => "No OS input";
        public override string GetIdentifier() => "neutral-regression";
        public override string GetFullDisplayName() => GetDisplayName();
    }
}
