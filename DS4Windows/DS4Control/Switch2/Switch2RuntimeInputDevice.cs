/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

public enum Switch2RuntimeInputDeviceCreateFailure : byte
{
    None = 0,
    InvalidGeneration,
    InvalidTransport,
    InvalidModel,
    InvalidPairEpoch,
}

public enum Switch2RuntimeInputDeviceState : byte
{
    Created = 0,
    Active,
    Terminal,
    AbortedUnpublished,
}

/// <summary>
/// Immutable physical ownership shape for a Switch 2 runtime. Standalone side
/// identity belongs here; vertical/horizontal presentation is a persisted
/// controller preference with an active-profile fallback and must never
/// participate in transport authentication.
/// </summary>
internal enum Switch2JoyConRuntimeBindingMode : byte
{
    Invalid = 0,
    Joined = 1,
    StandaloneLeft = 2,
    StandaloneRight = 3,
}

public enum Switch2RuntimeReportKind : byte
{
    Regular = 1,
    TerminalNeutral,
}

/// <summary>
/// Immutable source evidence attached to the existing DS4Device.Report event.
/// The sealed envelope is created only by the runtime device and binds the
/// report kind to that device's exact nonzero logical generation.
/// </summary>
public sealed class Switch2RuntimeReportEventArgs : EventArgs
{
    internal Switch2RuntimeReportEventArgs(Switch2RuntimeReportKind kind,
        ulong runtimeGeneration)
    {
        if (kind is not (Switch2RuntimeReportKind.Regular or
                Switch2RuntimeReportKind.TerminalNeutral))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (runtimeGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }

        Kind = kind;
        RuntimeGeneration = runtimeGeneration;
    }

    public Switch2RuntimeReportKind Kind { get; }

    public ulong RuntimeGeneration { get; }
}

public enum Switch2TerminalNeutralRequestResult : byte
{
    RejectedAlreadyReserved = 0,
    AcceptedPending,
    AcceptedCompleted,
}

public enum Switch2RuntimePublicationResult : byte
{
    Published = 0,
    LifecycleClosed,
    FrameRejected,
    PublicationBusy,
    SubscriberRejected,
}

/// <summary>
/// Logical Switch 2 mapping device. It accepts only already-validated input
/// frames and publishes them through DS4Windows' existing DS4State/Report seam.
/// It owns no HID, discovery, WinRT, registration, command transport, or raw
/// physical writer. An authenticated transport lifetime can attach the shared
/// canonical feedback owner; this type may then publish DS4Windows profile and
/// preview effects into that owner without acquiring a second writer.
/// </summary>
public sealed partial class Switch2RuntimeInputDevice : DS4Device
{
    private const ulong LocalFeedbackTimeToLiveMicroseconds = 250_000;
    private const ulong LocalFeedbackRenewalIntervalMicroseconds = 100_000;
    private const int InputObservationWindowLength = 20;

    private readonly object publicationGate = new();
    private readonly object localFeedbackGate = new();
    private readonly Switch2Transport transport;
    private readonly Switch2JoyConRuntimeBindingMode joyConBindingMode;
    private readonly ulong pairEpoch;
    private readonly ulong leftDeviceGeneration;
    private readonly ulong leftTransportGeneration;
    private readonly ulong rightDeviceGeneration;
    private readonly ulong rightTransportGeneration;
    private readonly DS4State stagingState = new();
    private readonly DS4State neutralState = new();
    private readonly Switch2RuntimeReportEventArgs regularReportEventArgs;
    private readonly Switch2RuntimeReportEventArgs terminalReportEventArgs;
    private readonly double[] inputObservationIntervals =
        new double[InputObservationWindowLength];
    private bool hasObservedPhysicalInput;
    private long inputObservationTimestampQpc;
    private long inputObservationQpcFrequency;
    private int inputObservationCount;
    private int inputObservationNext;
    private double inputObservationSumMilliseconds;
    private Func<ulong, bool> bluetoothDisconnectRequestHandler;
    private int bluetoothDisconnectRequested;
    private long idleLastActivityTimestampQpc;
    private long idleQpcFrequency;
    private bool idleActivityTimestampInitialized;
    private long absoluteSessionStartTimestampQpc;
    private long absoluteSessionQpcFrequency;
    private bool absoluteSessionTimestampInitialized;
    private readonly Switch2JoyConMotionProjection joyConMotionProjection =
        new();
    private Switch2DualGyroModeState joyConGyroModeState;
    private readonly Switch2ProMotionProjection proMotionProjection = new();
    private readonly Switch2HighRateMousePresenter highRateMousePresenter =
        new();
    private Switch2BluetoothFeedbackLifetime bluetoothFeedbackLifetime;
    private Switch2ProUsbOwnedFeedbackActivationLifetime usbFeedbackLifetime;
    private ISwitch2MagnetometerCalibrationStore
        magnetometerCalibrationStore;
    private Switch2PersistentPeerId leftMagnetometerPeerId;
    private Switch2PersistentPeerId rightMagnetometerPeerId;
    private bool magnetometerCalibrationPersistenceBound;
    private ISwitch2GyroCalibrationStore gyroCalibrationStore;
    private Switch2PersistentPeerId leftGyroCalibrationPeerId;
    private Switch2PersistentPeerId rightGyroCalibrationPeerId;
    private ulong lastQueuedLeftGyroBiasRevision;
    private ulong lastQueuedRightGyroBiasRevision;
    private bool gyroCalibrationPersistenceBound;
    private Switch2RawStickCalibrationBinding rawStickCalibration;
    private readonly object joyConHoldModeMutationGate = new();
    private int virtualOutputTransitionDepth;
    private long virtualOutputTransitionRevision;
    private ISwitch2JoyConHoldModeStore joyConHoldModeStore;
    private Switch2PersistentPeerId joyConHoldModePeerId;
    private int joyConHoldModeOverride = -1;
    private bool joyConHoldModePersistenceBound;
    private ControllerFeedbackStateLanePump.Lane profileFeedbackLane;
    private ControllerFeedbackStateLanePump.Lane previewFeedbackLane;
    private CancellationTokenSource connectionHapticCancellation;
    private bool connectionHapticStarted;
    private bool connectionHapticOwnsProfileLane;
    private CancellationTokenSource identificationHapticCancellation;
    private bool identificationHapticOwnsPreviewLane;
    private byte profileLightFastRumble;
    private byte profileHeavySlowRumble;
    private ReportHandler<EventArgs> reportHandlers;
    private EventHandler batteryChangedHandlers;
    private ReportHandler<EventArgs>[] reportSubscribers =
        Array.Empty<ReportHandler<EventArgs>>();
    private ReportHandler<EventArgs>[] terminalNeutralSubscribers =
        Array.Empty<ReportHandler<EventArgs>>();

    private Switch2RuntimeInputDeviceState runtimeState =
        Switch2RuntimeInputDeviceState.Created;
    private bool publicationInProgress;
    private int publicationThreadId;
    private bool terminalNeutralReserved;
    private bool terminalNeutralPending;
    private bool terminalNeutralCompleted;
    private bool terminalNeutralReported;
    private bool stagingHasMotion;
    private bool publicationHasMotion;
    private bool publicationIsTerminal;
    private bool reportCallbacksActive;
    private uint lastPacketCounter;
    private Switch2BatteryStatus batteryStatus;
    private Switch2BatteryStatus leftBatteryStatus;
    private Switch2BatteryStatus rightBatteryStatus;

