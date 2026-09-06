/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothWindowsAddressType : byte
{
    Unspecified = 0,
    Public = 1,
    Random = 2,
}

internal delegate void Switch2BluetoothWindowsAdvertisementHandler(
    ulong bluetoothAddress, Switch2BluetoothWindowsAddressType addressType,
    ushort companyId, byte matchingCompanySections,
    ReadOnlySpan<byte> manufacturerValue, long observedQpc);

internal delegate void Switch2BluetoothWindowsWatcherStoppedHandler();

internal delegate void Switch2BluetoothWindowsValueChangedHandler(
    ReadOnlySpan<byte> value, long completionQpc);

internal delegate void Switch2BluetoothWindowsDisconnectedHandler();

/// <summary>
/// Active-scanning platform watcher. Implementations must stop accepting new
/// callbacks before completing <see cref="DetachHandlersAndDrainAsync"/> and
/// must not complete it while an already-admitted callback is still running.
/// </summary>
internal interface ISwitch2BluetoothWindowsAdvertisementWatcher : IDisposable
{
    bool IsConfiguredForActiveScanning { get; }

    void ConfigureActiveScanning();

    void AttachHandlers(Switch2BluetoothWindowsAdvertisementHandler received,
        Switch2BluetoothWindowsWatcherStoppedHandler stopped);

    void Start();

    void Stop();

    Task DetachHandlersAndDrainAsync();
}

internal enum Switch2BluetoothWindowsGattQueryStatus : byte
{
    Failed = 0,
    Success,
    Unreachable,
    ProtocolError,
    AccessDenied,
}

internal readonly struct Switch2BluetoothWindowsGattQuery<T>
    where T : class
{
    internal Switch2BluetoothWindowsGattQuery(bool succeeded,
        IReadOnlyList<T> items) : this(succeeded ?
            Switch2BluetoothWindowsGattQueryStatus.Success :
            Switch2BluetoothWindowsGattQueryStatus.Failed, items)
    {
    }

    internal Switch2BluetoothWindowsGattQuery(
        Switch2BluetoothWindowsGattQueryStatus status, IReadOnlyList<T> items)
    {
        Status = status;
        Items = items;
    }

    internal Switch2BluetoothWindowsGattQueryStatus Status { get; }
    internal bool Succeeded => Status == Switch2BluetoothWindowsGattQueryStatus.Success;

    internal IReadOnlyList<T> Items { get; }
}

internal interface ISwitch2BluetoothWindowsPlatform
{
    ISwitch2BluetoothWindowsAdvertisementWatcher CreateAdvertisementWatcher();

    // Returns the Windows device object, not a connected-link guarantee.
    // Uncached service discovery below may initiate the first GATT connection.
    ValueTask<ISwitch2BluetoothWindowsDevice> OpenDeviceAsync(
        ulong bluetoothAddress, Switch2BluetoothWindowsAddressType addressType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only device surface. There is intentionally no pairing, unpairing,
/// association, arbitrary GATT write, NVM, output, or reconnect method.
/// </summary>
internal interface ISwitch2BluetoothWindowsDevice : IDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// Best-effort host-link scheduling preference. This changes neither the
    /// controller's persistent state nor its pairing/bond state. A successful
    /// implementation owns the returned native request until this device is
    /// disposed; the default keeps older/fake platform surfaces compatible.
    /// </summary>
    bool TryRequestThroughputOptimized() => false;

    /// <summary>
    /// Copies Windows' stable associated-device identity into caller-owned
    /// transient memory. Implementations must not return a Bluetooth address,
    /// display name, bond key, or process-local object identity.
    /// </summary>
    bool TryCopyStableAssociationIdentity(Span<byte> destination,
        out int bytesWritten);

    void AttachDisconnectedHandler(
        Switch2BluetoothWindowsDisconnectedHandler disconnected);

    Task DetachDisconnectedHandlerAndDrainAsync();

    ValueTask<Switch2BluetoothWindowsGattQuery<
        ISwitch2BluetoothWindowsGattService>>
        GetServicesForUuidUncachedAsync(Guid serviceUuid,
            CancellationToken cancellationToken);
}

internal interface ISwitch2BluetoothWindowsGattService : IDisposable
{
    Guid Uuid { get; }

    ValueTask<Switch2BluetoothWindowsGattQuery<
        ISwitch2BluetoothWindowsGattCharacteristic>>
        GetCharacteristicsForUuidUncachedAsync(Guid characteristicUuid,
            CancellationToken cancellationToken);
}

/// <summary>
/// Narrow Common05 characteristic surface. ConfigureNotificationsAsync is
/// restricted to CCCD Notify/None; it cannot send an application payload.
/// </summary>
internal interface ISwitch2BluetoothWindowsGattCharacteristic : IDisposable
{
    Guid Uuid { get; }

    Switch2GattProperty EvidencedProperties { get; }

    bool HasOnlyReadAndNotifyProperties { get; }

    void AttachValueChangedHandler(
        Switch2BluetoothWindowsValueChangedHandler valueChanged);

    Task DetachValueChangedHandlerAndDrainAsync();

    ValueTask<bool> ConfigureNotificationsAsync(bool enabled,
        CancellationToken cancellationToken);

    /// <summary>
    /// Narrow GATT payload write used only by reviewed Switch 2 command and
    /// output owners. The caller selects WriteWithoutResponse only when the
    /// characteristic's evidenced properties advertise it.
    /// </summary>
    ValueTask<bool> WriteValueAsync(ReadOnlyMemory<byte> value,
        bool writeWithoutResponse, CancellationToken cancellationToken);
}

internal enum Switch2BluetoothWindowsScanStartFailure : byte
{
    None = 0,
    InvalidArgument,
    ScanAlreadyActive,
    GenerationRejected,
    WatcherCreationFailed,
    WatcherConfigurationFailed,
    WatcherStartFailed,
}

internal enum Switch2BluetoothWindowsOpenFailure : byte
{
    None = 0,
    InvalidObservation,
    StaleScan,
    AddressCapabilityUnavailable,
    Cancelled,
    StartupTimedOut,
    DeviceOpenFailed,
    DeviceDisconnected,
    PersistentPeerIdentityUnavailable,
    ServiceQueryFailed,
    ServiceIdentityMismatch,
    CharacteristicQueryFailed,
    CharacteristicIdentityMismatch,
    CharacteristicPropertiesMismatch,
    OutputCharacteristicQueryFailed,
    OutputCharacteristicIdentityMismatch,
    OutputCharacteristicPropertiesMismatch,
    CommandCharacteristicQueryFailed,
    CommandCharacteristicIdentityMismatch,
    ResponseCharacteristicQueryFailed,
    ResponseCharacteristicIdentityMismatch,
    CommandChannelPropertiesMismatch,
    CommandResponseSubscriptionFailed,
    NotificationSetupFailed,
    SensorInitializationFailed,
}

internal readonly struct Switch2BluetoothWindowsOpenResult
{
    private Switch2BluetoothWindowsOpenResult(
        Switch2BluetoothWindowsOpenFailure failure,
        Switch2BluetoothWindowsInputLease lease,
        Switch2BluetoothSensorInitializationFailure sensorFailure = default)
    {
        Failure = failure;
        Lease = lease;
        SensorFailure = sensorFailure;
    }

    internal bool Succeeded => Failure ==
        Switch2BluetoothWindowsOpenFailure.None && Lease != null;

    internal Switch2BluetoothWindowsOpenFailure Failure { get; }

    internal Switch2BluetoothWindowsInputLease Lease { get; }
    internal Switch2BluetoothSensorInitializationFailure SensorFailure { get; }

    internal static Switch2BluetoothWindowsOpenResult Success(
        Switch2BluetoothWindowsInputLease lease) => new(
            Switch2BluetoothWindowsOpenFailure.None, lease);

    internal static Switch2BluetoothWindowsOpenResult Failed(
        Switch2BluetoothWindowsOpenFailure failure,
        Switch2BluetoothSensorInitializationFailure sensorFailure = default) =>
        new(failure, null, sensorFailure);
}

/// <summary>
/// Windows discovery/open owner for already-remembered-this-host
/// Switch 2 peers. Raw addresses and manufacturer bytes never leave this
/// boundary. Each address is a one-scan, one-open capability keyed by a fresh
/// cryptographic secret and is erased when consumed or when the scan retires.
/// </summary>
internal sealed class Switch2BluetoothWindowsAdapter
{
    public const int MinimumTimeoutMilliseconds = 10;
    public const int MaximumTimeoutMilliseconds = 60_000;

    private const ulong BluetoothAddressMask = 0x0000FFFFFFFFFFFFUL;

    private readonly object sync = new();
    private readonly ISwitch2BluetoothWindowsPlatform platform;
    private readonly Switch2BluetoothCandidateRegistry registry;
    private readonly ISwitch2PersistentPeerIdentityDeriver identityDeriver;
    private readonly TimeSpan startupTimeout;
    private readonly TimeSpan teardownTimeout;
    private ScanLifetime activeScan;
    private ScanLifetime retiringScan;
    private long candidateCallbackFailureCount;

    internal Switch2BluetoothWindowsAdapter(
        ISwitch2BluetoothWindowsPlatform platform,
        Switch2BluetoothCandidateRegistry registry, TimeSpan startupTimeout,
        TimeSpan teardownTimeout)
        : this(platform, registry, startupTimeout, teardownTimeout, null)
    {
    }

    internal Switch2BluetoothWindowsAdapter(
        ISwitch2BluetoothWindowsPlatform platform,
        Switch2BluetoothCandidateRegistry registry, TimeSpan startupTimeout,
        TimeSpan teardownTimeout,
        ISwitch2PersistentPeerIdentityDeriver identityDeriver)
    {
        this.platform = platform ?? throw new ArgumentNullException(
            nameof(platform));
        this.registry = registry ?? throw new ArgumentNullException(
            nameof(registry));
        this.identityDeriver = identityDeriver;
        ValidateTimeout(startupTimeout, nameof(startupTimeout));
        ValidateTimeout(teardownTimeout, nameof(teardownTimeout));
        this.startupTimeout = startupTimeout;
        this.teardownTimeout = teardownTimeout;
    }

    internal bool IsScanning
    {
        get
        {
            lock (sync)
            {
                return activeScan != null;
            }
        }
    }

    internal long CandidateCallbackFailureCount =>
        Interlocked.Read(ref candidateCallbackFailureCount);

    internal bool TryStartScan(ulong scanGeneration,
        ReadOnlySpan<byte> selectedHostAddress,
        Action<Switch2BluetoothCandidateObservation> candidate,
        out Switch2BluetoothWindowsScanStartFailure failure) =>
        TryStartScan(scanGeneration, selectedHostAddress, candidate,
            out failure, out _);

