/*
DS4Windows
Copyright (C) 2026 hbashton
GPL-3.0-or-later; see LICENSE.

Picker interaction follows Switch2Connect, Copyright (C) 2026 TommyWabg,
GPL-3.0-or-later, commit 61ac6642ce12fe7217e38a860b14863b18ca7e28:
src/controller.py _handle_profile_selection_input and src/gui.py on_profile_nav.
See docs/protocols/switch2-profile-picker.md for provenance and integration gates.
*/
using System;

namespace DS4Windows.Switch2;

[Flags]
internal enum Switch2ProfilePickerButtons : byte
{
    None = 0, Up = 1, Down = 2, Confirm = 4, Cancel = 8, Cycle = 16,
}

internal enum Switch2ProfilePickerOutcome : byte
{
    None, Confirmed, Cancelled, Invalidated,
}

/// <summary>
/// Exact physical/orientation basis for a picker. Profile revision belongs to
/// the session separately. Invalid/default contexts cannot start an operation.
/// </summary>
internal readonly record struct Switch2ProfilePickerContext(
    bool IsPro, Switch2JoyConProfileMode Mode, Switch2Transport Transport,
    ulong LeftDeviceGeneration, ulong LeftTransportGeneration,
    ulong RightDeviceGeneration, ulong RightTransportGeneration, ulong PairEpoch,
    long QpcFrequency, Switch2FaceButtonLayout Layout)
{
    internal bool SamePhysicalLifetime(in Switch2ProfilePickerContext other) =>
        IsPro == other.IsPro && Transport == other.Transport &&
        LeftDeviceGeneration == other.LeftDeviceGeneration && LeftTransportGeneration == other.LeftTransportGeneration &&
        RightDeviceGeneration == other.RightDeviceGeneration && RightTransportGeneration == other.RightTransportGeneration &&
        PairEpoch == other.PairEpoch && QpcFrequency == other.QpcFrequency;

    internal bool IsValid => QpcFrequency > 0 && Switch2FaceButtonLayoutProjection.IsValid(Layout) &&
        (IsPro ? Mode == Switch2JoyConProfileMode.Invalid && PairEpoch == 0 &&
            Transport is Switch2Transport.Usb or Switch2Transport.BluetoothLe &&
            LeftDeviceGeneration != 0 && LeftTransportGeneration != 0 &&
            RightDeviceGeneration == 0 && RightTransportGeneration == 0 :
            Transport == Switch2Transport.BluetoothLe && (Mode switch
            {
                Switch2JoyConProfileMode.Joined => PairEpoch != 0 &&
                    LeftDeviceGeneration != 0 && LeftTransportGeneration != 0 &&
                    RightDeviceGeneration != 0 && RightTransportGeneration != 0,
                Switch2JoyConProfileMode.StandaloneHorizontalLeft or Switch2JoyConProfileMode.StandaloneVerticalLeft =>
                    PairEpoch == 0 && LeftDeviceGeneration != 0 && LeftTransportGeneration != 0 &&
                    RightDeviceGeneration == 0 && RightTransportGeneration == 0,
                Switch2JoyConProfileMode.StandaloneHorizontalRight or Switch2JoyConProfileMode.StandaloneVerticalRight =>
                    PairEpoch == 0 && RightDeviceGeneration != 0 && RightTransportGeneration != 0 &&
                    LeftDeviceGeneration == 0 && LeftTransportGeneration == 0,
                _ => false,
            }));
}

