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
/// Driver relationship observed by the discovery/control plane. These values
/// describe an already-bound Windows interface; this module never changes a
/// driver binding.
/// </summary>
public enum Switch2UsbBoundDriver : byte
{
    Unknown = 0,
    HidClass = 1,
    WinUsb = 2,
}

public enum Switch2UsbPipeTransferType : byte
{
    Unknown = 0,
    Control = 1,
    Isochronous = 2,
    Bulk = 3,
    Interrupt = 4,
}

/// <summary>
/// Opaque Windows container identity used only for equality and dictionary
/// ownership. The underlying identifier is deliberately not exposed, formatted,
/// or serializable by this type.
/// </summary>
public readonly struct Switch2PhysicalContainerIdentity :
    IEquatable<Switch2PhysicalContainerIdentity>
{
    private readonly Guid value;

    private Switch2PhysicalContainerIdentity(Guid value)
    {
        this.value = value;
    }

    public bool IsValid => value != Guid.Empty;

    public static bool TryCreate(Guid value,
        out Switch2PhysicalContainerIdentity identity)
    {
        if (value == Guid.Empty)
        {
            identity = default;
            return false;
        }

        identity = new Switch2PhysicalContainerIdentity(value);
        return true;
    }

    public bool Equals(Switch2PhysicalContainerIdentity other) =>
        value == other.value;

    public override bool Equals(object obj) =>
        obj is Switch2PhysicalContainerIdentity other && Equals(other);

    public override int GetHashCode() => value.GetHashCode();

    /// <summary>
    /// Copies the opaque container bytes only into the trusted HMAC identity
    /// boundary. Callers must clear the destination and must never format,
    /// log, or persist it directly.
    /// </summary>
    internal bool TryCopyPseudonymInput(Span<byte> destination,
        out int length)
    {
        length = 0;
        if (!IsValid || destination.Length < 16 ||
            !value.TryWriteBytes(destination, bigEndian: false,
                out int written) || written != 16)
        {
            return false;
        }
        length = written;
        return true;
    }
}

/// <summary>
/// Descriptor-only USB pipe observation. It contains no handle or device path.
/// </summary>
public readonly struct Switch2UsbPipeObservation :
    IEquatable<Switch2UsbPipeObservation>
{
    public Switch2UsbPipeObservation(byte endpointAddress,
        Switch2UsbPipeTransferType transferType, ushort maximumPacketSize,
        byte interval)
    {
        EndpointAddress = endpointAddress;
        TransferType = transferType;
        MaximumPacketSize = maximumPacketSize;
        Interval = interval;
    }

    public byte EndpointAddress { get; }

    public Switch2UsbPipeTransferType TransferType { get; }

    public ushort MaximumPacketSize { get; }

    public byte Interval { get; }

    public bool Equals(Switch2UsbPipeObservation other) =>
        EndpointAddress == other.EndpointAddress &&
        TransferType == other.TransferType &&
        MaximumPacketSize == other.MaximumPacketSize &&
        Interval == other.Interval;

    public override bool Equals(object obj) =>
        obj is Switch2UsbPipeObservation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(EndpointAddress,
        TransferType, MaximumPacketSize, Interval);
}

/// <summary>
/// HID interface facts gathered before admission.
/// <see cref="ContainerIdentity"/> is an in-memory Windows identity fence and
/// must not be logged or serialized.
/// </summary>
public readonly struct Switch2UsbHidInterfaceObservation
{
    public Switch2UsbHidInterfaceObservation(
        Switch2PhysicalContainerIdentity containerIdentity,
        byte interfaceNumber, byte alternateSetting,
        Switch2UsbBoundDriver boundDriver, ushort usagePage, ushort usage,
        ushort inputReportByteLength, ushort outputReportByteLength,
        ushort featureReportByteLength)
    {
        ContainerIdentity = containerIdentity;
        InterfaceNumber = interfaceNumber;
        AlternateSetting = alternateSetting;
        BoundDriver = boundDriver;
        UsagePage = usagePage;
        Usage = usage;
        InputReportByteLength = inputReportByteLength;
        OutputReportByteLength = outputReportByteLength;
        FeatureReportByteLength = featureReportByteLength;
    }

    public Switch2PhysicalContainerIdentity ContainerIdentity { get; }

    public byte InterfaceNumber { get; }

    public byte AlternateSetting { get; }

    public Switch2UsbBoundDriver BoundDriver { get; }

    public ushort UsagePage { get; }

    public ushort Usage { get; }

    public ushort InputReportByteLength { get; }

    public ushort OutputReportByteLength { get; }

    public ushort FeatureReportByteLength { get; }
}