    internal bool TryStartScan(ulong scanGeneration,
        ReadOnlySpan<byte> selectedHostAddress,
        Action<Switch2BluetoothCandidateObservation> candidate,
        out Switch2BluetoothWindowsScanStartFailure failure,
        out Task<bool> failedStartCleanup)
    {
        // Unknown ownership must remain false. Each failure below supplies
        // either explicit no-resource proof or the exact watcher cleanup task.
        failedStartCleanup = Task.FromResult(false);
        if (scanGeneration == 0 || selectedHostAddress.Length != 6 ||
            IsAllZero(selectedHostAddress) || candidate == null)
        {
            failedStartCleanup = Task.FromResult(true);
            failure = Switch2BluetoothWindowsScanStartFailure.InvalidArgument;
            return false;
        }

        lock (sync)
        {
            if (activeScan != null || HasIncompleteScanRetirementNoLock())
            {
                failure = Switch2BluetoothWindowsScanStartFailure.
                    ScanAlreadyActive;
                return false;
            }
        }

        ISwitch2BluetoothWindowsAdvertisementWatcher watcher;
        try
        {
            watcher = platform.CreateAdvertisementWatcher();
        }
        catch
        {
            watcher = null;
        }
        if (watcher == null)
        {
            failedStartCleanup = Task.FromResult(true);
            failure = Switch2BluetoothWindowsScanStartFailure.
                WatcherCreationFailed;
            return false;
        }

        ScanLifetime lifetime = null;
        try
        {
            lifetime = new ScanLifetime(scanGeneration, selectedHostAddress,
                registry.Capacity, watcher, candidate);
            lifetime.Received = (address, addressType, companyId,
                companySections, value, qpc) => OnAdvertisement(lifetime,
                    address, addressType, companyId,
                    companySections, value, qpc);
            lifetime.Stopped = () => OnWatcherStopped(lifetime);
        }
        catch
        {
            lifetime?.RetireSensitiveState();
            bool disposed = false;
            try
            {
                watcher.Dispose();
                disposed = true;
            }
            catch
            {
            }
            failedStartCleanup = Task.FromResult(disposed);
            failure = Switch2BluetoothWindowsScanStartFailure.
                WatcherCreationFailed;
            return false;
        }

        try
        {
            watcher.ConfigureActiveScanning();
            if (!watcher.IsConfiguredForActiveScanning)
            {
                throw new InvalidOperationException();
            }
            watcher.AttachHandlers(lifetime.Received, lifetime.Stopped);
        }
        catch
        {
            RetireUnpublishedWatcher(lifetime);
            failedStartCleanup = ObserveScanRetirementAsync(lifetime);
            failure = Switch2BluetoothWindowsScanStartFailure.
                WatcherConfigurationFailed;
            return false;
        }

        Switch2BluetoothWindowsScanStartFailure publicationFailure =
            Switch2BluetoothWindowsScanStartFailure.None;
        lock (sync)
        {
            if (activeScan != null || HasIncompleteScanRetirementNoLock())
            {
                publicationFailure = Switch2BluetoothWindowsScanStartFailure.
                    ScanAlreadyActive;
            }
            else if (!registry.TryBeginScan(scanGeneration))
            {
                publicationFailure = Switch2BluetoothWindowsScanStartFailure.
                    GenerationRejected;
            }
            else
            {
                lifetime.CallbackGate.Open();
                retiringScan = null;
                activeScan = lifetime;
            }
        }
        if (publicationFailure != Switch2BluetoothWindowsScanStartFailure.None)
        {
            // Watcher Stop/detach may enter platform code. It must never run
            // while the adapter publication lock is held.
            RetireUnpublishedWatcher(lifetime);
            failedStartCleanup = ObserveScanRetirementAsync(lifetime);
            failure = publicationFailure;
            return false;
        }

        try
        {
            lock (lifetime.PlatformLifecycleSync)
            {
                watcher.Start();
            }
        }
        catch
        {
            RetireScan(lifetime, stopWatcher: true);
            failedStartCleanup = ObserveScanRetirementAsync(lifetime);
            failure = Switch2BluetoothWindowsScanStartFailure.
                WatcherStartFailed;
            return false;
        }

        lock (sync)
        {
            // Start is allowed to raise callbacks inline. No candidate may
            // escape until Start itself has returned and this exact lifetime
            // is still the published scan. An inline Stopped callback retires
            // the lifetime and therefore makes startup fail closed.
            if (!ReferenceEquals(activeScan, lifetime))
            {
                failedStartCleanup = ObserveScanRetirementAsync(lifetime);
                failure = Switch2BluetoothWindowsScanStartFailure.
                    WatcherStartFailed;
                return false;
            }
            lifetime.StartPublished = true;
        }

        failure = Switch2BluetoothWindowsScanStartFailure.None;
        return true;
    }

    internal async ValueTask<bool> EndScanAsync(ulong scanGeneration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await EndScanAndDrainAsync(scanGeneration).
                WaitAsync(teardownTimeout, cancellationToken).
                ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private bool HasIncompleteScanRetirementNoLock() =>
        retiringScan != null && (!retiringScan.TeardownTask.IsCompleted ||
            retiringScan.TeardownFaulted);

    // Keep the exact retired generation discoverable after a bounded caller
    // wait, including when Windows raised Stopped before the owner called Stop.
    // A new generation may replace it only after actual clean retirement.
    internal async Task<bool> EndScanAndDrainAsync(ulong scanGeneration)
    {
        ScanLifetime lifetime;
        lock (sync)
        {
            lifetime = activeScan ?? retiringScan;
            if (lifetime == null || lifetime.Generation != scanGeneration)
            {
                return false;
            }
        }

        _ = RetireScan(lifetime, stopWatcher: true);
        return await ObserveScanRetirementAsync(lifetime).ConfigureAwait(false);
    }

    private static async Task<bool> ObserveScanRetirementAsync(ScanLifetime lifetime)
    {
        try
        {
            await lifetime.TeardownTask.ConfigureAwait(false);
            return !lifetime.TeardownFaulted;
        }
        catch
        {
            return false;
        }
    }

    internal ValueTask<Switch2BluetoothWindowsOpenResult>
        OpenRememberedInputAsync(
            Switch2BluetoothCandidateObservation observation,
            CancellationToken cancellationToken = default) =>
        OpenRememberedAsync(observation, requireHdRumbleOutput: false,
            cancellationToken);

    /// <summary>
    /// Production full-duplex open. Input-only callers and their established
    /// tests keep the narrower method above; this path additionally requires
    /// the exact model-specific Switch2Connect vibration characteristic and
    /// write-without-response evidence before publishing the lease.
    /// </summary>
    internal ValueTask<Switch2BluetoothWindowsOpenResult>
        OpenRememberedDuplexAsync(
            Switch2BluetoothCandidateObservation observation,
            CancellationToken cancellationToken = default) =>
        OpenRememberedAsync(observation, requireHdRumbleOutput: true,
            cancellationToken);

    /// <summary>
    /// Leaves an unconsumed address capability in place but requires a later
    /// matching advertisement to publish the remembered candidate again. The
    /// production coordinator uses this when the prior exact slot token still
    /// exists in the shared registration table.
    /// </summary>
    internal bool TryDeferRememberedInputCandidate(
        in Switch2BluetoothCandidateObservation observation) =>
        registry.TryDeferRememberedConnectionCandidate(observation);

    internal bool TryRejectRememberedInputCandidate(
        in Switch2BluetoothCandidateObservation observation) =>
        registry.TryRejectRememberedConnectionCandidate(observation);

    internal ValueTask<Switch2BluetoothWindowsOpenResult> ReopenReleasedJoyConAsync(
        Switch2BluetoothWindowsInputLease predecessor, CancellationToken cancellationToken)
    {
        if (predecessor?.ReopenCapability is not ReopenCapability capability ||
            !ReferenceEquals(capability.Adapter, this) || !predecessor.HasReleasedResources ||
            predecessor.Admission.Model is not (Switch2ControllerModel.JoyCon2Left or Switch2ControllerModel.JoyCon2Right))
            return ValueTask.FromResult(Switch2BluetoothWindowsOpenResult.Failed(
                Switch2BluetoothWindowsOpenFailure.InvalidObservation));
        return OpenRememberedAsync(capability.Observation, true, cancellationToken, predecessor);
    }

    // Address stays private to this adapter, not in coordinator/UI identity.
    // The registry and the exact completed release make this one-shot.
    private sealed record ReopenCapability(Switch2BluetoothWindowsAdapter Adapter,
        ScanLifetime Lifetime, Switch2BluetoothCandidateObservation Observation,
        ulong Address, Switch2BluetoothWindowsAddressType AddressType);

    private async ValueTask<Switch2BluetoothWindowsOpenResult>
        OpenRememberedAsync(
            Switch2BluetoothCandidateObservation observation,
            bool requireHdRumbleOutput,
            CancellationToken cancellationToken,
            Switch2BluetoothWindowsInputLease predecessor = null)
    {
        Switch2BluetoothConnectionAdmission admission;
        ScanLifetime lifetime;
        ulong bluetoothAddress;
        Switch2BluetoothWindowsAddressType addressType;
        lock (sync)
        {
            lifetime = activeScan;
            if (lifetime == null || observation.ScanGeneration == 0 ||
                observation.ScanGeneration != lifetime.Generation)
            {
                return Switch2BluetoothWindowsOpenResult.Failed(
                    Switch2BluetoothWindowsOpenFailure.StaleScan);
            }
            if (predecessor != null)
            {
                if (predecessor.ReopenCapability is not ReopenCapability capability ||
                    !ReferenceEquals(capability.Adapter, this) ||
                    !ReferenceEquals(capability.Lifetime, lifetime) ||
                    !predecessor.HasReleasedResources ||
                    !registry.TryCreateReplacementAdmission(predecessor.Admission, out admission))
                    return Switch2BluetoothWindowsOpenResult.Failed(
                        Switch2BluetoothWindowsOpenFailure.InvalidObservation);
                bluetoothAddress = capability.Address;
                addressType = capability.AddressType;
            }
            else
            {
                if (observation.Disposition !=
                        Switch2BluetoothObservationDisposition.RememberedThisHost ||
                    !registry.TryCreateRememberedConnectionAdmission(observation,
                        out admission))
                {
                    return Switch2BluetoothWindowsOpenResult.Failed(
                        Switch2BluetoothWindowsOpenFailure.InvalidObservation);
                }
                if (!lifetime.TryConsumeAddress(observation.PeerToken,
                        out bluetoothAddress, out addressType))
                {
                    return Switch2BluetoothWindowsOpenResult.Failed(
                        Switch2BluetoothWindowsOpenFailure.AddressCapabilityUnavailable);
                }
            }
        }

        ulong reopenAddress = bluetoothAddress;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.Cancellation.Token);
        deadline.CancelAfter(startupTimeout);
        CancellationToken boundedToken = deadline.Token;
        ISwitch2BluetoothWindowsDevice device = null;
        ISwitch2BluetoothWindowsGattService service = null;
        ISwitch2BluetoothWindowsGattCharacteristic characteristic = null;
        ISwitch2BluetoothWindowsGattCharacteristic outputCharacteristic = null;
        ISwitch2BluetoothWindowsGattCharacteristic commandCharacteristic = null;
        ISwitch2BluetoothWindowsGattCharacteristic responseCharacteristic = null;
        Switch2BluetoothWindowsInputLease lease = null;
        Switch2PersistentPeerId persistentPeerId = default;
        bool throughputOptimizedRequested = false;
        Switch2BluetoothWindowsOpenFailure exceptionalFailure =
            Switch2BluetoothWindowsOpenFailure.DeviceOpenFailed;
        try
        {
            boundedToken.ThrowIfCancellationRequested();
            device = await AwaitBoundedAsync(
                platform.OpenDeviceAsync(bluetoothAddress, addressType,
                    boundedToken),
                boundedToken, DisposeDeviceQueryResult).ConfigureAwait(false);
            bluetoothAddress = 0;
            if (device == null)
            {
                // This completed open returned no native owner, so there is
                // no pending operation, callback or GATT lifetime to overlap.
                // A later advertisement can offer an explicit remembered-peer
                // retry. Timeouts and late results do not take this shortcut.
                _ = registry.TryReleaseRememberedConnection(admission);
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.DeviceOpenFailed,
                    ref device, ref service, ref characteristic);
            }
            bool identityRequired = admission.Model is
                Switch2ControllerModel.JoyCon2Left or
                Switch2ControllerModel.JoyCon2Right;
            bool identityDerived = identityDeriver != null &&
                identityDeriver.TryDerive(device, admission.Model,
                    admission.ProductId, out persistentPeerId);
            if (identityRequired && !identityDerived)
            {
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        PersistentPeerIdentityUnavailable,
                    ref device, ref service, ref characteristic);
            }
            if (predecessor != null && persistentPeerId != predecessor.PersistentPeerId)
                return FailedAndDispose(Switch2BluetoothWindowsOpenFailure.PersistentPeerIdentityUnavailable,
                    ref device, ref service, ref characteristic);
            boundedToken.ThrowIfCancellationRequested();

