/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using DS4Windows.Sunshine;

namespace DS4Windows;

public partial class ControlService
{
    private readonly ConcurrentDictionary<SunshineControllerBridgeDeviceKey,
        SunshineStreamInputDevice> sunshineStreamDevices = new();
    private readonly SunshineControllerBridgeArrivalRegistry
        sunshineBridgeArrivals = new();
    private SunshineControllerBridgeServer sunshineControllerBridge;

    private void StartSunshineControllerBridge()
    {
        if (!deviceOptions.SunshineControllerBridgeEnabled) return;
        if (sunshineControllerBridge != null) return;
        sunshineControllerBridge = new SunshineControllerBridgeServer(
            OnSunshineBridgePacket, OnSunshineBridgeDisconnected);
        sunshineControllerBridge.Start();
        StartupDiag("Sunshine controller bridge listening");
    }

    private void StopSunshineControllerBridge()
    {
        SunshineControllerBridgeServer bridge = sunshineControllerBridge;
        sunshineControllerBridge = null;
        bridge?.Dispose();
        DisconnectSunshineStreamDevices();
        StartupDiag("Sunshine controller bridge stopped");
    }

    private void OnSunshineControllerBridgeEnabledChanged(object sender,
        EventArgs e)
    {
        lock (serviceLifecycleLock)
        {
            if (!running) return;
            if (deviceOptions.SunshineControllerBridgeEnabled)
            {
                try { StartSunshineControllerBridge(); }
                catch (Exception exception)
                {
                    LogDebug("Sunshine controller bridge could not start: " +
                        exception.Message, true);
                }
            }
            else
            {
                StopSunshineControllerBridge();
            }
        }
    }

    private void OnSunshineBridgePacket(SunshineControllerBridgePacket packet)
    {
        var key = new SunshineControllerBridgeDeviceKey(packet.SessionId,
            packet.ControllerId);
        if (packet.Kind == SunshineControllerBridgePacketKind.Arrival)
        {
            LogDebug($"Sunshine bridge received controller arrival id={packet.ControllerId} client={packet.ClientIndex} type={packet.ControllerType} capabilities=0x{packet.Capabilities:X4}.");
            lock (serviceLifecycleLock)
            {
                if (!running || sunshineControllerBridge == null)
                {
                    LogDebug("Sunshine bridge ignored controller arrival because DS4Windows is stopping or the bridge is unavailable.", true);
                    return;
                }
                sunshineBridgeArrivals.MarkActive(key);
                LogDebug($"Sunshine bridge starting controller attach for id={packet.ControllerId}.");
                AttachSunshineStreamDevice(packet);
                LogDebug($"Sunshine bridge controller attach returned for id={packet.ControllerId}.");
            }
            return;
        }

        if (packet.Kind == SunshineControllerBridgePacketKind.Remove)
        {
            lock (serviceLifecycleLock)
            {
                sunshineBridgeArrivals.Remove(key);
                if (sunshineStreamDevices.TryRemove(key,
                        out SunshineStreamInputDevice removedDevice))
                    removedDevice.RequestRemoval();
            }
            return;
        }

        if (!sunshineStreamDevices.TryGetValue(key,
                out SunshineStreamInputDevice device)) return;

        if (packet.Kind is SunshineControllerBridgePacketKind.State or
            SunshineControllerBridgePacketKind.Motion or
            SunshineControllerBridgePacketKind.Touch or
            SunshineControllerBridgePacketKind.Battery)
            device.Apply(packet);
    }

