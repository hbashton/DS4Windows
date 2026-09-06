using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Semantic controls proven for Pro Controller 2 common report <c>0x05</c>.
/// Values intentionally retain the report's raw bit positions so projection is
/// allocation-free and unknown bits can be carried separately.
/// </summary>
[Flags]
public enum Switch2ProButton : uint
{
    None = 0,
    FaceWest = 0x00000001,
    FaceNorth = 0x00000002,
    FaceSouth = 0x00000004,
    FaceEast = 0x00000008,
    RightShoulder = 0x00000040,
    RightTrigger = 0x00000080,
    Back = 0x00000100,
    Start = 0x00000200,
    RightStick = 0x00000400,
    LeftStick = 0x00000800,
    Guide = 0x00001000,
    Capture = 0x00002000,
    C = 0x00004000,
    DpadDown = 0x00010000,
    DpadUp = 0x00020000,
    DpadRight = 0x00040000,
    DpadLeft = 0x00080000,
    LeftShoulder = 0x00400000,
    LeftTrigger = 0x00800000,
    RightPaddle = 0x01000000,
    LeftPaddle = 0x02000000,
}

public enum Switch2FirmwareEvidence : byte
{
    /// <summary>
    /// Firmware was deliberately not queried. The USB <c>bcdDevice</c> value is
    /// a device revision and must not be represented as a firmware version.
    /// </summary>
    UnknownNotQueried = 1,
}

public enum Switch2ProUsbProtocolRevision : byte
{
    Common05Bcd0201 = 1,
}

/// <summary>
/// Exact, source-pinned identity gate for the only Pro Controller 2 USB input
/// revision currently validated by project-owned passive evidence.
/// </summary>
public readonly struct Switch2ProUsbProtocolIdentity
{
    internal Switch2ProUsbProtocolIdentity(ushort vendorId, ushort productId,
        ushort bcdDevice, Switch2ProUsbProtocolRevision protocolRevision)
    {
        VendorId = vendorId;
        ProductId = productId;
        BcdDevice = bcdDevice;
        ProtocolRevision = protocolRevision;
        Model = Switch2ControllerModel.ProController2;
        FirmwareEvidence = Switch2FirmwareEvidence.UnknownNotQueried;
    }

    public ushort VendorId { get; }

    public ushort ProductId { get; }

    /// <summary>
    /// USB device-descriptor revision. This is not a firmware version.
    /// </summary>
    public ushort BcdDevice { get; }

    public Switch2ControllerModel Model { get; }

    public Switch2FirmwareEvidence FirmwareEvidence { get; }

    public Switch2ProUsbProtocolRevision ProtocolRevision { get; }
}

public enum Switch2ProUsbProjectionFailure : byte
{
    None = 0,
    UnrecognizedUsbIdentity,
    MissingReplayFixture,
    UnsupportedTransport,
    UnsupportedModel,
    UnsupportedFirmwareEvidence,
    UnsupportedReport,
    InvalidHostClock,
}

/// <summary>
/// High-resolution Pro Controller 2 input at the parser/mapping boundary.
/// Sticks remain in the controller's 12-bit units; calibration and axis
/// orientation belong to the later mapping layer. Sensor and unknown fields are
/// retained in <see cref="RawReport"/> without assigning unproven semantics.
/// </summary>
public readonly struct Switch2ProUsbCanonicalInputFrame
{
    internal Switch2ProUsbCanonicalInputFrame(
        in Switch2ProUsbProtocolIdentity identity,
        in Switch2CanonicalInputFrame canonical, Switch2ProButton buttons,
        uint unknownButtonBits)
    {
        Model = identity.Model;
        UsbVendorId = identity.VendorId;
        UsbProductId = identity.ProductId;
        UsbBcdDevice = identity.BcdDevice;
        ProtocolRevision = identity.ProtocolRevision;
        FirmwareEvidence = identity.FirmwareEvidence;
        HostTimestampTicks = canonical.CompletionTimestampQpc;
        HostTimestampFrequency = canonical.QpcFrequency;
        DeviceCounterRaw = canonical.DeviceCounterRaw;
        HasDeviceCounterDelta = canonical.HasCounterDelta;
        DeviceCounterDeltaRaw = canonical.CounterDeltaRaw;
        CounterSequence = canonical.CounterSequence;
        Buttons = buttons;
        RawButtonBits = canonical.Report.Common.Buttons;
        UnknownButtonBits = unknownButtonBits;
        LeftStickRaw = canonical.Report.Common.LeftStick;
        RightStickRaw = canonical.Report.Common.RightStick;
        RawReport = canonical.Report.Common;
    }

    public Switch2ControllerModel Model { get; }

    public ushort UsbVendorId { get; }

    public ushort UsbProductId { get; }

    public ushort UsbBcdDevice { get; }

    public Switch2ProUsbProtocolRevision ProtocolRevision { get; }

    public Switch2FirmwareEvidence FirmwareEvidence { get; }

    /// <summary>
    /// Host monotonic timestamp captured at report delivery. This is the only
    /// timestamp promoted for ordering; the report's motion timestamp remains
    /// raw because its units are not established.
    /// </summary>
    public long HostTimestampTicks { get; }

    public long HostTimestampFrequency { get; }

    public uint DeviceCounterRaw { get; }

    public bool HasDeviceCounterDelta { get; }

    /// <summary>
    /// Raw modular counter movement. It is not a packet-loss count and does not
    /// establish a physical report rate.
    /// </summary>
    public uint DeviceCounterDeltaRaw { get; }

    public Switch2CounterSequenceKind CounterSequence { get; }

    public Switch2ProButton Buttons { get; }

    public uint RawButtonBits { get; }

    /// <summary>
    /// Every report bit not assigned a Pro semantic by the pinned sources.
    /// </summary>
    public uint UnknownButtonBits { get; }

    public Switch2StickRaw LeftStickRaw { get; }

    public Switch2StickRaw RightStickRaw { get; }

    public Switch2CommonInputReport RawReport { get; }
}