            exceptionalFailure = Switch2BluetoothWindowsOpenFailure.
                ServiceQueryFailed;
            Task<Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>> serviceTask =
                Switch2BluetoothServiceDiscovery.QueryAsync(device, Switch2InputCodec.ServiceUuid,
                    boundedToken).AsTask();
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService> serviceQuery;
            try
            {
                serviceQuery = await serviceTask.WaitAsync(boundedToken).
                    ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!serviceTask.IsCompleted)
            {
                ISwitch2BluetoothWindowsDevice retainedDevice = device;
                device = null;
                _ = DisposeLateResultAndOwnersAsync(serviceTask,
                    DisposeServiceQuery, retainedDevice);
                throw;
            }
            catch (OperationCanceledException)
            {
                DisposeCompletedResult(serviceTask, DisposeServiceQuery);
                throw;
            }
            if (!serviceQuery.Succeeded || serviceQuery.Items == null)
            {
                DisposeServiceQuery(serviceQuery);
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.ServiceQueryFailed,
                    ref device, ref service, ref characteristic);
            }
            if (serviceQuery.Items.Count != 1 ||
                serviceQuery.Items[0] == null)
            {
                DisposeServiceQuery(serviceQuery);
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        ServiceIdentityMismatch,
                    ref device, ref service, ref characteristic);
            }
            service = serviceQuery.Items[0];
            if (service.Uuid != Switch2InputCodec.ServiceUuid)
            {
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        ServiceIdentityMismatch,
                    ref device, ref service, ref characteristic);
            }
            boundedToken.ThrowIfCancellationRequested();
            // FromBluetoothAddressAsync alone need not connect. The uncached
            // query above initiates GATT; checking before it rejects a valid
            // first connection. Still reject a link lost during discovery
            // before requesting throughput or opening writable endpoints.
            if (!SafeIsConnected(device))
            {
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.DeviceDisconnected,
                    ref device, ref service, ref characteristic);
            }

            // Proven Switch2Connect/SDL ordering requests the faster host-link
            // preference only after uncached service discovery established the
            // GATT link. It is explicitly best effort because Windows 10 lacks
            // the API and a radio may reject it under connection pressure.
            try
            {
                throughputOptimizedRequested = device.
                    TryRequestThroughputOptimized();
            }
            catch
            {
                throughputOptimizedRequested = false;
            }

            if (requireHdRumbleOutput)
            {
                Guid outputUuid = Switch2BluetoothHdRumblePhysicalWriter.
                    CharacteristicUuidFor(admission.Model);
                if (outputUuid == Guid.Empty)
                {
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            OutputCharacteristicIdentityMismatch,
                        ref device, ref service, ref characteristic);
                }

                exceptionalFailure = Switch2BluetoothWindowsOpenFailure.
                    OutputCharacteristicQueryFailed;
                Task<Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattCharacteristic>> outputTask =
                    service.GetCharacteristicsForUuidUncachedAsync(outputUuid,
                        boundedToken).AsTask();
                Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattCharacteristic> outputQuery;
                try
                {
                    outputQuery = await outputTask.WaitAsync(boundedToken).
                        ConfigureAwait(false);
                }
                catch (OperationCanceledException) when
                    (!outputTask.IsCompleted)
                {
                    ISwitch2BluetoothWindowsGattService retainedService =
                        service;
                    ISwitch2BluetoothWindowsDevice retainedDevice = device;
                    service = null;
                    device = null;
                    _ = DisposeLateResultAndOwnersAsync(outputTask,
                        DisposeCharacteristicQuery, retainedService,
                        retainedDevice);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    DisposeCompletedResult(outputTask,
                        DisposeCharacteristicQuery);
                    throw;
                }
                if (!outputQuery.Succeeded || outputQuery.Items == null)
                {
                    DisposeCharacteristicQuery(outputQuery);
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            OutputCharacteristicQueryFailed,
                        ref device, ref service, ref characteristic);
                }
                if (outputQuery.Items.Count != 1 ||
                    outputQuery.Items[0] == null)
                {
                    DisposeCharacteristicQuery(outputQuery);
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            OutputCharacteristicIdentityMismatch,
                        ref device, ref service, ref characteristic);
                }
                outputCharacteristic = outputQuery.Items[0];
                if (outputCharacteristic.Uuid != outputUuid)
                {
                    SafeDispose(outputCharacteristic);
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            OutputCharacteristicIdentityMismatch,
                        ref device, ref service, ref characteristic);
                }
                Switch2GattProperty outputProperties = outputCharacteristic.
                    EvidencedProperties;
                if ((outputProperties &
                        Switch2GattProperty.WriteWithoutResponse) == 0 ||
                    (outputProperties & (Switch2GattProperty.Read |
                        Switch2GattProperty.Notify)) != 0)
                {
                    SafeDispose(outputCharacteristic);
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            OutputCharacteristicPropertiesMismatch,
                        ref device, ref service, ref characteristic);
                }
                boundedToken.ThrowIfCancellationRequested();
            }

            if (requireHdRumbleOutput)
            {
                exceptionalFailure = Switch2BluetoothWindowsOpenFailure.
                    CommandCharacteristicQueryFailed;
                Task<Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattCharacteristic>> commandTask =
                    service.GetCharacteristicsForUuidUncachedAsync(
                        Switch2BluetoothPlayerLedCodec.
                            CommandWriteCharacteristicUuid,
                        boundedToken).AsTask();
                Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattCharacteristic> commandQuery;
                try
                {
                    commandQuery = await commandTask.WaitAsync(boundedToken).
                        ConfigureAwait(false);
                }
                catch (OperationCanceledException) when
                    (!commandTask.IsCompleted)
                {
                    ISwitch2BluetoothWindowsGattCharacteristic retainedOutput =
                        outputCharacteristic;
                    ISwitch2BluetoothWindowsGattService retainedService =
                        service;
                    ISwitch2BluetoothWindowsDevice retainedDevice = device;
                    outputCharacteristic = null;
                    service = null;
                    device = null;
                    _ = DisposeLateResultAndOwnersAsync(commandTask,
                        DisposeCharacteristicQuery, retainedOutput,
                        retainedService, retainedDevice);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    DisposeCompletedResult(commandTask,
                        DisposeCharacteristicQuery);
                    throw;
                }
                if (!commandQuery.Succeeded || commandQuery.Items == null)
                {
                    DisposeCharacteristicQuery(commandQuery);
                    SafeDispose(outputCharacteristic);
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            CommandCharacteristicQueryFailed,
                        ref device, ref service, ref characteristic);
                }
                if (commandQuery.Items.Count != 1 ||
                    commandQuery.Items[0] == null)
                {
                    DisposeCharacteristicQuery(commandQuery);
                    SafeDispose(outputCharacteristic);
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            CommandCharacteristicIdentityMismatch,
                        ref device, ref service, ref characteristic);
                }
                commandCharacteristic = commandQuery.Items[0];
                Switch2GattProperty commandProperties = commandCharacteristic.
                    EvidencedProperties;
                bool commandCanWrite = (commandProperties &
                    (Switch2GattProperty.Write |
                        Switch2GattProperty.WriteWithoutResponse)) != 0;
                if (commandCharacteristic.Uuid !=
                        Switch2BluetoothPlayerLedCodec.
                            CommandWriteCharacteristicUuid ||
                    !commandCanWrite ||
                    (commandProperties & (Switch2GattProperty.Read |
                        Switch2GattProperty.Notify)) != 0)
                {
                    SafeDispose(commandCharacteristic);
                    SafeDispose(outputCharacteristic);
                    commandCharacteristic = null;
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            CommandChannelPropertiesMismatch,
                        ref device, ref service, ref characteristic);
                }

                exceptionalFailure = Switch2BluetoothWindowsOpenFailure.
                    ResponseCharacteristicQueryFailed;
                Task<Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattCharacteristic>> responseTask =
                    service.GetCharacteristicsForUuidUncachedAsync(
                        Switch2BluetoothPlayerLedCodec.
                            CommandResponseCharacteristicUuid,
                        boundedToken).AsTask();
                Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattCharacteristic> responseQuery;
                try
                {
                    responseQuery = await responseTask.WaitAsync(boundedToken).
                        ConfigureAwait(false);
                }
                catch (OperationCanceledException) when
                    (!responseTask.IsCompleted)
                {
                    ISwitch2BluetoothWindowsGattCharacteristic retainedCommand =
                        commandCharacteristic;
                    ISwitch2BluetoothWindowsGattCharacteristic retainedOutput =
                        outputCharacteristic;
                    ISwitch2BluetoothWindowsGattService retainedService =
                        service;
                    ISwitch2BluetoothWindowsDevice retainedDevice = device;
                    commandCharacteristic = null;
                    outputCharacteristic = null;
                    service = null;
                    device = null;
                    _ = DisposeLateResultAndOwnersAsync(responseTask,
                        DisposeCharacteristicQuery, retainedCommand,
                        retainedOutput, retainedService, retainedDevice);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    DisposeCompletedResult(responseTask,
                        DisposeCharacteristicQuery);
                    throw;
                }
                if (!responseQuery.Succeeded || responseQuery.Items == null)
                {
                    DisposeCharacteristicQuery(responseQuery);
                    SafeDispose(commandCharacteristic);
                    SafeDispose(outputCharacteristic);
                    commandCharacteristic = null;
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            ResponseCharacteristicQueryFailed,
                        ref device, ref service, ref characteristic);
                }
                if (responseQuery.Items.Count != 1 ||
                    responseQuery.Items[0] == null)
                {
                    DisposeCharacteristicQuery(responseQuery);
                    SafeDispose(commandCharacteristic);
                    SafeDispose(outputCharacteristic);
                    commandCharacteristic = null;
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            ResponseCharacteristicIdentityMismatch,
                        ref device, ref service, ref characteristic);
                }
                responseCharacteristic = responseQuery.Items[0];
                Switch2GattProperty responseProperties = responseCharacteristic.
                    EvidencedProperties;
                if (responseCharacteristic.Uuid !=
                        Switch2BluetoothPlayerLedCodec.
                            CommandResponseCharacteristicUuid ||
                    (responseProperties & Switch2GattProperty.Notify) == 0 ||
                    (responseProperties & (Switch2GattProperty.Write |
                        Switch2GattProperty.WriteWithoutResponse)) != 0)
                {
                    SafeDispose(responseCharacteristic);
                    SafeDispose(commandCharacteristic);
                    SafeDispose(outputCharacteristic);
                    responseCharacteristic = null;
                    commandCharacteristic = null;
                    outputCharacteristic = null;
                    return FailedAndDispose(
                        Switch2BluetoothWindowsOpenFailure.
                            CommandChannelPropertiesMismatch,
                        ref device, ref service, ref characteristic);
                }
                boundedToken.ThrowIfCancellationRequested();
            }

            exceptionalFailure = Switch2BluetoothWindowsOpenFailure.
                CharacteristicQueryFailed;
            Task<Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>>
                characteristicTask = service.
                    GetCharacteristicsForUuidUncachedAsync(
                        Switch2InputCodec.Common05CharacteristicUuid,
                        boundedToken).AsTask();
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>
                characteristicQuery;
            try
            {
                characteristicQuery = await characteristicTask.
                    WaitAsync(boundedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when
                (!characteristicTask.IsCompleted)
            {
                ISwitch2BluetoothWindowsGattCharacteristic retainedResponse =
                    responseCharacteristic;
                ISwitch2BluetoothWindowsGattCharacteristic retainedCommand =
                    commandCharacteristic;
                ISwitch2BluetoothWindowsGattCharacteristic retainedOutput =
                    outputCharacteristic;
                ISwitch2BluetoothWindowsGattService retainedService = service;
                ISwitch2BluetoothWindowsDevice retainedDevice = device;
                responseCharacteristic = null;
                commandCharacteristic = null;
                outputCharacteristic = null;
                service = null;
                device = null;
                _ = DisposeLateResultAndOwnersAsync(characteristicTask,
                    DisposeCharacteristicQuery, retainedResponse,
                    retainedCommand, retainedOutput,
                    retainedService, retainedDevice);
                throw;
            }
            catch (OperationCanceledException)
            {
                DisposeCompletedResult(characteristicTask,
                    DisposeCharacteristicQuery);
                throw;
            }
            if (!characteristicQuery.Succeeded ||
                characteristicQuery.Items == null)
            {
                DisposeCharacteristicQuery(characteristicQuery);
                SafeDispose(responseCharacteristic);
                SafeDispose(commandCharacteristic);
                SafeDispose(outputCharacteristic);
                responseCharacteristic = null;
                commandCharacteristic = null;
                outputCharacteristic = null;
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        CharacteristicQueryFailed,
                    ref device, ref service, ref characteristic);
            }
            if (characteristicQuery.Items.Count != 1 ||
                characteristicQuery.Items[0] == null)
            {
                DisposeCharacteristicQuery(characteristicQuery);
                SafeDispose(responseCharacteristic);
                SafeDispose(commandCharacteristic);
                SafeDispose(outputCharacteristic);
                responseCharacteristic = null;
                commandCharacteristic = null;
                outputCharacteristic = null;
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        CharacteristicIdentityMismatch,
                    ref device, ref service, ref characteristic);
            }
            characteristic = characteristicQuery.Items[0];
            if (characteristic.Uuid !=
                Switch2InputCodec.Common05CharacteristicUuid)
            {
                SafeDispose(responseCharacteristic);
                SafeDispose(commandCharacteristic);
                SafeDispose(outputCharacteristic);
                responseCharacteristic = null;
                commandCharacteristic = null;
                outputCharacteristic = null;
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        CharacteristicIdentityMismatch,
                    ref device, ref service, ref characteristic);
            }
            const Switch2GattProperty exactProperties =
                Switch2GattProperty.Read | Switch2GattProperty.Notify;
            if (!characteristic.HasOnlyReadAndNotifyProperties ||
                characteristic.EvidencedProperties != exactProperties)
            {
                SafeDispose(responseCharacteristic);
                SafeDispose(commandCharacteristic);
                SafeDispose(outputCharacteristic);
                responseCharacteristic = null;
                commandCharacteristic = null;
                outputCharacteristic = null;
                return FailedAndDispose(
                    Switch2BluetoothWindowsOpenFailure.
                        CharacteristicPropertiesMismatch,
                    ref device, ref service, ref characteristic);
            }
            boundedToken.ThrowIfCancellationRequested();

            exceptionalFailure = Switch2BluetoothWindowsOpenFailure.
                NotificationSetupFailed;
            lease = new Switch2BluetoothWindowsInputLease(admission,
                registry,
                device, service, characteristic, outputCharacteristic,
                commandCharacteristic, responseCharacteristic,
                persistentPeerId, throughputOptimizedRequested,
                teardownTimeout, identityRequired ?
                    new ReopenCapability(this, lifetime, observation, reopenAddress, addressType) : null);
            device = null;
            service = null;
            characteristic = null;
            outputCharacteristic = null;
            commandCharacteristic = null;
            responseCharacteristic = null;
            if (!await lease.PrepareAsync(boundedToken).ConfigureAwait(false))
            {
                bool sensorInitializationFailed = lease.SensorInitializationFailure !=
                    Switch2BluetoothSensorInitializationFailure.None;
                bool commandResponseFailed = lease.
                    PlayerLedPreparationFailed;
                await lease.BeginAndWaitForBoundedTeardownAsync(
                    CancellationToken.None).ConfigureAwait(false);
                return Switch2BluetoothWindowsOpenResult.Failed(
                    sensorInitializationFailed ?
                        Switch2BluetoothWindowsOpenFailure.SensorInitializationFailed :
                    commandResponseFailed ?
                        Switch2BluetoothWindowsOpenFailure.
                            CommandResponseSubscriptionFailed :
                        Switch2BluetoothWindowsOpenFailure.
                            NotificationSetupFailed, lease.SensorInitializationFailure);
            }
            boundedToken.ThrowIfCancellationRequested();

            lock (sync)
            {
                if (!ReferenceEquals(activeScan, lifetime) ||
                    lifetime.Generation != observation.ScanGeneration)
                {
                    _ = lease.BeginAndWaitForBoundedTeardownAsync(
                        CancellationToken.None);
                    return Switch2BluetoothWindowsOpenResult.Failed(
                        Switch2BluetoothWindowsOpenFailure.StaleScan);
                }
            }

            // This is the cancellation linearization point. Cancellation after
            // it belongs to the caller-owned successful lease; cancellation at
            // or before it retires the prepared CCCD lifetime below.
            boundedToken.ThrowIfCancellationRequested();
            Switch2BluetoothWindowsInputLease publishedLease = lease;
            lease = null;
            return Switch2BluetoothWindowsOpenResult.Success(publishedLease);
        }
        catch (OperationCanceledException)
        {
            RetireLeaseNoWait(ref lease);
            SafeDispose(responseCharacteristic);
            SafeDispose(commandCharacteristic);
            SafeDispose(outputCharacteristic);
            responseCharacteristic = null;
            commandCharacteristic = null;
            outputCharacteristic = null;
            Switch2BluetoothWindowsOpenFailure failure = boundedToken.
                IsCancellationRequested ? ClassifyCancellation(lifetime,
                    cancellationToken) : exceptionalFailure;
            return FailedAndDispose(failure, ref device, ref service,
                ref characteristic);
        }
        catch (TimeoutException)
        {
            RetireLeaseNoWait(ref lease);
            SafeDispose(responseCharacteristic);
            SafeDispose(commandCharacteristic);
            SafeDispose(outputCharacteristic);
            responseCharacteristic = null;
            commandCharacteristic = null;
            outputCharacteristic = null;
            // Token-based WaitAsync reports the configured deadline as
            // OperationCanceledException. A TimeoutException here came from
            // the platform operation itself and belongs to that startup stage.
            return FailedAndDispose(exceptionalFailure, ref device,
                ref service, ref characteristic);
        }
        catch
        {
            RetireLeaseNoWait(ref lease);
            SafeDispose(responseCharacteristic);
            SafeDispose(commandCharacteristic);
            SafeDispose(outputCharacteristic);
            responseCharacteristic = null;
            commandCharacteristic = null;
            outputCharacteristic = null;
            return FailedAndDispose(exceptionalFailure,
                ref device, ref service, ref characteristic);
        }
        finally
        {
            bluetoothAddress = 0;
        }
    }

    internal async ValueTask<Switch2BluetoothWindowsAssociationResult>
        AssociateAsync(Switch2BluetoothCandidateObservation observation,
            CancellationToken cancellationToken = default)
    {
        ScanLifetime lifetime;
        ulong bluetoothAddress;
        Switch2BluetoothWindowsAddressType addressType;
        byte[] localHostAddress;
        lock (sync)
        {
            lifetime = activeScan;
            if (lifetime == null || observation.ScanGeneration == 0 ||
                observation.ScanGeneration != lifetime.Generation)
            {
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.StaleScan);
            }
            if (observation.Disposition !=
                    Switch2BluetoothObservationDisposition.
                        RequiresExplicitAssociation ||
                !registry.TryCreateAssociationConnectionAdmission(observation,
                    out _))
            {
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.
                        InvalidObservation);
            }
            if (!lifetime.TryConsumeAddress(observation.PeerToken,
                    out bluetoothAddress, out addressType))
            {
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.
                        AddressCapabilityUnavailable);
            }
            localHostAddress = (byte[])lifetime.SelectedHostAddress.Clone();
        }

        try
        {
            Switch2BluetoothWindowsAssociationResult result = await
                Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
                platform, bluetoothAddress, addressType, localHostAddress,
                startupTimeout, lifetime.Cancellation.Token,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                lock (sync)
                {
                    if (ReferenceEquals(activeScan, lifetime) &&
                        observation.ScanGeneration == lifetime.Generation)
                    {
                        registry.TryRejectAssociation(observation);
                    }
                }
                return result;
            }

            lock (sync)
            {
                if (!ReferenceEquals(activeScan, lifetime) ||
                    observation.ScanGeneration != lifetime.Generation)
                {
                    return Switch2BluetoothWindowsAssociationResult.Failed(
                        Switch2BluetoothWindowsAssociationFailure.StaleScan,
                        Switch2BluetoothAssociationStep.Commit);
                }
                if (!registry.TryCommitSuccessfulAssociation(observation))
                {
                    return Switch2BluetoothWindowsAssociationResult.Failed(
                        Switch2BluetoothWindowsAssociationFailure.
                            PostCommitPromotionRejected,
                        Switch2BluetoothAssociationStep.Commit);
                }
            }
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(localHostAddress);
        }
    }

    private void OnAdvertisement(ScanLifetime lifetime,
        ulong bluetoothAddress, Switch2BluetoothWindowsAddressType addressType,
        ushort companyId, byte companySections, ReadOnlySpan<byte>
            manufacturerValue, long observedQpc)
    {
        if (!lifetime.CallbackGate.TryEnter())
        {
            return;
        }

        Switch2BluetoothCandidateObservation observation = default;
        Action<Switch2BluetoothCandidateObservation> callback = null;
        bool publish = false;
        try
        {
            lock (sync)
            {
                if (!ReferenceEquals(activeScan, lifetime) ||
                    !lifetime.StartPublished ||
                    bluetoothAddress == 0 ||
                    (bluetoothAddress & ~BluetoothAddressMask) != 0 ||
                    addressType is < Switch2BluetoothWindowsAddressType.
                        Unspecified or > Switch2BluetoothWindowsAddressType.
                        Random ||
                    companySections != 1 ||
                    !Switch2AdvertisementCodec.TryDecode(companyId,
                        manufacturerValue, lifetime.SelectedHostAddress,
                        out Switch2Advertisement advertisement) ||
                    !Switch2BluetoothPeerToken.TryDerive(lifetime.SessionKey,
                        lifetime.Generation, bluetoothAddress,
                        out Switch2BluetoothPeerToken peerToken))
                {
                    return;
                }

                observation = registry.Observe(lifetime.Generation, peerToken,
                    observedQpc, advertisement);
                if (observation.IsConnectionCandidate)
                {
                    if (!lifetime.TryAddAddress(peerToken, bluetoothAddress,
                            addressType))
                    {
                        return;
                    }
                }
                callback = lifetime.Candidate;
                publish = callback != null;
            }

            if (publish)
            {
                try
                {
                    callback(observation);
                }
                catch
                {
                    Interlocked.Increment(ref candidateCallbackFailureCount);
                }
            }
        }
        finally
        {
            lifetime.CallbackGate.Exit();
        }
    }

    private void OnWatcherStopped(ScanLifetime lifetime)
    {
        if (!lifetime.CallbackGate.TryEnter())
        {
            return;
        }
        try
        {
            RetireScan(lifetime, stopWatcher: false);
        }
        finally
        {
            lifetime.CallbackGate.Exit();
        }
    }

    private Task RetireScan(ScanLifetime lifetime, bool stopWatcher)
    {
        lock (sync)
        {
            if (!ReferenceEquals(activeScan, lifetime))
            {
                return lifetime.TeardownTask;
            }

            activeScan = null;
            retiringScan = lifetime;
            registry.TryEndScan(lifetime.Generation);
            lifetime.RetireSensitiveState();
            lifetime.CallbackGate.Retire();
        }

        try
        {
            // Cancellation may synchronously invoke platform registrations.
            // Never run that external work while holding the adapter lock.
            lifetime.Cancellation.Cancel();
        }
        catch
        {
            lifetime.TeardownFaulted = true;
        }

        if (stopWatcher)
        {
            try
            {
                lock (lifetime.PlatformLifecycleSync)
                {
                    lifetime.Watcher.Stop();
                }
            }
            catch
            {
                lifetime.TeardownFaulted = true;
            }
        }

        _ = FinalizeWatcherAsync(lifetime);
        return lifetime.TeardownTask;
    }

    private void RetireUnpublishedWatcher(ScanLifetime lifetime)
    {
        lock (sync)
        {
            // Failed configuration may already have installed native handlers.
            // Retain that cleanup fence too; absence from activeScan is not
            // proof that the unpublished watcher has finished draining.
            if (activeScan == null && !HasIncompleteScanRetirementNoLock())
                retiringScan = lifetime;
        }
        lifetime.RetireSensitiveState();
        lifetime.CallbackGate.Retire();
        try
        {
            lock (lifetime.PlatformLifecycleSync)
            {
                lifetime.Watcher.Stop();
            }
        }
        catch
        {
            lifetime.TeardownFaulted = true;
        }
        _ = FinalizeWatcherAsync(lifetime);
    }

    private static async Task FinalizeWatcherAsync(ScanLifetime lifetime)
    {
        bool detached = false;
        try
        {
            Task platformDrain;
            lock (lifetime.PlatformLifecycleSync)
            {
                platformDrain = lifetime.Watcher.
                    DetachHandlersAndDrainAsync();
            }
            await Task.WhenAll(platformDrain,
                lifetime.CallbackGate.Drained).ConfigureAwait(false);
            detached = true;
        }
        catch
        {
            lifetime.TeardownFaulted = true;
            // Handler removal/drain is ambiguous. Keep the watcher, delegates,
            // and scan lifetime strongly retained instead of disposing under a
            // possible late WinRT callback.
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return;
        }

        if (detached)
        {
            try
            {
                lock (lifetime.PlatformLifecycleSync)
                {
                    lifetime.Watcher.Dispose();
                }
            }
            catch
            {
                lifetime.TeardownFaulted = true;
            }
        }
        try
        {
            lifetime.Cancellation.Dispose();
        }
        catch
        {
            lifetime.TeardownFaulted = true;
        }
        lifetime.TeardownCompletion.TrySetResult(true);
    }

    private Switch2BluetoothWindowsOpenFailure ClassifyCancellation(
        ScanLifetime lifetime, CancellationToken callerToken)
    {
        lock (sync)
        {
            if (!ReferenceEquals(activeScan, lifetime))
            {
                return Switch2BluetoothWindowsOpenFailure.StaleScan;
            }
        }
        return callerToken.IsCancellationRequested ?
            Switch2BluetoothWindowsOpenFailure.Cancelled :
            Switch2BluetoothWindowsOpenFailure.StartupTimedOut;
    }

    private static async ValueTask<T> AwaitBoundedAsync<T>(
        ValueTask<T> operation, CancellationToken boundedToken,
        Action<T> disposeLateResult)
    {
        Task<T> task = operation.AsTask();
        try
        {
            return await task.WaitAsync(boundedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            _ = DisposeLateResultAsync(task, disposeLateResult);
            throw;
        }
        catch (OperationCanceledException)
        {
            // WaitAsync cancellation and source completion can race. If the
            // source won with a handle, dispose it here instead of letting the
            // completed result escape through the cancellation path.
            DisposeCompletedResult(task, disposeLateResult);
            throw;
        }
    }

    private static void DisposeCompletedResult<T>(Task<T> task,
        Action<T> disposeResult)
    {
        if (task.Status != TaskStatus.RanToCompletion)
        {
            return;
        }
        try
        {
            disposeResult(task.Result);
        }
        catch
        {
        }
    }

    private static async Task DisposeLateResultAsync<T>(Task<T> task,
        Action<T> disposeResult)
    {
        try
        {
            T result = await task.ConfigureAwait(false);
            disposeResult(result);
        }
        catch
        {
            // The operation owns its own failed result. Keeping this continuation
            // alive prevents a late successful handle from escaping teardown.
        }
    }

    private static async Task DisposeLateResultAndOwnersAsync<T>(Task<T> task,
        Action<T> disposeResult, params IDisposable[] owners)
    {
        try
        {
            T result = await task.ConfigureAwait(false);
            disposeResult(result);
        }
        catch
        {
            // The failed operation has no result to release.
        }
        finally
        {
            // Owners stay alive until the non-cooperative operation no longer
            // uses them. Query results are released before their parent graph.
            if (owners != null)
            {
                for (int index = 0; index < owners.Length; index++)
                {
                    SafeDispose(owners[index]);
                }
            }
        }
    }

    private static void RetireLeaseNoWait(
        ref Switch2BluetoothWindowsInputLease lease)
    {
        Switch2BluetoothWindowsInputLease retained = lease;
        lease = null;
        if (retained != null)
        {
            _ = retained.BeginAndWaitForBoundedTeardownAsync(
                CancellationToken.None);
        }
    }

    private static Switch2BluetoothWindowsOpenResult FailedAndDispose(
        Switch2BluetoothWindowsOpenFailure failure,
        ref ISwitch2BluetoothWindowsDevice device,
        ref ISwitch2BluetoothWindowsGattService service,
        ref ISwitch2BluetoothWindowsGattCharacteristic characteristic)
    {
        SafeDispose(characteristic);
        SafeDispose(service);
        SafeDispose(device);
        characteristic = null;
        service = null;
        device = null;
        return Switch2BluetoothWindowsOpenResult.Failed(failure);
    }

    private static void DisposeDeviceQueryResult(
        ISwitch2BluetoothWindowsDevice device) => SafeDispose(device);

    private static void DisposeServiceQuery(
        Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService> query)
    {
        if (query.Items == null)
        {
            return;
        }
        for (int index = 0; index < query.Items.Count; index++)
        {
            SafeDispose(query.Items[index]);
        }
    }

    private static void DisposeCharacteristicQuery(
        Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic> query)
    {
        if (query.Items == null)
        {
            return;
        }
        for (int index = 0; index < query.Items.Count; index++)
        {
            SafeDispose(query.Items[index]);
        }
    }

    private static bool SafeIsConnected(
        ISwitch2BluetoothWindowsDevice device)
    {
        try
        {
            return device.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private static void SafeDispose(IDisposable value)
    {
        if (value == null)
        {
            return;
        }
        try
        {
            value.Dispose();
        }
        catch
        {
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        byte aggregate = 0;
        foreach (byte current in value)
        {
            aggregate |= current;
        }
        return aggregate == 0;
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameter)
    {
        double milliseconds = timeout.TotalMilliseconds;
        if (!double.IsFinite(milliseconds) ||
            milliseconds < MinimumTimeoutMilliseconds ||
            milliseconds > MaximumTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(parameter);
        }
    }

    private sealed class ScanLifetime
    {
        private readonly AddressCapability[] addresses;

        internal ScanLifetime(ulong generation,
            ReadOnlySpan<byte> selectedHostAddress, int capacity,
            ISwitch2BluetoothWindowsAdvertisementWatcher watcher,
            Action<Switch2BluetoothCandidateObservation> candidate)
        {
            Generation = generation;
            SessionKey = new byte[Switch2BluetoothPeerToken.SessionKeyLength];
            RandomNumberGenerator.Fill(SessionKey);
            SelectedHostAddress = selectedHostAddress.ToArray();
            addresses = new AddressCapability[capacity];
            Watcher = watcher;
            Candidate = candidate;
        }

        internal ulong Generation { get; }
        internal byte[] SessionKey { get; }
        internal byte[] SelectedHostAddress { get; }
        internal ISwitch2BluetoothWindowsAdvertisementWatcher Watcher { get; }
        internal Action<Switch2BluetoothCandidateObservation> Candidate
        {
            get;
            private set;
        }
        internal Switch2CallbackDrainGate CallbackGate { get; } = new();
        internal object PlatformLifecycleSync { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Switch2BluetoothWindowsAdvertisementHandler Received { get; set; }
        internal Switch2BluetoothWindowsWatcherStoppedHandler Stopped { get; set; }
        internal TaskCompletionSource<bool> TeardownCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task TeardownTask => TeardownCompletion.Task;
        internal bool TeardownFaulted { get; set; }
        internal bool StartPublished { get; set; }

        internal bool TryAddAddress(Switch2BluetoothPeerToken token,
            ulong bluetoothAddress,
            Switch2BluetoothWindowsAddressType addressType)
        {
            int empty = -1;
            for (int index = 0; index < addresses.Length; index++)
            {
                ref AddressCapability current = ref addresses[index];
                if (!current.InUse)
                {
                    if (empty < 0)
                    {
                        empty = index;
                    }
                    continue;
                }
                if (current.Token == token)
                {
                    if (!current.Consumed)
                    {
                        return current.BluetoothAddress == bluetoothAddress &&
                            current.AddressType == addressType;
                    }

                    // OnAdvertisement calls this method only for a fresh
                    // registry-issued connection candidate. The sole way the
                    // same scan token can become a candidate after consuming
                    // its address is exact clean-association promotion or an
                    // exact clean input-lease release. Rearm from the new OS
                    // observation; duplicates never reach this branch.
                    if (current.BluetoothAddress != 0)
                    {
                        return false;
                    }
                    current.BluetoothAddress = bluetoothAddress;
                    current.AddressType = addressType;
                    current.Consumed = false;
                    return true;
                }
            }
            if (empty < 0)
            {
                return false;
            }
            addresses[empty] = new AddressCapability(token,
                bluetoothAddress, addressType);
            return true;
        }

        internal bool TryConsumeAddress(Switch2BluetoothPeerToken token,
            out ulong bluetoothAddress,
            out Switch2BluetoothWindowsAddressType addressType)
        {
            for (int index = 0; index < addresses.Length; index++)
            {
                ref AddressCapability current = ref addresses[index];
                if (!current.InUse || current.Token != token ||
                    current.Consumed)
                {
                    continue;
                }
                bluetoothAddress = current.BluetoothAddress;
                addressType = current.AddressType;
                current.BluetoothAddress = 0;
                current.AddressType = default;
                current.Consumed = true;
                return bluetoothAddress != 0;
            }
            bluetoothAddress = 0;
            addressType = default;
            return false;
        }

        internal void RetireSensitiveState()
        {
            CryptographicOperations.ZeroMemory(SessionKey);
            CryptographicOperations.ZeroMemory(SelectedHostAddress);
            Array.Clear(addresses, 0, addresses.Length);
            Received = null;
            Stopped = null;
            Candidate = null;
        }

        private struct AddressCapability
        {
            internal AddressCapability(Switch2BluetoothPeerToken token,
                ulong bluetoothAddress,
                Switch2BluetoothWindowsAddressType addressType)
            {
                Token = token;
                BluetoothAddress = bluetoothAddress;
                AddressType = addressType;
                InUse = true;
                Consumed = false;
            }

            internal Switch2BluetoothPeerToken Token;
            internal ulong BluetoothAddress;
            internal Switch2BluetoothWindowsAddressType AddressType;
            internal bool InUse;
            internal bool Consumed;
        }
    }
}

internal enum Switch2BluetoothCalibrationReadFailure : byte
{
    None = 0,
    InvalidLifetime,
    CommandChannelUnavailable,
    CommandRejected,
    SnapshotRejected,
}

internal readonly struct Switch2BluetoothCalibrationReadResult
{
    private Switch2BluetoothCalibrationReadResult(
        Switch2BluetoothCalibrationReadFailure failure,
        Switch2BluetoothMemoryReadChannelFailure commandFailure,
        Switch2BluetoothMemoryReadChannelFailure optionalUserCommandFailure,
        in Switch2InputCalibrationSnapshot calibration)
    {
        Failure = failure;
        CommandFailure = commandFailure;
        OptionalUserCommandFailure = optionalUserCommandFailure;
        Calibration = calibration;
    }

    internal Switch2BluetoothCalibrationReadFailure Failure { get; }

    internal Switch2BluetoothMemoryReadChannelFailure CommandFailure
        { get; }

    internal Switch2BluetoothMemoryReadChannelFailure
        OptionalUserCommandFailure { get; }

    internal Switch2InputCalibrationSnapshot Calibration { get; }

    internal bool Succeeded => Failure ==
        Switch2BluetoothCalibrationReadFailure.None;

    internal static Switch2BluetoothCalibrationReadResult Success(
        in Switch2InputCalibrationSnapshot calibration,
        Switch2BluetoothMemoryReadChannelFailure optionalUserCommandFailure) =>
        new(Switch2BluetoothCalibrationReadFailure.None,
            Switch2BluetoothMemoryReadChannelFailure.None,
            optionalUserCommandFailure, calibration);

    internal static Switch2BluetoothCalibrationReadResult Failed(
        Switch2BluetoothCalibrationReadFailure failure,
        Switch2BluetoothMemoryReadChannelFailure commandFailure =
            Switch2BluetoothMemoryReadChannelFailure.None) => new(failure,
                commandFailure,
                Switch2BluetoothMemoryReadChannelFailure.None, default);
}

/// <summary>
/// Prepared Common05 lease. Notification enablement is complete before this
/// object is returned. TrySubscribe merely publishes the transport-generation
/// callbacks to the prepared handler and therefore performs no async work on
/// the input owner path.
/// </summary>
internal sealed class Switch2BluetoothWindowsInputLease :
    ISwitch2BluetoothInputLease, ISwitch2BluetoothInputLeaseReleaseProof,
    ISwitch2BluetoothHdRumbleBindableTransportLease,
    ISwitch2BluetoothDisconnectedOutputProof,
    ISwitch2BluetoothPlayerLedTransportLease, IAsyncDisposable
{
    private const int HdRumbleWriteTimeoutMilliseconds = 100;
    private const int PlayerLedCommandTimeoutMilliseconds = 2_000;

    private readonly object sync = new();
    private readonly Switch2BluetoothCandidateRegistry candidateRegistry;
    private readonly ISwitch2BluetoothWindowsDevice device;
    private readonly ISwitch2BluetoothWindowsGattService service;
    private readonly ISwitch2BluetoothWindowsGattCharacteristic characteristic;
    private readonly ISwitch2BluetoothWindowsGattCharacteristic
        outputCharacteristic;
    private readonly ISwitch2BluetoothWindowsGattCharacteristic
        commandCharacteristic;
    private readonly ISwitch2BluetoothWindowsGattCharacteristic
        responseCharacteristic;
    private readonly Switch2BluetoothPlayerLedCommandChannel playerLedChannel;
    private readonly TimeSpan teardownTimeout;
    private readonly Switch2CallbackDrainGate callbackGate = new();
    private readonly ManualResetEventSlim outputWritesIdle = new(true);
    private readonly Switch2BluetoothWindowsValueChangedHandler valueChanged;
    private readonly Switch2BluetoothWindowsDisconnectedHandler disconnected;
    private LeaseState state;
    private ulong transportGeneration;
    private Switch2BluetoothInputNotification notification;
    private Switch2BluetoothInputDisconnected disconnectNotification;
    private Task<bool> notificationEnableTask;
    private Task<bool> boundedTeardown;
    private Task<bool> resourceRelease;
    private Switch2ControllerModel outputModel;
    private ulong outputDeviceGeneration;
    private ulong outputTransportGeneration;
    private bool outputBound;
    private bool disconnectObserved;
    private int outputWriteActive;
    private Task playerLedOperation = Task.CompletedTask;
    private bool playerLedOperationActive;
    private bool playerLedRequestPending;
    private byte pendingPlayerLedPattern;
    private bool playerLedPreparationFailed;
    private Switch2BluetoothPlayerLedChannelFailure lastPlayerLedFailure;
    internal Switch2BluetoothSensorInitializationFailure SensorInitializationFailure
        { get; private set; }
    internal bool JoyConSensorsInitialized { get; private set; }

    internal Switch2BluetoothWindowsInputLease(
        Switch2BluetoothConnectionAdmission admission,
        Switch2BluetoothCandidateRegistry candidateRegistry,
        ISwitch2BluetoothWindowsDevice device,
        ISwitch2BluetoothWindowsGattService service,
        ISwitch2BluetoothWindowsGattCharacteristic characteristic,
        ISwitch2BluetoothWindowsGattCharacteristic outputCharacteristic,
        ISwitch2BluetoothWindowsGattCharacteristic commandCharacteristic,
        ISwitch2BluetoothWindowsGattCharacteristic responseCharacteristic,
        Switch2PersistentPeerId persistentPeerId,
        bool throughputOptimizedRequested,
        TimeSpan teardownTimeout, object reopenCapability = null)
    {
        Admission = admission;
        ReopenCapability = reopenCapability;
        this.candidateRegistry = candidateRegistry ??
            throw new ArgumentNullException(nameof(candidateRegistry));
        this.device = device;
        this.service = service;
        this.characteristic = characteristic;
        this.outputCharacteristic = outputCharacteristic;
        this.commandCharacteristic = commandCharacteristic;
        this.responseCharacteristic = responseCharacteristic;
        bool isJoyCon = admission.Model is
            Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.JoyCon2Right;
        if (isJoyCon && !persistentPeerId.IsValid)
        {
            throw new ArgumentException(
                "Persistent peer identity must exist for Joy-Con leases only.",
                nameof(persistentPeerId));
        }
        PersistentPeerId = persistentPeerId;
        ThroughputOptimizedRequested = throughputOptimizedRequested;
        if ((commandCharacteristic == null) != (responseCharacteristic == null))
        {
            throw new ArgumentException(
                "The player-LED command and response edges are indivisible.");
        }
        if (commandCharacteristic != null)
        {
            playerLedChannel = new Switch2BluetoothPlayerLedCommandChannel(
                commandCharacteristic, responseCharacteristic);
        }
        this.teardownTimeout = teardownTimeout;
        GattSnapshot = new Switch2BluetoothGattSnapshot(
            admission.ScanGeneration, 1, 1, service.Uuid,
            characteristic.Uuid, characteristic.EvidencedProperties);
        valueChanged = OnValueChanged;
        disconnected = OnDisconnected;
    }

    public Switch2BluetoothConnectionAdmission Admission { get; }

    internal object ReopenCapability { get; }
    internal bool HasDisconnected => Volatile.Read(ref disconnectObserved);
    internal bool HasReleasedResources
    {
        get { lock (sync) { return resourceRelease?.IsCompletedSuccessfully == true && resourceRelease.Result; } }
    }

    internal Switch2PersistentPeerId PersistentPeerId { get; }

    internal bool ThroughputOptimizedRequested { get; }

    public Switch2BluetoothGattSnapshot GattSnapshot { get; }

    public bool HasHdRumbleOutput => outputCharacteristic != null;

    public bool HasPlayerLedOutput => playerLedChannel != null;

    internal bool PlayerLedPreparationFailed
    {
        get { lock (sync) { return playerLedPreparationFailed; } }
    }

    internal Switch2BluetoothPlayerLedChannelFailure LastPlayerLedFailure
    {
        get { lock (sync) { return lastPlayerLedFailure; } }
    }

    internal Task PlayerLedOperation
    {
        get { lock (sync) { return playerLedOperation; } }
    }

    /// <summary>
    /// Reads immutable factory and optional read-only user stick records before
    /// input publication. A marked, adoptable user record overrides factory;
    /// an absent, malformed, unadoptable, or unreadable optional record leaves
    /// the factory result intact.
    /// The persistent command/response owner serializes these exchanges with
    /// later LED commands, so this cannot create a second GATT writer.
    /// </summary>
    internal async ValueTask<Switch2BluetoothCalibrationReadResult>
        ReadCalibrationAsync(Switch2ControllerModel model,
            ulong deviceGeneration, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (state != LeaseState.Prepared || model != Admission.Model ||
                deviceGeneration == 0)
            {
                return Switch2BluetoothCalibrationReadResult.Failed(
                    Switch2BluetoothCalibrationReadFailure.
                        InvalidLifetime);
            }
            if (playerLedChannel == null)
            {
                return Switch2BluetoothCalibrationReadResult.Failed(
                    Switch2BluetoothCalibrationReadFailure.
                        CommandChannelUnavailable);
            }
        }

        byte[] leftRecord = Array.Empty<byte>();
        byte[] rightRecord = Array.Empty<byte>();
        byte[] leftUserRecord = Array.Empty<byte>();
        byte[] rightUserRecord = Array.Empty<byte>();
        Switch2BluetoothMemoryReadChannelFailure optionalUserFailure =
            Switch2BluetoothMemoryReadChannelFailure.None;
        if (Switch2CalibrationCodec.TryGetFactoryStickMetadata(model,
                Switch2StickSide.Left,
                out Switch2FactoryCalibrationMetadata leftMetadata))
        {
            Switch2BluetoothMemoryReadChannelResult read = await
                playerLedChannel.ReadMemoryAsync(leftMetadata.Length,
                    leftMetadata.Address, cancellationToken).
                    ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return Switch2BluetoothCalibrationReadResult.Failed(
                    Switch2BluetoothCalibrationReadFailure.
                        CommandRejected, read.Failure);
            }
            leftRecord = read.Value.ToArray();
        }
        if (Switch2CalibrationCodec.TryGetFactoryStickMetadata(model,
                Switch2StickSide.Right,
                out Switch2FactoryCalibrationMetadata rightMetadata))
        {
            Switch2BluetoothMemoryReadChannelResult read = await
                playerLedChannel.ReadMemoryAsync(rightMetadata.Length,
                    rightMetadata.Address, cancellationToken).
                    ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return Switch2BluetoothCalibrationReadResult.Failed(
                    Switch2BluetoothCalibrationReadFailure.
                        CommandRejected, read.Failure);
            }
            rightRecord = read.Value.ToArray();
        }

        if (Switch2CalibrationCodec.TryGetLiveUserStickMetadata(model,
                Switch2StickSide.Left,
                out Switch2UserCalibrationMetadata leftUserMetadata))
        {
            Switch2BluetoothMemoryReadChannelResult read = await
                playerLedChannel.ReadMemoryAsync(leftUserMetadata.Length,
                    leftUserMetadata.Address, cancellationToken).
                    ConfigureAwait(false);
            if (read.Succeeded)
            {
                leftUserRecord = read.Value.ToArray();
            }
            else
            {
                optionalUserFailure = read.Failure;
            }
        }
        if (Switch2CalibrationCodec.TryGetLiveUserStickMetadata(model,
                Switch2StickSide.Right,
                out Switch2UserCalibrationMetadata rightUserMetadata))
        {
            Switch2BluetoothMemoryReadChannelResult read = await
                playerLedChannel.ReadMemoryAsync(rightUserMetadata.Length,
                    rightUserMetadata.Address, cancellationToken).
                    ConfigureAwait(false);
            if (read.Succeeded)
            {
                rightUserRecord = read.Value.ToArray();
            }
            else if (optionalUserFailure ==
                     Switch2BluetoothMemoryReadChannelFailure.None)
            {
                optionalUserFailure = read.Failure;
            }
        }

        if (!Switch2InputCalibrationSnapshot.TryCreate(model,
                deviceGeneration, leftRecord, rightRecord, leftUserRecord,
                rightUserRecord,
                out Switch2InputCalibrationSnapshot calibration))
        {
            return Switch2BluetoothCalibrationReadResult.Failed(
                Switch2BluetoothCalibrationReadFailure.
                    SnapshotRejected);
        }
        return Switch2BluetoothCalibrationReadResult.Success(calibration,
            optionalUserFailure);
    }

    public bool TryBindHdRumbleLifetime(Switch2ControllerModel model,
        ulong deviceGeneration, ulong candidateTransportGeneration)
    {
        if (outputCharacteristic == null || deviceGeneration == 0 ||
            candidateTransportGeneration == 0 ||
            Switch2BluetoothHdRumblePhysicalWriter.CharacteristicUuidFor(
                model) != outputCharacteristic.Uuid)
        {
            return false;
        }

        lock (sync)
        {
            if (state is not (LeaseState.Prepared or LeaseState.Active))
            {
                return false;
            }
            if (outputBound)
            {
                return outputModel == model &&
                    outputDeviceGeneration == deviceGeneration &&
                    outputTransportGeneration == candidateTransportGeneration;
            }

            outputModel = model;
            outputDeviceGeneration = deviceGeneration;
            outputTransportGeneration = candidateTransportGeneration;
            outputBound = true;
            return true;
        }
    }

    public bool Authenticates(Switch2ControllerModel model,
        ulong deviceGeneration, ulong candidateTransportGeneration)
    {
        lock (sync)
        {
            return outputCharacteristic != null && outputBound &&
                state == LeaseState.Active && outputModel == model &&
                outputDeviceGeneration == deviceGeneration &&
                outputTransportGeneration == candidateTransportGeneration;
        }
    }

    public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(
        ReadOnlySpan<byte> payload, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration,
        ulong expectedTransportGeneration)
    {
        int expectedLength = Switch2BluetoothHdRumblePhysicalWriter.
            PayloadLengthFor(expectedModel);
        if (payload.Length != expectedLength || expectedLength == 0)
        {
            return Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration,
                Switch2BluetoothHdRumbleTransportWriteFailure.InvalidPayload);
        }
        if (!Authenticates(expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration))
        {
            return Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration,
                Switch2BluetoothHdRumbleTransportWriteFailure.StaleLifetime);
        }
        if (Interlocked.CompareExchange(ref outputWriteActive, 1, 0) != 0)
        {
            return Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration,
                Switch2BluetoothHdRumbleTransportWriteFailure.Busy);
        }

        outputWritesIdle.Reset();
        try
        {
            if (!Authenticates(expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration))
            {
                return Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                    expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration,
                    Switch2BluetoothHdRumbleTransportWriteFailure.
                        StaleLifetime);
            }

            // The WinRT adapter detaches this value before returning from its
            // async boundary. One bounded feedback-lane call may therefore
            // block only this output writer, never the BLE input callback.
            byte[] detachedPayload = payload.ToArray();
            try
            {
                using var deadline = new CancellationTokenSource(
                    HdRumbleWriteTimeoutMilliseconds);
                bool completed = outputCharacteristic.WriteValueAsync(
                        detachedPayload, writeWithoutResponse: true,
                        deadline.Token).AsTask().GetAwaiter().GetResult();
                return completed ?
                    Switch2BluetoothHdRumbleTransportWriteResult.Complete(
                        expectedModel, expectedDeviceGeneration,
                        expectedTransportGeneration, detachedPayload.Length) :
                    Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                        expectedModel, expectedDeviceGeneration,
                        expectedTransportGeneration,
                        Switch2BluetoothHdRumbleTransportWriteFailure.
                            TransportRejected);
            }
            catch (OperationCanceledException)
            {
                return Switch2BluetoothHdRumbleTransportWriteResult.Uncertain(
                    expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration,
                    Switch2BluetoothHdRumbleTransportWriteFailure.TimedOut);
            }
            catch
            {
                return Switch2BluetoothHdRumbleTransportWriteResult.Uncertain(
                    expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration,
                    Switch2BluetoothHdRumbleTransportWriteFailure.
                        DependencyThrew);
            }
        }
        finally
        {
            Volatile.Write(ref outputWriteActive, 0);
            outputWritesIdle.Set();
        }
    }

    public Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLed(
        byte playerNumber, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
    {
        if (!Switch2BluetoothPlayerLedCodec.TryGetPattern(playerNumber,
                out byte pattern))
        {
            return Switch2BluetoothPlayerLedRequestResult.Reject(
                Switch2BluetoothPlayerLedRequestFailure.InvalidArgument);
        }
        return TryRequestPlayerLedMask(pattern, expectedModel,
            expectedDeviceGeneration, expectedTransportGeneration);
    }

    public Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLedMask(
        byte playerLedMask, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
    {
        if ((playerLedMask & 0xF0) != 0)
        {
            return Switch2BluetoothPlayerLedRequestResult.Reject(
                Switch2BluetoothPlayerLedRequestFailure.InvalidArgument);
        }
        if (playerLedChannel == null)
        {
            return Switch2BluetoothPlayerLedRequestResult.Reject(
                Switch2BluetoothPlayerLedRequestFailure.OutputUnavailable);
        }
        if (!Authenticates(expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration))
        {
            return Switch2BluetoothPlayerLedRequestResult.Reject(
                Switch2BluetoothPlayerLedRequestFailure.StaleLifetime);
        }

        lock (sync)
        {
            if (state != LeaseState.Active)
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.StaleLifetime);
            }
            if (playerLedOperationActive)
            {
                // Player indicators are state, not an event stream. Retain
                // exactly the newest requested state while the acknowledged
                // command lane is occupied instead of dropping a one-shot game
                // update behind the initial slot indication.
                pendingPlayerLedPattern = playerLedMask;
                playerLedRequestPending = true;
                return Switch2BluetoothPlayerLedRequestResult.Admit();
            }
            playerLedOperationActive = true;
            // Publish the task before another requester can observe the lane
            // idle. The async method may complete synchronously when a test or
            // platform characteristic supplies an inline acknowledgement.
            playerLedOperation = CompletePlayerLedRequestsAsync(playerLedMask);
        }
        return Switch2BluetoothPlayerLedRequestResult.Admit();
    }

    private async Task CompletePlayerLedRequestsAsync(byte playerLedPattern)
    {
        while (true)
        {
            Switch2BluetoothPlayerLedChannelResult result;
            try
            {
                using var deadline = new CancellationTokenSource(
                    PlayerLedCommandTimeoutMilliseconds);
                result = await playerLedChannel.SetPatternAsync(
                    playerLedPattern,
                    deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                result = Switch2BluetoothPlayerLedChannelResult.Failed(
                    Switch2BluetoothPlayerLedChannelFailure.DependencyThrew);
            }

            lock (sync)
            {
                lastPlayerLedFailure = result.Failure;
                if (state != LeaseState.Active || !playerLedRequestPending)
                {
                    playerLedRequestPending = false;
                    pendingPlayerLedPattern = 0;
                    playerLedOperationActive = false;
                    return;
                }

                playerLedPattern = pendingPlayerLedPattern;
                playerLedRequestPending = false;
                pendingPlayerLedPattern = 0;
            }
        }
    }

    internal Task ResourceRelease
    {
        get
        {
            lock (sync)
            {
                return resourceRelease ?? Task.CompletedTask;
            }
        }
    }

    public bool IsDisconnectedAndReleased(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration)
    {
        lock (sync)
        {
            return disconnectObserved && state == LeaseState.Released &&
                outputBound && outputModel == model &&
                outputDeviceGeneration == deviceGeneration &&
                outputTransportGeneration == transportGeneration &&
                resourceRelease?.IsCompletedSuccessfully == true && resourceRelease.Result;
        }
    }

    internal async ValueTask<bool> PrepareAsync(
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (state != LeaseState.Created)
            {
                return false;
            }
            state = LeaseState.Preparing;
            callbackGate.Open();
        }

        try
        {
            if (playerLedChannel != null &&
                !await playerLedChannel.PrepareAsync(cancellationToken).
                    ConfigureAwait(false))
            {
                lock (sync)
                {
                    playerLedPreparationFailed = true;
                }
                BeginTeardown();
                return false;
            }
            characteristic.AttachValueChangedHandler(valueChanged);
            device.AttachDisconnectedHandler(disconnected);
            bool stillPreparing;
            lock (sync)
            {
                stillPreparing = state == LeaseState.Preparing;
            }
            if (!stillPreparing || !device.IsConnected)
            {
                BeginTeardown();
                return false;
            }

            if (playerLedChannel != null && Admission.Model is
                (Switch2ControllerModel.JoyCon2Left or Switch2ControllerModel.JoyCon2Right))
            {
                using var sensorTimeout = CancellationTokenSource.
                    CreateLinkedTokenSource(cancellationToken);
                sensorTimeout.CancelAfter(PlayerLedCommandTimeoutMilliseconds);
                SensorInitializationFailure = await playerLedChannel.
                    InitializeJoyConSensorsAsync(sensorTimeout.Token).ConfigureAwait(false);
                if (SensorInitializationFailure !=
                    Switch2BluetoothSensorInitializationFailure.None)
                {
                    BeginTeardown();
                    return false;
                }
                JoyConSensorsInitialized = true;
            }

            Task<bool> enableTask = characteristic.
                ConfigureNotificationsAsync(true, cancellationToken).AsTask();
            lock (sync)
            {
                notificationEnableTask = enableTask;
            }
            if (!await AwaitBoundedBooleanAsync(enableTask,
                    cancellationToken).ConfigureAwait(false) ||
                !device.IsConnected)
            {
                BeginTeardown();
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            BeginTeardown();
            throw;
        }
        catch
        {
            BeginTeardown();
            return false;
        }

        lock (sync)
        {
            if (state == LeaseState.Preparing)
            {
                state = LeaseState.Prepared;
                return true;
            }
        }
        BeginTeardown();
        return false;
    }

    public bool TrySubscribeCccdNotify(ulong transportGeneration,
        Switch2BluetoothInputNotification notification,
        Switch2BluetoothInputDisconnected disconnected)
    {
        if (transportGeneration == 0 || notification == null ||
            disconnected == null)
        {
            return false;
        }

        bool disconnectedLifetime;
        lock (sync)
        {
            disconnectedLifetime = state == LeaseState.Disconnected;
            if (state != LeaseState.Prepared)
            {
                // Complete outside the lock; teardown can synchronously enter
                // platform code owned by the same callback thread.
            }
            else
            {
                this.transportGeneration = transportGeneration;
                this.notification = notification;
                disconnectNotification = disconnected;
                state = LeaseState.Active;
            }
        }
        if (disconnectedLifetime)
        {
            BeginTeardown();
            return false;
        }
        lock (sync)
        {
            if (state != LeaseState.Active ||
                this.transportGeneration != transportGeneration)
            {
                return false;
            }
        }

        bool connected;
        try
        {
            connected = device.IsConnected;
        }
        catch
        {
            // An unreadable status is not definite physical-disconnect evidence.
            BeginTeardown();
            return false;
        }
        if (!connected)
        {
            OnDisconnected();
            return false;
        }
        lock (sync)
        {
            // This is subscription-success linearization. A disconnect that
            // won before it cannot be reported as a successful publication.
            return state == LeaseState.Active &&
                this.transportGeneration == transportGeneration;
        }
    }

    public bool TryUnsubscribeCccdNone(ulong transportGeneration)
    {
        lock (sync)
        {
            if (state is LeaseState.Teardown or LeaseState.Released ||
                this.transportGeneration == 0 ||
                transportGeneration != this.transportGeneration)
            {
                return false;
            }
        }
        BeginTeardown();
        return true;
    }

    public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
        ulong transportGeneration, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            return Switch2BluetoothInputLeaseReleaseResult.Invalid;
        }
        lock (sync)
        {
            if (this.transportGeneration == 0 ||
                this.transportGeneration != transportGeneration)
            {
                return Switch2BluetoothInputLeaseReleaseResult.Invalid;
            }
        }

        BeginTeardown();
        Task<bool> exactRelease;
        lock (sync)
        {
            exactRelease = resourceRelease;
        }
        if (exactRelease == null)
        {
            return Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }

        try
        {
            if (!exactRelease.Wait(timeoutMilliseconds))
            {
                return Switch2BluetoothInputLeaseReleaseResult.TimedOut;
            }
            return exactRelease.Result ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }
        catch
        {
            return Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }
    }

    internal ValueTask<bool> BeginAndWaitForBoundedTeardownAsync(
        CancellationToken cancellationToken)
    {
        BeginTeardown();
        return boundedTeardown == null ? ValueTask.FromResult(false) :
            new ValueTask<bool>(WaitBoundedAsync(boundedTeardown,
                cancellationToken));
    }

    internal Task<bool> BeginAndWaitForResourceReleaseAsync()
    {
        BeginTeardown();
        lock (sync)
        {
            return resourceRelease ?? Task.FromResult(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        BeginTeardown();
        if (boundedTeardown != null)
        {
            try
            {
                await boundedTeardown.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void OnValueChanged(ReadOnlySpan<byte> value,
        long completionQpc)
    {
        if (!callbackGate.TryEnter())
        {
            return;
        }
        try
        {
            Switch2BluetoothInputNotification callback;
            ulong generation;
            lock (sync)
            {
                if (state != LeaseState.Active)
                {
                    return;
                }
                callback = notification;
                generation = transportGeneration;
            }
            callback?.Invoke(generation, Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid, value,
                completionQpc);
        }
        finally
        {
            callbackGate.Exit();
        }
    }

    private void OnDisconnected()
    {
        if (!callbackGate.TryEnter())
        {
            return;
        }
        try
        {
            Switch2BluetoothInputDisconnected callback;
            ulong generation;
            lock (sync)
            {
                if (state is not LeaseState.Active and
                    not LeaseState.Preparing and not LeaseState.Prepared)
                {
                    return;
                }
                callback = disconnectNotification;
                generation = transportGeneration;
                Volatile.Write(ref disconnectObserved, true);
                state = LeaseState.Disconnected;
            }
            if (generation != 0)
            {
                callback?.Invoke(generation);
            }
        }
        finally
        {
            callbackGate.Exit();
        }
    }

    private void BeginTeardown()
    {
        TaskCompletionSource<bool> releaseCompletion;
        lock (sync)
        {
            if (state is LeaseState.Teardown or LeaseState.Released)
            {
                return;
            }
            state = LeaseState.Teardown;
            notification = null;
            disconnectNotification = null;
            callbackGate.Retire();
            releaseCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            resourceRelease = releaseCompletion.Task;
            boundedTeardown = ObserveBoundedTeardownAsync(resourceRelease);
        }

        // Detach and CCCD implementations are platform code and can complete or
        // callback inline. Publish the exact completion task under the lock,
        // then enter that platform surface only after releasing it.
        _ = CompleteResourceReleaseAsync(releaseCompletion);
    }

    private async Task CompleteResourceReleaseAsync(
        TaskCompletionSource<bool> completion)
    {
        bool released;
        try
        {
            released = await ReleaseResourcesWhenSafeAsync().
                ConfigureAwait(false);
        }
        catch
        {
            // An unexpected release failure has ambiguous callback ownership.
            // Keep this state machine and the complete object graph alive.
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return;
        }
        completion.TrySetResult(released);
    }

    private async Task<bool> ReleaseResourcesWhenSafeAsync()
    {
        bool playerLedClean = true;
        if (playerLedChannel != null)
        {
            try
            {
                playerLedClean = await playerLedChannel.RetireAsync(
                    CancellationToken.None,
                    () => Volatile.Read(ref disconnectObserved)).ConfigureAwait(false);
            }
            catch
            {
                playerLedClean = false;
            }
        }

        bool outputDrained;
        try
        {
            outputDrained = outputWritesIdle.Wait(teardownTimeout);
        }
        catch
        {
            outputDrained = false;
        }
        if (!outputDrained)
        {
            // The in-flight WinRT write may still retain the vibration
            // characteristic. Preserve the complete graph; the bounded
            // teardown observer reports failure and the runtime owner keeps
            // this lifetime quarantined rather than disposing beneath it.
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return false;
        }

        Task characteristicDrain = null;
        Task deviceDrain = null;
        bool handlerRemovalAmbiguous = !playerLedClean;
        try
        {
            characteristicDrain = characteristic.
                DetachValueChangedHandlerAndDrainAsync();
        }
        catch
        {
            handlerRemovalAmbiguous = true;
        }
        try
        {
            deviceDrain = device.DetachDisconnectedHandlerAndDrainAsync();
        }
        catch
        {
            handlerRemovalAmbiguous = true;
        }

        // Always attempt the sole CCCD compensation after both detach attempts.
        // If handler removal is ambiguous the graph is still quarantined, but
        // silently leaving Notify enabled would violate the lease contract.
        Task<bool> cccdTask = CompleteCccdTeardownAsync(
            notificationEnableTask);
        if (handlerRemovalAmbiguous || characteristicDrain == null ||
            deviceDrain == null)
        {
            // A handler whose removal is ambiguous keeps its source object and
            // delegate alive. Disposing that graph would permit a late callback
            // to cross the released generation. Both removals were attempted
            // before entering quarantine.
            await RetainAmbiguousTeardownAsync(characteristicDrain,
                deviceDrain, callbackGate.Drained, cccdTask).
                ConfigureAwait(false);
            return false;
        }

        try
        {
            await Task.WhenAll(characteristicDrain, deviceDrain,
                callbackGate.Drained).ConfigureAwait(false);
        }
        catch
        {
            // Handler removal/drain is ambiguous. Retain the complete object
            // graph rather than disposing beneath a late callback.
            await RetainAmbiguousTeardownAsync(characteristicDrain,
                deviceDrain, callbackGate.Drained, cccdTask).
                ConfigureAwait(false);
            return false;
        }

        bool cccdSucceeded;
        try
        {
            // Do not dispose a WinRT characteristic while its async CCCD
            // operation still owns it. A non-cooperative operation is retained
            // until it eventually completes, even after the bounded observer
            // reports failure.
            cccdSucceeded = await cccdTask.ConfigureAwait(false);
        }
        catch
        {
            cccdSucceeded = false;
        }

        // Disposal is part of the exact release proof, not best-effort
        // diagnostics. Attempt every owner in dependency order even when an
        // earlier Dispose throws, but preserve any ambiguity in the one
        // retained Task<bool> observed by every waiter.
        bool responseDisposed = TryDispose(responseCharacteristic);
        bool commandDisposed = TryDispose(commandCharacteristic);
        bool outputDisposed = TryDispose(outputCharacteristic);
        bool characteristicDisposed = TryDispose(characteristic);
        bool serviceDisposed = TryDispose(service);
        bool deviceDisposed = TryDispose(device);
        bool outputGateDisposed = TryDispose(outputWritesIdle);
        lock (sync)
        {
            state = LeaseState.Released;
        }
        bool resourcesReleased = playerLedClean && responseDisposed &&
            commandDisposed &&
            cccdSucceeded && outputDisposed && characteristicDisposed &&
            serviceDisposed && deviceDisposed && outputGateDisposed;
        if (resourcesReleased)
        {
            // Rearming discovery is deliberately downstream of the complete
            // resource proof. Registry state cannot downgrade that proof: an
            // identity conflict may correctly refuse reconnect even though
            // every WinRT owner was released without ambiguity.
            _ = candidateRegistry.TryReleaseRememberedConnection(Admission);
        }
        return resourcesReleased;
    }

    private async Task<bool> CompleteCccdTeardownAsync(
        Task<bool> enableTask)
    {
        if (enableTask != null)
        {
            try
            {
                await enableTask.ConfigureAwait(false);
            }
            catch
            {
                // A failed/cancelled Notify attempt still requires a best-effort
                // None write because completion at the platform is ambiguous.
            }
        }

        // A GATT write can reconnect an absent target and wait in the Windows
        // request queue. After definite disconnect, drain local handlers and
        // operations instead; do not create a new remote CCCD obligation.
        if (Volatile.Read(ref disconnectObserved)) return true;

        try
        {
            using var cccdDeadline = new CancellationTokenSource(
                teardownTimeout);
            return await characteristic.ConfigureNotificationsAsync(false,
                cccdDeadline.Token).AsTask().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task RetainAmbiguousTeardownAsync(
        Task characteristicDrain, Task deviceDrain, Task consumerDrain,
        Task<bool> cccdTask)
    {
        Task never = Task.Delay(Timeout.InfiniteTimeSpan);
        // WhenAll deliberately never completes. It strongly retains and
        // observes every operation and owner-dependent continuation without
        // permitting disposal under an ambiguous callback-removal boundary.
        await Task.WhenAll(characteristicDrain ?? never,
            deviceDrain ?? never, consumerDrain ?? never, cccdTask ?? never,
            never).ConfigureAwait(false);
    }

    private async Task<bool> ObserveBoundedTeardownAsync(
        Task<bool> releaseTask)
    {
        try
        {
            return await releaseTask.WaitAsync(teardownTimeout).
                ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitBoundedAsync(Task<bool> teardown,
        CancellationToken cancellationToken)
    {
        try
        {
            return await teardown.WaitAsync(cancellationToken).
                ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async ValueTask<bool> AwaitBoundedBooleanAsync(
        Task<bool> operation, CancellationToken cancellationToken)
    {
        return await operation.WaitAsync(cancellationToken).
            ConfigureAwait(false);
    }

    private static void SafeDispose(IDisposable value)
    {
        _ = TryDispose(value);
    }

    private static bool TryDispose(IDisposable value)
    {
        if (value == null)
        {
            return true;
        }
        try
        {
            value.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum LeaseState : byte
    {
        Created = 0,
        Preparing,
        Prepared,
        Active,
        Disconnected,
        Teardown,
        Released,
    }
}

/// <summary>
/// One-shot admission gate used by the WinRT wrappers. Retire prevents new
/// entries and Drained completes only after every already-entered callback has
/// returned.
/// </summary>
internal sealed class Switch2CallbackDrainGate
{
    private readonly object sync = new();
    private TaskCompletionSource<bool> drained = CompletedSource();
    private int active;
    private bool accepting;

    internal Task Drained
    {
        get
        {
            lock (sync)
            {
                return drained.Task;
            }
        }
    }

    internal void Open()
    {
        lock (sync)
        {
            if (accepting || active != 0)
            {
                throw new InvalidOperationException();
            }
            accepting = true;
            drained = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal bool TryEnter()
    {
        lock (sync)
        {
            if (!accepting)
            {
                return false;
            }
            active++;
            return true;
        }
    }

    internal void Exit()
    {
        TaskCompletionSource<bool> complete = null;
        lock (sync)
        {
            if (active <= 0)
            {
                throw new InvalidOperationException();
            }
            active--;
            if (!accepting && active == 0)
            {
                complete = drained;
            }
        }
        complete?.TrySetResult(true);
    }

    internal Task Retire()
    {
        TaskCompletionSource<bool> complete = null;
        lock (sync)
        {
            accepting = false;
            if (active == 0)
            {
                complete = drained;
            }
        }
        complete?.TrySetResult(true);
        return drained.Task;
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }
}