/// <summary>
/// WinUSB command-interface facts gathered before admission. The fixed two-pipe
/// storage avoids an array allocation and makes extra endpoints explicit.
/// </summary>
public readonly struct Switch2UsbCommandInterfaceObservation
{
    public Switch2UsbCommandInterfaceObservation(
        Switch2PhysicalContainerIdentity containerIdentity,
        byte interfaceNumber, byte alternateSetting,
        Switch2UsbBoundDriver boundDriver, byte endpointCount,
        in Switch2UsbPipeObservation pipe0,
        in Switch2UsbPipeObservation pipe1)
    {
        ContainerIdentity = containerIdentity;
        InterfaceNumber = interfaceNumber;
        AlternateSetting = alternateSetting;
        BoundDriver = boundDriver;
        EndpointCount = endpointCount;
        Pipe0 = pipe0;
        Pipe1 = pipe1;
    }

    public Switch2PhysicalContainerIdentity ContainerIdentity { get; }

    public byte InterfaceNumber { get; }

    public byte AlternateSetting { get; }

    public Switch2UsbBoundDriver BoundDriver { get; }

    public byte EndpointCount { get; }

    public Switch2UsbPipeObservation Pipe0 { get; }

    public Switch2UsbPipeObservation Pipe1 { get; }
}

/// <summary>
/// One composite-device observation supplied by future SetupAPI/HID/WinUSB
/// discovery. Admission compares the root and both child container identities;
/// VID/PID alone can never bind a command interface to an input interface.
/// </summary>
public readonly struct Switch2ProUsbCompositeObservation
{
    public Switch2ProUsbCompositeObservation(ushort vendorId,
        ushort productId, ushort bcdDevice,
        Switch2PhysicalContainerIdentity containerIdentity,
        byte matchingInputInterfaceCount,
        byte matchingCommandInterfaceCount,
        in Switch2UsbHidInterfaceObservation inputInterface,
        in Switch2UsbCommandInterfaceObservation commandInterface)
    {
        VendorId = vendorId;
        ProductId = productId;
        BcdDevice = bcdDevice;
        ContainerIdentity = containerIdentity;
        MatchingInputInterfaceCount = matchingInputInterfaceCount;
        MatchingCommandInterfaceCount = matchingCommandInterfaceCount;
        InputInterface = inputInterface;
        CommandInterface = commandInterface;
    }

    public ushort VendorId { get; }

    public ushort ProductId { get; }

    public ushort BcdDevice { get; }

    /// <summary>
    /// Private in-memory Windows container identity. It is intentionally not a
    /// serial number, persistent association key, or diagnostic string.
    /// </summary>
    public Switch2PhysicalContainerIdentity ContainerIdentity { get; }

    /// <summary>
    /// Number of present HID interfaces satisfying the discovery predicate.
    /// Admission requires exactly one and never chooses the first match.
    /// </summary>
    public byte MatchingInputInterfaceCount { get; }

    /// <summary>
    /// Number of present command interfaces satisfying the discovery predicate.
    /// Admission requires exactly one and never chooses the first match.
    /// </summary>
    public byte MatchingCommandInterfaceCount { get; }

    public Switch2UsbHidInterfaceObservation InputInterface { get; }

    public Switch2UsbCommandInterfaceObservation CommandInterface { get; }
}

