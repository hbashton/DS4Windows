using System;
using System.Text;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms;

internal enum MappingLiveInputStatus { Waiting, Live, Stale, Disconnected, Replaced }

internal interface IMappingLiveInputSource
{
    DS4Device DeviceAt(int index);
    bool TryCopy(int index, DS4Device expected, DS4StateOwnedSnapshot raw, DS4StateOwnedSnapshot mapped);
}

internal sealed class MappingLiveInputSource : IMappingLiveInputSource
{
    public DS4Device DeviceAt(int index)
    {
        var hub = Program.rootHub;
        return hub != null && (uint)index < (uint)hub.DS4Controllers.Length
            ? hub.DS4Controllers[index] : null;
    }

    public bool TryCopy(int index, DS4Device expected, DS4StateOwnedSnapshot raw, DS4StateOwnedSnapshot mapped)
    {
        var hub = Program.rootHub;
        if (hub == null || (uint)index >= (uint)hub.DS4Controllers.Length ||
            !ReferenceEquals(expected, hub.DS4Controllers[index])) return false;
        DS4State input = hub.getDS4State(index);
        DS4State intermediate = hub.getDS4StateTemp(index);
        return input != null && intermediate != null &&
            expected.TryCopyControllerReadings(input, intermediate, raw, mapped);
    }
}

/// <summary>UI-owned snapshots. Never waits for input or adopts another slot occupant.</summary>
internal sealed class MappingLiveInputSnapshot
{
    internal const long StaleAfterMilliseconds = 1000;
    private readonly IMappingLiveInputSource source;
    private readonly DS4StateOwnedSnapshot raw = new();
    private readonly DS4StateOwnedSnapshot mapped = new();
    private int index = -1;
    private DS4Device expected;
    private bool hasSample;
    private uint packet;
    private DateTime reportTimestamp;
    private long lastAdvance;

    internal MappingLiveInputSnapshot(IMappingLiveInputSource source) => this.source = source;
    internal DS4State Raw => raw.State;
    internal bool HasBoundDevice => expected != null;
    internal InputDeviceType DeviceType => expected?.DeviceType ?? InputDeviceType.DS4;
    internal MappingLiveInputStatus Status { get; private set; } = MappingLiveInputStatus.Waiting;
    internal DS4Device ResolveDevice(int physicalIndex) => source.DeviceAt(physicalIndex);

    internal void UseDevice(int physicalIndex, DS4Device expectedDevice)
    {
        index = physicalIndex;
        expected = expectedDevice;
        Reset();
    }

    internal void Reset()
    {
        hasSample = false;
        packet = 0;
        reportTimestamp = default;
        lastAdvance = 0;
        Status = MappingLiveInputStatus.Waiting;
    }

    internal MappingLiveInputStatus Read(long nowMilliseconds)
    {
        if (expected == null) return Status = MappingLiveInputStatus.Waiting;
        DS4Device current = source.DeviceAt(index);
        if (current == null || expected.IsRemoving || expected.IsRemoved)
            return Status = MappingLiveInputStatus.Disconnected;
        if (!ReferenceEquals(current, expected)) return Status = MappingLiveInputStatus.Replaced;

        bool copied;
        try { copied = source.TryCopy(index, expected, raw, mapped); }
        catch (ObjectDisposedException) { return Status = MappingLiveInputStatus.Disconnected; }
        current = source.DeviceAt(index);
        if (current == null || expected.IsRemoving || expected.IsRemoved)
            return Status = MappingLiveInputStatus.Disconnected;
        if (!ReferenceEquals(current, expected)) return Status = MappingLiveInputStatus.Replaced;

        if (copied && (Raw.PacketCounter != 0 || Raw.ReportTimeStamp != default))
        {
            if (!hasSample || Raw.PacketCounter != packet || Raw.ReportTimeStamp != reportTimestamp)
            {
                hasSample = true;
                packet = Raw.PacketCounter;
                reportTimestamp = Raw.ReportTimeStamp;
                lastAdvance = nowMilliseconds;
            }
        }
        if (!hasSample) return Status = MappingLiveInputStatus.Waiting;
        if (nowMilliseconds < lastAdvance) lastAdvance = nowMilliseconds;
        return Status = nowMilliseconds - lastAdvance >= StaleAfterMilliseconds
            ? MappingLiveInputStatus.Stale : MappingLiveInputStatus.Live;
    }
}