/// <summary>
/// Offline-only Pro Controller 2 USB identity and canonical projection. No
/// discovery, I/O, controller registration, output or state publication occurs.
/// </summary>
public static class Switch2ProUsbInputProjection
{
    public const ushort NintendoUsbVendorId =
        Switch2InputProtocolIdentity.NintendoUsbVendorId;
    public const ushort ProController2ProductId =
        Switch2InputProtocolIdentity.ProController2UsbProductId;
    public const ushort AuditedBcdDevice =
        Switch2InputProtocolIdentity.AuditedProController2UsbBcdDevice;

    public const uint KnownButtonMask =
        Switch2CanonicalInputFrame.ProController2KnownCommonButtonMask;

    public static bool TryResolveIdentity(ushort vendorId, ushort productId,
        ushort bcdDevice, out Switch2ProUsbProtocolIdentity identity)
    {
        if (vendorId != NintendoUsbVendorId ||
            productId != ProController2ProductId ||
            bcdDevice != AuditedBcdDevice)
        {
            identity = default;
            return false;
        }

        identity = new Switch2ProUsbProtocolIdentity(vendorId, productId,
            bcdDevice, Switch2ProUsbProtocolRevision.Common05Bcd0201);
        return true;
    }

    public static bool TryProject(in Switch2ReplayEvent replayEvent,
        in Switch2ProUsbProtocolIdentity identity,
        out Switch2ProUsbCanonicalInputFrame frame,
        out Switch2ProUsbProjectionFailure failure)
    {
        if (identity.ProtocolRevision !=
                Switch2ProUsbProtocolRevision.Common05Bcd0201 ||
            identity.VendorId != NintendoUsbVendorId ||
            identity.ProductId != ProController2ProductId ||
            identity.BcdDevice != AuditedBcdDevice ||
            identity.Model != Switch2ControllerModel.ProController2 ||
            identity.FirmwareEvidence !=
                Switch2FirmwareEvidence.UnknownNotQueried)
        {
            return Fail(Switch2ProUsbProjectionFailure.UnrecognizedUsbIdentity,
                out frame, out failure);
        }

        if (replayEvent.Fixture == null)
        {
            return Fail(Switch2ProUsbProjectionFailure.MissingReplayFixture,
                out frame, out failure);
        }

        if (replayEvent.Fixture.Transport != Switch2Transport.Usb)
        {
            return Fail(Switch2ProUsbProjectionFailure.UnsupportedTransport,
                out frame, out failure);
        }

        if (replayEvent.Fixture.Model != Switch2ControllerModel.ProController2 ||
            replayEvent.Report.Model != Switch2ControllerModel.ProController2)
        {
            return Fail(Switch2ProUsbProjectionFailure.UnsupportedModel,
                out frame, out failure);
        }

        if (!string.Equals(replayEvent.Fixture.Firmware, "unknown",
                StringComparison.Ordinal))
        {
            return Fail(
                Switch2ProUsbProjectionFailure.UnsupportedFirmwareEvidence,
                out frame, out failure);
        }

        if (!replayEvent.Report.IsCommon ||
            replayEvent.Report.Kind != Switch2InputReportKind.Common05)
        {
            return Fail(Switch2ProUsbProjectionFailure.UnsupportedReport,
                out frame, out failure);
        }

        if (replayEvent.Fixture.HostTimestampTicks < 0 ||
            replayEvent.Fixture.HostTimestampFrequency <= 0)
        {
            return Fail(Switch2ProUsbProjectionFailure.InvalidHostClock,
                out frame, out failure);
        }

        if (!Switch2InputProtocolIdentity.TryCreateProController2Usb(
                identity.VendorId, identity.ProductId, identity.BcdDevice,
                out Switch2InputProtocolIdentity inputIdentity) ||
            !Switch2InputSessionDescriptor.TryCreate(inputIdentity,
                replayEvent.Fixture.Generation,
                // Replay schema v1 has one stream generation. The explicit
                // transport generation is a replay-local fence, not hardware
                // evidence, and is deliberately fixed to one.
                1, replayEvent.Fixture.HostTimestampFrequency,
                out Switch2InputSessionDescriptor descriptor) ||
            !Switch2InputCalibrationSnapshot.TryCreateFallback(
                Switch2ControllerModel.ProController2,
                replayEvent.Fixture.Generation,
                out Switch2InputCalibrationSnapshot calibration) ||
            !Switch2InputSession.TryBuildReplayFrame(descriptor, calibration,
                replayEvent.Fixture.PacketBytes,
                replayEvent.Fixture.HostTimestampTicks,
                replayEvent.HasCounterDelta, replayEvent.CounterDelta,
                replayEvent.CounterSequence,
                out Switch2CanonicalInputFrame canonical, out _))
        {
            return Fail(Switch2ProUsbProjectionFailure.UnsupportedReport,
                out frame, out failure);
        }

        uint rawButtons = canonical.RawButtonBits;
        var buttons = (Switch2ProButton)(rawButtons & KnownButtonMask);
        frame = new Switch2ProUsbCanonicalInputFrame(identity, canonical,
            buttons, rawButtons & ~KnownButtonMask);
        failure = Switch2ProUsbProjectionFailure.None;
        return true;
    }

    private static bool Fail(Switch2ProUsbProjectionFailure reason,
        out Switch2ProUsbCanonicalInputFrame frame,
        out Switch2ProUsbProjectionFailure failure)
    {
        frame = default;
        failure = reason;
        return false;
    }
}