public enum Switch2PhysicalAdmissionFailure : byte
{
    None = 0,
    UnrecognizedUsbIdentity,
    MissingContainerIdentity,
    InputInterfaceMultiplicityMismatch,
    CommandInterfaceMultiplicityMismatch,
    InputContainerMismatch,
    CommandContainerMismatch,
    InputDriverMismatch,
    InputInterfaceMismatch,
    InputHidUsageMismatch,
    InputReportShapeMismatch,
    CommandDriverMismatch,
    CommandInterfaceMismatch,
    CommandEndpointCountMismatch,
    CommandPipeTopologyMismatch,
}

/// <summary>
/// Versioned control-plane result shared by transport-specific workers and the
/// future DS4Windows registration adapter. It deliberately contains no
/// <c>HidDevice</c>, WinUSB handle, GATT object, path, or callback.
/// </summary>
public readonly struct Switch2PhysicalInputRegistration :
    IEquatable<Switch2PhysicalInputRegistration>
{
    public const ushort CurrentVersion = 1;
    public const byte ProUsbInputInterfaceNumber = 0;
    public const byte ProUsbCommandInterfaceNumber = 1;
    public const ushort ProUsbReportByteLength = 64;

    internal Switch2PhysicalInputRegistration(
        Switch2PhysicalContainerIdentity containerIdentity,
        in Switch2InputProtocolIdentity protocolIdentity)
    {
        Version = CurrentVersion;
        ContainerIdentity = containerIdentity;
        ProtocolIdentity = protocolIdentity;
        InputInterfaceNumber = ProUsbInputInterfaceNumber;
        CommandInterfaceNumber = ProUsbCommandInterfaceNumber;
        InputReportByteLength = ProUsbReportByteLength;
    }

    public ushort Version { get; }

    /// <summary>
    /// Private in-memory Windows container identity used only to prevent
    /// cross-controller interface binding. Do not log or serialize it.
    /// </summary>
    public Switch2PhysicalContainerIdentity ContainerIdentity { get; }

    public Switch2InputProtocolIdentity ProtocolIdentity { get; }

    public Switch2ControllerModel Model => ProtocolIdentity.Model;

    public Switch2Transport Transport => ProtocolIdentity.Transport;

    public byte InputInterfaceNumber { get; }

    public byte CommandInterfaceNumber { get; }

    public ushort InputReportByteLength { get; }

    public bool IsValid => Version == CurrentVersion &&
        ContainerIdentity.IsValid && ProtocolIdentity.IsValid &&
        Model == Switch2ControllerModel.ProController2 &&
        Transport == Switch2Transport.Usb &&
        ProtocolIdentity.ProtocolRevision ==
            Switch2InputProtocolRevision.ProUsbCommon05Bcd0201 &&
        ProtocolIdentity.VendorId ==
            Switch2InputProtocolIdentity.NintendoUsbVendorId &&
        ProtocolIdentity.ProductId ==
            Switch2InputProtocolIdentity.ProController2UsbProductId &&
        ProtocolIdentity.BcdDevice ==
            Switch2InputProtocolIdentity.AuditedProController2UsbBcdDevice &&
        InputInterfaceNumber == ProUsbInputInterfaceNumber &&
        CommandInterfaceNumber == ProUsbCommandInterfaceNumber &&
        InputReportByteLength == ProUsbReportByteLength;

    public bool Equals(Switch2PhysicalInputRegistration other) =>
        Version == other.Version &&
        ContainerIdentity.Equals(other.ContainerIdentity) &&
        ProtocolIdentity.Equals(other.ProtocolIdentity) &&
        InputInterfaceNumber == other.InputInterfaceNumber &&
        CommandInterfaceNumber == other.CommandInterfaceNumber &&
        InputReportByteLength == other.InputReportByteLength;

    public override bool Equals(object obj) =>
        obj is Switch2PhysicalInputRegistration other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Version,
        ContainerIdentity, ProtocolIdentity, InputInterfaceNumber,
        CommandInterfaceNumber, InputReportByteLength);
}

