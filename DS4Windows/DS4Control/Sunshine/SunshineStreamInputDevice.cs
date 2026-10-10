/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DS4Windows.InputDevices;

namespace DS4Windows.Sunshine;

/// <summary>
/// A no-HID input source fed by Sunshine's local controller bridge. State
/// enters DS4Windows' existing profile/mapping pipeline directly and never
/// appears as a Sunshine-created virtual HID device.
/// </summary>
internal sealed class SunshineStreamInputDevice : DS4Device
{
    private const uint DpadUp = 0x0001;
    private const uint DpadDown = 0x0002;
    private const uint DpadLeft = 0x0004;
    private const uint DpadRight = 0x0008;
    private const uint Start = 0x0010;
    private const uint Back = 0x0020;
    private const uint LeftStick = 0x0040;
    private const uint RightStick = 0x0080;
    private const uint LeftShoulder = 0x0100;
    private const uint RightShoulder = 0x0200;
    private const uint Home = 0x0400;
    private const uint A = 0x1000;
    private const uint B = 0x2000;
    private const uint X = 0x4000;
    private const uint Y = 0x8000;
    private const uint Paddle1 = 0x010000;
    private const uint Paddle2 = 0x020000;
    private const uint TouchpadButton = 0x100000;
    private const uint MiscButton = 0x200000;

    private readonly object gate = new();
    private readonly object publicationGate = new();
    private readonly TouchContact[] touches = { new(), new() };
    private readonly ulong sessionId;
    private readonly ushort controllerId;
    private readonly string profileLinkId;
    private bool hasSequence;
    private uint lastSequence;
    private uint packetCounter;
    private float accelX;
    private float accelY;
    private float accelZ;
    private float gyroX;
    private float gyroY;
    private float gyroZ;
    private SunshineControllerBridgePacket latestPacket;
    private int removalRequested;
    private int firstActiveStateLogged;
    private int queuedEventDrainScheduled;

    internal SunshineStreamInputDevice(ulong sessionId, ushort controllerId,
        byte clientIndex, byte controllerType, ushort capabilities,
        uint supportedButtons)
        : base($"Sunshine controller {clientIndex + 1}",
            InputDeviceType.DS4, ConnectionType.USB)
    {
        if (sessionId == 0 || controllerId >= 16)
            throw new ArgumentOutOfRangeException(nameof(sessionId));

        this.sessionId = sessionId;
        this.controllerId = controllerId;
        // A controller that reconnects on the same Sunshine host-global slot
        // should keep its profile link. The session ID and client index are
        // transient or client-scoped, so neither belongs in this identity.
        profileLinkId = "SUNSHINE-SLOT-" +
            controllerId.ToString("D2", CultureInfo.InvariantCulture);
        ClientIndex = clientIndex;
        ControllerType = controllerType;
        Capabilities = capabilities;
        SupportedButtons = supportedButtons;
        PrimaryDevice = true;
        PerformStateMerge = false;
        OutputMapGyro = true;
        battery = 0;
    }

    internal byte ClientIndex { get; }
    internal byte ControllerType { get; }
    internal ushort Capabilities { get; }
    internal uint SupportedButtons { get; }
    public override string ProfileLinkId => profileLinkId;
    public override string DisplayIdentity => profileLinkId;
    internal ulong SessionIdForBridge => sessionId;
    internal ushort ControllerIdForBridge => controllerId;
    internal long LastInputTimestampQpc { get; private set; }

    internal event Action<byte, byte> RumbleRequested;
    internal event Action<byte, byte, byte> RgbRequested;

    internal bool TryGetLatestPacket(out SunshineControllerBridgePacket packet)
    {
        lock (gate)
        {
            packet = latestPacket;
            return hasSequence;
        }
    }

