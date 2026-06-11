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
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

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

        private SafeFileHandle handle;
        private uint padId;

        public bool IsConnected => handle != null && !handle.IsInvalid && !handle.IsClosed;

        public void Connect()
        {
            handle = CreateFile(DevicePath, FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero,
                FileMode.Open, FileAttributes.Normal, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Virtual DualSense driver is not installed or its device interface is unavailable.");
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
        private static extern SafeFileHandle CreateFile(string fileName, FileAccess desiredAccess,
            FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition,
            FileAttributes flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle deviceHandle, uint ioControlCode,
            IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);
    }
}