/// <summary>
/// Picker-only controls derived from admitted profile frames, never from a new
/// transport parser or from game-specific/custom remapped DS4State. The physical
/// A/B positions follow the donor's held-orientation UI, not SDL mini labels.
/// cyclePressed must evaluate the session's CAPTURED opening binding through
/// drain, never a replacement profile's binding. A held rail/shoulder/custom
/// stick action cannot disappear from release tracking when profiles change.
/// </summary>
internal readonly record struct Switch2ProfilePickerInput(
    Switch2ProfilePickerContext Context, long TimestampQpc, Switch2ProfilePickerButtons Buttons,
    bool PhysicalControlsHeld = false)
{
    internal bool IsValid => Context.IsValid && TimestampQpc >= 0 &&
        (Buttons & ~(Switch2ProfilePickerButtons.Up | Switch2ProfilePickerButtons.Down |
            Switch2ProfilePickerButtons.Confirm | Switch2ProfilePickerButtons.Cancel |
            Switch2ProfilePickerButtons.Cycle)) == 0;

    internal static bool TryFromPro(in Switch2ProProfileInputFrame frame,
        Switch2FaceButtonLayout layout, bool cyclePressed, out Switch2ProfilePickerInput input)
    {
        input = default;
        if (frame.Version != Switch2ProProfileInputFrame.CurrentVersion || !frame.HasValidRawStickObservation ||
            !Switch2FaceButtonLayoutProjection.IsValid(layout)) return false;
        var context = new Switch2ProfilePickerContext(true, Switch2JoyConProfileMode.Invalid, frame.Transport,
            frame.DeviceGeneration, frame.TransportGeneration, 0, 0, 0, frame.QpcFrequency, layout);
        bool dpadUp = (frame.Buttons & Switch2ProButton.DpadUp) != 0;
        bool dpadDown = (frame.Buttons & Switch2ProButton.DpadDown) != 0;
        input = new(context, frame.CompletionTimestampQpc, Project(
            IsUp(frame.LeftY.SignedValue) || IsUp(frame.RightY.SignedValue) || dpadUp,
            IsDown(frame.LeftY.SignedValue) || IsDown(frame.RightY.SignedValue) || dpadDown,
            (frame.Buttons & Switch2ProButton.FaceSouth) != 0,
            (frame.Buttons & Switch2ProButton.FaceEast) != 0,
            layout, cyclePressed && !dpadUp && !dpadDown),
            cyclePressed || ((uint)frame.Buttons & 0x000F000F) != 0 ||
            StickDeflected(frame.LeftX.SignedValue, frame.LeftY.SignedValue) ||
            StickDeflected(frame.RightX.SignedValue, frame.RightY.SignedValue));
        return input.IsValid;
    }

    internal static bool TryFromJoyCon(in Switch2JoyConProfileInputFrame frame,
        Switch2FaceButtonLayout layout, bool cyclePressed, out Switch2ProfilePickerInput input)
    {
        input = default;
        if (frame.Version != Switch2JoyConProfileInputFrame.CurrentVersion ||
            !Switch2FaceButtonLayoutProjection.IsValid(layout) ||
            (frame.LeftSource.IsPresent && !frame.LeftSource.HasValidRawStickObservation) ||
            (frame.RightSource.IsPresent && !frame.RightSource.HasValidRawStickObservation)) return false;
        var context = new Switch2ProfilePickerContext(false, frame.Mode, Switch2Transport.BluetoothLe,
            frame.LeftSource.DeviceGeneration, frame.LeftSource.TransportGeneration,
            frame.RightSource.DeviceGeneration, frame.RightSource.TransportGeneration,
            frame.PairEpoch, frame.QpcFrequency, layout);
        if (!context.IsValid) return false;
        bool joined = frame.Mode == Switch2JoyConProfileMode.Joined;
        uint left = frame.LeftSource.RawButtonBits, right = frame.RightSource.RawButtonBits;
        bool dpadUp = joined && (left & 0x00020000) != 0;
        bool dpadDown = joined && (left & 0x00010000) != 0;
        // Standalone left's directional cluster is A/B/X/Y for this interaction,
        // not D-pad navigation. Horizontal button positions deliberately use the
        // source's physical controls; game mappings remain in the existing mapper.
        (uint raw, uint bottom, uint east) = frame.Mode switch
        {
            Switch2JoyConProfileMode.StandaloneVerticalLeft => (left, 0x00010000u, 0x00040000u),
            Switch2JoyConProfileMode.StandaloneHorizontalLeft => (left, 0x00080000u, 0x00010000u),
            Switch2JoyConProfileMode.StandaloneHorizontalRight => (right, 0x00000008u, 0x00000002u),
            _ => (right, 0x00000004u, 0x00000008u),
        };
        input = new(context, frame.CompletionTimestampQpc, Project(
            IsUp(frame.LeftY.SignedValue) || IsUp(frame.RightY.SignedValue) || dpadUp,
            IsDown(frame.LeftY.SignedValue) || IsDown(frame.RightY.SignedValue) || dpadDown,
            (raw & bottom) != 0, (raw & east) != 0, layout,
            cyclePressed && !dpadUp && !dpadDown),
            cyclePressed || (left & 0x000F0000) != 0 || (right & 0x0000000F) != 0 ||
            StickDeflected(frame.LeftX.SignedValue, frame.LeftY.SignedValue) ||
            StickDeflected(frame.RightX.SignedValue, frame.RightY.SignedValue));
        return input.IsValid;
    }

    // Calibrated profile axes are signed, down-positive. Match strict >0.6
    // normalized magnitude without first quantizing through an eight-bit axis.
    private static bool IsUp(short y) => y < -0.6 * 32768;
    private static bool IsDown(short y) => y > 0.6 * 32767;
    // Separate exit hysteresis: navigating at 60% must not count as released
    // at 59%. A fixed 20% center band is picker policy, not calibration truth.
    private static bool StickDeflected(short x, short y) =>
        x < -0.2 * 32768 || x > 0.2 * 32767 || y < -0.2 * 32768 || y > 0.2 * 32767;

    private static Switch2ProfilePickerButtons Project(bool up, bool down, bool bottom, bool east,
        Switch2FaceButtonLayout layout, bool cycle)
    {
        bool confirm = layout == Switch2FaceButtonLayout.Nintendo ? east : bottom;
        bool cancel = layout == Switch2FaceButtonLayout.Nintendo ? bottom : east;
        return (up ? Switch2ProfilePickerButtons.Up : 0) |
            (down ? Switch2ProfilePickerButtons.Down : 0) |
            (confirm ? Switch2ProfilePickerButtons.Confirm : 0) |
            (cancel ? Switch2ProfilePickerButtons.Cancel : 0) |
            (cycle && !confirm && !cancel ? Switch2ProfilePickerButtons.Cycle : 0);
    }
}

