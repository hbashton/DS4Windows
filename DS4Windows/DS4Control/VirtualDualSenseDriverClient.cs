/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DS4Windows
{
    public sealed class VirtualDualSenseDriverClient : IDisposable
    {
        public const string DevicePath = @"\\.\HBashtonVirtualDualSense";
        public const string DeviceInterfaceGuid = "{F7F9D9A2-16A8-49D7-AC95-75D9289A1DA6}";
        public const uint IoctlDeviceType = 0x00000022;
        public const uint IoctlMethodBuffered = 0;
        public const uint IoctlFileAnyAccess = 0;
        public const int MaxOutputReportLength = 1024;
        public static readonly uint IoctlCreatePad = CtlCode(IoctlDeviceType, 0x901, IoctlMethodBuffered, IoctlFileAnyAccess);
        public static readonly uint IoctlDestroyPad = CtlCode(IoctlDeviceType, 0x902, IoctlMethodBuffered, IoctlFileAnyAccess);
        public static readonly uint IoctlSubmitInputReport = CtlCode(IoctlDeviceType, 0x903, IoctlMethodBuffered, IoctlFileAnyAccess);
        public static readonly uint IoctlReadOutputReport = CtlCode(IoctlDeviceType, 0x904, IoctlMethodBuffered, IoctlFileAnyAccess);

        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const int FileShareRead = 0x00000001;
        private const int FileShareWrite = 0x00000002;
        private const int OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private static readonly Guid DriverInterfaceGuid = new Guid(DeviceInterfaceGuid);
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private SafeFileHandle handle;
        private uint padId;

        public bool IsConnected => handle != null && !handle.IsInvalid && !handle.IsClosed;

        public void Connect()
        {
            List<string> attemptedPaths = new List<string>();
            int lastError = ErrorFileNotFound;

            foreach (string candidatePath in EnumerateCandidateDevicePaths())
            {
                attemptedPaths.Add(candidatePath);
                handle = CreateFile(candidatePath, GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                    FileAttributeNormal, IntPtr.Zero);

                if (handle != null && !handle.IsInvalid)
                {
                    break;
                }

                lastError = Marshal.GetLastWin32Error();
                handle?.Dispose();
                handle = null;
            }

            if (handle == null || handle.IsInvalid)
            {
                throw new Win32Exception(lastError, BuildUnavailableMessage(lastError, attemptedPaths));
            }

            try
            {
                Span<byte> createBuffer = stackalloc byte[4];
                DeviceIoControlChecked(IoctlCreatePad, ReadOnlySpan<byte>.Empty, createBuffer);
                padId = BitConverter.ToUInt32(createBuffer);
            }
            catch
            {
                handle.Dispose();
                handle = null;
                throw;
            }
        }

        public void SubmitInputReport(byte[] report)
        {
            if (!IsConnected)
            {
                return;
            }

            byte[] buffer = new byte[sizeof(uint) + report.Length];
            BitConverter.GetBytes(padId).CopyTo(buffer, 0);
            Buffer.BlockCopy(report, 0, buffer, sizeof(uint), report.Length);
            DeviceIoControlChecked(IoctlSubmitInputReport, buffer, Span<byte>.Empty);
        }

        public bool TryReadOutputReport(out byte[] report, out uint sequence)
        {
            report = Array.Empty<byte>();
            sequence = 0;

            if (!IsConnected)
            {
                return false;
            }

            Span<byte> padBuffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(padBuffer, padId);

            byte[] outputBuffer = new byte[sizeof(uint) * 3 + MaxOutputReportLength];
            DeviceIoControlChecked(IoctlReadOutputReport, padBuffer, outputBuffer);

            sequence = BitConverter.ToUInt32(outputBuffer, sizeof(uint));
            uint reportLength = BitConverter.ToUInt32(outputBuffer, sizeof(uint) * 2);
            if (reportLength == 0 || reportLength > MaxOutputReportLength)
            {
                return false;
            }

            report = new byte[reportLength];
            Buffer.BlockCopy(outputBuffer, sizeof(uint) * 3, report, 0, report.Length);
            return true;
        }

        public void Disconnect()
        {
            if (!IsConnected)
            {
                return;
            }

            try
            {
                Span<byte> padBuffer = stackalloc byte[4];
                BitConverter.TryWriteBytes(padBuffer, padId);
                DeviceIoControlChecked(IoctlDestroyPad, padBuffer, Span<byte>.Empty);
            }
            catch
            {
                // The driver might already be gone during shutdown.
            }
            finally
            {
                handle.Dispose();
                handle = null;
                padId = 0;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        public static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        {
            return (deviceType << 16) | (access << 14) | (function << 2) | method;
        }

        private static IEnumerable<string> EnumerateCandidateDevicePaths()
        {
            foreach (string devicePath in EnumerateDeviceInterfacePaths())
            {
                yield return devicePath;
            }

            yield return DevicePath;
        }

        private static IEnumerable<string> EnumerateDeviceInterfacePaths()
        {
            List<string> devicePaths = new List<string>();
            Guid interfaceGuid = DriverInterfaceGuid;
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == InvalidHandleValue)
            {
                return devicePaths;
            }

            try
            {
                for (int memberIndex = 0; ; memberIndex++)
                {
                    SP_DEVICE_INTERFACE_DATA interfaceData = new SP_DEVICE_INTERFACE_DATA
                    {
                        cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                    };

                    if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid,
                            memberIndex, ref interfaceData))
                    {
                        break;
                    }

                    string devicePath = GetDeviceInterfacePath(deviceInfoSet, ref interfaceData);
                    if (!string.IsNullOrWhiteSpace(devicePath))
                    {
                        devicePaths.Add(devicePath);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return devicePaths;
        }

        private static string GetDeviceInterfacePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            int requiredSize = 0;
            SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0,
                ref requiredSize, IntPtr.Zero);
            if (requiredSize <= 0)
            {
                return null;
            }

            IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer,
                        requiredSize, ref requiredSize, IntPtr.Zero))
                {
                    return null;
                }

                return Marshal.PtrToStringAuto(IntPtr.Add(detailBuffer, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        private static string BuildUnavailableMessage(int lastError, List<string> attemptedPaths)
        {
            bool looksMissing = lastError == ErrorFileNotFound || lastError == ErrorPathNotFound;
            bool accessDenied = lastError == ErrorAccessDenied;
            string systemMessage = new Win32Exception(lastError).Message;
            string pathList = attemptedPaths.Count > 0 ?
                string.Join(", ", attemptedPaths.Distinct()) :
                DevicePath;

            StringBuilder builder = new StringBuilder();
            if (looksMissing)
            {
                builder.Append("Virtual DualSense driver was not found. ");
                builder.Append("Install the bundled HBashton Virtual DualSense driver package as Administrator, ");
                builder.Append("then restart DS4Windows.");
            }
            else if (accessDenied)
            {
                builder.Append("Virtual DualSense driver is installed but DS4Windows could not open it. ");
                builder.Append("Run DS4Windows as Administrator or reinstall the driver package.");
            }
            else
            {
                builder.Append("Virtual DualSense driver is installed or partially installed, but DS4Windows could not open it. ");
                builder.Append("Restart DS4Windows, reconnect the virtual output, or reinstall the driver package.");
            }

            builder.Append($" Last CreateFile error {lastError}: {systemMessage}. ");
            builder.Append($"Tried: {pathList}.");
            return builder.ToString();
        }

        private void DeviceIoControlChecked(uint ioctl, ReadOnlySpan<byte> input, Span<byte> output)
        {
            unsafe
            {
                fixed (byte* inputPtr = input)
                fixed (byte* outputPtr = output)
                {
                    bool ok = DeviceIoControl(handle, ioctl,
                        input.IsEmpty ? IntPtr.Zero : (IntPtr)inputPtr, input.Length,
                        output.IsEmpty ? IntPtr.Zero : (IntPtr)outputPtr, output.Length,
                        out _, IntPtr.Zero);

                    if (!ok)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            int shareMode, IntPtr securityAttributes, int creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle deviceHandle, uint ioControlCode,
            IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator,
            IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