/// <summary>
/// Exact, side-effect-free admission gate for the currently evidenced physical
/// Switch 2 Pro USB composite device. OS enumeration and handle opening remain
/// outside this class.
/// </summary>
public static class Switch2PhysicalDeviceFactory
{
    public const byte ProUsbAlternateSetting = 0;
    public const ushort GenericDesktopUsagePage = 0x0001;
    public const ushort GamePadUsage = 0x0005;
    public const byte CommandBulkOutEndpoint = 0x02;
    public const byte CommandBulkInEndpoint = 0x82;
    public const ushort CommandMaximumPacketSize = 64;

    public static bool TryAdmitProUsb(
        in Switch2ProUsbCompositeObservation observation,
        out Switch2PhysicalInputRegistration registration,
        out Switch2PhysicalAdmissionFailure failure)
    {
        if (!Switch2InputProtocolIdentity.TryCreateProController2Usb(
                observation.VendorId, observation.ProductId,
                observation.BcdDevice,
                out Switch2InputProtocolIdentity protocolIdentity))
        {
            return Fail(
                Switch2PhysicalAdmissionFailure.UnrecognizedUsbIdentity,
                out registration, out failure);
        }
        if (!observation.ContainerIdentity.IsValid)
        {
            return Fail(
                Switch2PhysicalAdmissionFailure.MissingContainerIdentity,
                out registration, out failure);
        }
        if (observation.MatchingInputInterfaceCount != 1)
        {
            return Fail(Switch2PhysicalAdmissionFailure.
                InputInterfaceMultiplicityMismatch, out registration,
                out failure);
        }
        if (observation.MatchingCommandInterfaceCount != 1)
        {
            return Fail(Switch2PhysicalAdmissionFailure.
                CommandInterfaceMultiplicityMismatch, out registration,
                out failure);
        }

        Switch2UsbHidInterfaceObservation input =
            observation.InputInterface;
        if (!input.ContainerIdentity.Equals(observation.ContainerIdentity))
        {
            return Fail(Switch2PhysicalAdmissionFailure.InputContainerMismatch,
                out registration, out failure);
        }
        if (input.BoundDriver != Switch2UsbBoundDriver.HidClass)
        {
            return Fail(Switch2PhysicalAdmissionFailure.InputDriverMismatch,
                out registration, out failure);
        }
        if (input.InterfaceNumber !=
                Switch2PhysicalInputRegistration.ProUsbInputInterfaceNumber ||
            input.AlternateSetting != ProUsbAlternateSetting)
        {
            return Fail(Switch2PhysicalAdmissionFailure.InputInterfaceMismatch,
                out registration, out failure);
        }
        if (input.UsagePage != GenericDesktopUsagePage ||
            input.Usage != GamePadUsage)
        {
            return Fail(Switch2PhysicalAdmissionFailure.InputHidUsageMismatch,
                out registration, out failure);
        }
        if (input.InputReportByteLength !=
                Switch2PhysicalInputRegistration.ProUsbReportByteLength ||
            input.OutputReportByteLength !=
                Switch2PhysicalInputRegistration.ProUsbReportByteLength ||
            input.FeatureReportByteLength != 0)
        {
            return Fail(Switch2PhysicalAdmissionFailure.InputReportShapeMismatch,
                out registration, out failure);
        }

        Switch2UsbCommandInterfaceObservation command =
            observation.CommandInterface;
        if (!command.ContainerIdentity.Equals(
                observation.ContainerIdentity))
        {
            return Fail(
                Switch2PhysicalAdmissionFailure.CommandContainerMismatch,
                out registration, out failure);
        }
        if (command.BoundDriver != Switch2UsbBoundDriver.WinUsb)
        {
            return Fail(Switch2PhysicalAdmissionFailure.CommandDriverMismatch,
                out registration, out failure);
        }
        if (command.InterfaceNumber !=
                Switch2PhysicalInputRegistration.ProUsbCommandInterfaceNumber ||
            command.AlternateSetting != ProUsbAlternateSetting)
        {
            return Fail(
                Switch2PhysicalAdmissionFailure.CommandInterfaceMismatch,
                out registration, out failure);
        }
        if (command.EndpointCount != 2)
        {
            return Fail(
                Switch2PhysicalAdmissionFailure.CommandEndpointCountMismatch,
                out registration, out failure);
        }
        if (!HasExactCommandPipes(command.Pipe0, command.Pipe1))
        {
            return Fail(
                Switch2PhysicalAdmissionFailure.CommandPipeTopologyMismatch,
                out registration, out failure);
        }

        registration = new Switch2PhysicalInputRegistration(
            observation.ContainerIdentity, protocolIdentity);
        failure = Switch2PhysicalAdmissionFailure.None;
        return true;
    }