/// <summary>
/// Single-owner, allocation-free report reducer. The cold owner supplies a fixed
/// catalog and commits a confirmed index through the existing profile worker.
/// No profile loading, global configuration, callbacks, timers or queues live here.
/// A confirmed index alone is not authority to mutate a slot or load a file.
/// </summary>
internal sealed class Switch2ProfilePickerSession
{
    private readonly Switch2ProfilePickerContext context;
    private readonly long profileRevision;
    private readonly int count;
    private Switch2ProfilePickerButtons previous;
    private long lastTimestamp, lastNavigation;
    private bool hasNavigated, confirmationTaken, physicalDrainReleased, drainCompleted;

    private Switch2ProfilePickerSession(int count, int currentIndex, long profileRevision,
        in Switch2ProfilePickerInput initial)
    {
        this.count = count; this.profileRevision = profileRevision; context = initial.Context;
        previous = initial.Buttons; lastTimestamp = initial.TimestampQpc;
        SelectedIndex = currentIndex < 0 || currentIndex == count - 1 ? 0 : currentIndex + 1;
        InputSuppressed = true;
    }

    internal int SelectedIndex { get; private set; }
    internal Switch2ProfilePickerOutcome Outcome { get; private set; }
    internal bool InputSuppressed { get; private set; }

    internal static bool TryBegin(int count, int currentIndex, long profileRevision,
        in Switch2ProfilePickerInput initial, out Switch2ProfilePickerSession session)
    {
        session = null;
        if (count <= 0 || currentIndex < -1 || currentIndex >= count || profileRevision < 0 || !initial.IsValid) return false;
        session = new(count, currentIndex, profileRevision, initial);
        return true;
    }