/// <summary>Physical labels and values only: no profile mapping or virtual output.</summary>
internal sealed class MappingLiveInputProjection
{
    private readonly StringBuilder buttons = new(256);
    internal int LeftX { get; private set; }
    internal int LeftY { get; private set; }
    internal int RightX { get; private set; }
    internal int RightY { get; private set; }
    internal int AxisMaximum { get; private set; }
    internal bool InvertPlotY { get; private set; }
    internal bool LeftPresent { get; private set; }
    internal bool RightPresent { get; private set; }
    internal double LeftTrigger { get; private set; }
    internal double RightTrigger { get; private set; }
    internal string LeftTriggerName { get; private set; }
    internal string RightTriggerName { get; private set; }
    internal string PressedButtons { get; private set; } = string.Empty;
    internal bool Valid { get; private set; }

    internal void Update(DS4State state, InputDeviceType type)
    {
        buttons.Clear();
        Valid = true;
        bool nintendo = type is InputDeviceType.SwitchPro or InputDeviceType.JoyConL or
            InputDeviceType.JoyConR or InputDeviceType.JoyConGrip or InputDeviceType.Switch2Pro or
            InputDeviceType.Switch2JoyConLeft or InputDeviceType.Switch2JoyConRight or InputDeviceType.Switch2JoyConJoined;
        LeftTriggerName = nintendo ? "ZL" : "L2";
        RightTriggerName = nintendo ? "ZR" : "R2";
        LeftPresent = RightPresent = true;
        AxisMaximum = 255;
        InvertPlotY = false;
        LeftX = state.LX; LeftY = state.LY; RightX = state.RX; RightY = state.RY;
        LeftTrigger = state.L2 * 100.0 / 255;
        RightTrigger = state.R2 * 100.0 / 255;

        if (type == InputDeviceType.Switch2Pro)
        {
            var pro = state.Switch2RawInputStatus;
            Valid = pro.IsValid && pro.ContractVersion == Switch2ProProfileInputFrame.CurrentVersion &&
                !state.Switch2JoyConRawInputStatus.IsValid;
            if (Valid)
            {
                LeftX = pro.LeftStickXRaw; LeftY = pro.LeftStickYRaw;
                RightX = pro.RightStickXRaw; RightY = pro.RightStickYRaw;
                AxisMaximum = 4095; InvertPlotY = true;
                NintendoButtons(pro.RawButtonBits, true, true);
                Add((pro.RawButtonBits & (uint)Switch2ProButton.LeftPaddle) != 0, "GL");
                Add((pro.RawButtonBits & (uint)Switch2ProButton.RightPaddle) != 0, "GR");
            }
        }
        else if (type is InputDeviceType.Switch2JoyConLeft or InputDeviceType.Switch2JoyConRight or InputDeviceType.Switch2JoyConJoined)
        {
            var joy = state.Switch2JoyConRawInputStatus;
            Valid = joy.IsValid && joy.ContractVersion == Switch2JoyConProfileInputFrame.CurrentVersion &&
                !state.Switch2RawInputStatus.IsValid;
            if (Valid)
            {
                LeftPresent = joy.LeftPresent; RightPresent = joy.RightPresent;
                LeftX = joy.LeftPhysicalStickXRaw; LeftY = joy.LeftPhysicalStickYRaw;
                RightX = joy.RightPhysicalStickXRaw; RightY = joy.RightPhysicalStickYRaw;
                AxisMaximum = 4095; InvertPlotY = true;
                NintendoButtons(LeftPresent ? joy.LeftRawButtonBits : 0, true, false);
                NintendoButtons(RightPresent ? joy.RightRawButtonBits : 0, false, true);
                AddSource(state, DS4Controls.Switch2JoyConLeftSL, "L SL");
                AddSource(state, DS4Controls.Switch2JoyConLeftSR, "L SR");
                AddSource(state, DS4Controls.Switch2JoyConRightSL, "R SL");
                AddSource(state, DS4Controls.Switch2JoyConRightSR, "R SR");
            }
        }
        else
        {
            Add(state.Cross, nintendo ? "B" : "Cross");
            Add(state.Circle, nintendo ? "A" : "Circle");
            Add(state.Square, nintendo ? "Y" : "Square");
            Add(state.Triangle, nintendo ? "X" : "Triangle");
            Add(state.DpadUp, "↑"); Add(state.DpadDown, "↓"); Add(state.DpadLeft, "←"); Add(state.DpadRight, "→");
            Add(state.L1, nintendo ? "L" : "L1"); Add(state.R1, nintendo ? "R" : "R1");
            Add(state.L2 > 0, LeftTriggerName); Add(state.R2 > 0, RightTriggerName);
            Add(state.L3, "L stick"); Add(state.R3, "R stick");
            Add(state.Share, nintendo ? "−" : type == InputDeviceType.DS3 ? "Select" : "Share");
            Add(state.Options, nintendo ? "+" : type == InputDeviceType.DS3 ? "Start" : "Options");
            Add(state.PS, nintendo ? "Home" : "PS"); Add(state.Capture, "Capture");
            Add(state.TouchButton, "Touchpad"); Add(state.Mute, "Mute");
            Add(state.BLP, "Left paddle"); Add(state.BRP, "Right paddle");
            Add(state.FnL, "Fn L"); Add(state.FnR, "Fn R");
            Add(state.SideL, "SL"); Add(state.SideR, "SR");
        }
        if (Valid) AddSource(state, DS4Controls.Switch2C, "C");
        if (!Valid) PressedButtons = string.Empty;
        else
        {
            bool unchanged = buttons.Length == PressedButtons.Length;
            for (int i = 0; unchanged && i < buttons.Length; i++)
                unchanged = buttons[i] == PressedButtons[i];
            if (!unchanged) PressedButtons = buttons.ToString();
        }
    }

