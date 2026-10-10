/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System.Collections.Generic;

namespace DS4Windows.Sunshine;

internal readonly record struct SunshineControllerBridgeDeviceKey(
    ulong SessionId, ushort ControllerId);

/// <summary>Tracks live Sunshine controllers and their slot-retry generations.</summary>
internal sealed class SunshineControllerBridgeArrivalRegistry
{
    private readonly object gate = new();
    private readonly HashSet<SunshineControllerBridgeDeviceKey> active = new();
    private readonly Dictionary<SunshineControllerBridgeDeviceKey, object> retries = new();

    internal void MarkActive(SunshineControllerBridgeDeviceKey key)
    {
        lock (gate) active.Add(key);
    }

    internal bool IsActive(SunshineControllerBridgeDeviceKey key)
    {
        lock (gate) return active.Contains(key);
    }

    internal object TryBeginRetry(SunshineControllerBridgeDeviceKey key)
    {
        lock (gate)
        {
            if (!active.Contains(key) || retries.ContainsKey(key)) return null;
            object token = new();
            retries.Add(key, token);
            return token;
        }
    }

    internal bool IsRetryCurrent(SunshineControllerBridgeDeviceKey key,
        object token)
    {
        lock (gate)
        {
            return active.Contains(key) && retries.TryGetValue(key,
                out object current) && ReferenceEquals(current, token);
        }
    }

    internal void Remove(SunshineControllerBridgeDeviceKey key)
    {
        lock (gate)
        {
            active.Remove(key);
            retries.Remove(key);
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            active.Clear();
            retries.Clear();
        }
    }

    internal void CompleteRetry(SunshineControllerBridgeDeviceKey key,
        object token)
    {
        lock (gate)
        {
            if (retries.TryGetValue(key, out object current) &&
                ReferenceEquals(current, token))
                retries.Remove(key);
        }
    }
}
