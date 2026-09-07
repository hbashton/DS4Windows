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
using System.Text.Json;
using System.Linq;
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
        ISwitch2BluetoothWindowsGattService, ISwitch2BluetoothLabAudioAccess
    {
        private readonly GattDeviceService service;
        private bool disposed;
        private bool labAudioFenced;
        private readonly object notificationStateGate = new();
        private readonly Dictionary<Guid, bool> ownedNotificationStates = new();

        private void ObserveOwnedNotificationState(Guid uuid, bool enabled)
        {
            lock (notificationStateGate) ownedNotificationStates[uuid] = enabled;
        }

        internal WinRtGattService(GattDeviceService service)
        {
            this.service = service ?? throw new ArgumentNullException(
                nameof(service));
        }

        public Guid Uuid => service.Uuid;

        public async Task<string> QueryAudioLabAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (labAudioFenced) throw new InvalidOperationException("Lab audio notification cleanup is ambiguous; probe fenced until reconnect.");
            if (command == "headset-observe")
                return await ObserveLabHeadsetAsync(cancellationToken).ConfigureAwait(false);
            if (Switch2BluetoothLabTone.IsActiveTone(command))
                return await ObserveLabHeadsetAsync(cancellationToken, "tone-" + command.Substring(7)).ConfigureAwait(false);
            if (Switch2BluetoothLabTone.IsTone(command))
            {
                var outputQuery = await service.GetCharacteristicsForUuidAsync(Switch2BluetoothLabAudioProtocol.OutputUuid,
                    BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
                if (outputQuery.Status != GattCommunicationStatus.Success || outputQuery.Characteristics.Count != 1 ||
                    !outputQuery.Characteristics[0].CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
                    throw new InvalidOperationException("Exact headphone output characteristic unavailable.");
                var output = outputQuery.Characteristics[0];
                var tone = Switch2BluetoothLabTone.Create(command);
                var result = await tone.SendAsync(Math.Max(0, service.Session.MaxPduSize - 3), async packet =>
                {
                    // All writes stay on this owned service, with one real WinRT
                    // operation in flight. Never abandon it via AsTask(token).
                    using var writer = new DataWriter();
                    writer.WriteBytes(packet);
                    var response = await output.WriteValueWithResultAsync(writer.DetachBuffer(),
                        GattWriteOption.WriteWithoutResponse).AsTask().ConfigureAwait(false);
                    return response.Status == GattCommunicationStatus.Success;
                }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }
            if (command == "inventory")
            {
                // Actual completion retained by the lab worker, not a cancelled
                // WinRT projection. Reuses this exact live service.
                var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    Status = result.Status.ToString(), Service = service.Uuid,
                    MaxPduSize = service.Session.MaxPduSize,
                    MaximumSingleAttValueBytes = Math.Max(0, service.Session.MaxPduSize - 3),
                    Characteristics = result.Characteristics.Select(c => new
                    { c.Uuid, c.AttributeHandle, Properties = c.CharacteristicProperties.ToString() }),
                    BluetoothPlaybackConfirmed = false,
                });
            }
            if (command != "headset-header") throw new ArgumentException("Unsupported read-only lab query.");
            var query = await service.GetCharacteristicsForUuidAsync(Switch2BluetoothLabAudioProtocol.InputUuid,
                BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
            if (query.Status != GattCommunicationStatus.Success || query.Characteristics.Count != 1)
                return JsonSerializer.Serialize(new { Error = "Headset characteristic unavailable", Status = query.Status.ToString() });
            var headset = query.Characteristics[0];
            if (!headset.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                return "{\"Error\":\"Headset characteristic does not permit Read\"}";
            cancellationToken.ThrowIfCancellationRequested();
            var read = await headset.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            byte? jack = null, audioLength = null;
            if (read.Status == GattCommunicationStatus.Success && read.Value.Length >= 15)
            {
                using var reader = DataReader.FromBuffer(read.Value);
                byte[] prefix = new byte[15];
                reader.ReadBytes(prefix);
                jack = prefix[13];
                audioLength = prefix[14];
            }
            // Only framing metadata leaves this read. No microphone PCM/encoded
            // bytes, button state, or motion data is returned, stored or played.
            return JsonSerializer.Serialize(new { Status = read.Status.ToString(), read.ProtocolError, Length = read.Value?.Length,
                JackStateRaw = jack, DeclaredAudioBytes = audioLength, NotificationsChanged = false,
                BluetoothPlaybackConfirmed = false });
        }

        private async Task<string> ObserveLabHeadsetAsync(CancellationToken cancellationToken, string toneCommand = null)
        {
            var query = await service.GetCharacteristicsForUuidAsync(Switch2BluetoothLabAudioProtocol.InputUuid,
                BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
            var inputQuery = await service.GetCharacteristicsForUuidAsync(Switch2InputCodec.Common05CharacteristicUuid,
                BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
            if (query.Status != GattCommunicationStatus.Success || query.Characteristics.Count != 1 ||
                inputQuery.Status != GattCommunicationStatus.Success || inputQuery.Characteristics.Count != 1)
                throw new InvalidOperationException("Exact headset and existing common05 interfaces are required.");
            var headset = query.Characteristics[0];
            var commonInput = inputQuery.Characteristics[0];
            var before = await headset.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask().ConfigureAwait(false);
            var inputBefore = await commonInput.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask().ConfigureAwait(false);
            bool ownedInput;
            lock (notificationStateGate)
                ownedInput = ownedNotificationStates.GetValueOrDefault(Switch2InputCodec.Common05CharacteristicUuid) &&
                    !ownedNotificationStates.GetValueOrDefault(Switch2BluetoothLabAudioProtocol.InputUuid);
            var beforeDetails = new { HeadsetStatus = before.Status.ToString(), HeadsetProtocolError = before.ProtocolError,
                HeadsetCccd = before.Status == GattCommunicationStatus.Success ? before.ClientCharacteristicConfigurationDescriptor.ToString() : null, InputStatus = inputBefore.Status.ToString(),
                InputProtocolError = inputBefore.ProtocolError, InputCccd = inputBefore.Status == GattCommunicationStatus.Success ? inputBefore.ClientCharacteristicConfigurationDescriptor.ToString() : null,
                OwnedCommonInputNotify = ownedInput };
            // Nintendo may reject CCCD reads even though writes work. Use the
            // exact input lease's successful CCCD writes as authority, not a
            // failed read's default enum value. No other path opens headset
            // notifications in this fresh lab service lifetime.
            if (!ownedInput ||
                before.Status == GattCommunicationStatus.Success && before.ClientCharacteristicConfigurationDescriptor != GattClientCharacteristicConfigurationDescriptorValue.None ||
                inputBefore.Status == GattCommunicationStatus.Success && inputBefore.ClientCharacteristicConfigurationDescriptor != GattClientCharacteristicConfigurationDescriptorValue.Notify ||
                before.Status is not (GattCommunicationStatus.Success or GattCommunicationStatus.ProtocolError) ||
                inputBefore.Status is not (GattCommunicationStatus.Success or GattCommunicationStatus.ProtocolError))
                return JsonSerializer.Serialize(new { Error = "Unexpected CCCD ownership; no notification settings changed", Before = beforeDetails });
            var gate = new object();
            int reports = 0, valid = 0, idle = 0;
            var jackCounts = new Dictionary<byte, int>();
            var lengths = new Dictionary<uint, int>();
            bool accepting = true;
            void OnHeadset(GattCharacteristic sender, GattValueChangedEventArgs args)
            {
                lock (gate)
                {
                    if (!accepting) return;
                    reports++;
                    uint size = args.CharacteristicValue.Length;
                    if (lengths.Count < 8 || lengths.ContainsKey(size)) lengths[size] = lengths.GetValueOrDefault(size) + 1;
                    if (size != 112) return;
                    using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                    byte[] value = new byte[112];
                    reader.ReadBytes(value);
                    if (Switch2BluetoothLabHeadsetHeader.TryRead(value, out byte jack, out _, out bool opusIdle))
                    {
                        valid++;
                        jackCounts[jack] = jackCounts.GetValueOrDefault(jack) + 1;
                        if (opusIdle) idle++;
                    }
                    Array.Clear(value); // no recorded or returned microphone payload
                }
            }
            bool attached = false, attempted = false;
            string restored = null, inputRestored = null;
            JsonElement? toneResult = null;
            GattCommunicationStatus enabled;
            try
            {
                headset.ValueChanged += OnHeadset;
                attached = true;
                cancellationToken.ThrowIfCancellationRequested();
                attempted = true;
                enabled = await headset.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
                if (enabled == GattCommunicationStatus.Success)
                {
                    if (toneCommand == null) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    else toneResult = JsonSerializer.Deserialize<JsonElement>(await QueryAudioLabAsync(toneCommand, cancellationToken).ConfigureAwait(false));
                }
            }
            finally
            {
                try
                {
                    if (attempted)
                    {
                        var restore = await headset.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().ConfigureAwait(false);
                        restored = restore.ToString();
                        // Donor reports headset notifications can replace normal
                        // input. Reassert the exact original common05 CCCD state.
                        var resume = await commonInput.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
                        inputRestored = resume.ToString();
                        labAudioFenced = restore != GattCommunicationStatus.Success || resume != GattCommunicationStatus.Success;
                    }
                }
                catch { labAudioFenced = true; throw; }
                finally
                {
                    lock (gate) accepting = false;
                    if (attached) headset.ValueChanged -= OnHeadset;
                }
            }
            lock (gate)
                return JsonSerializer.Serialize(new { Status = enabled.ToString(), Reports = reports, ValidHeaders = valid,
                    PublishedOpusIdleMatches = idle, JackStates = jackCounts, Lengths = lengths,
                    RestoreHeadset = restored, RestoreCommonInput = inputRestored, LabAudioFenced = labAudioFenced,
                    Before = beforeDetails,
                    Tone = toneResult,
                    StoredAudio = false, BluetoothPlaybackConfirmed = false });
        }

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
                    nativeCharacteristics[index], Switch2BluetoothLabProbe.IsEnabled ? ObserveOwnedNotificationState : null);
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

        private readonly Action<Guid, bool> observeNotificationState;

        internal WinRtGattCharacteristic(GattCharacteristic characteristic, Action<Guid, bool> observeNotificationState = null)
        {
            this.observeNotificationState = observeNotificationState;
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
            if (status == GattCommunicationStatus.Success) observeNotificationState?.Invoke(Uuid, enabled);
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