    internal bool Apply(in SunshineControllerBridgePacket packet)
    {
        if (packet.SessionId != sessionId ||
            packet.ControllerId != controllerId ||
            packet.ClientIndex != ClientIndex ||
            packet.Kind is not (SunshineControllerBridgePacketKind.State or
                SunshineControllerBridgePacketKind.Motion or
                SunshineControllerBridgePacketKind.Touch or
                SunshineControllerBridgePacketKind.Battery))
        {
            return false;
        }

        lock (publicationGate)
        {
            bool batteryChanged = false;
            lock (gate)
            {
                if (IsRemoving || IsRemoved ||
                    hasSequence && !IsNewer(packet.Sequence, lastSequence))
                {
                    return false;
                }

                latestPacket = packet;
                lastSequence = packet.Sequence;
                hasSequence = true;
                LastInputTimestampQpc = Stopwatch.GetTimestamp();

                if (packet.Kind == SunshineControllerBridgePacketKind.State &&
                    (packet.Buttons != 0 || packet.LeftTrigger != 0 ||
                     packet.RightTrigger != 0 || packet.LeftX != 0 ||
                     packet.LeftY != 0 || packet.RightX != 0 ||
                     packet.RightY != 0) &&
                    Interlocked.CompareExchange(ref firstActiveStateLogged,
                        1, 0) == 0)
                {
                    DS4Windows.AppLogger.LogToGui(
                        $"Sunshine bridge received active gamepad input: buttons=0x{packet.Buttons:X6}, triggers={packet.LeftTrigger}/{packet.RightTrigger}, left_stick={packet.LeftX},{packet.LeftY}, right_stick={packet.RightX},{packet.RightY}.", false);
                }

                // State, motion, touch, and battery arrive as separate packets.
                // Keep the other controls from the last report when a partial
                // packet arrives; the HID worker's usual pState -> cState copy
                // does not run for this externally-fed source.
                switch (packet.Kind)
                {
                    case SunshineControllerBridgePacketKind.State:
                        ApplyState(packet);
                        break;
                    case SunshineControllerBridgePacketKind.Motion:
                        if (!ApplyMotion(packet)) return false;
                        break;
                    case SunshineControllerBridgePacketKind.Touch:
                        if (!ApplyTouch(packet)) return false;
                        break;
                    case SunshineControllerBridgePacketKind.Battery:
                        batteryChanged = battery != packet.BatteryPercent ||
                            charging != (packet.BatteryState == 3);
                        battery = packet.BatteryPercent;
                        charging = packet.BatteryState == 3;
                        cState.Battery = (byte)battery;
                        break;
                }

                unchecked { cState.PacketCounter = ++packetCounter; }
                cState.ReportTimeStamp = DateTime.UtcNow;
                cState.elapsedTime = 0;
                cState.calculateStickAngles();
                lastActive = DateTime.UtcNow;
            }

            if (batteryChanged) PublishNoHidBatteryChanged();
            PublishNoHidReport();
            // HID input workers publish the current report, then make it the
            // previous report for edge detection on the next input tick.
            cState.CopyTo(pState);
        }
        return true;
    }

    internal void RequestRemoval()
    {
        if (System.Threading.Interlocked.Exchange(ref removalRequested, 1) != 0)
            return;
        IsRemoving = true;
        RunRemoval();
    }

    public override void StartUpdate()
    {
        // Sunshine owns discovery, transport, and report cadence. DS4Windows
        // still runs this device through the normal report/mapping pipeline,
        // which expects the per-slot button debouncer normally initialized by
        // the physical HID input worker.
        if (Debouncer == null && DeviceSlotNumber >= 0 &&
            Global.DebouncingMs != null &&
            DeviceSlotNumber < Global.DebouncingMs.Length)
        {
            Debouncer = SetupDebouncer();
        }
    }

    public override void StopUpdate()
    {
        // The bridge owner cancels and joins its pipe reader before removing
        // this device; this object owns no worker or operating-system handle.
    }

    public override void queueEvent(Action act)
    {
        if (act == null || IsRemoving || IsRemoved) return;

        lock (eventQueueLock)
        {
            if (IsRemoving || IsRemoved) return;
            eventQueue.Enqueue(act);
            hasInputEvts = true;
        }

        // HID-backed devices drain this queue on their input worker. Sunshine
        // has no HID worker, so without its own drain profile/output changes
        // (including switching the emulated pad) would stay queued forever.
        if (Interlocked.CompareExchange(ref queuedEventDrainScheduled, 1, 0) == 0)
        {
            if (!ThreadPool.QueueUserWorkItem(
                    static state => ((SunshineStreamInputDevice)state).
                        DrainQueuedEvents(), this))
            {
                Interlocked.Exchange(ref queuedEventDrainScheduled, 0);
            }
        }
    }

