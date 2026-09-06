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
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace DS4Windows.Switch2;

/// <summary>
/// Direct Windows Runtime implementation of the dormant BLE boundary. Opening
/// by address and uncached GATT reads do not request pairing or mutate a bond.
/// Windows' public advertisement watcher is process/global rather than bound
/// to a selectable radio, so production multi-radio routing remains gated.
/// </summary>
internal sealed class Switch2BluetoothWinRtPlatform :
    ISwitch2BluetoothWindowsPlatform
{
    internal static async ValueTask<byte[]> GetDefaultHostAddressAsync(
        CancellationToken cancellationToken)
    {
        BluetoothAdapter adapter = await BluetoothAdapter.GetDefaultAsync().
            AsTask(cancellationToken).ConfigureAwait(false);
        ulong address = adapter?.BluetoothAddress ?? 0;
        if (address == 0 || (address & 0xFFFF000000000000UL) != 0)
        {
            return null;
        }

        // Windows exposes the address as a 48-bit integer. The protocol
        // boundary uses canonical/network display order; Nintendo stores the
        // corresponding remembered-host bytes reversed in advertisements.
        return new[]
        {
            (byte)(address >> 40), (byte)(address >> 32),
            (byte)(address >> 24), (byte)(address >> 16),
            (byte)(address >> 8), (byte)address,
        };
    }

    public ISwitch2BluetoothWindowsAdvertisementWatcher
        CreateAdvertisementWatcher() => new WinRtAdvertisementWatcher();

    public async ValueTask<ISwitch2BluetoothWindowsDevice> OpenDeviceAsync(
        ulong bluetoothAddress, Switch2BluetoothWindowsAddressType addressType,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice device = await BluetoothLEDevice.
            FromBluetoothAddressAsync(bluetoothAddress,
                ConvertAddressType(addressType)).
            AsTask(cancellationToken).ConfigureAwait(false);
        return device == null ? null : new WinRtDevice(device);
    }

    private static BluetoothAddressType ConvertAddressType(
        Switch2BluetoothWindowsAddressType addressType) => addressType switch
        {
            Switch2BluetoothWindowsAddressType.Unspecified =>
                BluetoothAddressType.Unspecified,
            Switch2BluetoothWindowsAddressType.Public =>
                BluetoothAddressType.Public,
            Switch2BluetoothWindowsAddressType.Random =>
                BluetoothAddressType.Random,
            _ => throw new ArgumentOutOfRangeException(nameof(addressType)),
        };

    internal static Switch2BluetoothWindowsGattQueryStatus ClassifyServiceStatus(
        GattCommunicationStatus? status) => status switch
        {
            GattCommunicationStatus.Success => Switch2BluetoothWindowsGattQueryStatus.Success,
            GattCommunicationStatus.Unreachable => Switch2BluetoothWindowsGattQueryStatus.Unreachable,
            GattCommunicationStatus.ProtocolError => Switch2BluetoothWindowsGattQueryStatus.ProtocolError,
            GattCommunicationStatus.AccessDenied => Switch2BluetoothWindowsGattQueryStatus.AccessDenied,
            _ => Switch2BluetoothWindowsGattQueryStatus.Failed,
        };

    private sealed class WinRtAdvertisementWatcher :
        ISwitch2BluetoothWindowsAdvertisementWatcher
    {
        private const int MaximumAdvertisementValueLength = 64;

        private readonly object sync = new();
        private readonly BluetoothLEAdvertisementWatcher watcher = new();
        private readonly Switch2CallbackDrainGate callbackGate = new();
        private Switch2BluetoothWindowsAdvertisementHandler received;
        private Switch2BluetoothWindowsWatcherStoppedHandler stopped;
        private bool attached;
        private bool attachAttempted;
        private bool disposed;

        public bool IsConfiguredForActiveScanning =>
            watcher.ScanningMode == BluetoothLEScanningMode.Active;

        public void ConfigureActiveScanning()
        {
            ThrowIfDisposed();
            watcher.ScanningMode = BluetoothLEScanningMode.Active;
            watcher.AllowExtendedAdvertisements = true;
        }

        public void AttachHandlers(
            Switch2BluetoothWindowsAdvertisementHandler received,
            Switch2BluetoothWindowsWatcherStoppedHandler stopped)
        {
            ArgumentNullException.ThrowIfNull(received);
            ArgumentNullException.ThrowIfNull(stopped);
            lock (sync)
            {
                ThrowIfDisposed();
                if (attached)
                {
                    throw new InvalidOperationException();
                }
                this.received = received;
                this.stopped = stopped;
                callbackGate.Open();
                attachAttempted = true;
                watcher.Received += OnReceived;
                watcher.Stopped += OnStopped;
                attached = true;
            }
        }

        public void Start()
        {
            ThrowIfDisposed();
            watcher.Start();
        }

        public void Stop()
        {
            if (!disposed)
            {
                watcher.Stop();
            }
        }

        public Task DetachHandlersAndDrainAsync()
        {
            lock (sync)
            {
                if (!attachAttempted)
                {
                    return callbackGate.Retire();
                }
                callbackGate.Retire();
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
                received = null;
                stopped = null;
                attached = false;
                attachAttempted = false;
                return callbackGate.Drained;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                if (attached || attachAttempted ||
                    !callbackGate.Drained.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "Watcher callbacks must drain before disposal.");
                }
                disposed = true;
            }
        }

        private void OnReceived(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (!callbackGate.TryEnter())
            {
                return;
            }
            try
            {
                IList<BluetoothLEManufacturerData> sections =
                    args?.Advertisement?.ManufacturerData;
                if (sections == null)
                {
                    return;
                }

                BluetoothLEManufacturerData selected = null;
                int count = 0;
                for (int index = 0; index < sections.Count; index++)
                {
                    BluetoothLEManufacturerData current = sections[index];
                    if (current?.CompanyId != Switch2AdvertisementCodec.
                            NintendoBluetoothCompanyId)
                    {
                        continue;
                    }
                    count++;
                    selected ??= current;
                }
                if (selected == null)
                {
                    return;
                }

                IBuffer buffer = selected.Data;
                uint length = buffer?.Length ?? 0;
                if (length > MaximumAdvertisementValueLength)
                {
                    received?.Invoke(args.BluetoothAddress,
                        ConvertAddressType(args.BluetoothAddressType),
                        selected.CompanyId, checked((byte)Math.Min(count, 255)),
                        ReadOnlySpan<byte>.Empty, Stopwatch.GetTimestamp());
                    return;
                }

                Span<byte> value = stackalloc byte[(int)length];
                buffer?.CopyTo(value);
                received?.Invoke(args.BluetoothAddress,
                    ConvertAddressType(args.BluetoothAddressType),
                    selected.CompanyId,
                    checked((byte)Math.Min(count, 255)), value,
                    Stopwatch.GetTimestamp());
            }
            catch
            {
                // Never let malformed platform event data or a boundary
                // callback failure escape the WinRT event dispatcher.
            }
            finally
            {
                callbackGate.Exit();
            }
        }

        private void OnStopped(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (!callbackGate.TryEnter())
            {
                return;
            }
            try
            {
                stopped?.Invoke();
            }
            catch
            {
            }
            finally
            {
                callbackGate.Exit();
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        private static Switch2BluetoothWindowsAddressType ConvertAddressType(
            BluetoothAddressType addressType) => addressType switch
            {
                BluetoothAddressType.Unspecified =>
                    Switch2BluetoothWindowsAddressType.Unspecified,
                BluetoothAddressType.Public =>
                    Switch2BluetoothWindowsAddressType.Public,
                BluetoothAddressType.Random =>
                    Switch2BluetoothWindowsAddressType.Random,
                _ => throw new ArgumentOutOfRangeException(nameof(addressType)),
            };
    }

    private sealed class WinRtDevice : ISwitch2BluetoothWindowsDevice
    {
        private readonly object sync = new();
        private readonly BluetoothLEDevice device;
        private readonly Switch2CallbackDrainGate callbackGate = new();
        private Switch2BluetoothThroughputPreference throughputPreference;
        private Switch2BluetoothWindowsDisconnectedHandler disconnected;
        private bool attached;
        private bool attachAttempted;
        private bool disposed;

        internal WinRtDevice(BluetoothLEDevice device)
        {
            this.device = device;
        }

        public bool IsConnected => !disposed && device.ConnectionStatus ==
            BluetoothConnectionStatus.Connected;

        public bool TryRequestThroughputOptimized()
        {
            lock (sync)
            {
                if (disposed || throughputPreference != null)
                {
                    return throughputPreference != null;
                }
            }

            if (!Switch2BluetoothThroughputPreference.TryAcquire(device,
                    out Switch2BluetoothThroughputPreference acquired))
            {
                return false;
            }

            lock (sync)
            {
                if (disposed || throughputPreference != null)
                {
                    acquired.Dispose();
                    return !disposed && throughputPreference != null;
                }
                throughputPreference = acquired;
                return true;
            }
        }

        public bool TryCopyStableAssociationIdentity(Span<byte> destination,
            out int bytesWritten)
        {
            lock (sync)
            {
                bytesWritten = 0;
                if (disposed || string.IsNullOrWhiteSpace(device.DeviceId))
                {
                    return false;
                }
                int required = Encoding.UTF8.GetByteCount(device.DeviceId);
                if (required <= 0 || required > destination.Length)
                {
                    return false;
                }
                bytesWritten = Encoding.UTF8.GetBytes(device.DeviceId,
                    destination);
                return bytesWritten == required;
            }
        }

        public void AttachDisconnectedHandler(
            Switch2BluetoothWindowsDisconnectedHandler disconnected)
        {
            ArgumentNullException.ThrowIfNull(disconnected);
            lock (sync)
            {
                ThrowIfDisposed();
                if (attached)
                {
                    throw new InvalidOperationException();
                }
                this.disconnected = disconnected;
                callbackGate.Open();
                attachAttempted = true;
                device.ConnectionStatusChanged += OnConnectionStatusChanged;
                attached = true;
            }
        }

        public Task DetachDisconnectedHandlerAndDrainAsync()
        {
            lock (sync)
            {
                if (!attachAttempted)
                {
                    return callbackGate.Retire();
                }
                callbackGate.Retire();
                device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                disconnected = null;
                attached = false;
                attachAttempted = false;
                return callbackGate.Drained;
            }
        }

        public async ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>
            GetServicesForUuidUncachedAsync(Guid serviceUuid,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            // The caller observes its deadline with WaitAsync and retains this
            // device until the query task completes. Do not cancel just the
            // WinRT task projection and lose a late owned service result:
            // Windows does not guarantee cancellation of the connection itself.
            GattDeviceServicesResult result = await device.
                GetGattServicesForUuidAsync(serviceUuid,
                    BluetoothCacheMode.Uncached).AsTask().
                ConfigureAwait(false);
            IReadOnlyList<GattDeviceService> nativeServices = result?.Services;
            var services = nativeServices == null ?
                Array.Empty<ISwitch2BluetoothWindowsGattService>() :
                new ISwitch2BluetoothWindowsGattService[
                    nativeServices.Count];
            for (int index = 0; index < services.Length; index++)
            {
                services[index] = new WinRtGattService(
                    nativeServices[index]);
            }
            return new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>(ClassifyServiceStatus(result?.Status), services);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                if (attached || attachAttempted ||
                    !callbackGate.Drained.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "Disconnect callbacks must drain before disposal.");
                }
                disposed = true;
                throughputPreference?.Dispose();
                throughputPreference = null;
                device.Dispose();
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender,
            object args)
        {
            if (!callbackGate.TryEnter())
            {
                return;
            }
            try
            {
                if (sender.ConnectionStatus ==
                    BluetoothConnectionStatus.Disconnected)
                {
                    disconnected?.Invoke();
                }
            }
            catch
            {
            }
            finally
            {
                callbackGate.Exit();
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    private sealed class WinRtGattService :
        ISwitch2BluetoothWindowsGattService
    {
        private readonly GattDeviceService service;
        private bool disposed;

        internal WinRtGattService(GattDeviceService service)
        {
            this.service = service ?? throw new ArgumentNullException(
                nameof(service));
        }

        public Guid Uuid => service.Uuid;

        public async ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>>
            GetCharacteristicsForUuidUncachedAsync(Guid characteristicUuid,
                CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            GattCharacteristicsResult result = await service.
                GetCharacteristicsForUuidAsync(characteristicUuid,
                    BluetoothCacheMode.Uncached).AsTask(cancellationToken).
                ConfigureAwait(false);
            IReadOnlyList<GattCharacteristic> nativeCharacteristics = result?.
                Characteristics;
            var characteristics = nativeCharacteristics == null ?
                Array.Empty<ISwitch2BluetoothWindowsGattCharacteristic>() :
                new ISwitch2BluetoothWindowsGattCharacteristic[
                    nativeCharacteristics.Count];
            for (int index = 0; index < characteristics.Length; index++)
            {
                characteristics[index] = new WinRtGattCharacteristic(
                    nativeCharacteristics[index]);
            }
            return new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>(result?.Status ==
                    GattCommunicationStatus.Success, characteristics);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            service.Dispose();
        }
    }

    private sealed class WinRtGattCharacteristic :
        ISwitch2BluetoothWindowsGattCharacteristic
    {
        private const int MaximumNotificationLength = 64;

        private readonly object sync = new();
        private readonly GattCharacteristic characteristic;
        private readonly Switch2CallbackDrainGate callbackGate = new();
        private Switch2BluetoothWindowsValueChangedHandler valueChanged;
        private bool attached;
        private bool attachAttempted;
        private bool disposed;

        internal WinRtGattCharacteristic(GattCharacteristic characteristic)
        {
            this.characteristic = characteristic ??
                throw new ArgumentNullException(nameof(characteristic));
            GattCharacteristicProperties nativeProperties = characteristic.
                CharacteristicProperties;
            HasOnlyReadAndNotifyProperties = nativeProperties ==
                (GattCharacteristicProperties.Read |
                    GattCharacteristicProperties.Notify);
            EvidencedProperties = ConvertProperties(nativeProperties);
        }

        public Guid Uuid => characteristic.Uuid;

        public Switch2GattProperty EvidencedProperties { get; }

        public bool HasOnlyReadAndNotifyProperties { get; }

        public void AttachValueChangedHandler(
            Switch2BluetoothWindowsValueChangedHandler valueChanged)
        {
            ArgumentNullException.ThrowIfNull(valueChanged);
            lock (sync)
            {
                ThrowIfDisposed();
                if (attached)
                {
                    throw new InvalidOperationException();
                }
                this.valueChanged = valueChanged;
                callbackGate.Open();
                attachAttempted = true;
                characteristic.ValueChanged += OnValueChanged;
                attached = true;
            }
        }

        public Task DetachValueChangedHandlerAndDrainAsync()
        {
            lock (sync)
            {
                if (!attachAttempted)
                {
                    return callbackGate.Retire();
                }
                callbackGate.Retire();
                characteristic.ValueChanged -= OnValueChanged;
                valueChanged = null;
                attached = false;
                attachAttempted = false;
                return callbackGate.Drained;
            }
        }

        public async ValueTask<bool> ConfigureNotificationsAsync(bool enabled,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            GattCommunicationStatus status = await characteristic.
                WriteClientCharacteristicConfigurationDescriptorAsync(enabled ?
                    GattClientCharacteristicConfigurationDescriptorValue.Notify :
                    GattClientCharacteristicConfigurationDescriptorValue.None).
                AsTask(cancellationToken).ConfigureAwait(false);
            return status == GattCommunicationStatus.Success;
        }

        public async ValueTask<bool> WriteValueAsync(
            ReadOnlyMemory<byte> value, bool writeWithoutResponse,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            GattCharacteristicProperties required = writeWithoutResponse ?
                GattCharacteristicProperties.WriteWithoutResponse :
                GattCharacteristicProperties.Write;
            if ((characteristic.CharacteristicProperties & required) == 0)
            {
                return false;
            }

            // The WinRT operation retains this detached buffer independently
            // of the caller's bounded command scratch storage.
            IBuffer buffer = value.ToArray().AsBuffer();
            GattCommunicationStatus status = await characteristic.
                WriteValueAsync(buffer, writeWithoutResponse ?
                    GattWriteOption.WriteWithoutResponse :
                    GattWriteOption.WriteWithResponse).
                AsTask(cancellationToken).ConfigureAwait(false);
            return status == GattCommunicationStatus.Success;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                if (attached || attachAttempted ||
                    !callbackGate.Drained.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "ValueChanged callbacks must drain before disposal.");
                }
                disposed = true;
            }
        }

        private void OnValueChanged(GattCharacteristic sender,
            GattValueChangedEventArgs args)
        {
            if (!callbackGate.TryEnter())
            {
                return;
            }
            try
            {
                IBuffer buffer = args?.CharacteristicValue;
                uint length = buffer?.Length ?? 0;
                if (length > MaximumNotificationLength)
                {
                    valueChanged?.Invoke(ReadOnlySpan<byte>.Empty,
                        Stopwatch.GetTimestamp());
                    return;
                }
                Span<byte> value = stackalloc byte[(int)length];
                buffer?.CopyTo(value);
                valueChanged?.Invoke(value, Stopwatch.GetTimestamp());
            }
            catch
            {
                // Input rejection is owned by the exact lease generation; a
                // WinRT event callback must never fault the dispatcher thread.
            }
            finally
            {
                callbackGate.Exit();
            }
        }

        private static Switch2GattProperty ConvertProperties(
            GattCharacteristicProperties native)
        {
            Switch2GattProperty converted = Switch2GattProperty.None;
            if ((native & GattCharacteristicProperties.Read) != 0)
            {
                converted |= Switch2GattProperty.Read;
            }
            if ((native & GattCharacteristicProperties.Notify) != 0)
            {
                converted |= Switch2GattProperty.Notify;
            }
            if ((native & GattCharacteristicProperties.Write) != 0)
            {
                converted |= Switch2GattProperty.Write;
            }
            if ((native & GattCharacteristicProperties.
                    WriteWithoutResponse) != 0)
            {
                converted |= Switch2GattProperty.WriteWithoutResponse;
            }
            return converted;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }
}