    internal bool TryObserve(in Switch2ProfilePickerInput input, long currentProfileRevision)
    {
        // Foreign lifetime or stale input cannot release someone else's picker.
        // Layout/orientation changes on the SAME physical lifetime revoke pending
        // authority, but release detection remains pre-layout and basis-independent.
        if (!input.IsValid || !context.SamePhysicalLifetime(input.Context) || input.TimestampQpc < lastTimestamp) return false;
        lastTimestamp = input.TimestampQpc;
        physicalDrainReleased = !input.PhysicalControlsHeld && input.Buttons == Switch2ProfilePickerButtons.None;
        if (!confirmationTaken && (currentProfileRevision != profileRevision || input.Context != context)) Invalidate();
        if (Outcome != Switch2ProfilePickerOutcome.None)
        {
            // Drain every control consumed by the picker, including sticks and
            // the open/cycle binding. Releasing only A/B leaks held navigation.
            UpdateSuppression();
            previous = input.Buttons;
            return true;
        }

        var pressed = input.Buttons & ~previous;
        previous = input.Buttons;
        // Simultaneous A/B is cancellation, never an ambiguous profile switch.
        if ((pressed & Switch2ProfilePickerButtons.Cancel) != 0)
        {
            Outcome = Switch2ProfilePickerOutcome.Cancelled;
            UpdateSuppression();
            return true;
        }
        bool up = (input.Buttons & Switch2ProfilePickerButtons.Up) != 0;
        bool down = (input.Buttons & Switch2ProfilePickerButtons.Down) != 0;
        bool navUp = up && !down && (pressed & Switch2ProfilePickerButtons.Up) != 0;
        bool navDown = down && !up && (pressed & Switch2ProfilePickerButtons.Down) != 0;
        bool cycle = !up && !down && (pressed & Switch2ProfilePickerButtons.Cycle) != 0;
        if ((navUp || navDown || cycle) &&
            (!hasNavigated || input.TimestampQpc - lastNavigation >= context.QpcFrequency * 0.18))
        {
            SelectedIndex = navUp ? (SelectedIndex == 0 ? count - 1 : SelectedIndex - 1) :
                (SelectedIndex == count - 1 ? 0 : SelectedIndex + 1);
            hasNavigated = true;
            lastNavigation = input.TimestampQpc;
        }
        if ((pressed & Switch2ProfilePickerButtons.Confirm) != 0)
            Outcome = Switch2ProfilePickerOutcome.Confirmed;
        UpdateSuppression();
        return true;
    }

    internal void Cancel()
    {
        if (!confirmationTaken && Outcome is Switch2ProfilePickerOutcome.None or Switch2ProfilePickerOutcome.Confirmed)
            Outcome = Switch2ProfilePickerOutcome.Cancelled;
        UpdateSuppression();
    }

    /// <summary>
    /// One-shot intent transfer only. The owner must still atomically admit its
    /// expected revision and exact slot/token before enqueuing a named load.
    /// Output drain deliberately survives that request's own new revision.
    /// </summary>
    internal bool TryTakeConfirmation(long currentProfileRevision,
        in Switch2ProfilePickerContext currentContext, out int selectedIndex)
    {
        selectedIndex = -1;
        // The runtime owner reads BOTH current settings and revision under its
        // publication gate; a setting change need not have produced a frame yet.
        if (!confirmationTaken && (currentProfileRevision != profileRevision || currentContext != context)) Invalidate();
        if (confirmationTaken || Outcome != Switch2ProfilePickerOutcome.Confirmed) return false;
        confirmationTaken = true;
        selectedIndex = SelectedIndex;
        UpdateSuppression();
        return true;
    }

    internal void Invalidate()
    {
        if (!confirmationTaken && Outcome is Switch2ProfilePickerOutcome.None or Switch2ProfilePickerOutcome.Confirmed)
            Outcome = Switch2ProfilePickerOutcome.Invalidated;
        UpdateSuppression();
    }

    private void UpdateSuppression()
    {
        // Physical neutral can precede a delayed cold intent transfer. Keep
        // gameplay suppressed until that authority is transferred or revoked.
        // Completed drain is a latch: later ordinary input never represses it.
        if (Outcome != Switch2ProfilePickerOutcome.None &&
            (Outcome != Switch2ProfilePickerOutcome.Confirmed || confirmationTaken) && physicalDrainReleased)
            drainCompleted = true;
        InputSuppressed = !drainCompleted;
    }
}