    private static bool HasExactCommandPipes(
        in Switch2UsbPipeObservation first,
        in Switch2UsbPipeObservation second) =>
        (IsExactCommandPipe(first, CommandBulkOutEndpoint) &&
            IsExactCommandPipe(second, CommandBulkInEndpoint)) ||
        (IsExactCommandPipe(first, CommandBulkInEndpoint) &&
            IsExactCommandPipe(second, CommandBulkOutEndpoint));

    private static bool IsExactCommandPipe(
        in Switch2UsbPipeObservation pipe, byte endpointAddress) =>
        pipe.EndpointAddress == endpointAddress &&
        pipe.TransferType == Switch2UsbPipeTransferType.Bulk &&
        pipe.MaximumPacketSize == CommandMaximumPacketSize &&
        pipe.Interval == 0;

    private static bool Fail(Switch2PhysicalAdmissionFailure reason,
        out Switch2PhysicalInputRegistration registration,
        out Switch2PhysicalAdmissionFailure failure)
    {
        registration = default;
        failure = reason;
        return false;
    }
}

/// <summary>
/// One admitted controller lifetime. The registration fences the composite
/// device while the session descriptor fences parser identity and generations.
/// </summary>
public readonly struct Switch2PhysicalInputLifetime :
    IEquatable<Switch2PhysicalInputLifetime>
{
    private Switch2PhysicalInputLifetime(
        in Switch2PhysicalInputRegistration registration,
        in Switch2InputSessionDescriptor sessionDescriptor)
    {
        Registration = registration;
        SessionDescriptor = sessionDescriptor;
    }

    public Switch2PhysicalInputRegistration Registration { get; }

    public Switch2InputSessionDescriptor SessionDescriptor { get; }

    public bool IsValid => Registration.IsValid &&
        SessionDescriptor.IsValid &&
        Registration.ProtocolIdentity.Equals(SessionDescriptor.Identity);

    public static bool TryCreate(
        in Switch2PhysicalInputRegistration registration,
        ulong deviceGeneration, ulong transportGeneration, long qpcFrequency,
        out Switch2PhysicalInputLifetime lifetime)
    {
        if (!registration.IsValid ||
            !Switch2InputSessionDescriptor.TryCreate(
                registration.ProtocolIdentity, deviceGeneration,
                transportGeneration, qpcFrequency,
                out Switch2InputSessionDescriptor descriptor))
        {
            lifetime = default;
            return false;
        }

        lifetime = new Switch2PhysicalInputLifetime(registration, descriptor);
        return true;
    }

    public bool Equals(Switch2PhysicalInputLifetime other) =>
        Registration.Equals(other.Registration) &&
        SessionDescriptor.Equals(other.SessionDescriptor);

    public override bool Equals(object obj) =>
        obj is Switch2PhysicalInputLifetime other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Registration,
        SessionDescriptor);
}

public enum Switch2PhysicalInputFailureKind : byte
{
    None = 0,
    InvalidLifetime,
    InvalidCalibration,
    LifetimeMismatch,
    RegistrationMismatch,
    SessionRejected,
}

public readonly struct Switch2PhysicalInputFailure
{
    internal Switch2PhysicalInputFailure(
        Switch2PhysicalInputFailureKind kind,
        Switch2InputSessionFailure sessionFailure)
    {
        Kind = kind;
        SessionFailure = sessionFailure;
    }

    public Switch2PhysicalInputFailureKind Kind { get; }

    public Switch2InputSessionFailure SessionFailure { get; }

    public bool IsNone => Kind == Switch2PhysicalInputFailureKind.None;
}