    private void DrainQueuedEvents()
    {
        while (true)
        {
            Action action;
            lock (eventQueueLock)
            {
                if (eventQueue.Count == 0)
                {
                    hasInputEvts = false;
                    Interlocked.Exchange(ref queuedEventDrainScheduled, 0);
                    return;
                }

                action = eventQueue.Dequeue();
            }

            try
            {
                // Match the HID worker's report-boundary serialization. The
                // pipe reader may publish the next sample as soon as this
                // action completes, but never during an output transition.
                lock (publicationGate)
                {
                    if (!IsRemoving && !IsRemoved)
                        action();
                }
            }
            catch (Exception exception)
            {
                AppLogger.LogToGui(
                    $"Sunshine queued controller action failed: {exception.Message}",
                    true);
            }
        }
    }

    public override bool TryHaltReportingRunAction(Action act)
    {
        // This source has no HID worker to signal readWaitEv. Serialize profile
        // and output changes against report publication instead. A callback
        // must not pause its own in-flight report reentrantly.
        if (IsRemoving || Monitor.IsEntered(publicationGate) ||
            !Monitor.TryEnter(publicationGate, 500))
        {
            return false;
        }

        try
        {
            if (IsRemoving || IsRemoved) return false;
            act?.Invoke();
            return true;
        }
        finally
        {
            Monitor.Exit(publicationGate);
        }
    }

    public override bool IsAlive() => !IsRemoving && !IsRemoved;

    public override void setRumble(byte rightLightFastMotor,
        byte leftHeavySlowMotor)
    {
        base.setRumble(rightLightFastMotor, leftHeavySlowMotor);
        RumbleRequested?.Invoke(leftHeavySlowMotor, rightLightFastMotor);
    }

    public override void SetLightbarState(ref DS4LightbarState lightState)
    {
        base.SetLightbarState(ref lightState);
        RgbRequested?.Invoke(lightState.LightBarExplicitlyOff ? (byte)0 :
                lightState.LightBarColor.red,
            lightState.LightBarExplicitlyOff ? (byte)0 :
                lightState.LightBarColor.green,
            lightState.LightBarExplicitlyOff ? (byte)0 :
                lightState.LightBarColor.blue);
    }

    private void ApplyState(in SunshineControllerBridgePacket packet)
    {
        uint flags = packet.Buttons;
        cState.DpadUp = (flags & DpadUp) != 0;
        cState.DpadDown = (flags & DpadDown) != 0;
        cState.DpadLeft = (flags & DpadLeft) != 0;
        cState.DpadRight = (flags & DpadRight) != 0;
        cState.Options = (flags & Start) != 0;
        cState.Share = (flags & Back) != 0;
        cState.L3 = (flags & LeftStick) != 0;
        cState.R3 = (flags & RightStick) != 0;
        cState.L1 = (flags & LeftShoulder) != 0;
        cState.R1 = (flags & RightShoulder) != 0;
        cState.PS = (flags & Home) != 0;
        cState.Cross = (flags & A) != 0;
        cState.Circle = (flags & B) != 0;
        cState.Square = (flags & X) != 0;
        cState.Triangle = (flags & Y) != 0;
        cState.BLP = (flags & Paddle1) != 0;
        cState.BRP = (flags & Paddle2) != 0;
        cState.TouchButton = (flags & (TouchpadButton | MiscButton)) != 0;
        cState.L2 = cState.L2Raw = packet.LeftTrigger;
        cState.R2 = cState.R2Raw = packet.RightTrigger;
        cState.L2Btn = packet.LeftTrigger != 0;
        cState.R2Btn = packet.RightTrigger != 0;
        cState.LXAxis = DS4MappedStickAxis.FromSigned(packet.LeftX);
        cState.LYAxis = DS4MappedStickAxis.FromSigned(InvertY(packet.LeftY));
        cState.RXAxis = DS4MappedStickAxis.FromSigned(packet.RightX);
        cState.RYAxis = DS4MappedStickAxis.FromSigned(InvertY(packet.RightY));
    }

