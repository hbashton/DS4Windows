using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProfilePickerTests
{
    private static readonly Switch2ProfilePickerContext ProContext = new(true,
        Switch2JoyConProfileMode.Invalid, Switch2Transport.Usb, 1, 2, 0, 0, 0, 1_000_000, Switch2FaceButtonLayout.Xbox);

    [DataTestMethod]
    [DataRow(true, Switch2FaceButtonLayout.Xbox)]
    [DataRow(false, Switch2FaceButtonLayout.Xbox)]
    [DataRow(true, Switch2FaceButtonLayout.Nintendo)]
    [DataRow(false, Switch2FaceButtonLayout.Nintendo)]
    public void ProUsesRealDecoderAndPhysicalFaceLayout(bool usb, Switch2FaceButtonLayout layout)
    {
        foreach (var (raw, xbox) in new[] {
            (0x04u, Switch2ProfilePickerButtons.Confirm), (0x08u, Switch2ProfilePickerButtons.Cancel),
            (0x00020000u, Switch2ProfilePickerButtons.Up), (0x00010000u, Switch2ProfilePickerButtons.Down) })
        {
            var frame = Pro(usb, raw);
            Assert.IsTrue(Switch2ProfilePickerInput.TryFromPro(frame, layout, false, out var input));
            var expected = layout == Switch2FaceButtonLayout.Nintendo ? xbox switch
            {
                Switch2ProfilePickerButtons.Confirm => Switch2ProfilePickerButtons.Cancel,
                Switch2ProfilePickerButtons.Cancel => Switch2ProfilePickerButtons.Confirm,
                _ => xbox,
            } : xbox;
            Assert.AreEqual(expected, input.Buttons);
            Assert.AreEqual(usb ? Switch2Transport.Usb : Switch2Transport.BluetoothLe, input.Context.Transport);
        }
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromPro(Pro(usb, 0, 2048, 4095), layout, false, out var up));
        Assert.AreEqual(Switch2ProfilePickerButtons.Up, up.Buttons);
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromPro(Pro(usb, 0, 2048, 0), layout, false, out var down));
        Assert.AreEqual(Switch2ProfilePickerButtons.Down, down.Buttons);
    }

    [DataTestMethod]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalLeft, 0x00010000u, 0x00040000u)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0x00080000u, 0x00010000u)]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalRight, 0x00000004u, 0x00000008u)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalRight, 0x00000008u, 0x00000002u)]
    public void StandalonePhysicalConfirmCancelMatchesPinnedPickerInBothLayouts(
        Switch2JoyConProfileMode mode, uint bottom, uint east)
    {
        foreach (var layout in new[] { Switch2FaceButtonLayout.Xbox, Switch2FaceButtonLayout.Nintendo })
        {
            Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(JoyCon(mode, bottom), layout, true, out var bottomInput));
            Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(JoyCon(mode, east), layout, true, out var eastInput));
            Assert.AreEqual(layout == Switch2FaceButtonLayout.Xbox ? Switch2ProfilePickerButtons.Confirm :
                Switch2ProfilePickerButtons.Cancel, bottomInput.Buttons, "A/B cannot also cycle or navigate.");
            Assert.AreEqual(layout == Switch2FaceButtonLayout.Xbox ? Switch2ProfilePickerButtons.Cancel :
                Switch2ProfilePickerButtons.Confirm, eastInput.Buttons);
        }
    }

    [DataTestMethod]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalLeft, 2048, 4095)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalLeft, 4095, 2048)]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalRight, 2048, 4095)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalRight, 0, 2048)]
    public void StandaloneNavigatesWithHeldOrientationStickOnly(Switch2JoyConProfileMode mode, int upX, int upY)
    {
        var frame = JoyCon(mode, 0, (ushort)upX, (ushort)upY);
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(frame, Switch2FaceButtonLayout.Xbox, false, out var input));
        Assert.AreEqual(Switch2ProfilePickerButtons.Up, input.Buttons);
        if (Switch2JoyConProfileInputMapper.IsStandaloneLeftMode(mode))
        {
            Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(JoyCon(mode, 0x00020000),
                Switch2FaceButtonLayout.Xbox, false, out input));
            Assert.AreEqual(Switch2ProfilePickerButtons.None, input.Buttons, "Standalone directional face buttons aren't a D-pad.");
        }
    }

    [TestMethod]
    public void JoinedPairHasOneNavigationStreamAndRightSideConfirmation()
    {
        var left = Canonical(Switch2ControllerModel.JoyCon2Left, false, 0x00020000, 2048, 4095);
        var right = Canonical(Switch2ControllerModel.JoyCon2Right, false, 0x04, 2048, 4095);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(3, left.Descriptor, right.Descriptor, out var mapper));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(mapper,
            new Switch2JoyConPairSnapshot(3, left, right, 0), out _, out var frame, out _));
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(frame, Switch2FaceButtonLayout.Xbox, true, out var input));
        Assert.AreEqual(Switch2ProfilePickerButtons.Up | Switch2ProfilePickerButtons.Confirm, input.Buttons);
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, input with { Buttons = 0 }, out var session));
        Assert.IsTrue(session.TryObserve(input, 7));
        Assert.AreEqual(0, session.SelectedIndex, "Both halves' same up direction produces one step, not two.");
        Assert.AreEqual(Switch2ProfilePickerOutcome.Confirmed, session.Outcome);
    }

    [TestMethod]
    public void InvalidInputsCatalogsAndContextsAreRejected()
    {
        Assert.IsFalse(Switch2ProfilePickerInput.TryFromPro(default, Switch2FaceButtonLayout.Xbox, false, out _));
        Assert.IsFalse(Switch2ProfilePickerInput.TryFromJoyCon(default, Switch2FaceButtonLayout.Xbox, false, out _));
        Assert.IsFalse(Switch2ProfilePickerInput.TryFromPro(Pro(true, 0), (Switch2FaceButtonLayout)99, false, out _));
        var valid = Input(0);
        foreach (var (count, current, revision) in new[] { (0, 0, 1L), (-1, -1, 1L), (3, 3, 1L), (3, -2, 1L), (3, 0, -1L) })
            Assert.IsFalse(Switch2ProfilePickerSession.TryBegin(count, current, revision, valid, out _));
        foreach (var invalid in new[] { default(Switch2ProfilePickerInput), valid with { TimestampQpc = -1 },
            valid with { Buttons = (Switch2ProfilePickerButtons)128 },
            valid with { Context = ProContext with { QpcFrequency = 0 } },
            valid with { Context = ProContext with { PairEpoch = 2 } } })
            Assert.IsFalse(Switch2ProfilePickerSession.TryBegin(3, 0, 1, invalid, out _));
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(int.MaxValue, int.MaxValue - 1, 0, valid, out var largest));
        Assert.AreEqual(0, largest.SelectedIndex);
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(1, -1, 0, valid, out var single));
        Assert.AreEqual(0, single.SelectedIndex);
    }

    [TestMethod]
    public void HeldEntryButtonsDoNotFireUntilReleasedAndPressedAgain()
    {
        foreach (var button in new[] { Switch2ProfilePickerButtons.Up, Switch2ProfilePickerButtons.Down,
            Switch2ProfilePickerButtons.Cycle, Switch2ProfilePickerButtons.Confirm, Switch2ProfilePickerButtons.Cancel })
        {
            Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(4, 0, 7, Input(0, button), out var session));
            Assert.IsTrue(session.TryObserve(Input(10_000, button), 7));
            Assert.AreEqual(1, session.SelectedIndex);
            Assert.AreEqual(Switch2ProfilePickerOutcome.None, session.Outcome);
            Assert.IsTrue(session.TryObserve(Input(20_000), 7));
            Assert.IsTrue(session.TryObserve(Input(20_000, button), 7), "Distinct admitted edges may share QPC ticks.");
            if (button == Switch2ProfilePickerButtons.Confirm) Assert.AreEqual(Switch2ProfilePickerOutcome.Confirmed, session.Outcome);
            else if (button == Switch2ProfilePickerButtons.Cancel) Assert.AreEqual(Switch2ProfilePickerOutcome.Cancelled, session.Outcome);
            else Assert.AreNotEqual(1, session.SelectedIndex);
        }
    }

    [TestMethod]
    public void NavigationWrapsDebouncesAndNeverAutoRepeatsHeldInput()
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 2, 7, Input(0), out var session));
        Observe(1, Switch2ProfilePickerButtons.Up); Assert.AreEqual(2, session.SelectedIndex);
        Observe(2, 0); Observe(179_999, Switch2ProfilePickerButtons.Up); Assert.AreEqual(2, session.SelectedIndex);
        Observe(1_000_000, Switch2ProfilePickerButtons.Up); Assert.AreEqual(2, session.SelectedIndex, "Held input must not become repeat after debounce.");
        Observe(1_000_001, 0); Observe(1_000_002, Switch2ProfilePickerButtons.Cycle); Assert.AreEqual(0, session.SelectedIndex);
        Observe(1_000_003, 0); Observe(1_180_002, Switch2ProfilePickerButtons.Down); Assert.AreEqual(1, session.SelectedIndex);
        Observe(1_180_003, 0); Observe(2_000_000, Switch2ProfilePickerButtons.Up | Switch2ProfilePickerButtons.Down);
        Assert.AreEqual(1, session.SelectedIndex, "Opposing directions must not issue two navigations.");
        void Observe(long time, Switch2ProfilePickerButtons buttons) => Assert.IsTrue(session.TryObserve(Input(time, buttons), 7));
    }

    [TestMethod]
    public void ConfirmCancelAreImmediateAndAllConsumedControlsDrainWithoutRepress()
    {
        foreach (bool cancel in new[] { false, true })
        {
            Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
            session.TryObserve(Input(1, Switch2ProfilePickerButtons.Down), 7);
            var terminal = Switch2ProfilePickerButtons.Confirm | (cancel ? Switch2ProfilePickerButtons.Cancel : 0);
            session.TryObserve(Input(2, terminal | Switch2ProfilePickerButtons.Down), 7);
            Assert.AreEqual(cancel ? Switch2ProfilePickerOutcome.Cancelled : Switch2ProfilePickerOutcome.Confirmed, session.Outcome);
            Assert.AreEqual(2, session.SelectedIndex);
            if (!cancel) Assert.IsTrue(session.TryTakeConfirmation(7, ProContext, out _));
            foreach (var held in new[] { terminal, Switch2ProfilePickerButtons.Down, Switch2ProfilePickerButtons.Cycle })
            {
                session.TryObserve(Input(3, held), 7);
                Assert.IsTrue(session.InputSuppressed);
                Assert.AreEqual(2, session.SelectedIndex);
            }
            session.TryObserve(Input(4), 7);
            Assert.IsFalse(session.InputSuppressed);
            session.TryObserve(Input(5, Switch2ProfilePickerButtons.Down), 7);
            Assert.IsFalse(session.InputSuppressed, "Completed reducer cannot suppress/reopen a later ordinary press.");
            Assert.AreEqual(2, session.SelectedIndex);
        }
    }

    [TestMethod]
    public void StaleForeignAndChangedProfilesCannotConfirmOrReleaseCurrentPicker()
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(100), out var session));
        Assert.IsFalse(session.TryObserve(Input(99, Switch2ProfilePickerButtons.Confirm), 7));
        foreach (var context in new[] { ProContext with { LeftDeviceGeneration = 3 }, ProContext with { LeftTransportGeneration = 3 },
            ProContext with { Transport = Switch2Transport.BluetoothLe },
            ProContext with { QpcFrequency = 10_000_000 } })
            Assert.IsFalse(session.TryObserve(Input(101, Switch2ProfilePickerButtons.Confirm) with { Context = context }, 7));
        Assert.AreEqual(Switch2ProfilePickerOutcome.None, session.Outcome);
        Assert.IsTrue(session.InputSuppressed);
        Assert.IsTrue(session.TryObserve(Input(102, Switch2ProfilePickerButtons.Confirm), 8));
        Assert.AreEqual(Switch2ProfilePickerOutcome.Invalidated, session.Outcome);
        Assert.IsTrue(session.InputSuppressed);
        Assert.IsTrue(session.TryObserve(Input(103), 8));
        Assert.IsFalse(session.InputSuppressed);
    }

    [TestMethod]
    public void ConfirmationIsOneShotAndReleaseDrainSurvivesItsOwnProfileAndLayoutChange()
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
        session.TryObserve(Input(1, Switch2ProfilePickerButtons.Confirm), 7);
        Assert.IsTrue(session.TryTakeConfirmation(7, ProContext, out int index));
        Assert.AreEqual(1, index);
        Assert.IsFalse(session.TryTakeConfirmation(7, ProContext, out _));
        var changed = Input(2) with { Context = ProContext with { Layout = Switch2FaceButtonLayout.Nintendo }, PhysicalControlsHeld = true };
        Assert.IsTrue(session.TryObserve(changed, 8));
        Assert.AreEqual(Switch2ProfilePickerOutcome.Confirmed, session.Outcome);
        Assert.IsTrue(session.InputSuppressed, "Physical held input remains blocked even if the new UI semantic differs.");
        Assert.IsTrue(session.TryObserve(changed with { TimestampQpc = 3, PhysicalControlsHeld = false }, 8));
        Assert.IsFalse(session.InputSuppressed);
        Assert.IsFalse(session.TryTakeConfirmation(8, changed.Context, out _));
    }

    [TestMethod]
    public void PendingConfirmationCanBeCancelledOrInvalidatedBeforeIntentTransfer()
    {
        foreach (int change in new[] { 0, 1, 2 })
        {
            Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
            session.TryObserve(Input(1, Switch2ProfilePickerButtons.Confirm), 7);
            if (change == 0) session.Cancel();
            else if (change == 1) session.TryObserve(Input(2) with { Context = ProContext with { Layout = Switch2FaceButtonLayout.Nintendo } }, 7);
            Assert.IsFalse(session.TryTakeConfirmation(change == 2 ? 8 : 7, ProContext, out _));
            Assert.AreEqual(change == 0 ? Switch2ProfilePickerOutcome.Cancelled : Switch2ProfilePickerOutcome.Invalidated, session.Outcome);
        }
    }

    [TestMethod]
    public void PhysicalDrainSurvivesStandaloneOrientationChangeWithoutNewSwitchAuthority()
    {
        var horizontal = JoyCon(Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0x00080000);
        var vertical = JoyCon(Switch2JoyConProfileMode.StandaloneVerticalLeft, 0x00080000);
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(horizontal, Switch2FaceButtonLayout.Xbox, false, out var initial));
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(vertical, Switch2FaceButtonLayout.Xbox, false, out var changed));
        Assert.AreEqual(Switch2ProfilePickerButtons.Confirm, initial.Buttons);
        Assert.AreEqual(Switch2ProfilePickerButtons.None, changed.Buttons);
        Assert.IsTrue(changed.PhysicalControlsHeld);
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, initial, out var session));
        Assert.IsTrue(session.TryObserve(changed, 7));
        Assert.AreEqual(Switch2ProfilePickerOutcome.Invalidated, session.Outcome);
        Assert.IsTrue(session.InputSuppressed);
        Assert.IsFalse(session.TryTakeConfirmation(7, changed.Context, out _));
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromJoyCon(JoyCon(Switch2JoyConProfileMode.StandaloneVerticalLeft, 0),
            Switch2FaceButtonLayout.Xbox, false, out var released));
        Assert.IsTrue(session.TryObserve(released, 7));
        Assert.IsFalse(session.InputSuppressed);
    }

    [TestMethod]
    public void ReleasedConfirmCannotResumeGameplayWhileIntentTransferIsDelayed()
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
        session.TryObserve(Input(1, Switch2ProfilePickerButtons.Confirm), 7);
        session.TryObserve(Input(2), 7);
        Assert.IsTrue(session.InputSuppressed, "Neutral is release evidence, not permission to resume before intent transfer.");
        session.TryObserve(Input(3, Switch2ProfilePickerButtons.Up), 7);
        Assert.IsTrue(session.InputSuppressed);
        Assert.AreEqual(1, session.SelectedIndex, "Pending confirmation freezes selection while the worker is delayed.");
        session.Cancel();
        Assert.IsTrue(session.InputSuppressed);
        session.TryObserve(Input(4), 7);
        Assert.IsFalse(session.InputSuppressed);
    }

    [TestMethod]
    public void TransferRevalidatesBasisWithoutWaitingForAnotherReport()
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
        session.TryObserve(Input(1, Switch2ProfilePickerButtons.Confirm), 7);
        Assert.IsFalse(session.TryTakeConfirmation(7, ProContext with { Layout = Switch2FaceButtonLayout.Nintendo }, out _));
        Assert.AreEqual(Switch2ProfilePickerOutcome.Invalidated, session.Outcome);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void NeutralBeforeTransferWaitsForTransferOrRevocationAndThenNeverRepresses(int finish)
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
        session.TryObserve(Input(1, Switch2ProfilePickerButtons.Confirm), 7);
        Assert.IsFalse(session.TryObserve(Input(2) with { Context = ProContext with { LeftDeviceGeneration = 2 } }, 7));
        Assert.IsTrue(session.InputSuppressed, "A foreign neutral cannot release this operation.");
        session.TryObserve(Input(2), 7);
        Assert.IsTrue(session.InputSuppressed);
        if (finish == 0) Assert.IsTrue(session.TryTakeConfirmation(7, ProContext, out _));
        else if (finish == 1) session.Cancel();
        else session.Invalidate();
        Assert.IsFalse(session.InputSuppressed);
        var completed = session.Outcome;
        session.Cancel(); session.Invalidate();
        Assert.AreEqual(completed, session.Outcome, "Completed or transferred authority is one-way.");
        session.TryObserve(Input(3, Switch2ProfilePickerButtons.Confirm), 8);
        Assert.IsFalse(session.InputSuppressed);
        Assert.IsFalse(session.TryTakeConfirmation(8, ProContext, out _));
    }

    [TestMethod]
    public void PhysicalReleaseRequiresNearCenterNotJustCrossingTheNavigationThreshold()
    {
        var original = Pro(true, 0);
        foreach (var (y, held) in new[] { ((short)19333, true), ((short)-19333, true), // about 59%
            ((short)6881, true), ((short)6225, false), ((short)0, false) }) // 21%, 19%, center
        {
            var frame = new Switch2ProProfileInputFrame(Canonical(Switch2ControllerModel.ProController2, true, 0, 2048, 2048),
                0, original.LeftX, new Switch2ProfileAxis(2048, y, 128), original.RightX, original.RightY);
            Assert.IsTrue(Switch2ProfilePickerInput.TryFromPro(frame, Switch2FaceButtonLayout.Xbox, false, out var input));
            Assert.AreEqual(Switch2ProfilePickerButtons.None, input.Buttons);
            Assert.AreEqual(held, input.PhysicalControlsHeld);
        }
    }

    [TestMethod]
    public void NavigationThresholdUsesSignedPrecisionAndPhysicalDrainIncludesRotatedAxis()
    {
        var original = Pro(true, 0);
        foreach (var (y, expected) in new[] { ((short)-19660, Switch2ProfilePickerButtons.None),
            ((short)-19661, Switch2ProfilePickerButtons.Up), ((short)19660, Switch2ProfilePickerButtons.None),
            ((short)19661, Switch2ProfilePickerButtons.Down) })
        {
            var frame = new Switch2ProProfileInputFrame(Canonical(Switch2ControllerModel.ProController2, true, 0, 2048, 2048),
                0, original.LeftX, new Switch2ProfileAxis(2048, y, 128), original.RightX, original.RightY);
            Assert.IsTrue(Switch2ProfilePickerInput.TryFromPro(frame, Switch2FaceButtonLayout.Xbox, false, out var input));
            Assert.AreEqual(expected, input.Buttons, "Byte projection was deliberately fixed at center; use the signed axis.");
        }
        Assert.IsTrue(Switch2ProfilePickerInput.TryFromPro(Pro(true, 0, 4095, 2048), Switch2FaceButtonLayout.Xbox, false, out var sideways));
        Assert.AreEqual(Switch2ProfilePickerButtons.None, sideways.Buttons);
        Assert.IsTrue(sideways.PhysicalControlsHeld, "A new orientation can make this same physical axis a navigation axis.");
    }

    [TestMethod]
    public void ProfileFrameProjectionHasZeroWarmedManagedAllocation()
    {
        var pro = Pro(true, 0x08);
        var joy = JoyCon(Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0x00080000);
        ProjectFrames(pro, joy, 10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int accepted = ProjectFrames(pro, joy, 50_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(100_000, accepted);
        Assert.AreEqual(0L, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProjectFrames(in Switch2ProProfileInputFrame pro, in Switch2JoyConProfileInputFrame joy, int count)
    {
        int accepted = 0;
        for (int i = 0; i < count; ++i)
        {
            if (Switch2ProfilePickerInput.TryFromPro(pro, Switch2FaceButtonLayout.Xbox, false, out _)) ++accepted;
            if (Switch2ProfilePickerInput.TryFromJoyCon(joy, Switch2FaceButtonLayout.Nintendo, false, out _)) ++accepted;
        }
        return accepted;
    }

    [TestMethod]
    public void ReducerHasZeroWarmedManagedAllocation()
    {
        Assert.IsTrue(Switch2ProfilePickerSession.TryBegin(3, 0, 7, Input(0), out var session));
        Exercise(session, 0, 20_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exercise(session, 20_000, 100_000);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exercise(Switch2ProfilePickerSession session, int start, int count)
    {
        for (int i = start; i < start + count; ++i)
            session.TryObserve(Input(i + 1, (i & 1) == 0 ? Switch2ProfilePickerButtons.Down : 0), 7);
    }

    private static Switch2ProfilePickerInput Input(long time, Switch2ProfilePickerButtons buttons = 0) => new(ProContext, time, buttons);

    private static Switch2ProProfileInputFrame Pro(bool usb, uint buttons, ushort x = 2048, ushort y = 2048)
    {
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(Canonical(Switch2ControllerModel.ProController2, usb, buttons, x, y), out var frame, out _));
        return frame;
    }

    private static Switch2JoyConProfileInputFrame JoyCon(Switch2JoyConProfileMode mode, uint buttons, ushort x = 2048, ushort y = 2048)
    {
        var model = Switch2JoyConProfileInputMapper.IsStandaloneLeftMode(mode) ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right;
        var canonical = Canonical(model, false, buttons, x, y);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode, canonical.Descriptor, out var mapper));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(mapper, canonical, out _, out var frame, out _));
        return frame;
    }

    private static Switch2CanonicalInputFrame Canonical(Switch2ControllerModel model, bool usb, uint buttons, ushort x, ushort y)
    {
        Switch2InputProtocolIdentity identity;
        Assert.IsTrue(usb ? Switch2InputProtocolIdentity.TryCreateProController2Usb(0x057e, 0x2069, 0x0201, out identity) :
            Switch2InputProtocolIdentity.TryCreateBluetoothLe(Switch2InputCodec.ServiceUuid, Switch2InputCodec.Common05CharacteristicUuid,
                Switch2GattProperty.Read | Switch2GattProperty.Notify, model, out identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, 1, 2, 1_000_000, out var descriptor));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model, 1, out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] packet = new byte[usb ? Switch2InputCodec.UsbPacketLength : Switch2InputCodec.BluetoothLeBodyLength];
        int offset = usb ? 1 : 0;
        if (usb) packet[0] = (byte)Switch2InputReportKind.Common05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset + 4), buttons);
        foreach (int stickOffset in new[] { offset + 10, offset + 13 })
        {
            packet[stickOffset] = (byte)x;
            packet[stickOffset + 1] = (byte)((x >> 8) | ((y & 15) << 4));
            packet[stickOffset + 2] = (byte)(y >> 4);
        }
        Assert.IsTrue(session.TryProcess(descriptor, packet, 100, out var frame, out var failure), failure.ToString());
        return frame;
    }
}