/// <summary>
/// Transport-neutral parser/lifetime seam for a future USB or BLE worker.
/// The worker timestamps read completion and passes the exact packet span here;
/// this class performs no I/O, discovery, logging, waiting, or publication.
/// Calls are single-writer: reset occurs only after the read worker is quiescent.
/// </summary>
public sealed class Switch2PhysicalInputAdapter
{
    private Switch2PhysicalInputLifetime lifetime;
    private readonly Switch2InputSession session;

    private Switch2PhysicalInputAdapter(
        in Switch2PhysicalInputLifetime lifetime,
        in Switch2InputCalibrationSnapshot calibration)
    {
        this.lifetime = lifetime;
        session = new Switch2InputSession(lifetime.SessionDescriptor,
            calibration);
    }

    public Switch2PhysicalInputLifetime Lifetime => lifetime;

    public static bool TryCreate(in Switch2PhysicalInputLifetime lifetime,
        in Switch2InputCalibrationSnapshot calibration,
        out Switch2PhysicalInputAdapter adapter,
        out Switch2PhysicalInputFailure failure)
    {
        if (!lifetime.IsValid)
        {
            return Fail(Switch2PhysicalInputFailureKind.InvalidLifetime,
                Switch2InputSessionFailure.InvalidDescriptor, out adapter,
                out failure);
        }
        if (!calibration.IsValid ||
            calibration.Model != lifetime.Registration.Model ||
            calibration.DeviceGeneration !=
                lifetime.SessionDescriptor.DeviceGeneration)
        {
            return Fail(Switch2PhysicalInputFailureKind.InvalidCalibration,
                Switch2InputSessionFailure.InvalidCalibration, out adapter,
                out failure);
        }

        adapter = new Switch2PhysicalInputAdapter(lifetime, calibration);
        failure = default;
        return true;
    }

    public bool TryProcess(in Switch2PhysicalInputLifetime observationLifetime,
        ReadOnlySpan<byte> packet, long completionTimestampQpc,
        out Switch2CanonicalInputFrame frame,
        out Switch2PhysicalInputFailure failure)
    {
        if (!observationLifetime.Equals(lifetime))
        {
            frame = default;
            failure = new Switch2PhysicalInputFailure(
                Switch2PhysicalInputFailureKind.LifetimeMismatch,
                Switch2InputSessionFailure.DescriptorMismatch);
            return false;
        }
        if (!session.TryProcess(observationLifetime.SessionDescriptor, packet,
                completionTimestampQpc, out frame,
                out Switch2InputSessionFailure sessionFailure))
        {
            failure = new Switch2PhysicalInputFailure(
                Switch2PhysicalInputFailureKind.SessionRejected,
                sessionFailure);
            return false;
        }

        failure = default;
        return true;
    }

    public bool TryReset(in Switch2PhysicalInputLifetime next,
        in Switch2InputCalibrationSnapshot nextCalibration,
        out Switch2PhysicalInputFailure failure)
    {
        if (!next.IsValid)
        {
            failure = new Switch2PhysicalInputFailure(
                Switch2PhysicalInputFailureKind.InvalidLifetime,
                Switch2InputSessionFailure.InvalidDescriptor);
            return false;
        }
        if (!next.Registration.Equals(lifetime.Registration))
        {
            failure = new Switch2PhysicalInputFailure(
                Switch2PhysicalInputFailureKind.RegistrationMismatch,
                Switch2InputSessionFailure.DescriptorMismatch);
            return false;
        }
        if (!session.TryReset(next.SessionDescriptor, nextCalibration,
                out Switch2InputSessionFailure sessionFailure))
        {
            failure = new Switch2PhysicalInputFailure(
                Switch2PhysicalInputFailureKind.SessionRejected,
                sessionFailure);
            return false;
        }

        lifetime = next;
        failure = default;
        return true;
    }

    private static bool Fail(Switch2PhysicalInputFailureKind kind,
        Switch2InputSessionFailure sessionFailure,
        out Switch2PhysicalInputAdapter adapter,
        out Switch2PhysicalInputFailure failure)
    {
        adapter = null;
        failure = new Switch2PhysicalInputFailure(kind, sessionFailure);
        return false;
    }
}