    private void AttachSunshineStreamDevice(
        in SunshineControllerBridgePacket packet)
    {
        LogDebug($"Sunshine bridge attach entered for id={packet.ControllerId}.");
        lock (serviceLifecycleLock)
        {
            if (!running || packet.SessionId == 0 ||
                packet.ControllerId >= 16 ||
                sunshineControllerBridge == null)
            {
                LogDebug($"Sunshine bridge attach skipped for id={packet.ControllerId}: lifecycle or packet precondition failed.", true);
                return;
            }

            var key = new SunshineControllerBridgeDeviceKey(packet.SessionId,
                packet.ControllerId);
            if (!sunshineBridgeArrivals.IsActive(key) ||
                sunshineStreamDevices.ContainsKey(key))
            {
                LogDebug($"Sunshine bridge attach skipped for id={packet.ControllerId}: arrival inactive or already attached.", true);
                return;
            }

            var device = new SunshineStreamInputDevice(packet.SessionId,
                packet.ControllerId, packet.ClientIndex,
                packet.ControllerType, packet.Capabilities,
                packet.SupportedButtons);
            device.RumbleRequested += (low, high) =>
                SendSunshineFeedback(device,
                    SunshineControllerBridgeFeedbackKind.Rumble,
                    low, high);
            device.RgbRequested += (red, green, blue) =>
                SendSunshineRgb(device, red, green, blue);

            int slot = 0;
            while (slot < CURRENT_DS4_CONTROLLER_LIMIT &&
                !inputSlotAdmission.TryClaimLegacySlot(slot, device)) slot++;
            if (slot >= CURRENT_DS4_CONTROLLER_LIMIT)
            {
                LogDebug($"Sunshine bridge has no free DS4Windows slot for id={packet.ControllerId}; scheduling retry.", true);
                ScheduleSunshineArrivalRetry(packet, key);
                return;
            }

            try
            {
                LogDebug($"Sunshine bridge claimed DS4Windows slot={slot}; preparing input controller.");
                BeginPrepareConnectedInputController(device);
                LogDebug($"Sunshine bridge input controller preparation completed for slot={slot}; preparing profile/output.");
                int controllerCount = DS4Controllers.Count(candidate => candidate != null);
                PrepareConnectedInputControllerAtSlot(
                    Math.Max(1, controllerCount), device, slot);
                LogDebug($"Sunshine bridge profile/output preparation completed for slot={slot}.");
                if (!sunshineStreamDevices.TryAdd(key, device))
                {
                    LogDebug($"Sunshine bridge device registration lost a race for slot={slot}; cleaning up.", true);
                    RetireControllerPresentation(device, slot,
                        commitNeutralMapping: true, logRemoval: false);
                    ClearExactControllerSlot(device, slot);
                    return;
                }
                PublishPreparedHotplug(device, slot);
                LogDebug($"Sunshine controller {packet.ClientIndex + 1} connected through the local bridge.");
            }
            catch (Exception exception)
            {
                LogDebug($"Could not prepare Sunshine controller: {exception.Message}", true);
                TryClaimControllerRemoval(device);
                try
                {
                    RetireControllerPresentation(device, slot,
                        commitNeutralMapping: true, logRemoval: false);
                    ClearExactControllerSlot(device, slot);
                }
                catch (Exception cleanupException)
                {
                    StartupDiag($"Sunshine controller setup cleanup failed: {cleanupException.GetType().Name}: {cleanupException.Message}");
                }
            }
        }
    }

    private void ScheduleSunshineArrivalRetry(
        SunshineControllerBridgePacket packet,
        SunshineControllerBridgeDeviceKey key)
    {
        object retryToken = sunshineBridgeArrivals.TryBeginRetry(key);
        if (retryToken == null) return;
        _ = Task.Run(async () =>
        {
            int attempt = 0;
            bool capacityWarningLogged = false;
            try
            {
                while (true)
                {
                    await Task.Delay(attempt < 80 ? 25 : 250).
                        ConfigureAwait(false);
                    lock (serviceLifecycleLock)
                    {
                        if (!running || sunshineControllerBridge == null ||
                            !sunshineBridgeArrivals.IsRetryCurrent(key,
                                retryToken)) return;
                        if (!sunshineStreamDevices.ContainsKey(key))
                            AttachSunshineStreamDevice(packet);
                        if (sunshineStreamDevices.ContainsKey(key)) return;
                        if (attempt >= 80 && !capacityWarningLogged)
                        {
                            capacityWarningLogged = true;
                            LogDebug("Sunshine controller is waiting for a free DS4Windows input slot.", true);
                        }
                    }
                    if (attempt < 80) attempt++;
                }
            }
            finally
            {
                sunshineBridgeArrivals.CompleteRetry(key, retryToken);
            }
        });
    }

    private void SendSunshineFeedback(SunshineStreamInputDevice device,
        SunshineControllerBridgeFeedbackKind kind, byte low, byte high)
    {
        SunshineControllerBridgeServer bridge = sunshineControllerBridge;
        if (bridge == null) return;
        var packet = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Feedback,
            device.SessionIdForBridge, device.ControllerIdForBridge,
            device.ClientIndex, 0, feedbackKind: (byte)kind,
            lowRumble: low, highRumble: high);
        bridge.Send(packet);
    }

    private void SendSunshineRgb(SunshineStreamInputDevice device,
        byte red, byte green, byte blue)
    {
        SunshineControllerBridgeServer bridge = sunshineControllerBridge;
        if (bridge == null) return;
        var packet = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Feedback,
            device.SessionIdForBridge, device.ControllerIdForBridge,
            device.ClientIndex, 0, feedbackKind: (byte)
                SunshineControllerBridgeFeedbackKind.RgbLed,
            red: red, green: green, blue: blue);
        bridge.Send(packet);
    }

    private void OnSunshineBridgeDisconnected()
    {
        DisconnectSunshineStreamDevices();
    }

    private void DisconnectSunshineStreamDevices()
    {
        lock (serviceLifecycleLock)
        {
            sunshineBridgeArrivals.Clear();
            foreach (var entry in sunshineStreamDevices.ToArray())
            {
                if (sunshineStreamDevices.TryRemove(entry.Key, out var device))
                    device.RequestRemoval();
            }
        }
    }
}
