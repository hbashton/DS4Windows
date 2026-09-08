/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Controls extracted from the dedicated 112-byte Pro headset report. This
/// value owns only decoded scalars: no borrowed buffer, microphone data,
/// opaque motion, Common05/Pro09 identity, or runtime publication authority.
/// Calibration and axis orientation remain in the canonical profile mapper.
/// </summary>
internal readonly struct Switch2ProHeadsetControls
{
    internal Switch2ProHeadsetControls(byte counterRaw, byte powerInfoRaw,
        uint rawButtonBits, Switch2ProButton buttons,
        in Switch2StickRaw leftStick, in Switch2StickRaw rightStick)
    {
        Counter = counterRaw;
        PowerInfo = powerInfoRaw;
        RawButtonBits = rawButtonBits;
        Buttons = buttons;
        LeftStick = leftStick;
        RightStick = rightStick;
    }

    internal byte Counter { get; }
    internal byte PowerInfo { get; }
    internal uint RawButtonBits { get; }
    internal uint UnknownButtonBits => RawButtonBits & Switch2ProHeadsetControlsCodec.UnknownButtonMask;
    internal Switch2ProButton Buttons { get; }
    internal Switch2StickRaw LeftStick { get; }
    internal Switch2StickRaw RightStick { get; }
}

/// <summary>
/// Source-specific, allocation-free controls decoder. Parsing succeeds only
/// for the dedicated headset layout; the caller must separately admit the
/// exact characteristic and controller lifetime before any runtime use.
/// </summary>
internal static class Switch2ProHeadsetControlsCodec
{
    internal const int ReportLength = 112;
    internal const uint KnownButtonMask = 0x001FFFFF;
    internal const uint UnknownButtonMask = 0x00E00000;

    // Pinned ndeadly/switch2_controller_research d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92:
    // bluetooth_interface.md, dedicated UUID 7492866c-ec3e-4619-8258-32755ffcc0f9,
    // and hid_reports.md "Input Report 0x09" button table. The latter establishes
    // the shared control bit layout, NOT that the 112-byte report is Pro09.
    // Live b93/b94 evidence also established zero declared audio bytes.
    internal static bool TryDecode(ReadOnlySpan<byte> body, out Switch2ProHeadsetControls controls)
    {
        if (body.Length != ReportLength || body[14] is not (0 or 50) || body[65] is not (0 or 30 or 40))
        {
            controls = default;
            return false;
        }

        uint raw = (uint)(body[2] | body[3] << 8 | body[4] << 16);
        controls = new Switch2ProHeadsetControls(body[0], body[1], raw, MapButtons(raw),
            Switch2InputCodec.DecodePackedStick(body.Slice(5, 3)),
            Switch2InputCodec.DecodePackedStick(body.Slice(8, 3)));
        return true;
    }

    private static Switch2ProButton MapButtons(uint raw)
    {
        Switch2ProButton buttons = Switch2ProButton.None;
        // Native B/A/Y/X retain physical position. The existing profile face
        // layout setting, not this source decoder, chooses Nintendo/Xbox labels.
        Add(ref buttons, raw, 0, Switch2ProButton.FaceSouth);
        Add(ref buttons, raw, 1, Switch2ProButton.FaceEast);
        Add(ref buttons, raw, 2, Switch2ProButton.FaceWest);
        Add(ref buttons, raw, 3, Switch2ProButton.FaceNorth);
        Add(ref buttons, raw, 4, Switch2ProButton.RightShoulder);
        Add(ref buttons, raw, 5, Switch2ProButton.RightTrigger);
        Add(ref buttons, raw, 6, Switch2ProButton.Start);
        Add(ref buttons, raw, 7, Switch2ProButton.RightStick);
        Add(ref buttons, raw, 8, Switch2ProButton.DpadDown);
        Add(ref buttons, raw, 9, Switch2ProButton.DpadRight);
        Add(ref buttons, raw, 10, Switch2ProButton.DpadLeft);
        Add(ref buttons, raw, 11, Switch2ProButton.DpadUp);
        Add(ref buttons, raw, 12, Switch2ProButton.LeftShoulder);
        Add(ref buttons, raw, 13, Switch2ProButton.LeftTrigger);
        Add(ref buttons, raw, 14, Switch2ProButton.Back);
        Add(ref buttons, raw, 15, Switch2ProButton.LeftStick);
        Add(ref buttons, raw, 16, Switch2ProButton.Guide);
        Add(ref buttons, raw, 17, Switch2ProButton.Capture);
        Add(ref buttons, raw, 18, Switch2ProButton.RightPaddle);
        Add(ref buttons, raw, 19, Switch2ProButton.LeftPaddle);
        Add(ref buttons, raw, 20, Switch2ProButton.C);
        return buttons;
    }

    private static void Add(ref Switch2ProButton buttons, uint raw, int bit, Switch2ProButton semantic)
    {
        if ((raw & (1u << bit)) != 0) buttons |= semantic;
    }
}