    private void NintendoButtons(uint bits, bool left, bool right)
    {
        if (right)
        {
            Add((bits & (uint)Switch2ProButton.FaceSouth) != 0, "B");
            Add((bits & (uint)Switch2ProButton.FaceEast) != 0, "A");
            Add((bits & (uint)Switch2ProButton.FaceWest) != 0, "Y");
            Add((bits & (uint)Switch2ProButton.FaceNorth) != 0, "X");
            Add((bits & (uint)Switch2ProButton.RightShoulder) != 0, "R");
            Add((bits & (uint)Switch2ProButton.RightTrigger) != 0, "ZR");
            Add((bits & (uint)Switch2ProButton.Start) != 0, "+");
            Add((bits & (uint)Switch2ProButton.Guide) != 0, "Home");
            Add((bits & (uint)Switch2ProButton.RightStick) != 0, "R stick");
            RightTrigger = (bits & (uint)Switch2ProButton.RightTrigger) != 0 ? 100 : 0;
        }
        if (left)
        {
            Add((bits & (uint)Switch2ProButton.DpadUp) != 0, "↑");
            Add((bits & (uint)Switch2ProButton.DpadDown) != 0, "↓");
            Add((bits & (uint)Switch2ProButton.DpadLeft) != 0, "←");
            Add((bits & (uint)Switch2ProButton.DpadRight) != 0, "→");
            Add((bits & (uint)Switch2ProButton.LeftShoulder) != 0, "L");
            Add((bits & (uint)Switch2ProButton.LeftTrigger) != 0, "ZL");
            Add((bits & (uint)Switch2ProButton.Back) != 0, "−");
            Add((bits & (uint)Switch2ProButton.Capture) != 0, "Capture");
            Add((bits & (uint)Switch2ProButton.LeftStick) != 0, "L stick");
            LeftTrigger = (bits & (uint)Switch2ProButton.LeftTrigger) != 0 ? 100 : 0;
        }
    }

    private void AddSource(DS4State state, DS4Controls control, string name) =>
        Add(DS4StateFieldMapping.GetValidatedSwitch2SourceButton(state, control), name);
    private void Add(bool pressed, string name)
    {
        if (!pressed) return;
        if (buttons.Length != 0) buttons.Append("  ·  ");
        buttons.Append(name);
    }
}