    private Switch2RuntimeInputDevice(string displayName,
        InputDeviceType inputDeviceType, ConnectionType connectionType,
        Switch2Transport transport, ulong runtimeGeneration,
        Switch2JoyConRuntimeBindingMode joyConBindingMode, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
        : base(displayName, inputDeviceType, connectionType)
    {
        this.transport = transport;
        RuntimeGeneration = runtimeGeneration;
        this.joyConBindingMode = joyConBindingMode;
        this.pairEpoch = pairEpoch;
        this.leftDeviceGeneration = leftDeviceGeneration;
        this.leftTransportGeneration = leftTransportGeneration;
        this.rightDeviceGeneration = rightDeviceGeneration;
        this.rightTransportGeneration = rightTransportGeneration;
        regularReportEventArgs = new Switch2RuntimeReportEventArgs(
            Switch2RuntimeReportKind.Regular, runtimeGeneration);
        terminalReportEventArgs = new Switch2RuntimeReportEventArgs(
            Switch2RuntimeReportKind.TerminalNeutral, runtimeGeneration);

        PrimaryDevice = true;
        PerformStateMerge = false;
        // The Switch 2 projections already produce DS4Windows' canonical
        // SixAxis shape. Keep the ordinary gyro mapping seam enabled so the
        // existing mouse, mouse-joystick, controls, and steering modes consume
        // that state rather than requiring a Switch 2-specific mapper.
        OutputMapGyro = true;
        // Switch 2 owns family-specific, QPC-driven per-IMU calibration in
        // its serialized projection. The legacy wall-clock sampler is unused.
        sixAxis.StopContinuousCalibration();
        featureSet &= ~(VidPidFeatureSet.NoBatteryReading |
            VidPidFeatureSet.NoGyroCalib);
        battery = 99;
        charging = false;
    }

    public ulong RuntimeGeneration { get; }

    public override bool IsAlive()
    {
        lock (publicationGate)
        {
            // The legacy implementation inspects a DS4-specific HID byte.
            // This logical device instead proves life through an admitted,
            // generation-checked physical frame, never through StartUpdate or
            // its synthetic terminal neutral.
            return runtimeState == Switch2RuntimeInputDeviceState.Active &&
                !terminalNeutralReserved && hasObservedPhysicalInput;
        }
    }

    public override event ReportHandler<EventArgs> Report
    {
        add
        {
            if (value == null)
            {
                return;
            }

            lock (publicationGate)
            {
                reportHandlers += value;
                RefreshReportSubscriberSnapshotNoLock();
            }
        }
        remove
        {
            if (value == null)
            {
                return;
            }

            lock (publicationGate)
            {
                reportHandlers -= value;
                RefreshReportSubscriberSnapshotNoLock();
            }
        }
    }

    public override event EventHandler BatteryChanged
    {
        add
        {
            lock (publicationGate)
            {
                batteryChangedHandlers += value;
            }
        }
        remove
        {
            lock (publicationGate)
            {
                batteryChangedHandlers -= value;
            }
        }
    }

    public Switch2Transport Transport => transport;

    /// <summary>
    /// Binds the logical Bluetooth device to the exact runtime owner's
    /// lifecycle-attention lane. The callback may only reserve teardown; it
    /// must not synchronously stop a producer or release a native lease.
    /// </summary>
    internal bool TryBindBluetoothDisconnectRequest(
        Func<ulong, bool> requestHandler)
    {
        if (requestHandler == null || transport != Switch2Transport.BluetoothLe)
        {
            return false;
        }

        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                bluetoothDisconnectRequestHandler != null)
            {
                return false;
            }
            bluetoothDisconnectRequestHandler = requestHandler;
            return true;
        }
    }

    internal Switch2JoyConRuntimeBindingMode JoyConBindingMode =>
        joyConBindingMode;

    internal bool SupportsStandaloneJoyConHoldMode =>
        joyConBindingMode is
            Switch2JoyConRuntimeBindingMode.StandaloneLeft or
            Switch2JoyConRuntimeBindingMode.StandaloneRight;

    internal bool HasJoyConHoldModeOverride =>
        IsValidJoyConHoldMode((Switch2JoyConHoldMode)Volatile.Read(
            ref joyConHoldModeOverride));

    public ulong PairEpoch => pairEpoch;

    public override long ContinuousGyroCalibrationElapsedMilliseconds
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType == InputDeviceType.Switch2Pro ?
                    proMotionProjection.GyroCalibrationElapsedMilliseconds :
                    joyConMotionProjection.
                        GyroCalibrationElapsedMilliseconds;
            }
        }
    }

    public override void ResetContinuousGyroCalibration()
    {
        lock (publicationGate)
        {
            if (DeviceType == InputDeviceType.Switch2Pro)
            {
                proMotionProjection.RestartGyroCalibration();
            }
            else
            {
                joyConMotionProjection.RestartGyroCalibration();
            }
        }
    }

    internal bool IsMagnetometerCalibrationActive
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType == InputDeviceType.Switch2Pro ?
                    proMotionProjection.IsMagnetometerCalibrationActive :
                    joyConMotionProjection.IsMagnetometerCalibrationActive;
            }
        }
    }

    internal int LeftMagnetometerCalibrationSampleCount
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType == InputDeviceType.Switch2Pro ?
                    proMotionProjection.MagnetometerCalibrationSampleCount :
                    joyConMotionProjection.
                        LeftMagnetometerCalibrationSampleCount;
            }
        }
    }

    internal int RightMagnetometerCalibrationSampleCount
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType == InputDeviceType.Switch2Pro ? 0 :
                    joyConMotionProjection.
                        RightMagnetometerCalibrationSampleCount;
            }
        }
    }

    internal bool HasLeftMagnetometerCalibration
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType == InputDeviceType.Switch2Pro ?
                    proMotionProjection.MagnetometerCalibration.IsValid :
                    joyConMotionProjection.LeftMagnetometerCalibration.
                        IsValid;
            }
        }
    }

    internal bool HasRightMagnetometerCalibration
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType != InputDeviceType.Switch2Pro &&
                    joyConMotionProjection.RightMagnetometerCalibration.
                        IsValid;
            }
        }
    }

    internal bool HasLeftCalibratedGyroBias
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType == InputDeviceType.Switch2Pro ?
                    proMotionProjection.HasCalibratedGyroBias :
                    joyConMotionProjection.HasCalibratedLeftGyroBias;
            }
        }
    }

    internal bool HasRightCalibratedGyroBias
    {
        get
        {
            lock (publicationGate)
            {
                return DeviceType != InputDeviceType.Switch2Pro &&
                    joyConMotionProjection.HasCalibratedRightGyroBias;
            }
        }
    }

    /// <summary>
    /// Begins an explicit figure-eight calibration for this logical device.
    /// Joined Joy-Cons collect both physical halves atomically. Ordinary input
    /// publication continues, but is neutralized until completion or cancel.
    /// </summary>
    internal bool StartMagnetometerCalibration()
    {
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved || publicationInProgress || rawStickOperation != null)
            {
                return false;
            }
            if (DeviceType == InputDeviceType.Switch2Pro)
            {
                proMotionProjection.StartMagnetometerCalibration();
                return true;
            }
            bool left = DeviceType is InputDeviceType.Switch2JoyConLeft or
                InputDeviceType.Switch2JoyConJoined;
            bool right = DeviceType is InputDeviceType.Switch2JoyConRight or
                InputDeviceType.Switch2JoyConJoined;
            return joyConMotionProjection.StartMagnetometerCalibration(left,
                right);
        }
    }

    internal void CancelMagnetometerCalibration()
    {
        lock (publicationGate)
        {
            if (DeviceType == InputDeviceType.Switch2Pro)
            {
                proMotionProjection.CancelMagnetometerCalibration();
            }
            else
            {
                joyConMotionProjection.CancelMagnetometerCalibration();
            }
        }
    }

    internal bool TryCompleteMagnetometerCalibration(
        out Switch2MagnetometerCalibrationQuality leftQuality,
        out Switch2MagnetometerCalibrationQuality rightQuality)
        => TryCompleteMagnetometerCalibration(out leftQuality,
            out rightQuality, out _);

    internal bool TryCompleteMagnetometerCalibration(
        out Switch2MagnetometerCalibrationQuality leftQuality,
        out Switch2MagnetometerCalibrationQuality rightQuality,
        out bool persisted)
    {
        lock (publicationGate)
        {
            persisted = false;
            leftQuality = default;
            rightQuality = default;
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved || publicationInProgress)
            {
                return false;
            }
            if (DeviceType == InputDeviceType.Switch2Pro)
            {
                bool completed = proMotionProjection.
                    TryCompleteMagnetometerCalibration(out leftQuality);
                persisted = completed && PersistMagnetometerCalibrationNoLock();
                return completed;
            }
            bool left = DeviceType is InputDeviceType.Switch2JoyConLeft or
                InputDeviceType.Switch2JoyConJoined;
            bool right = DeviceType is InputDeviceType.Switch2JoyConRight or
                InputDeviceType.Switch2JoyConJoined;
            bool succeeded = joyConMotionProjection.
                TryCompleteMagnetometerCalibration(
                left, right, out leftQuality, out rightQuality);
            persisted = succeeded && PersistMagnetometerCalibrationNoLock();
            return succeeded;
        }
    }

    /// <summary>
    /// Binds this unpublished runtime to opaque install-local persistence.
    /// Missing records are valid; malformed records are ignored by the store.
    /// The raw OS or transport identity never reaches this device.
    /// </summary>
    internal bool TryBindMagnetometerCalibrationPersistence(
        ISwitch2MagnetometerCalibrationStore store,
        Switch2PersistentPeerId leftPeerId,
        Switch2PersistentPeerId rightPeerId = default)
    {
        lock (publicationGate)
        {
            if (store == null || runtimeState !=
                    Switch2RuntimeInputDeviceState.Created ||
                publicationInProgress || magnetometerCalibrationPersistenceBound)
            {
                return false;
            }
            bool shapeValid = DeviceType switch
            {
                InputDeviceType.Switch2Pro => leftPeerId.IsValid &&
                    !rightPeerId.IsValid,
                InputDeviceType.Switch2JoyConLeft => leftPeerId.IsValid &&
                    !rightPeerId.IsValid,
                InputDeviceType.Switch2JoyConRight => !leftPeerId.IsValid &&
                    rightPeerId.IsValid,
                InputDeviceType.Switch2JoyConJoined => leftPeerId.IsValid &&
                    rightPeerId.IsValid && leftPeerId != rightPeerId,
                _ => false,
            };
            if (!shapeValid)
            {
                return false;
            }

            magnetometerCalibrationStore = store;
            leftMagnetometerPeerId = leftPeerId;
            rightMagnetometerPeerId = rightPeerId;
            magnetometerCalibrationPersistenceBound = true;
            if (DeviceType == InputDeviceType.Switch2Pro)
            {
                if (store.TryLoad(leftPeerId, out var calibration))
                {
                    proMotionProjection.TryAdoptMagnetometerCalibration(
                        calibration);
                }
                return true;
            }
            if (leftPeerId.IsValid && store.TryLoad(leftPeerId,
                    out Switch2MagnetometerCalibration leftCalibration))
            {
                joyConMotionProjection.TryAdoptMagnetometerCalibration(
                    left: true, leftCalibration);
            }
            if (rightPeerId.IsValid && store.TryLoad(rightPeerId,
                    out Switch2MagnetometerCalibration rightCalibration))
            {
                joyConMotionProjection.TryAdoptMagnetometerCalibration(
                    left: false, rightCalibration);
            }
            return true;
        }
    }

    /// <summary>
    /// Loads local stick calibration outside publication locks, then adopts
    /// it only if this exact runtime is still unpublished. Raw controller
    /// identity never crosses this opaque peer boundary.
    /// </summary>
    internal bool TryBindRawStickCalibrationPersistence(
        ISwitch2RawStickCalibrationStore store,
        Switch2PersistentPeerId leftPeerId,
        Switch2PersistentPeerId rightPeerId = default)
    {
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                publicationInProgress || rawStickCalibration != null)
                return false;
        }
        // Cold read outside the publication lock. Activation/removal may win
        // while storage is slow; the final Created check then refuses adoption.
        if (!Switch2RawStickCalibrationBinding.TryLoad(DeviceType, transport,
                leftDeviceGeneration, leftTransportGeneration,
                rightDeviceGeneration, rightTransportGeneration,
                store, leftPeerId, rightPeerId, out var loaded))
            return false;
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                publicationInProgress || rawStickCalibration != null)
                return false;
            Volatile.Write(ref rawStickCalibration, loaded);
            return true;
        }
    }

    internal bool HasLocalLeftStickCalibration =>
        Volatile.Read(ref rawStickCalibration)?.HasLeft == true;
    internal bool HasLocalRightStickCalibration =>
        Volatile.Read(ref rawStickCalibration)?.HasRight == true;

    /// <summary>Loads stationary gyro bias before activation.</summary>
    internal bool TryBindGyroCalibrationPersistence(
        ISwitch2GyroCalibrationStore store,
        Switch2PersistentPeerId leftPeerId,
        Switch2PersistentPeerId rightPeerId = default)
    {
        lock (publicationGate)
        {
            if (store == null || runtimeState !=
                    Switch2RuntimeInputDeviceState.Created ||
                publicationInProgress || gyroCalibrationPersistenceBound)
            {
                return false;
            }
            bool shapeValid = DeviceType switch
            {
                InputDeviceType.Switch2Pro => leftPeerId.IsValid &&
                    !rightPeerId.IsValid,
                InputDeviceType.Switch2JoyConLeft => leftPeerId.IsValid &&
                    !rightPeerId.IsValid,
                InputDeviceType.Switch2JoyConRight => !leftPeerId.IsValid &&
                    rightPeerId.IsValid,
                InputDeviceType.Switch2JoyConJoined => leftPeerId.IsValid &&
                    rightPeerId.IsValid && leftPeerId != rightPeerId,
                _ => false,
            };
            if (!shapeValid)
            {
                return false;
            }

            gyroCalibrationStore = store;
            leftGyroCalibrationPeerId = leftPeerId;
            rightGyroCalibrationPeerId = rightPeerId;
            gyroCalibrationPersistenceBound = true;
            if (DeviceType == InputDeviceType.Switch2Pro)
            {
                if (store.TryLoad(leftPeerId, out var calibration))
                {
                    proMotionProjection.TryAdoptGyroCalibration(calibration);
                }
                lastQueuedLeftGyroBiasRevision =
                    proMotionProjection.GyroCalibrationBiasRevision;
                return true;
            }
            if (leftPeerId.IsValid && store.TryLoad(leftPeerId,
                    out Switch2GyroCalibrationRecord leftCalibration))
            {
                joyConMotionProjection.TryAdoptGyroCalibration(left: true,
                    leftCalibration);
            }
            if (rightPeerId.IsValid && store.TryLoad(rightPeerId,
                    out Switch2GyroCalibrationRecord rightCalibration))
            {
                joyConMotionProjection.TryAdoptGyroCalibration(left: false,
                    rightCalibration);
            }
            lastQueuedLeftGyroBiasRevision = joyConMotionProjection.
                LeftGyroCalibrationBiasRevision;
            lastQueuedRightGyroBiasRevision = joyConMotionProjection.
                RightGyroCalibrationBiasRevision;
            return true;
        }
    }

    /// <summary>
    /// Binds an unpublished standalone Joy-Con runtime to its opaque local
    /// orientation record. A missing or malformed record leaves the active
    /// profile as the default. Joined Joy-Cons and Pro controllers reject this
    /// binding because their presentation is never a sideways mini-pad.
    /// </summary>
    internal bool TryBindJoyConHoldModePersistence(
        ISwitch2JoyConHoldModeStore store,
        Switch2PersistentPeerId peerId)
    {
        lock (joyConHoldModeMutationGate)
        {
            lock (publicationGate)
            {
                if (store == null || !peerId.IsValid ||
                    runtimeState != Switch2RuntimeInputDeviceState.Created ||
                    publicationInProgress ||
                    joyConHoldModePersistenceBound ||
                    !SupportsStandaloneJoyConHoldMode)
                {
                    return false;
                }

                joyConHoldModeStore = store;
                joyConHoldModePeerId = peerId;
                joyConHoldModePersistenceBound = true;
            }

            if (store.TryLoad(peerId, out Switch2JoyConHoldMode holdMode) &&
                IsValidJoyConHoldMode(holdMode))
            {
                Volatile.Write(ref joyConHoldModeOverride, (int)holdMode);
            }
            return true;
        }
    }

    /// <summary>
    /// Resolves the one complete orientation snapshot used by the next
    /// physical report. The lock-free enum read cannot produce mixed axis and
    /// button projections while a UI change is being persisted.
    /// </summary>
    internal Switch2JoyConHoldMode ResolveStandaloneJoyConHoldMode(
        Switch2JoyConHoldMode profileFallback)
    {
        Switch2JoyConHoldMode persisted = (Switch2JoyConHoldMode)
            Volatile.Read(ref joyConHoldModeOverride);
        if (SupportsStandaloneJoyConHoldMode &&
            IsValidJoyConHoldMode(persisted))
        {
            return persisted;
        }
        return IsValidJoyConHoldMode(profileFallback) ? profileFallback :
            Switch2JoyConHoldMode.Vertical;
    }

    /// <summary>
    /// Applies a live standalone orientation without reconnecting. Persistence
    /// is intentionally outside the report-publication gate; input continues
    /// while the small atomic record is flushed.
    /// </summary>
    internal bool TrySetStandaloneJoyConHoldMode(
        Switch2JoyConHoldMode holdMode, out bool persisted)
    {
        persisted = false;
        if (!IsValidJoyConHoldMode(holdMode))
        {
            return false;
        }

        lock (joyConHoldModeMutationGate)
        {
            ISwitch2JoyConHoldModeStore store;
            Switch2PersistentPeerId peerId;
            lock (publicationGate)
            {
                if (!SupportsStandaloneJoyConHoldMode ||
                    runtimeState is Switch2RuntimeInputDeviceState.Terminal or
                        Switch2RuntimeInputDeviceState.AbortedUnpublished ||
                    terminalNeutralReserved)
                {
                    return false;
                }
                Volatile.Write(ref joyConHoldModeOverride, (int)holdMode);
                store = joyConHoldModeStore;
                peerId = joyConHoldModePeerId;
            }

            persisted = joyConHoldModePersistenceBound && store != null &&
                peerId.IsValid && store.TryStore(peerId, holdMode);
            return true;
        }
    }

    private static bool IsValidJoyConHoldMode(
        Switch2JoyConHoldMode holdMode) =>
        holdMode is Switch2JoyConHoldMode.Vertical or
            Switch2JoyConHoldMode.Horizontal;

    /// <summary>
    /// The compatibility battery status currently presented by the logical
    /// controller. Joined Joy-Cons use the lowest valid observed half.
    /// </summary>
    public Switch2BatteryStatus Switch2BatteryStatus
    {
        get
        {
            lock (publicationGate)
            {
                return batteryStatus;
            }
        }
    }

    public Switch2BatteryStatus LeftSwitch2BatteryStatus
    {
        get
        {
            lock (publicationGate)
            {
                return leftBatteryStatus;
            }
        }
    }

    public Switch2BatteryStatus RightSwitch2BatteryStatus
    {
        get
        {
            lock (publicationGate)
            {
                return rightBatteryStatus;
            }
        }
    }

    internal bool TryAttachBluetoothFeedbackLifetime(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration,
        Switch2BluetoothFeedbackLifetime feedbackLifetime)
    {
        if (feedbackLifetime == null ||
            !feedbackLifetime.Authenticates(model, deviceGeneration,
                transportGeneration) ||
            !HasExactStandaloneBluetoothBinding(model, deviceGeneration,
                transportGeneration))
        {
            return false;
        }

        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                bluetoothFeedbackLifetime != null ||
                usbFeedbackLifetime != null)
            {
                return false;
            }
            bluetoothFeedbackLifetime = feedbackLifetime;
            return true;
        }
    }

    internal bool TryAttachJoinedBluetoothFeedbackLifetime(
        ulong runtimeGeneration, ulong exactPairEpoch,
        Switch2BluetoothFeedbackLifetime feedbackLifetime)
    {
        if (feedbackLifetime == null || pairEpoch == 0 ||
            RuntimeGeneration != runtimeGeneration ||
            pairEpoch != exactPairEpoch ||
            !feedbackLifetime.AuthenticatesJoined(runtimeGeneration,
                exactPairEpoch))
        {
            return false;
        }

        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                bluetoothFeedbackLifetime != null ||
                usbFeedbackLifetime != null)
            {
                return false;
            }
            bluetoothFeedbackLifetime = feedbackLifetime;
            return true;
        }
    }

    /// <summary>
    /// Attaches the exact owned USB feedback lifetime while the runtime remains
    /// unpublished. The opaque composite authority, not matching numeric
    /// generations alone, authenticates the physical writer owner.
    /// </summary>
    internal bool TryAttachUsbFeedbackLifetime(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        Switch2ProUsbOwnedFeedbackActivationLifetime feedbackLifetime)
    {
        if (feedbackLifetime == null || !authority.IsValid ||
            !feedbackLifetime.Authenticates(authority) ||
            !HasExactProUsbBinding(authority.DeviceGeneration,
                authority.TransportGeneration))
        {
            return false;
        }

        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created ||
                bluetoothFeedbackLifetime != null ||
                usbFeedbackLifetime != null)
            {
                return false;
            }
            usbFeedbackLifetime = feedbackLifetime;
            return true;
        }
    }

    internal bool TryCreateVirtualFeedbackSession(
        ControllerFeedbackSource source, ulong deviceGeneration,
        ulong transportGeneration,
        out Switch2VirtualFeedbackSession session)
    {
        Switch2BluetoothFeedbackLifetime bluetoothFeedback;
        Switch2ProUsbOwnedFeedbackActivationLifetime usbFeedback;
        lock (publicationGate)
        {
            bluetoothFeedback = bluetoothFeedbackLifetime;
            usbFeedback = usbFeedbackLifetime;
            if (runtimeState is not (Switch2RuntimeInputDeviceState.Created or
                    Switch2RuntimeInputDeviceState.Active))
            {
                session = null;
                return false;
            }

            bool bluetoothAuthenticated = bluetoothFeedback != null &&
                (pairEpoch != 0 ?
                    bluetoothFeedback.AuthenticatesJoined(deviceGeneration,
                        transportGeneration) &&
                    deviceGeneration == RuntimeGeneration &&
                    transportGeneration == pairEpoch :
                    bluetoothFeedback.Authenticates(DeviceType switch
                        {
                            InputDeviceType.Switch2Pro =>
                                Switch2ControllerModel.ProController2,
                            InputDeviceType.Switch2JoyConLeft =>
                                Switch2ControllerModel.JoyCon2Left,
                            InputDeviceType.Switch2JoyConRight =>
                                Switch2ControllerModel.JoyCon2Right,
                            _ => Switch2ControllerModel.Unknown,
                        }, deviceGeneration, transportGeneration));
            bool usbAuthenticated = usbFeedback != null &&
                HasExactProUsbBinding(deviceGeneration,
                    transportGeneration);
            if (bluetoothAuthenticated == usbAuthenticated)
            {
                session = null;
                return false;
            }
        }
        return bluetoothFeedback != null ?
            bluetoothFeedback.TryCreateVirtualFeedbackSession(source,
                out session) :
            usbFeedback.TryCreateVirtualFeedbackSession(source,
                out session);
    }

    internal bool TryGetStandaloneFeedbackBinding(
        out ulong deviceGeneration, out ulong transportGeneration)
    {
        lock (publicationGate)
        {
            if (runtimeState is not (Switch2RuntimeInputDeviceState.Created or
                    Switch2RuntimeInputDeviceState.Active) ||
                bluetoothFeedbackLifetime == null || pairEpoch != 0)
            {
                deviceGeneration = 0;
                transportGeneration = 0;
                return false;
            }

            if (DeviceType == InputDeviceType.Switch2JoyConRight)
            {
                deviceGeneration = rightDeviceGeneration;
                transportGeneration = rightTransportGeneration;
            }
            else
            {
                deviceGeneration = leftDeviceGeneration;
                transportGeneration = leftTransportGeneration;
            }
            return deviceGeneration != 0 && transportGeneration != 0;
        }
    }

    internal bool TryGetFeedbackBinding(out ulong deviceGeneration,
        out ulong transportGeneration)
    {
        lock (publicationGate)
        {
            if (runtimeState is not (Switch2RuntimeInputDeviceState.Created or
                    Switch2RuntimeInputDeviceState.Active) ||
                (bluetoothFeedbackLifetime == null) ==
                    (usbFeedbackLifetime == null))
            {
                deviceGeneration = 0;
                transportGeneration = 0;
                return false;
            }

            if (pairEpoch != 0)
            {
                deviceGeneration = RuntimeGeneration;
                transportGeneration = pairEpoch;
            }
            else if (DeviceType == InputDeviceType.Switch2JoyConRight)
            {
                deviceGeneration = rightDeviceGeneration;
                transportGeneration = rightTransportGeneration;
            }
            else
            {
                deviceGeneration = leftDeviceGeneration;
                transportGeneration = leftTransportGeneration;
            }
            return deviceGeneration != 0 && transportGeneration != 0;
        }
    }

    /// <summary>
    /// Authenticates the complete immutable source binding for a Pro or one
    /// standalone Joy-Con Bluetooth lifetime. Joined-pair runtimes are
    /// deliberately excluded: their two independently generated sources must
    /// be authenticated through the joined owner rather than collapsed into
    /// this single-source predicate.
    /// </summary>
    internal bool HasExactStandaloneBluetoothBinding(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        if (transport != Switch2Transport.BluetoothLe || pairEpoch != 0 ||
            deviceGeneration == 0 || transportGeneration == 0)
        {
            return false;
        }

        return model switch
        {
            Switch2ControllerModel.ProController2 =>
                DeviceType == InputDeviceType.Switch2Pro &&
                joyConBindingMode == Switch2JoyConRuntimeBindingMode.Invalid &&
                RuntimeGeneration == deviceGeneration &&
                leftDeviceGeneration == deviceGeneration &&
                leftTransportGeneration == transportGeneration &&
                rightDeviceGeneration == 0 && rightTransportGeneration == 0,
            Switch2ControllerModel.JoyCon2Left =>
                DeviceType == InputDeviceType.Switch2JoyConLeft &&
                joyConBindingMode ==
                    Switch2JoyConRuntimeBindingMode.StandaloneLeft &&
                RuntimeGeneration == deviceGeneration &&
                leftDeviceGeneration == deviceGeneration &&
                leftTransportGeneration == transportGeneration &&
                rightDeviceGeneration == 0 && rightTransportGeneration == 0,
            Switch2ControllerModel.JoyCon2Right =>
                DeviceType == InputDeviceType.Switch2JoyConRight &&
                joyConBindingMode ==
                    Switch2JoyConRuntimeBindingMode.StandaloneRight &&
                RuntimeGeneration == deviceGeneration &&
                leftDeviceGeneration == 0 && leftTransportGeneration == 0 &&
                rightDeviceGeneration == deviceGeneration &&
                rightTransportGeneration == transportGeneration,
            _ => false,
        };
    }

    internal bool HasExactProUsbBinding(ulong deviceGeneration,
        ulong transportGeneration) =>
        transport == Switch2Transport.Usb && pairEpoch == 0 &&
        DeviceType == InputDeviceType.Switch2Pro &&
        joyConBindingMode == Switch2JoyConRuntimeBindingMode.Invalid &&
        deviceGeneration != 0 && transportGeneration != 0 &&
        RuntimeGeneration == deviceGeneration &&
        leftDeviceGeneration == deviceGeneration &&
        leftTransportGeneration == transportGeneration &&
        rightDeviceGeneration == 0 && rightTransportGeneration == 0;

    /// <summary>
    /// Authenticates the complete immutable source binding for one joined
    /// Joy-Con Bluetooth lifetime. The pair epoch and both independently
    /// generated physical lifetimes are part of the identity; a sink must not
    /// infer them from the logical runtime generation.
    /// </summary>
    internal bool HasExactJoinedBluetoothBinding(ulong expectedPairEpoch,
        ulong expectedLeftDeviceGeneration,
        ulong expectedLeftTransportGeneration,
        ulong expectedRightDeviceGeneration,
        ulong expectedRightTransportGeneration) =>
        transport == Switch2Transport.BluetoothLe &&
        DeviceType == InputDeviceType.Switch2JoyConJoined &&
        joyConBindingMode == Switch2JoyConRuntimeBindingMode.Joined &&
        pairEpoch == expectedPairEpoch &&
        leftDeviceGeneration == expectedLeftDeviceGeneration &&
        leftTransportGeneration == expectedLeftTransportGeneration &&
        rightDeviceGeneration == expectedRightDeviceGeneration &&
        rightTransportGeneration == expectedRightTransportGeneration;

    public Switch2RuntimeInputDeviceState RuntimeState
    {
        get
        {
            lock (publicationGate)
            {
                return runtimeState;
            }
        }
    }

    public bool TerminalNeutralReported
    {
        get
        {
            lock (publicationGate)
            {
                return terminalNeutralReported;
            }
        }
    }

    public bool TerminalNeutralCompleted
    {
        get
        {
            lock (publicationGate)
            {
                return terminalNeutralCompleted;
            }
        }
    }

    public uint LastPublishedPacketCounter
    {
        get
        {
            lock (publicationGate)
            {
                return lastPacketCounter;
            }
        }
    }

    public static bool TryCreatePro(ulong deviceGeneration,
        ulong transportGeneration, Switch2Transport transport,
        out Switch2RuntimeInputDevice device,
        out Switch2RuntimeInputDeviceCreateFailure failure)
    {
        if (deviceGeneration == 0 || transportGeneration == 0)
        {
            return Fail(Switch2RuntimeInputDeviceCreateFailure.
                InvalidGeneration, out device, out failure);
        }
        if (transport is not (Switch2Transport.Usb or
                Switch2Transport.BluetoothLe))
        {
            return Fail(Switch2RuntimeInputDeviceCreateFailure.
                InvalidTransport, out device, out failure);
        }

        device = new Switch2RuntimeInputDevice("Switch 2 Pro",
            InputDeviceType.Switch2Pro, ConnectionFor(transport), transport,
            deviceGeneration, Switch2JoyConRuntimeBindingMode.Invalid, 0,
            deviceGeneration, transportGeneration, 0, 0);
        failure = Switch2RuntimeInputDeviceCreateFailure.None;
        return true;
    }

    public static bool TryCreateStandaloneJoyCon(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, out Switch2RuntimeInputDevice device,
        out Switch2RuntimeInputDeviceCreateFailure failure)
    {
        if (deviceGeneration == 0 || transportGeneration == 0)
        {
            return Fail(Switch2RuntimeInputDeviceCreateFailure.
                InvalidGeneration, out device, out failure);
        }

        InputDeviceType inputType;
        Switch2JoyConRuntimeBindingMode bindingMode;
        string displayName;
        switch (model)
        {
            case Switch2ControllerModel.JoyCon2Left:
                inputType = InputDeviceType.Switch2JoyConLeft;
                bindingMode = Switch2JoyConRuntimeBindingMode.StandaloneLeft;
                displayName = "Joy-Con 2 (L)";
                break;
            case Switch2ControllerModel.JoyCon2Right:
                inputType = InputDeviceType.Switch2JoyConRight;
                bindingMode = Switch2JoyConRuntimeBindingMode.StandaloneRight;
                displayName = "Joy-Con 2 (R)";
                break;
            default:
                return Fail(Switch2RuntimeInputDeviceCreateFailure.InvalidModel,
                    out device, out failure);
        }

        device = new Switch2RuntimeInputDevice(displayName, inputType,
            ConnectionType.BT, Switch2Transport.BluetoothLe, deviceGeneration,
            bindingMode, 0,
            model == Switch2ControllerModel.JoyCon2Left ? deviceGeneration : 0,
            model == Switch2ControllerModel.JoyCon2Left ?
                transportGeneration : 0,
            model == Switch2ControllerModel.JoyCon2Right ? deviceGeneration : 0,
            model == Switch2ControllerModel.JoyCon2Right ?
                transportGeneration : 0);
        failure = Switch2RuntimeInputDeviceCreateFailure.None;
        return true;
    }

    public static bool TryCreateJoinedJoyCon(ulong runtimeGeneration,
        ulong pairEpoch, ulong leftDeviceGeneration,
        ulong leftTransportGeneration, ulong rightDeviceGeneration,
        ulong rightTransportGeneration, out Switch2RuntimeInputDevice device,
        out Switch2RuntimeInputDeviceCreateFailure failure)
    {
        if (runtimeGeneration == 0 || leftDeviceGeneration == 0 ||
            leftTransportGeneration == 0 || rightDeviceGeneration == 0 ||
            rightTransportGeneration == 0)
        {
            return Fail(Switch2RuntimeInputDeviceCreateFailure.
                InvalidGeneration, out device, out failure);
        }
        if (pairEpoch == 0)
        {
            return Fail(Switch2RuntimeInputDeviceCreateFailure.InvalidPairEpoch,
                out device, out failure);
        }

        device = new Switch2RuntimeInputDevice("Joy-Con 2 (Joined)",
            InputDeviceType.Switch2JoyConJoined, ConnectionType.BT,
            Switch2Transport.BluetoothLe, runtimeGeneration,
            Switch2JoyConRuntimeBindingMode.Joined, pairEpoch,
            leftDeviceGeneration,
            leftTransportGeneration, rightDeviceGeneration,
            rightTransportGeneration);
        failure = Switch2RuntimeInputDeviceCreateFailure.None;
        return true;
    }

    public bool TryPublishPro(in Switch2ProProfileInputFrame frame) =>
        TryPublishProDetailed(frame) ==
            Switch2RuntimePublicationResult.Published;

    /// <summary>
    /// Publishes one Pro frame while preserving the distinction between a
    /// closed/invalid lifetime, subscriber refusal, and temporary admission
    /// backpressure caused by an existing publication or profile action.
    /// </summary>
    public Switch2RuntimePublicationResult TryPublishProDetailed(
        in Switch2ProProfileInputFrame frame)
    {
        ReportHandler<EventArgs>[] subscribers;
        EventHandler batteryChanged;
        bool requestIdleDisconnect;
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved)
            {
                return Switch2RuntimePublicationResult.LifecycleClosed;
            }
            if (publicationInProgress)
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            var calibratedFrame = rawStickCalibration?.ApplyPro(frame) ?? frame;
            if (DeviceType != InputDeviceType.Switch2Pro ||
                frame.Version != Switch2ProProfileInputFrame.CurrentVersion ||
                !frame.HasValidRawStickObservation ||
                frame.DeviceGeneration != leftDeviceGeneration ||
                frame.TransportGeneration != leftTransportGeneration ||
                frame.Transport != transport ||
                !IsExpectedProRevision(frame.Transport,
                    frame.ProtocolRevision) ||
                !calibratedFrame.TryWriteLegacyState(stagingState,
                    GetFaceButtonLayoutNoLock()))
            {
                return Switch2RuntimePublicationResult.FrameRejected;
            }

            ObserveRawStickCalibrationNoLock(frame.RawStickObservation);
            requestIdleDisconnect = ShouldRequestAutoDisconnectNoLock(
                frame.CompletionTimestampQpc, frame.QpcFrequency,
                frame.Buttons == Switch2ProButton.None, stagingState);

            stagingHasMotion = proMotionProjection.TryApply(calibratedFrame,
                stagingState, IsMagnetometerYawAssistEnabledNoLock(),
                GetVirtualGyroSoftDeadzoneNoLock(),
                IsHorizonStabilizationEnabledNoLock());
            if (!stagingHasMotion)
            {
                proMotionProjection.Reset(stagingState);
            }
            else
            {
                QueueGyroCalibrationPersistenceNoLock();
            }
            if (proMotionProjection.IsMagnetometerCalibrationActive || rawStickOperation != null)
            {
                SuppressStagingForMagnetometerCalibrationNoLock();
            }

            batteryChanged = UpdateProBatteryNoLock(frame);

            if (!TryReserveStagingNoLock(out subscribers))
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            ObservePhysicalInputNoLock(frame.CompletionTimestampQpc,
                frame.QpcFrequency);
        }

        bool published = InvokeAndCommitPublication(subscribers,
            isTerminalNeutral: false);
        InvokeBatteryChanged(batteryChanged);
        if (requestIdleDisconnect)
        {
            _ = DisconnectBT(callRemoval: true);
        }
        return published ?
            Switch2RuntimePublicationResult.Published :
            Switch2RuntimePublicationResult.SubscriberRejected;
    }

    /// <summary>
    /// Waits without polling for the current publication/profile action to
    /// release admission. A true result never reserves publication; the caller
    /// must retry and tolerate a new publisher winning the race.
    /// </summary>
    public bool TryWaitForPublicationAvailability(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            return false;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (publicationGate)
        {
            while (publicationInProgress)
            {
                if (publicationThreadId ==
                    Environment.CurrentManagedThreadId)
                {
                    return false;
                }

                int remaining = (int)Math.Min(int.MaxValue,
                    deadline - Environment.TickCount64);
                if (remaining <= 0 || !System.Threading.Monitor.Wait(
                        publicationGate, remaining))
                {
                    return !publicationInProgress && runtimeState ==
                            Switch2RuntimeInputDeviceState.Active &&
                        !terminalNeutralReserved;
                }
            }

            return runtimeState == Switch2RuntimeInputDeviceState.Active &&
                !terminalNeutralReserved;
        }
    }

    public bool TryPublishStandaloneJoyCon(
        in Switch2JoyConProfileInputFrame frame) =>
        TryPublishStandaloneJoyConDetailed(frame) ==
            Switch2RuntimePublicationResult.Published;

    /// <summary>
    /// Publishes one standalone Joy-Con frame while preserving the same exact
    /// lifecycle, frame, subscriber, and transient-admission distinctions as
    /// <see cref="TryPublishProDetailed"/>.
    /// </summary>
    public Switch2RuntimePublicationResult
        TryPublishStandaloneJoyConDetailed(
            in Switch2JoyConProfileInputFrame frame)
    {
        ReportHandler<EventArgs>[] subscribers;
        EventHandler batteryChanged;
        bool requestIdleDisconnect;
        lock (publicationGate)
        {
            bool left = DeviceType == InputDeviceType.Switch2JoyConLeft;
            bool right = DeviceType == InputDeviceType.Switch2JoyConRight;
            Switch2JoyConProfileSide expected = left ? frame.LeftSource :
                frame.RightSource;
            Switch2JoyConProfileSide foreign = left ? frame.RightSource :
                frame.LeftSource;
            ulong expectedDeviceGeneration = left ? leftDeviceGeneration :
                rightDeviceGeneration;
            ulong expectedTransportGeneration = left ?
                leftTransportGeneration : rightTransportGeneration;
            Switch2ControllerModel expectedModel = left ?
                Switch2ControllerModel.JoyCon2Left :
                Switch2ControllerModel.JoyCon2Right;

            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved)
            {
                return Switch2RuntimePublicationResult.LifecycleClosed;
            }
            if (publicationInProgress)
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            var calibratedFrame = rawStickCalibration?.ApplyJoyCon(frame) ?? frame;
            if ((!left && !right) ||
                frame.Version != Switch2JoyConProfileInputFrame.CurrentVersion ||
                (left && !Switch2JoyConProfileInputMapper.
                    IsStandaloneLeftMode(frame.Mode)) ||
                (right && !Switch2JoyConProfileInputMapper.
                    IsStandaloneRightMode(frame.Mode)) ||
                frame.PairEpoch != 0 ||
                !expected.IsPresent || foreign.IsPresent ||
                !expected.HasValidRawStickObservation ||
                expected.Model != expectedModel ||
                expected.DeviceGeneration != expectedDeviceGeneration ||
                expected.TransportGeneration != expectedTransportGeneration ||
                !calibratedFrame.TryWriteLegacyState(stagingState,
                    GetFaceButtonLayoutNoLock()))
            {
                return Switch2RuntimePublicationResult.FrameRejected;
            }

            ObserveRawStickCalibrationNoLock(expected.RawStickObservation);
            requestIdleDisconnect = ShouldRequestAutoDisconnectNoLock(
                frame.CompletionTimestampQpc, frame.QpcFrequency,
                frame.Buttons == Switch2JoyConProfileButton.None,
                stagingState);

            stagingHasMotion = ApplyJoyConMotionNoLock(calibratedFrame);
            if (joyConMotionProjection.IsMagnetometerCalibrationActive || rawStickOperation != null)
            {
                SuppressStagingForMagnetometerCalibrationNoLock();
            }
            batteryChanged = UpdateStandaloneBatteryNoLock(expected, left);

            if (!TryReserveStagingNoLock(out subscribers))
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            ObservePhysicalInputNoLock(frame.CompletionTimestampQpc,
                frame.QpcFrequency);
        }

        bool published = InvokeAndCommitPublication(subscribers,
            isTerminalNeutral: false);
        InvokeBatteryChanged(batteryChanged);
        if (requestIdleDisconnect)
        {
            _ = DisconnectBT(callRemoval: true);
        }
        return published ?
            Switch2RuntimePublicationResult.Published :
            Switch2RuntimePublicationResult.SubscriberRejected;
    }

    public bool TryPublishJoinedJoyCon(
        in Switch2JoyConProfileInputFrame frame) =>
        TryPublishJoinedJoyConDetailed(frame) ==
            Switch2RuntimePublicationResult.Published;

    /// <summary>
    /// Publishes one joined Joy-Con frame while preserving the same exact
    /// lifecycle, frame, subscriber, and transient-admission distinctions as
    /// the Pro and standalone paths. A joined sink may retry only
    /// <see cref="Switch2RuntimePublicationResult.PublicationBusy"/>.
    /// </summary>
    public Switch2RuntimePublicationResult TryPublishJoinedJoyConDetailed(
        in Switch2JoyConProfileInputFrame frame)
    {
        ReportHandler<EventArgs>[] subscribers;
        EventHandler batteryChanged;
        bool requestIdleDisconnect;
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved)
            {
                return Switch2RuntimePublicationResult.LifecycleClosed;
            }
            if (publicationInProgress)
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            var calibratedFrame = rawStickCalibration?.ApplyJoyCon(frame) ?? frame;
            if (DeviceType !=
                    InputDeviceType.Switch2JoyConJoined ||
                frame.Version != Switch2JoyConProfileInputFrame.CurrentVersion ||
                frame.Mode != Switch2JoyConProfileMode.Joined ||
                frame.PairEpoch != pairEpoch ||
                !frame.LeftSource.IsPresent || !frame.RightSource.IsPresent ||
                !frame.LeftSource.HasValidRawStickObservation ||
                !frame.RightSource.HasValidRawStickObservation ||
                frame.LeftSource.Model != Switch2ControllerModel.JoyCon2Left ||
                frame.RightSource.Model != Switch2ControllerModel.JoyCon2Right ||
                frame.LeftSource.DeviceGeneration != leftDeviceGeneration ||
                frame.LeftSource.TransportGeneration !=
                    leftTransportGeneration ||
                frame.RightSource.DeviceGeneration != rightDeviceGeneration ||
                frame.RightSource.TransportGeneration !=
                    rightTransportGeneration ||
                !calibratedFrame.TryWriteLegacyState(stagingState,
                    GetFaceButtonLayoutNoLock()))
            {
                return Switch2RuntimePublicationResult.FrameRejected;
            }

            ObserveRawStickCalibrationNoLock(frame.LeftSource.RawStickObservation);
            ObserveRawStickCalibrationNoLock(frame.RightSource.RawStickObservation);
            requestIdleDisconnect = ShouldRequestAutoDisconnectNoLock(
                frame.CompletionTimestampQpc, frame.QpcFrequency,
                frame.Buttons == Switch2JoyConProfileButton.None,
                stagingState);

            stagingHasMotion = ApplyJoyConMotionNoLock(calibratedFrame);
            if (joyConMotionProjection.IsMagnetometerCalibrationActive || rawStickOperation != null)
            {
                SuppressStagingForMagnetometerCalibrationNoLock();
            }
            batteryChanged = UpdateJoinedBatteryNoLock(frame.LeftSource,
                frame.RightSource);

            if (!TryReserveStagingNoLock(out subscribers))
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            ObservePhysicalInputNoLock(frame.CompletionTimestampQpc,
                frame.QpcFrequency);
        }

        bool published = InvokeAndCommitPublication(subscribers,
            isTerminalNeutral: false);
        InvokeBatteryChanged(batteryChanged);
        if (requestIdleDisconnect)
        {
            _ = DisconnectBT(callRemoval: true);
        }
        return published ?
            Switch2RuntimePublicationResult.Published :
            Switch2RuntimePublicationResult.SubscriberRejected;
    }

    private bool ShouldRequestAutoDisconnectNoLock(long timestampQpc,
        long qpcFrequency, bool noPhysicalButtons, DS4State state)
    {
        if (rawStickOperation != null) return false;
        if (transport != Switch2Transport.BluetoothLe || timestampQpc < 0 ||
            qpcFrequency <= 0)
        {
            idleLastActivityTimestampQpc = timestampQpc;
            idleQpcFrequency = qpcFrequency;
            idleActivityTimestampInitialized = timestampQpc >= 0 &&
                qpcFrequency > 0;
            absoluteSessionStartTimestampQpc = timestampQpc;
            absoluteSessionQpcFrequency = qpcFrequency;
            absoluteSessionTimestampInitialized = timestampQpc >= 0 &&
                qpcFrequency > 0;
            return false;
        }

        if (!idleActivityTimestampInitialized ||
            idleQpcFrequency != qpcFrequency ||
            timestampQpc < idleLastActivityTimestampQpc)
        {
            idleLastActivityTimestampQpc = timestampQpc;
            idleQpcFrequency = qpcFrequency;
            idleActivityTimestampInitialized = true;
        }
        else if (!noPhysicalButtons || !AreLegacySticksIdle(state))
        {
            idleLastActivityTimestampQpc = timestampQpc;
        }

        if (!absoluteSessionTimestampInitialized ||
            absoluteSessionQpcFrequency != qpcFrequency ||
            timestampQpc < absoluteSessionStartTimestampQpc)
        {
            absoluteSessionStartTimestampQpc = timestampQpc;
            absoluteSessionQpcFrequency = qpcFrequency;
            absoluteSessionTimestampInitialized = true;
        }

        Switch2AutoDisconnectMode configuredMode =
            Switch2AutoDisconnectMode.LegacyProfile;
        long configuredTimeoutSeconds = 0;
        int slot = DeviceSlotNumber;
        if (slot >= 0 && slot < Global.Switch2AutoDisconnectMode.Length &&
            slot < Global.Switch2AutoDisconnectTimeoutSeconds.Length)
        {
            configuredMode = Global.Switch2AutoDisconnectMode[slot];
            configuredTimeoutSeconds = Volatile.Read(
                ref Global.Switch2AutoDisconnectTimeoutSeconds[slot]);
        }

        Switch2AutoDisconnectPolicy policy =
            Switch2AutoDisconnectPolicyResolver.Resolve(configuredMode,
                configuredTimeoutSeconds, Volatile.Read(ref idleTimeout));
        if (!policy.Enabled)
        {
            return false;
        }

        long timeoutTicks = Switch2AutoDisconnectPolicyResolver.ToQpcTicks(
            policy.TimeoutSeconds, qpcFrequency);
        long startTimestamp = policy.Mode ==
                Switch2AutoDisconnectMode.Absolute ?
            absoluteSessionStartTimestampQpc : idleLastActivityTimestampQpc;
        return timeoutTicks > 0 && timestampQpc >= startTimestamp &&
            timestampQpc - startTimestamp >= timeoutTicks;
    }

    private static bool AreLegacySticksIdle(DS4State state)
    {
        const int slop = 64;
        return state != null &&
            state.LX > 127 - slop && state.LX < 128 + slop &&
            state.LY > 127 - slop && state.LY < 128 + slop &&
            state.RX > 127 - slop && state.RX < 128 + slop &&
            state.RY > 127 - slop && state.RY < 128 + slop;
    }

    private EventHandler UpdateProBatteryNoLock(
        in Switch2ProProfileInputFrame frame)
    {
        if (!frame.HasBatteryStatus)
        {
            return null;
        }

        leftBatteryStatus = default;
        rightBatteryStatus = default;
        return CommitCompatibilityBatteryNoLock(frame.BatteryStatus);
    }

    private EventHandler UpdateStandaloneBatteryNoLock(
        in Switch2JoyConProfileSide source, bool left)
    {
        if (!source.HasBatteryStatus)
        {
            return null;
        }

        if (left)
        {
            leftBatteryStatus = source.BatteryStatus;
            rightBatteryStatus = default;
        }
        else
        {
            leftBatteryStatus = default;
            rightBatteryStatus = source.BatteryStatus;
        }
        return CommitCompatibilityBatteryNoLock(source.BatteryStatus);
    }

    private EventHandler UpdateJoinedBatteryNoLock(
        in Switch2JoyConProfileSide left,
        in Switch2JoyConProfileSide right)
    {
        if (left.HasBatteryStatus)
        {
            leftBatteryStatus = left.BatteryStatus;
        }
        if (right.HasBatteryStatus)
        {
            rightBatteryStatus = right.BatteryStatus;
        }

        Switch2BatteryStatus aggregate;
        if (leftBatteryStatus.IsValid && rightBatteryStatus.IsValid)
        {
            aggregate = leftBatteryStatus.CompatibilityPercentage <=
                rightBatteryStatus.CompatibilityPercentage ?
                leftBatteryStatus : rightBatteryStatus;
        }
        else if (leftBatteryStatus.IsValid)
        {
            aggregate = leftBatteryStatus;
        }
        else if (rightBatteryStatus.IsValid)
        {
            aggregate = rightBatteryStatus;
        }
        else
        {
            return null;
        }

        return CommitCompatibilityBatteryNoLock(aggregate);
    }

    private EventHandler CommitCompatibilityBatteryNoLock(
        in Switch2BatteryStatus next)
    {
        bool visibleChanged = !batteryStatus.IsValid ||
            batteryStatus.Band != next.Band;
        batteryStatus = next;
        battery = next.CompatibilityPercentage;
        // The common report establishes raw current, not its direction or a
        // charging bit. Keep the legacy charging API false until that semantic
        // is proven by project-owned evidence.
        charging = false;
        return visibleChanged ? batteryChangedHandlers : null;
    }

    private void InvokeBatteryChanged(EventHandler handlers)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Battery status is ancillary to an already completed input
                // publication. One UI observer cannot reject controller input
                // or prevent the remaining observers from refreshing.
            }
        }
    }

    private bool ApplyJoyConMotionNoLock(
        in Switch2JoyConProfileInputFrame frame)
    {
        int slot = DeviceSlotNumber;
        bool fusionEnabled = slot >= 0 &&
            slot < Global.Switch2DualJoyConGyroFusionEnabled.Length &&
            Global.Switch2DualJoyConGyroFusionEnabled[slot];
        Switch2DualGyroDominantSide dominantSide =
            Switch2DualGyroDominantSide.Right;
        if (slot >= 0 &&
            slot < Global.Switch2DualJoyConGyroDominantSide.Length)
        {
            dominantSide = Global.Switch2DualJoyConGyroDominantSide[slot];
        }
        Switch2DualGyroMode mode = slot >= 0 && slot <
                Global.Switch2DualJoyConGyroMode.Length ?
            Global.Switch2DualJoyConGyroMode[slot] :
            Switch2DualGyroMode.SwitchDominantSide;
        Switch2DualGyroActivationMode activationMode = slot >= 0 && slot <
                Global.Switch2DualJoyConGyroActivationMode.Length ?
            Global.Switch2DualJoyConGyroActivationMode[slot] :
            Switch2DualGyroActivationMode.Hold;
        Switch2JoyConProfileButton leftActivationButton = slot >= 0 &&
                slot < Global.Switch2DualJoyConGyroLeftActivationButton.
                    Length ?
            Global.Switch2DualJoyConGyroLeftActivationButton[slot] :
            Switch2JoyConProfileButton.None;
        Switch2JoyConProfileButton rightActivationButton = slot >= 0 &&
                slot < Global.Switch2DualJoyConGyroRightActivationButton.
                    Length ?
            Global.Switch2DualJoyConGyroRightActivationButton[slot] :
            Switch2JoyConProfileButton.None;
        Switch2IrGyroConfiguration irConfiguration =
            GetIrGyroConfigurationNoLock();
        if (!Switch2DualGyroConfiguration.TryCreate(fusionEnabled, mode,
                dominantSide, activationMode, leftActivationButton,
                rightActivationButton,
                out Switch2DualGyroConfiguration configuration,
                (leftActivationButton & Switch2JoyConProfileButton.LeftIrSensor) != 0 ?
                    irConfiguration.Left.ActivationThreshold :
                    Switch2IrActivationThreshold.Strict,
                (rightActivationButton & Switch2JoyConProfileButton.RightIrSensor) != 0 ?
                    irConfiguration.Right.ActivationThreshold :
                    Switch2IrActivationThreshold.Strict,
                irConfiguration.ProfileRevision))
        {
            joyConGyroModeState = default;
            joyConMotionProjection.Reset(stagingState);
            return false;
        }
        if (!Switch2DualJoyConGyroMode.TryResolve(ref joyConGyroModeState,
                frame.PairEpoch,
                Switch2DualJoyConGyroMode.ObserveActivationButtons(
                    frame.LeftSource, Switch2JoyConSide.Left,
                    configuration.LeftIrThreshold),
                Switch2DualJoyConGyroMode.ObserveActivationButtons(
                    frame.RightSource, Switch2JoyConSide.Right,
                    configuration.RightIrThreshold), configuration,
                out Switch2DualGyroRuntimePolicy policy))
        {
            joyConMotionProjection.Reset(stagingState);
            return false;
        }

        bool applied = joyConMotionProjection.TryApply(frame, stagingState,
            policy, IsMagnetometerYawAssistEnabledNoLock(),
            GetVirtualGyroSoftDeadzoneNoLock(),
            IsHorizonStabilizationEnabledNoLock(),
            irConfiguration);
        if (!applied)
        {
            joyConGyroModeState = default;
            joyConMotionProjection.Reset(stagingState);
        }
        else
        {
            QueueGyroCalibrationPersistenceNoLock();
        }
        return applied;
    }

    private bool IsMagnetometerYawAssistEnabledNoLock()
    {
        int slot = DeviceSlotNumber;
        return slot >= 0 && slot <
            Global.Switch2MagnetometerYawAssistEnabled.Length &&
            Global.Switch2MagnetometerYawAssistEnabled[slot];
    }

    private Switch2FaceButtonLayout GetFaceButtonLayoutNoLock()
    {
        int slot = DeviceSlotNumber;
        if (slot < 0 || slot >= Global.Switch2FaceButtonLayout.Length)
        {
            return Switch2FaceButtonLayout.Xbox;
        }

        Switch2FaceButtonLayout layout =
            Global.Switch2FaceButtonLayout[slot];
        return Switch2FaceButtonLayoutProjection.IsValid(layout) ? layout :
            Switch2FaceButtonLayout.Xbox;
    }

    private void SuppressStagingForMagnetometerCalibrationNoLock()
    {
        neutralState.CopyTo(stagingState);
        stagingHasMotion = false;
    }

    private bool PersistMagnetometerCalibrationNoLock()
    {
        if (!magnetometerCalibrationPersistenceBound ||
            magnetometerCalibrationStore == null)
        {
            return false;
        }
        if (DeviceType == InputDeviceType.Switch2Pro)
        {
            return magnetometerCalibrationStore.TryStore(
                leftMagnetometerPeerId,
                proMotionProjection.MagnetometerCalibration);
        }
        bool leftStored = !leftMagnetometerPeerId.IsValid ||
            magnetometerCalibrationStore.TryStore(leftMagnetometerPeerId,
                joyConMotionProjection.LeftMagnetometerCalibration);
        bool rightStored = !rightMagnetometerPeerId.IsValid ||
            magnetometerCalibrationStore.TryStore(rightMagnetometerPeerId,
                joyConMotionProjection.RightMagnetometerCalibration);
        return leftStored && rightStored;
    }

    private void QueueGyroCalibrationPersistenceNoLock()
    {
        if (!gyroCalibrationPersistenceBound || gyroCalibrationStore == null)
        {
            return;
        }
        if (DeviceType == InputDeviceType.Switch2Pro)
        {
            ulong revision = proMotionProjection.
                GyroCalibrationBiasRevision;
            if (revision != 0 && revision !=
                    lastQueuedLeftGyroBiasRevision &&
                proMotionProjection.TryGetGyroCalibrationRecord(
                    out var calibration) &&
                gyroCalibrationStore.TryQueueStore(
                    leftGyroCalibrationPeerId, calibration))
            {
                lastQueuedLeftGyroBiasRevision = revision;
            }
            return;
        }

        ulong leftRevision = joyConMotionProjection.
            LeftGyroCalibrationBiasRevision;
        if (leftGyroCalibrationPeerId.IsValid && leftRevision != 0 &&
            leftRevision != lastQueuedLeftGyroBiasRevision &&
            joyConMotionProjection.TryGetGyroCalibrationRecord(left: true,
                out Switch2GyroCalibrationRecord leftCalibration) &&
            gyroCalibrationStore.TryQueueStore(leftGyroCalibrationPeerId,
                leftCalibration))
        {
            lastQueuedLeftGyroBiasRevision = leftRevision;
        }

        ulong rightRevision = joyConMotionProjection.
            RightGyroCalibrationBiasRevision;
        if (rightGyroCalibrationPeerId.IsValid && rightRevision != 0 &&
            rightRevision != lastQueuedRightGyroBiasRevision &&
            joyConMotionProjection.TryGetGyroCalibrationRecord(left: false,
                out Switch2GyroCalibrationRecord rightCalibration) &&
            gyroCalibrationStore.TryQueueStore(rightGyroCalibrationPeerId,
                rightCalibration))
        {
            lastQueuedRightGyroBiasRevision = rightRevision;
        }
    }

    private double GetVirtualGyroSoftDeadzoneNoLock()
    {
        int slot = DeviceSlotNumber;
        return slot >= 0 && slot <
            Global.Switch2VirtualGyroSoftDeadzone.Length ?
                Switch2MotionSoftDeadzone.Normalize(
                    Global.Switch2VirtualGyroSoftDeadzone[slot]) :
                Switch2MotionSoftDeadzone.Default;
    }

    private bool IsHorizonStabilizationEnabledNoLock()
    {
        int slot = DeviceSlotNumber;
        return slot >= 0 && slot <
            Global.Switch2HorizonStabilizationEnabled.Length &&
            Global.Switch2HorizonStabilizationEnabled[slot];
    }

    private Switch2IrGyroConfiguration GetIrGyroConfigurationNoLock()
    {
        int slot = DeviceSlotNumber;
        if (slot < 0 || slot >= Global.TEST_PROFILE_ITEM_COUNT)
        {
            return Switch2IrGyroConfiguration.Disabled;
        }

        string triggers = Global.GetGyroOutMode(slot) switch
        {
            GyroOutMode.Controls =>
                Global.GetGyroControlsInfo(slot).triggers,
            GyroOutMode.Mouse => Global.getSATriggers(slot),
            GyroOutMode.MouseJoystick =>
                Global.GetSAMouseStickTriggers(slot),
            GyroOutMode.DirectionalSwipe =>
                Global.GetGyroSwipeInfo(slot).triggers,
            _ => string.Empty,
        };
        bool leftEnabled = Switch2IrGyroMotionModifier.
            ContainsSerializedTrigger(triggers,
                Switch2IrGyroMotionModifier.LeftIrGyroTriggerIndex);
        bool rightEnabled = Switch2IrGyroMotionModifier.
            ContainsSerializedTrigger(triggers,
                Switch2IrGyroMotionModifier.RightIrGyroTriggerIndex);
        Switch2IrActivationThreshold leftThreshold =
            NormalizeIrActivationThreshold(Global.
                Switch2JoyConLeftIrMouseActivationThreshold[slot]);
        Switch2IrActivationThreshold rightThreshold =
            NormalizeIrActivationThreshold(Global.
                Switch2JoyConRightIrMouseActivationThreshold[slot]);
        Switch2IrGyroTuning leftTuning = Switch2IrGyroTuning.Normalize(
            Global.Switch2JoyConLeftIrGyroTuning[slot]);
        Switch2IrGyroTuning rightTuning = Switch2IrGyroTuning.Normalize(
            Global.Switch2JoyConRightIrGyroTuning[slot]);
        var left = new Switch2IrGyroSideConfiguration(leftEnabled,
            leftThreshold, leftTuning);
        var right = new Switch2IrGyroSideConfiguration(rightEnabled,
            rightThreshold, rightTuning);
        long profileRevision = Global.ReadProfileSwitchRevision(slot);
        // The ninth profile-editor/test slot has no live switch-revision
        // counter. Zero is its stable configuration epoch; physical slots use
        // their monotonic revision exactly.
        if (profileRevision < 0)
        {
            profileRevision = 0;
        }
        return new Switch2IrGyroConfiguration(left, right, profileRevision);
    }

    private static Switch2IrActivationThreshold NormalizeIrActivationThreshold(
        Switch2IrActivationThreshold value) => value is
            Switch2IrActivationThreshold.Strict or
            Switch2IrActivationThreshold.Balanced or
            Switch2IrActivationThreshold.Relaxed ? value :
                Switch2IrActivationThreshold.Strict;

    /// <summary>
    /// Retires this logical generation and invokes Report with one neutral
    /// DS4State exactly once. Concurrent and repeated terminal requests cannot
    /// manufacture additional neutral reports.
    /// </summary>
    public bool TryPublishTerminalNeutral() => RequestTerminalNeutral() !=
        Switch2TerminalNeutralRequestResult.RejectedAlreadyReserved;

    public Switch2TerminalNeutralRequestResult RequestTerminalNeutral()
    {
        // Close continuous presentation before reserving terminal state. Once
        // Stop returns, a concurrent report can no longer resurrect or extend
        // mouse motion from this logical generation.
        highRateMousePresenter.Stop();
        lock (localFeedbackGate)
        {
            CancelConnectionHapticNoLock();
            CancelIdentificationHapticNoLock(withdraw: true);
        }
        ReportHandler<EventArgs>[] subscribers;
        lock (publicationGate)
        {
            if (terminalNeutralReserved)
            {
                return Switch2TerminalNeutralRequestResult.
                    RejectedAlreadyReserved;
            }

            terminalNeutralReserved = true;
            CancelRawStickCalibrationNoLock();
            terminalNeutralSubscribers = reportSubscribers;
            runtimeState = Switch2RuntimeInputDeviceState.Terminal;
            if (publicationInProgress)
            {
                terminalNeutralPending = true;
                return Switch2TerminalNeutralRequestResult.AcceptedPending;
            }

            if (!TryReserveTerminalNeutralNoLock(out subscribers))
            {
                return Switch2TerminalNeutralRequestResult.
                    RejectedAlreadyReserved;
            }
        }

        InvokeAndCommitPublication(subscribers,
            isTerminalNeutral: true);
        return Switch2TerminalNeutralRequestResult.AcceptedCompleted;
    }

    public bool TryWaitForTerminalNeutralCompletion(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            return false;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (publicationGate)
        {
            if (!terminalNeutralReserved)
            {
                return false;
            }

            while (!terminalNeutralCompleted)
            {
                if (publicationInProgress && publicationThreadId ==
                    Environment.CurrentManagedThreadId)
                {
                    return false;
                }

                int remaining = (int)Math.Min(int.MaxValue,
                    deadline - Environment.TickCount64);
                if (remaining <= 0 || !System.Threading.Monitor.Wait(
                        publicationGate, remaining))
                {
                    return terminalNeutralCompleted;
                }
            }

            return true;
        }
    }

    public override void PostInit()
    {
        // All safe non-HID invariants were established by the protected base
        // constructor. Transport discovery and initialization are deliberately
        // absent from this dormant class.
    }

    public override void StartUpdate()
    {
        Switch2BluetoothFeedbackLifetime playerLedLifetime = null;
        byte playerNumber = 0;
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Created)
            {
                return;
            }
            if (bluetoothFeedbackLifetime != null &&
                !bluetoothFeedbackLifetime.TryActivate())
            {
                return;
            }

            if (Debouncer == null && DeviceSlotNumber >= 0 &&
                Global.DebouncingMs != null &&
                DeviceSlotNumber < Global.DebouncingMs.Length)
            {
                Debouncer = SetupDebouncer();
            }
            firstActive = DateTime.UtcNow;
            absoluteSessionStartTimestampQpc = Stopwatch.GetTimestamp();
            absoluteSessionQpcFrequency = Stopwatch.Frequency;
            absoluteSessionTimestampInitialized = true;
            runtimeState = Switch2RuntimeInputDeviceState.Active;
            if (bluetoothFeedbackLifetime != null && DeviceSlotNumber >= 0 &&
                DeviceSlotNumber < 8)
            {
                playerLedLifetime = bluetoothFeedbackLifetime;
                playerNumber = checked((byte)(DeviceSlotNumber + 1));
            }
        }
        playerLedLifetime?.TryRequestPlayerLed(playerNumber);
    }

    public override void StopUpdate() => TryPublishTerminalNeutral();

    internal bool TrySetHighRateMouseSource(
        Switch2ContinuousMouseSource source, bool active,
        double velocityX, double velocityY, long profileRevision)
    {
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved || rawStickOperation != null)
            {
                return false;
            }
            return highRateMousePresenter.TrySetSource(source, active,
                velocityX, velocityY, profileRevision);
        }
    }

    internal bool TrySetHighRateMappingMouseSources(
        bool stickAssistActive, double stickAssistVelocityX,
        double stickAssistVelocityY, bool irActive, double irVelocityX,
        double irVelocityY, bool mappedStickActive,
        double mappedStickVelocityX, double mappedStickVelocityY,
        long profileRevision)
    {
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                terminalNeutralReserved || rawStickOperation != null)
            {
                return false;
            }
            return highRateMousePresenter.TrySetMappingSources(
                stickAssistActive, stickAssistVelocityX,
                stickAssistVelocityY, irActive, irVelocityX, irVelocityY,
                mappedStickActive, mappedStickVelocityX,
                mappedStickVelocityY, profileRevision);
        }
    }

    /// <summary>
    /// Retires a setup that provably never crossed its external report-admission
    /// commit gate. This is intentionally not terminal-neutral publication: a
    /// bound-but-unattached table cannot admit such a report, and no mapping
    /// state exists to neutralize.
    /// </summary>
    internal bool TryAbortUnpublishedActivation()
    {
        bool aborted;
        lock (publicationGate)
        {
            if (runtimeState is not (Switch2RuntimeInputDeviceState.Created or
                    Switch2RuntimeInputDeviceState.Active) ||
                publicationInProgress || terminalNeutralReserved ||
                lastPacketCounter != 0)
            {
                return false;
            }

            runtimeState =
                Switch2RuntimeInputDeviceState.AbortedUnpublished;
            CancelRawStickCalibrationNoLock();
            aborted = true;
        }

        if (aborted)
        {
            highRateMousePresenter.Stop();
            lock (localFeedbackGate)
            {
                CancelConnectionHapticNoLock();
                CancelIdentificationHapticNoLock(withdraw: true);
            }
        }
        return aborted;
    }

    public override void RefreshCalibration()
    {
    }

    public override VidPidFeatureSet FeatureSet
    {
        get => base.FeatureSet;
        set { }
    }

    public override VidPidFeatureSet ModifyFeatureSetFlag(
        VidPidFeatureSet featureBitFlag, bool flagSet) => base.FeatureSet;

    public override void removeReportHandlers()
    {
        lock (publicationGate)
        {
            reportHandlers = null;
            reportSubscribers = Array.Empty<ReportHandler<EventArgs>>();
        }
    }

    internal bool IsVirtualOutputTransitionActive =>
        Volatile.Read(ref virtualOutputTransitionDepth) != 0;

    // A retiring table may still be draining a previously admitted profile
    // action. Only its serialized controller thread may finish that exact
    // transition; another caller cannot borrow the general "active" flag.
    internal bool IsCurrentVirtualOutputTransitionThread =>
        Volatile.Read(ref virtualOutputTransitionDepth) != 0 &&
        Volatile.Read(ref publicationThreadId) == Environment.CurrentManagedThreadId;

    /// <summary>
    /// Only the serialized cold controller action may replace its virtual pad.
    /// BLE ingress keeps a current baseline while native detach/attach blocks
    /// this thread, then resumes its ordinary ordered queue. The in-progress
    /// old snapshot must not be replayed to the successor virtual device.
    /// </summary>
    internal void RunVirtualOutputTransition(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                !publicationInProgress || reportCallbacksActive ||
                publicationThreadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException(
                    "Virtual output replacement requires a serialized cold controller action.");
        }
        Interlocked.Increment(ref virtualOutputTransitionDepth);
        Interlocked.Increment(ref virtualOutputTransitionRevision);
        try { action(); }
        finally { Interlocked.Decrement(ref virtualOutputTransitionDepth); }
    }

    public override void HaltReportingRunAction(Action act)
        => HaltReportingRunActionCore(act, queueOnFailure: true);

    public override bool TryHaltReportingRunAction(Action act)
        => HaltReportingRunActionCore(act, queueOnFailure: false);

    private bool HaltReportingRunActionCore(Action act, bool queueOnFailure)
    {
        const int maximumWaitMilliseconds = 500;
        long deadline = Environment.TickCount64 + maximumWaitMilliseconds;
        lock (publicationGate)
        {
            if (!queueOnFailure && runtimeState != Switch2RuntimeInputDeviceState.Active)
                return false;
            while (publicationInProgress)
            {
                if (publicationThreadId ==
                    Environment.CurrentManagedThreadId)
                {
                    if (queueOnFailure)
                        queueEvent(act);
                    return false;
                }

                int remaining = (int)Math.Min(int.MaxValue,
                    deadline - Environment.TickCount64);
                if (remaining <= 0 || !System.Threading.Monitor.Wait(
                        publicationGate, remaining))
                {
                    if (queueOnFailure)
                        queueEvent(act);
                    return false;
                }
            }

            if (!queueOnFailure && runtimeState != Switch2RuntimeInputDeviceState.Active)
                return false;
            publicationInProgress = true;
            publicationThreadId = Environment.CurrentManagedThreadId;
        }

        Exception actionException = null;
        try
        {
            act?.Invoke();
        }
        catch (Exception exception)
        {
            actionException = exception;
        }

        ReportHandler<EventArgs>[] terminalSubscribers = null;
        bool publishTerminal = false;
        lock (publicationGate)
        {
            publicationInProgress = false;
            publicationThreadId = 0;
            if (terminalNeutralPending)
            {
                publishTerminal = TryReserveTerminalNeutralNoLock(
                    out terminalSubscribers);
            }
            System.Threading.Monitor.PulseAll(publicationGate);
        }

        if (publishTerminal)
        {
            InvokeAndCommitPublication(terminalSubscribers,
                isTerminalNeutral: true);
        }
        if (actionException != null)
        {
            ExceptionDispatchInfo.Capture(actionException).Throw();
        }
        return true;
    }

    public override bool DisconnectWireless(bool callRemoval = false) =>
        transport == Switch2Transport.BluetoothLe && DisconnectBT(callRemoval);

    public override bool DisconnectBT(bool callRemoval = false)
    {
        Func<ulong, bool> requestHandler;
        lock (publicationGate)
        {
            if (transport != Switch2Transport.BluetoothLe ||
                runtimeState != Switch2RuntimeInputDeviceState.Active ||
                bluetoothDisconnectRequestHandler == null)
            {
                return false;
            }
            requestHandler = bluetoothDisconnectRequestHandler;
        }

        if (Interlocked.CompareExchange(ref bluetoothDisconnectRequested,
                1, 0) != 0)
        {
            return true;
        }

        bool accepted = false;
        try
        {
            accepted = requestHandler(RuntimeGeneration);
        }
        catch
        {
            // A lifecycle observer cannot be allowed to escape through a
            // controller report callback or the Controllers-tab command.
        }
        if (accepted)
        {
            IsDisconnecting = true;
            return true;
        }

        Interlocked.Exchange(ref bluetoothDisconnectRequested, 0);
        return false;
    }

    public override bool DisconnectDongle(bool remove = false) => false;

    public override byte RightLightFastRumble
    {
        get { lock (localFeedbackGate) { return profileLightFastRumble; } }
        set
        {
            lock (localFeedbackGate)
            {
                TrySetProfileRumbleNoLock(value, profileHeavySlowRumble);
            }
        }
    }

    public override byte LeftHeavySlowRumble
    {
        get { lock (localFeedbackGate) { return profileHeavySlowRumble; } }
        set
        {
            lock (localFeedbackGate)
            {
                TrySetProfileRumbleNoLock(profileLightFastRumble, value);
            }
        }
    }

    public override byte getLeftHeavySlowRumble()
    {
        lock (localFeedbackGate)
        {
            return profileHeavySlowRumble;
        }
    }

    public override DS4Color LightBarColor
    {
        get => default;
        set { }
    }

    public override void setRumble(byte rightLightFastMotor,
        byte leftHeavySlowMotor)
    {
        lock (localFeedbackGate)
        {
            TrySetProfileRumbleNoLock(rightLightFastMotor,
                leftHeavySlowMotor);
        }
    }

    public override void SetRumblePreview(bool lightMotorActive,
        byte lightMotorStrength, bool heavyMotorActive,
        byte heavyMotorStrength)
    {
        lock (localFeedbackGate)
        {
            CancelIdentificationHapticNoLock(withdraw: true);
            if (lightMotorActive && lightMotorStrength != 0 ||
                heavyMotorActive && heavyMotorStrength != 0)
            {
                CancelConnectionHapticNoLock();
                if (connectionHapticOwnsProfileLane)
                {
                    _ = TryWithdrawLocalRumbleNoLock(
                        ControllerFeedbackPublicationOrigin.ProfileEffect);
                    connectionHapticOwnsProfileLane = false;
                }
            }
            TryPublishLocalRumbleNoLock(
                ControllerFeedbackPublicationOrigin.TestPreview,
                lightMotorActive ? lightMotorStrength : (byte)0,
                heavyMotorActive ? heavyMotorStrength : (byte)0);
        }
    }

    public override void ClearRumblePreview()
    {
        lock (localFeedbackGate)
        {
            CancelIdentificationHapticNoLock(withdraw: true);
            TryWithdrawLocalRumbleNoLock(
                ControllerFeedbackPublicationOrigin.TestPreview);
        }
    }

    public override void SetHapticState(ref DS4HapticState hs)
    {
        if (!hs.IsRumbleSet())
        {
            return;
        }
        SetRumbleState(ref hs.rumbleState);
    }

    public override void SetLightbarState(ref DS4LightbarState lightState)
    {
    }

    public override void SetRumbleState(
        ref DS4ForceFeedbackState rumbleState)
    {
        if (!rumbleState.IsRumbleSet())
        {
            return;
        }
        setRumble(rumbleState.RumbleMotorStrengthRightLightFast,
            rumbleState.RumbleMotorStrengthLeftHeavySlow);
    }

    private bool TrySetProfileRumbleNoLock(byte lightFast,
        byte heavySlow)
    {
        profileLightFastRumble = lightFast;
        profileHeavySlowRumble = heavySlow;
        if (lightFast != 0 || heavySlow != 0)
        {
            CancelConnectionHapticNoLock();
            connectionHapticOwnsProfileLane = false;
        }
        else if (connectionHapticOwnsProfileLane)
        {
            // The canonical mapper commonly repeats a neutral local profile
            // state while a controller connects. That is not an explicit cue
            // cancellation and must not truncate the source-backed signature.
            return true;
        }
        return TryPublishLocalRumbleNoLock(
            ControllerFeedbackPublicationOrigin.ProfileEffect,
            lightFast, heavySlow);
    }

    private bool TryPublishLocalRumbleNoLock(
        ControllerFeedbackPublicationOrigin origin, byte lightFast,
        byte heavySlow)
    {
        if (lightFast == 0 && heavySlow == 0)
        {
            return TryWithdrawLocalRumbleNoLock(origin);
        }
        if (!TryGetOrCreateLocalFeedbackLaneNoLock(origin,
                out ControllerFeedbackStateLanePump.Lane lane) ||
            !ControllerFeedbackClock.TryGetTimestampMicroseconds(
                out ulong nowMicroseconds) ||
            !lane.TryPublish(new ControllerFeedbackActuatorState(
                (ushort)(heavySlow * 257),
                (ushort)(lightFast * 257), 0, 0), nowMicroseconds))
        {
            return false;
        }

        return TryDrainLocalFeedbackNoLock(nowMicroseconds);
    }

    /// <summary>
    /// Starts the one-shot connection signature only after the transport owner
    /// has crossed its external activation commit. The scheduler performs no
    /// work on the controller input thread and cannot acquire another writer.
    /// </summary>
    internal bool TryStartConnectionHaptic()
    {
        CancellationTokenSource cancellation;
        lock (localFeedbackGate)
        {
            int slot = DeviceSlotNumber;
            if (connectionHapticStarted || slot < 0 ||
                slot >= Global.Switch2ConnectionHapticEnabled.Length ||
                !Global.Switch2ConnectionHapticEnabled[slot])
            {
                return false;
            }
            lock (publicationGate)
            {
                if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                    terminalNeutralReserved ||
                    (bluetoothFeedbackLifetime == null) ==
                        (usbFeedbackLifetime == null))
                {
                    return false;
                }
            }

            cancellation = new CancellationTokenSource();
            connectionHapticCancellation = cancellation;
            connectionHapticStarted = true;
        }

        _ = Task.Run(() => RunConnectionHapticAsync(cancellation));
        return true;
    }

    private async Task RunConnectionHapticAsync(
        CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        bool joyConPhysical = joyConBindingMode !=
            Switch2JoyConRuntimeBindingMode.Invalid;
        ControllerFeedbackActuatorState bassMarker = joyConPhysical ?
            Switch2ConnectionHaptic.JoyConBassMarker :
            Switch2ConnectionHaptic.ProBassMarker;
        Switch2HdRumbleGroup bassGroup = joyConPhysical ?
            Switch2ConnectionHaptic.JoyConBassGroup :
            Switch2ConnectionHaptic.ProBassGroup;
        ControllerFeedbackActuatorState sharpMarker = joyConPhysical ?
            Switch2ConnectionHaptic.JoyConSharpClickMarker :
            Switch2ConnectionHaptic.ProSharpClickMarker;
        Switch2HdRumbleGroup sharpGroup = joyConPhysical ?
            Switch2ConnectionHaptic.JoyConSharpClickGroup :
            Switch2ConnectionHaptic.ProSharpClickGroup;
        try
        {
            if (transport == Switch2Transport.Usb)
            {
                await Task.Delay(
                    Switch2ConnectionHaptic.UsbInitialDelayMilliseconds,
                    token).ConfigureAwait(false);
            }
            if (!TryPublishConnectionHapticStage(cancellation,
                    bassMarker, bassGroup))
            {
                return;
            }
            await Task.Delay(Switch2ConnectionHaptic.
                BassDurationMilliseconds, token).ConfigureAwait(false);
            if (!TryWithdrawConnectionHapticStage(cancellation))
            {
                return;
            }
            await Task.Delay(Switch2ConnectionHaptic.NeutralGapMilliseconds,
                token).ConfigureAwait(false);
            if (!TryPublishConnectionHapticStage(cancellation,
                    sharpMarker, sharpGroup))
            {
                return;
            }
            await Task.Delay(Switch2ConnectionHaptic.
                SharpClickDurationMilliseconds, token).ConfigureAwait(false);
            _ = TryWithdrawConnectionHapticStage(cancellation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (localFeedbackGate)
            {
                if (ReferenceEquals(connectionHapticCancellation,
                        cancellation))
                {
                    connectionHapticCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private bool TryPublishConnectionHapticStage(
        CancellationTokenSource cancellation,
        in ControllerFeedbackActuatorState marker,
        in Switch2HdRumbleGroup group)
    {
        lock (localFeedbackGate)
        {
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(connectionHapticCancellation,
                    cancellation) ||
                !TryGetOrCreateLocalFeedbackLaneNoLock(
                    ControllerFeedbackPublicationOrigin.ProfileEffect,
                    out ControllerFeedbackStateLanePump.Lane lane))
            {
                return false;
            }

            Switch2BluetoothFeedbackLifetime bluetoothFeedback;
            Switch2ProUsbOwnedFeedbackActivationLifetime usbFeedback;
            lock (publicationGate)
            {
                if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                    terminalNeutralReserved)
                {
                    return false;
                }
                bluetoothFeedback = bluetoothFeedbackLifetime;
                usbFeedback = usbFeedbackLifetime;
            }
            bool published = bluetoothFeedback != null ?
                bluetoothFeedback.TryPublishNativeProfileEffectAndPump(
                    lane, marker, group, group) :
                usbFeedback != null &&
                usbFeedback.TryPublishNativeProfileEffectAndPump(
                    lane, marker, group, group);
            connectionHapticOwnsProfileLane = published;
            return published;
        }
    }

    private bool TryWithdrawConnectionHapticStage(
        CancellationTokenSource cancellation)
    {
        lock (localFeedbackGate)
        {
            bool withdrawn = !cancellation.IsCancellationRequested &&
                ReferenceEquals(connectionHapticCancellation,
                    cancellation) &&
                TryWithdrawLocalRumbleNoLock(
                    ControllerFeedbackPublicationOrigin.ProfileEffect);
            if (withdrawn)
            {
                connectionHapticOwnsProfileLane = false;
            }
            return withdrawn;
        }
    }

    private void CancelConnectionHapticNoLock()
    {
        connectionHapticCancellation?.Cancel();
    }

    /// <summary>
    /// Starts (or restarts) the donor-compatible two-pulse identification cue
    /// for this exact logical Switch 2 controller. The explicit UI action uses
    /// TestPreview priority and therefore cannot be hidden behind a profile
    /// effect, while native game feedback still remains in the same canonical
    /// arbitration runtime.
    /// </summary>
    internal bool TryStartIdentificationHaptic()
    {
        CancellationTokenSource cancellation;
        lock (localFeedbackGate)
        {
            lock (publicationGate)
            {
                if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                    terminalNeutralReserved ||
                    (bluetoothFeedbackLifetime == null) ==
                        (usbFeedbackLifetime == null))
                {
                    return false;
                }
            }

            CancelIdentificationHapticNoLock(withdraw: true);
            CancelConnectionHapticNoLock();
            if (connectionHapticOwnsProfileLane)
            {
                _ = TryWithdrawLocalRumbleNoLock(
                    ControllerFeedbackPublicationOrigin.ProfileEffect);
                connectionHapticOwnsProfileLane = false;
            }
            cancellation = new CancellationTokenSource();
            identificationHapticCancellation = cancellation;
        }

        _ = Task.Run(() => RunIdentificationHapticAsync(cancellation));
        return true;
    }

    private async Task RunIdentificationHapticAsync(
        CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            if (!TryPublishIdentificationHapticStage(cancellation))
            {
                return;
            }
            await Task.Delay(Switch2IdentificationHaptic.
                PulseDurationMilliseconds, token).ConfigureAwait(false);
            if (!TryWithdrawIdentificationHapticStage(cancellation))
            {
                return;
            }
            await Task.Delay(Switch2IdentificationHaptic.
                PulseGapMilliseconds, token).ConfigureAwait(false);
            if (!TryPublishIdentificationHapticStage(cancellation))
            {
                return;
            }
            await Task.Delay(Switch2IdentificationHaptic.
                PulseDurationMilliseconds, token).ConfigureAwait(false);
            _ = TryWithdrawIdentificationHapticStage(cancellation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (localFeedbackGate)
            {
                if (ReferenceEquals(identificationHapticCancellation,
                        cancellation))
                {
                    identificationHapticCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private bool TryPublishIdentificationHapticStage(
        CancellationTokenSource cancellation)
    {
        lock (localFeedbackGate)
        {
            bool joyConPhysical = joyConBindingMode !=
                Switch2JoyConRuntimeBindingMode.Invalid;
            ControllerFeedbackActuatorState marker = joyConPhysical ?
                Switch2IdentificationHaptic.JoyConMarker :
                Switch2IdentificationHaptic.ProMarker;
            Switch2HdRumbleGroup group = joyConPhysical ?
                Switch2IdentificationHaptic.JoyConPulseGroup :
                Switch2IdentificationHaptic.ProPulseGroup;
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(identificationHapticCancellation,
                    cancellation) ||
                !TryGetOrCreateLocalFeedbackLaneNoLock(
                    ControllerFeedbackPublicationOrigin.TestPreview,
                    out ControllerFeedbackStateLanePump.Lane lane))
            {
                return false;
            }

            Switch2BluetoothFeedbackLifetime bluetoothFeedback;
            Switch2ProUsbOwnedFeedbackActivationLifetime usbFeedback;
            lock (publicationGate)
            {
                if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                    terminalNeutralReserved)
                {
                    return false;
                }
                bluetoothFeedback = bluetoothFeedbackLifetime;
                usbFeedback = usbFeedbackLifetime;
            }
            bool published = bluetoothFeedback != null ?
                bluetoothFeedback.TryPublishNativePreviewAndPump(lane,
                    marker, group, group) :
                usbFeedback != null &&
                usbFeedback.TryPublishNativePreviewAndPump(lane,
                    marker, group, group);
            identificationHapticOwnsPreviewLane = published;
            return published;
        }
    }

    private bool TryWithdrawIdentificationHapticStage(
        CancellationTokenSource cancellation)
    {
        lock (localFeedbackGate)
        {
            bool withdrawn = !cancellation.IsCancellationRequested &&
                ReferenceEquals(identificationHapticCancellation,
                    cancellation) &&
                TryWithdrawLocalRumbleNoLock(
                    ControllerFeedbackPublicationOrigin.TestPreview);
            if (withdrawn)
            {
                identificationHapticOwnsPreviewLane = false;
            }
            return withdrawn;
        }
    }

    private void CancelIdentificationHapticNoLock(bool withdraw)
    {
        identificationHapticCancellation?.Cancel();
        if (withdraw && identificationHapticOwnsPreviewLane)
        {
            _ = TryWithdrawLocalRumbleNoLock(
                ControllerFeedbackPublicationOrigin.TestPreview);
            identificationHapticOwnsPreviewLane = false;
        }
    }

    private bool TryWithdrawLocalRumbleNoLock(
        ControllerFeedbackPublicationOrigin origin)
    {
        ControllerFeedbackStateLanePump.Lane lane = origin ==
                ControllerFeedbackPublicationOrigin.TestPreview ?
            previewFeedbackLane : profileFeedbackLane;
        if (lane == null)
        {
            return true;
        }
        if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(
                out ulong nowMicroseconds) ||
            !lane.TryWithdraw(nowMicroseconds))
        {
            return false;
        }
        return TryDrainLocalFeedbackNoLock(nowMicroseconds);
    }

    private bool TryGetOrCreateLocalFeedbackLaneNoLock(
        ControllerFeedbackPublicationOrigin origin,
        out ControllerFeedbackStateLanePump.Lane lane)
    {
        lane = origin == ControllerFeedbackPublicationOrigin.TestPreview ?
            previewFeedbackLane : profileFeedbackLane;
        if (lane != null)
        {
            return true;
        }

        Switch2BluetoothFeedbackLifetime bluetoothFeedback;
        Switch2ProUsbOwnedFeedbackActivationLifetime usbFeedback;
        lock (publicationGate)
        {
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                (bluetoothFeedbackLifetime == null) ==
                    (usbFeedbackLifetime == null))
            {
                return false;
            }
            bluetoothFeedback = bluetoothFeedbackLifetime;
            usbFeedback = usbFeedbackLifetime;
        }

        ulong initialOwnershipEpoch = origin ==
                ControllerFeedbackPublicationOrigin.TestPreview ? 2UL : 1UL;
        bool created = bluetoothFeedback != null ?
            bluetoothFeedback.TryCreateLane(origin,
                ControllerFeedbackSource.Xbox360VirtualDevice,
                initialOwnershipEpoch,
                LocalFeedbackTimeToLiveMicroseconds,
                LocalFeedbackRenewalIntervalMicroseconds, out lane) :
            usbFeedback.TryCreateLane(origin,
                ControllerFeedbackSource.Xbox360VirtualDevice,
                initialOwnershipEpoch,
                LocalFeedbackTimeToLiveMicroseconds,
                LocalFeedbackRenewalIntervalMicroseconds, out lane);
        if (!created || lane == null)
        {
            lane = null;
            return false;
        }

        if (origin == ControllerFeedbackPublicationOrigin.TestPreview)
        {
            previewFeedbackLane = lane;
        }
        else
        {
            profileFeedbackLane = lane;
        }
        return true;
    }

    private bool TryDrainLocalFeedbackNoLock(ulong nowMicroseconds)
    {
        Switch2BluetoothFeedbackLifetime bluetoothFeedback;
        Switch2ProUsbOwnedFeedbackActivationLifetime usbFeedback;
        lock (publicationGate)
        {
            bluetoothFeedback = bluetoothFeedbackLifetime;
            usbFeedback = usbFeedbackLifetime;
            if (runtimeState != Switch2RuntimeInputDeviceState.Active ||
                (bluetoothFeedback == null) == (usbFeedback == null))
            {
                return false;
            }
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ControllerFeedbackPumpDisposition disposition =
                bluetoothFeedback != null ?
                    bluetoothFeedback.TryPumpOnce(nowMicroseconds,
                        out _) :
                    usbFeedback.TryPumpOnce(nowMicroseconds, out _);
            switch (disposition)
            {
                case ControllerFeedbackPumpDisposition.None:
                    return true;
                case ControllerFeedbackPumpDisposition.Delivered:
                case ControllerFeedbackPumpDisposition.Superseded:
                    continue;
                default:
                    return false;
            }
        }
        return false;
    }

    private bool TryReserveTerminalNeutralNoLock(
        out ReportHandler<EventArgs>[] subscribers)
    {
        terminalNeutralPending = false;
        neutralState.CopyTo(stagingState);
        stagingHasMotion = false;
        return TryReserveStagingNoLock(terminalNeutralSubscribers,
            out subscribers);
    }

    private void ObservePhysicalInputNoLock(long timestampQpc,
        long qpcFrequency)
    {
        hasObservedPhysicalInput = true;
        if (timestampQpc < 0 || qpcFrequency <= 0)
        {
            ResetInputObservationNoLock(0, 0);
            return;
        }
        if (inputObservationQpcFrequency != qpcFrequency ||
            timestampQpc < inputObservationTimestampQpc)
        {
            ResetInputObservationNoLock(timestampQpc, qpcFrequency);
            return;
        }
        if (timestampQpc == inputObservationTimestampQpc)
        {
            // Joined publications may share one source-completion timestamp.
            // Do not turn a duplicate observation into an infinite rate.
            return;
        }

        double intervalMilliseconds =
            (timestampQpc - inputObservationTimestampQpc) * 1000.0 /
            qpcFrequency;
        inputObservationTimestampQpc = timestampQpc;
        if (inputObservationCount == InputObservationWindowLength)
        {
            inputObservationSumMilliseconds -=
                inputObservationIntervals[inputObservationNext];
        }
        else
        {
            inputObservationCount++;
        }
        inputObservationIntervals[inputObservationNext] = intervalMilliseconds;
        inputObservationSumMilliseconds += intervalMilliseconds;
        inputObservationNext = (inputObservationNext + 1) %
            InputObservationWindowLength;
        Latency = inputObservationSumMilliseconds / inputObservationCount;
    }

    private void ResetInputObservationNoLock(long timestampQpc,
        long qpcFrequency)
    {
        inputObservationTimestampQpc = timestampQpc;
        inputObservationQpcFrequency = qpcFrequency;
        inputObservationCount = 0;
        inputObservationNext = 0;
        inputObservationSumMilliseconds = 0;
        Latency = 0;
    }

    private bool TryReserveStagingNoLock(
        out ReportHandler<EventArgs>[] subscribers) =>
        TryReserveStagingNoLock(reportSubscribers, out subscribers);

    /// <summary>
    /// Borrows the exact currently published state on its owning thread.
    /// The caller must already own registration report admission and finish
    /// before returning from Report. No extra gate or report allocation is
    /// needed: this thread owns cState until InvokeAndCommitPublication ends.
    /// The cached envelope is reusable, but grants no authority outside the
    /// actual Report callback phase or on another thread. Fabricated envelopes
    /// are never accepted.
    /// </summary>
    internal bool TryBorrowCurrentPublication(Switch2RuntimeReportEventArgs report,
        out DS4State state, out bool hasMotion)
    {
        state = null;
        hasMotion = false;
        if (Volatile.Read(ref publicationThreadId) != Environment.CurrentManagedThreadId ||
            !publicationInProgress || !reportCallbacksActive ||
            !ReferenceEquals(report, publicationIsTerminal ? terminalReportEventArgs : regularReportEventArgs))
            return false;
        state = cState;
        hasMotion = !publicationIsTerminal && publicationHasMotion;
        return true;
    }

    private bool TryReserveStagingNoLock(
        ReportHandler<EventArgs>[] subscriberSnapshot,
        out ReportHandler<EventArgs>[] subscribers)
    {
        if (publicationInProgress)
        {
            subscribers = null;
            return false;
        }

        publicationInProgress = true;
        publicationThreadId = Environment.CurrentManagedThreadId;
        lastPacketCounter = lastPacketCounter == uint.MaxValue ? 1 :
            lastPacketCounter + 1;
        stagingState.PacketCounter = lastPacketCounter;
        stagingState.ReportTimeStamp = DateTime.UtcNow;
        stagingState.elapsedTime = stagingHasMotion &&
            stagingState.Motion != null ? stagingState.Motion.elapsed : 0.0;
        if (stagingState.elapsedTime > 0.0)
        {
            double elapsedMicroseconds =
                stagingState.elapsedTime * 1_000_000.0;
            ulong elapsedDelta = elapsedMicroseconds >= ulong.MaxValue ?
                ulong.MaxValue : (ulong)elapsedMicroseconds;
            stagingState.totalMicroSec = unchecked(
                pState.totalMicroSec + elapsedDelta);
        }
        else
        {
            stagingState.totalMicroSec = pState.totalMicroSec;
        }
        stagingState.CopyTo(cState);
        publicationHasMotion = stagingHasMotion;
        subscribers = subscriberSnapshot;
        return true;
    }

    private bool InvokeAndCommitPublication(
        ReportHandler<EventArgs>[] subscribers, bool isTerminalNeutral)
    {
        publicationIsTerminal = isTerminalNeutral;
        long beforeActionsRevision = Interlocked.Read(ref virtualOutputTransitionRevision);
        bool reported = DrainQueuedActions(deferredActions: 0,
            out int actionsDeferredFromPreReport);
        bool reportCurrent = isTerminalNeutral || beforeActionsRevision ==
            Interlocked.Read(ref virtualOutputTransitionRevision);
        bool delivered = false;
        bool handlersSucceeded = true;
        if (reportCurrent && !isTerminalNeutral && publicationHasMotion)
        {
            try
            {
                // Legacy devices fire this seam after decoding motion and
                // before Report. Match that order so existing profile gyro
                // modes observe same-report controls and motion.
                sixAxis.FireProjectedSixAxisEvent(cState);
            }
            catch
            {
                // A mapping observer may reject this publication, but must not
                // strand the serialized runtime or suppress later Report
                // subscribers and terminal-neutral delivery.
                reported = false;
                handlersSucceeded = false;
            }
        }
        Switch2RuntimeReportEventArgs reportEventArgs = isTerminalNeutral ?
            terminalReportEventArgs : regularReportEventArgs;
        // Publication also reserves the lane for queued/profile actions. Only
        // actual Report callbacks may borrow the current mapper input.
        reportCallbacksActive = reportCurrent;
        try
        {
            for (int index = 0; reportCurrent && index < subscribers.Length; index++)
            {
                try
                {
                    subscribers[index](this, reportEventArgs);
                    delivered = true;
                }
                catch
                {
                    reported = false;
                    handlersSucceeded = false;
                }
            }
        }
        finally { reportCallbacksActive = false; }
        if (!DrainQueuedActions(actionsDeferredFromPreReport, out _))
        {
            reported = false;
        }
        ReportHandler<EventArgs>[] terminalSubscribers = null;
        bool publishTerminal = false;
        lock (publicationGate)
        {
            cState.CopyTo(pState);
            publicationInProgress = false;
            publicationThreadId = 0;
            if (isTerminalNeutral)
            {
                terminalNeutralReported = delivered && handlersSucceeded;
                terminalNeutralCompleted = true;
            }
            else if (terminalNeutralPending)
            {
                publishTerminal = TryReserveTerminalNeutralNoLock(
                    out terminalSubscribers);
            }
            System.Threading.Monitor.PulseAll(publicationGate);
        }

        if (publishTerminal)
        {
            InvokeAndCommitPublication(terminalSubscribers,
                isTerminalNeutral: true);
        }

        return reported;
    }

    private bool DrainQueuedActions(int deferredActions,
        out int actionsRemaining)
    {
        bool succeeded = true;
        int actionsToDrain;
        lock (eventQueueLock)
        {
            int queuedAtStart = eventQueue.Count;
            int preserveForFuturePublication = Math.Min(
                Math.Max(deferredActions, 0), queuedAtStart);
            // The pre-report pass can itself enqueue work. Rotate that exact
            // prefix behind actions queued by Report subscribers so subscriber
            // work still runs in the post-report pass, while pre-report
            // requeues cannot run twice in one publication.
            for (int index = 0; index < preserveForFuturePublication; index++)
            {
                eventQueue.Enqueue(eventQueue.Dequeue());
            }
            actionsToDrain = queuedAtStart - preserveForFuturePublication;
        }

        for (int index = 0; index < actionsToDrain; index++)
        {
            Action action;
            lock (eventQueueLock)
            {
                action = eventQueue.Dequeue();
            }

            try
            {
                action?.Invoke();
            }
            catch
            {
                succeeded = false;
            }
        }

        lock (eventQueueLock)
        {
            // Actions queued during this bounded pass belong to a later pass.
            // This matches the base DS4Device snapshot rule and prevents a
            // self-requeueing action or sustained producer from starving the
            // Report callback while publication admission remains closed.
            actionsRemaining = eventQueue.Count;
            hasInputEvts = actionsRemaining != 0;
        }
        return succeeded;
    }

    private void RefreshReportSubscriberSnapshotNoLock()
    {
        if (reportHandlers == null)
        {
            reportSubscribers = Array.Empty<ReportHandler<EventArgs>>();
            return;
        }

        Delegate[] invocationList = reportHandlers.GetInvocationList();
        var next = new ReportHandler<EventArgs>[invocationList.Length];
        for (int index = 0; index < invocationList.Length; index++)
        {
            next[index] = (ReportHandler<EventArgs>)invocationList[index];
        }
        reportSubscribers = next;
    }

    private static bool IsExpectedProRevision(Switch2Transport transport,
        Switch2InputProtocolRevision revision) => transport switch
    {
        Switch2Transport.Usb => revision ==
            Switch2InputProtocolRevision.ProUsbCommon05Bcd0201,
        Switch2Transport.BluetoothLe => revision ==
            Switch2InputProtocolRevision.BluetoothLeCommon05V1,
        _ => false,
    };

    private static ConnectionType ConnectionFor(Switch2Transport transport) =>
        transport == Switch2Transport.Usb ? ConnectionType.USB :
            ConnectionType.BT;

    private static bool Fail(Switch2RuntimeInputDeviceCreateFailure reason,
        out Switch2RuntimeInputDevice device,
        out Switch2RuntimeInputDeviceCreateFailure failure)
    {
        device = null;
        failure = reason;
        return false;
    }
}