    private bool ApplyMotion(in SunshineControllerBridgePacket packet)
    {
        if (!float.IsFinite(packet.X) || !float.IsFinite(packet.Y) ||
            !float.IsFinite(packet.Z)) return false;

        switch (packet.MotionType)
        {
            case 1: // Sunshine/Limelight acceleration, m/s^2.
                accelX = packet.X;
                accelY = packet.Y;
                accelZ = packet.Z;
                break;
            case 2: // Sunshine/Limelight gyroscope, degrees/second.
                gyroX = packet.X;
                gyroY = packet.Y;
                gyroZ = packet.Z;
                break;
            default:
                return false;
        }

        const double gravity = 9.80665;
        var motion = new SixAxis(
            ToReportUnits(gyroX * 16.0),
            ToReportUnits(gyroY * 16.0),
            ToReportUnits(gyroZ * 16.0),
            ToReportUnits(accelX / gravity * DS4Windows.SixAxis.F_ACC_RES_PER_G),
            ToReportUnits(accelY / gravity * DS4Windows.SixAxis.F_ACC_RES_PER_G),
            ToReportUnits(accelZ / gravity * DS4Windows.SixAxis.F_ACC_RES_PER_G),
            0);
        cState.Motion = motion;
        return true;
    }

    private bool ApplyTouch(in SunshineControllerBridgePacket packet)
    {
        if (!float.IsFinite(packet.X) || !float.IsFinite(packet.Y) ||
            !float.IsFinite(packet.Pressure)) return false;

        int index = FindTouch(packet.PointerId);
        switch (packet.TouchEvent)
        {
            case 1: // down
                if (index < 0) index = FindFreeTouch();
                if (index < 0) return false;
                touches[index].Active = true;
                touches[index].PointerId = packet.PointerId;
                UpdateTouchCoordinates(index, packet);
                break;
            case 2: // up
            case 4: // cancel
                if (index >= 0) touches[index].Active = false;
                break;
            case 3: // move
                if (index < 0) return false;
                UpdateTouchCoordinates(index, packet);
                break;
            case 7: // cancel all
                touches[0].Active = touches[1].Active = false;
                break;
            default:
                return false;
        }

        cState.TrackPadTouch0 = ToTrackPadTouch(0);
        cState.TrackPadTouch1 = ToTrackPadTouch(1);
        cState.Touch1 = touches[0].Active;
        cState.Touch2 = touches[1].Active;
        cState.Touch1Finger = cState.Touch1;
        cState.Touch2Fingers = cState.Touch1 && cState.Touch2;
        cState.Touch1Identifier = (byte)touches[0].PointerId;
        cState.Touch2Identifier = (byte)touches[1].PointerId;
        cState.TouchLeft = cState.Touch1 && touches[0].X < 960 ||
            cState.Touch2 && touches[1].X < 960;
        cState.TouchRight = cState.Touch1 && touches[0].X >= 960 ||
            cState.Touch2 && touches[1].X >= 960;
        return true;
    }

    private int FindTouch(uint pointerId)
    {
        for (int index = 0; index < touches.Length; index++)
            if (touches[index].Active && touches[index].PointerId == pointerId)
                return index;
        return -1;
    }

    private int FindFreeTouch()
    {
        for (int index = 0; index < touches.Length; index++)
            if (!touches[index].Active) return index;
        return -1;
    }

    private void UpdateTouchCoordinates(int index,
        in SunshineControllerBridgePacket packet)
    {
        // Keep the same DS4 touchpad coordinate scale used by ViGEm.
        TouchContact contact = touches[index];
        contact.Active = true;
        contact.PointerId = packet.PointerId;
        contact.X = (short)Math.Clamp(
            (int)(Math.Clamp(packet.X, 0f, 1f) * 1920f), 0, 1919);
        contact.Y = (short)Math.Clamp(
            (int)(Math.Clamp(packet.Y, 0f, 1f) * 943f), 0, 942);
    }

    private static DS4State.TrackPadTouch ToTrackPadTouch(TouchContact contact) =>
        new()
        {
            IsActive = contact.Active,
            Id = (byte)contact.PointerId,
            X = contact.X,
            Y = contact.Y,
            RawTrackingNum = (byte)contact.PointerId,
        };

    private DS4State.TrackPadTouch ToTrackPadTouch(int index) =>
        ToTrackPadTouch(touches[index]);

    private static int ToReportUnits(double value) =>
        (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero),
            short.MinValue, short.MaxValue);

    private static short InvertY(short value) => value == short.MinValue
        ? short.MaxValue : (short)-value;

    private static bool IsNewer(uint value, uint previous) =>
        unchecked((int)(value - previous)) > 0;

    private sealed class TouchContact
    {
        internal bool Active;
        internal uint PointerId;
        internal short X;
        internal short Y;
    }
}
