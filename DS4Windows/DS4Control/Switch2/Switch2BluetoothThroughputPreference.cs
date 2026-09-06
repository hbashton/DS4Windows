/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Foundation.Metadata;

namespace DS4Windows.Switch2;

/// <summary>
/// Retained Windows 11 BLE ThroughputOptimized request. The ABI surface is a
/// GPL-compatible C# adaptation of the proven SDL BLE Switch 2 driver while
/// preserving DS4Windows' Windows 10 target. Microsoft documents the returned
/// request as an IClosable lifetime; releasing it immediately would discard
/// the preference instead of owning it for the controller connection.
/// </summary>
internal sealed class Switch2BluetoothThroughputPreference : IDisposable
{
    private const string DeviceRuntimeClass =
        "Windows.Devices.Bluetooth.BluetoothLEDevice";
    private const string ParametersRuntimeClass =
        "Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters";
    private const int ThroughputOptimizedVtableSlot = 7;
    private const int RequestPreferredVtableSlot = 8;
    private const int RequestStatusVtableSlot = 6;
    private const int CloseVtableSlot = 6;
    private const int RequestStatusSuccess = 1;

    private static readonly Guid Device6Interface = new(
        "CA7190EF-0CAE-573C-A1CA-E1FC5BFC39E2");
    private static readonly Guid ParametersStaticsInterface = new(
        "0E3E8EDC-2751-55AA-A838-8FAEEE818D72");
    private static readonly Guid ClosableInterface = new(
        "30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    private nint request;

    private Switch2BluetoothThroughputPreference(nint request)
    {
        this.request = request;
    }

    internal static bool TryAcquire(BluetoothLEDevice device,
        out Switch2BluetoothThroughputPreference preference)
    {
        preference = null;
        if (device == null || !ApiInformation.IsMethodPresent(
                DeviceRuntimeClass, "RequestPreferredConnectionParameters") ||
            !ApiInformation.IsPropertyPresent(ParametersRuntimeClass,
                "ThroughputOptimized"))
        {
            return false;
        }

        nint unknown = 0;
        nint device6 = 0;
        nint className = 0;
        nint statics = 0;
        nint parameters = 0;
        nint admittedRequest = 0;
        try
        {
            unknown = AcquireNativeReference(device);
            Guid device6Id = Device6Interface;
            if (Marshal.QueryInterface(unknown, ref device6Id, out device6) < 0 ||
                device6 == 0)
            {
                return false;
            }
            if (WindowsCreateString(ParametersRuntimeClass,
                    (uint)ParametersRuntimeClass.Length, out className) < 0 ||
                className == 0)
            {
                return false;
            }
            Guid staticsId = ParametersStaticsInterface;
            if (RoGetActivationFactory(className, ref staticsId,
                    out statics) < 0 || statics == 0)
            {
                return false;
            }

            GetObjectDelegate getThroughput = GetVtableDelegate<
                GetObjectDelegate>(statics, ThroughputOptimizedVtableSlot);
            if (getThroughput(statics, out parameters) < 0 || parameters == 0)
            {
                return false;
            }
            RequestDelegate requestPreferred = GetVtableDelegate<
                RequestDelegate>(device6, RequestPreferredVtableSlot);
            if (requestPreferred(device6, parameters,
                    out admittedRequest) < 0 || admittedRequest == 0)
            {
                return false;
            }
            GetInt32Delegate getStatus = GetVtableDelegate<GetInt32Delegate>(
                admittedRequest, RequestStatusVtableSlot);
            if (getStatus(admittedRequest, out int status) < 0 ||
                status != RequestStatusSuccess)
            {
                return false;
            }

            preference = new Switch2BluetoothThroughputPreference(
                admittedRequest);
            admittedRequest = 0;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseAndRelease(ref admittedRequest);
            Release(ref parameters);
            Release(ref statics);
            if (className != 0)
            {
                WindowsDeleteString(className);
            }
            Release(ref device6);
            Release(ref unknown);
        }
    }

    public void Dispose()
    {
        nint detached = Interlocked.Exchange(ref request, 0);
        CloseAndRelease(ref detached);
    }

    // .NET 8 projects WinRT through ComWrappers. GetIUnknownForObject creates
    // a CLR wrapper around that projection, not the native Windows device.
    // GetRef adds our own reference, balanced by Release in TryAcquire's
    // finally; releasing NativeObject.ThisPtr would steal the projection's ref.
    // https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md#iunknown
    internal static nint AcquireNativeReference(object instance) =>
        ((WinRT.IWinRTObject)instance).NativeObject.GetRef();

    private static void CloseAndRelease(ref nint instance)
    {
        nint detached = Interlocked.Exchange(ref instance, 0);
        if (detached == 0)
        {
            return;
        }

        nint closable = 0;
        try
        {
            Guid closableId = ClosableInterface;
            if (Marshal.QueryInterface(detached, ref closableId,
                    out closable) >= 0 && closable != 0)
            {
                GetHResultDelegate close = GetVtableDelegate<
                    GetHResultDelegate>(closable, CloseVtableSlot);
                _ = close(closable);
            }
        }
        catch
        {
        }
        finally
        {
            Release(ref closable);
            Marshal.Release(detached);
        }
    }

    private static void Release(ref nint instance)
    {
        nint detached = Interlocked.Exchange(ref instance, 0);
        if (detached != 0)
        {
            Marshal.Release(detached);
        }
    }

    private static T GetVtableDelegate<T>(nint instance, int slot)
        where T : Delegate
    {
        nint vtable = Marshal.ReadIntPtr(instance);
        nint method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetObjectDelegate(nint instance, out nint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestDelegate(nint instance, nint parameters,
        out nint request);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInt32Delegate(nint instance, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetHResultDelegate(nint instance);

    [DllImport("combase.dll", ExactSpelling = true,
        CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString,
        uint length, out nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId,
        ref Guid iid, out nint factory);
}
