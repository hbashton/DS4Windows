/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using DS4Windows;

namespace DS4WinWPF.DS4Control
{
    class HidHideAPIDevice : IDisposable
    {
        private const uint IOCTL_GET_WHITELIST = 0x80016000;
        private const uint IOCTL_SET_WHITELIST = 0x80016004;
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const uint IOCTL_GET_ACTIVE = 0x80016010;
        private const uint IOCTL_SET_ACTIVE = 0x80016014;
        private const uint IOCTL_GET_WL_INVERT = 0x80016018;
        private const uint IOCTL_SET_WL_INVERT = 0x8001601C;
        private const uint IOCTL_ADD_SESSION_BLACKLIST = 0x80016020;
        private const uint IOCTL_CLR_SESSION_BLACKLIST = 0x80016024;

        private const string CONTROL_DEVICE_FILENAME = "\\\\.\\HidHide";

        private SafeHandle hidHideHandle;

        public HidHideAPIDevice(bool writeAccess = true)
        {
            uint desiredAccess = NativeMethods.GENERIC_READ;
            if (writeAccess)
            {
                desiredAccess |= NativeMethods.GENERIC_WRITE;
            }

            hidHideHandle = NativeMethods.CreateFile(CONTROL_DEVICE_FILENAME,
                    desiredAccess,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OpenExisting,
                    NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);
        }

        public bool GetActiveState()
        {
            return TryGetActiveState(out bool state) && state;
        }

        public bool TryGetActiveState(out bool state)
        {
            return TryGetBoolean(IOCTL_GET_ACTIVE, out state);
        }

        private bool TryGetBoolean(uint controlCode, out bool state)
        {
            state = false;
            if (!IsOpen()) return false;

            unsafe
            {
                bool value = false;
                int bytesReturned = 0;
                bool result = NativeMethods.DeviceIoControl(
                    hidHideHandle.DangerousGetHandle(),
                    controlCode,
                    IntPtr.Zero,
                    0,
                    new IntPtr(&value),
                    1,
                    ref bytesReturned,
                    IntPtr.Zero);
                state = value;
                return result && bytesReturned >= 1;
            }
        }

        public bool SetActiveState(bool state)
        {
            bool result = false;

            unsafe
            {
                int bytesReturned = 0;
                result = NativeMethods.DeviceIoControl(hidHideHandle.DangerousGetHandle(),
                    HidHideAPIDevice.IOCTL_SET_ACTIVE,
                    new IntPtr(&state),
                    1,
                    IntPtr.Zero,
                    0,
                    ref bytesReturned,
                    IntPtr.Zero);

                //int error = Marshal.GetLastWin32Error();
            }

            return result;
        }

        public List<string> GetBlacklist()
        {
            return TryGetBlacklist(out List<string> instances) ? instances :
                new List<string>();
        }

        /// <summary>
        /// Reads the persistent blacklist without conflating a driver/query
        /// failure with a valid empty list.  Callers performing read-modify-
        /// write must use this method so an unavailable HidHide device can
        /// never erase entries owned by the user or another application.
        /// </summary>
        public bool TryGetBlacklist(out List<string> instances)
        {
            return TryGetStringList(IOCTL_GET_BLACKLIST, out instances);
        }

        public bool SetBlacklist(List<string> instances)
        {
            bool result = false;
            int bytesReturned = 0;
            IntPtr inBuffer =
                StringListToMultiSzPointer(instances, out int inBufferLength);

            result = NativeMethods.DeviceIoControl(hidHideHandle.DangerousGetHandle(),
                IOCTL_SET_BLACKLIST,
                inBuffer,
                inBufferLength,
                IntPtr.Zero,
                0,
                ref bytesReturned,
                IntPtr.Zero);

            //int error = Marshal.GetLastWin32Error();
            // Free buffer returned from StringListToMultiSzPointer
            Marshal.FreeHGlobal(inBuffer);

            return result;
        }

        /// <summary>
        /// Adds device instance paths to a process-lifetime blacklist.
        /// Entries are automatically removed by HidHide when this process exits,
        /// regardless of whether the exit is clean or due to a crash.
        /// Requires a HidHide build with session blacklist support. Released
        /// HidHide 1.5 builds do not expose this API, so callers need a fallback.
        /// </summary>
        public bool AddSessionBlacklist(List<string> instances)
        {
            if (instances == null || instances.Count == 0) return true;

            int bytesReturned = 0;
            IntPtr inBuffer = StringListToMultiSzPointer(instances, out int inBufferLength);

            bool result = NativeMethods.DeviceIoControl(hidHideHandle.DangerousGetHandle(),
                IOCTL_ADD_SESSION_BLACKLIST,
                inBuffer,
                inBufferLength,
                IntPtr.Zero,
                0,
                ref bytesReturned,
                IntPtr.Zero);

            Marshal.FreeHGlobal(inBuffer);
            return result;
        }

        /// <summary>
        /// Removes all session blacklist entries registered by this process.
        /// Called automatically by HidHide on process exit; only needed for explicit early release.
        /// </summary>
        public bool ClearSessionBlacklist()
        {
            int bytesReturned = 0;
            return NativeMethods.DeviceIoControl(hidHideHandle.DangerousGetHandle(),
                IOCTL_CLR_SESSION_BLACKLIST,
                IntPtr.Zero, 0, IntPtr.Zero, 0,
                ref bytesReturned, IntPtr.Zero);
        }

        public List<string> GetWhitelist()
        {
            return TryGetWhitelist(out List<string> instances) ? instances :
                new List<string>();
        }

        public bool TryGetWhitelist(out List<string> instances)
        {
            return TryGetStringList(IOCTL_GET_WHITELIST, out instances);
        }

        private bool TryGetStringList(uint controlCode,
            out List<string> instances)
        {
            instances = new List<string>();
            if (!IsOpen()) return false;

            int requiredBytes = 0;
            bool sizeQuery = NativeMethods.DeviceIoControl(
                hidHideHandle.DangerousGetHandle(), controlCode,
                IntPtr.Zero, 0, IntPtr.Zero, 0, ref requiredBytes,
                IntPtr.Zero);
            if (requiredBytes <= 0)
            {
                return sizeQuery;
            }

            IntPtr buffer = Marshal.AllocHGlobal(requiredBytes);
            try
            {
                int bytesReturned = 0;
                bool result = NativeMethods.DeviceIoControl(
                    hidHideHandle.DangerousGetHandle(), controlCode,
                    IntPtr.Zero, 0, buffer, requiredBytes,
                    ref bytesReturned, IntPtr.Zero);
                if (!result)
                {
                    return false;
                }

                int bytesToCopy = bytesReturned > 0 ?
                    Math.Min(bytesReturned, requiredBytes) : requiredBytes;
                byte[] data = new byte[bytesToCopy];
                Marshal.Copy(buffer, data, 0, bytesToCopy);
                string value = Encoding.Unicode.GetString(data)
                    .TrimEnd(char.MinValue);
                if (value.Length > 0)
                {
                    instances.AddRange(value.Split(char.MinValue)
                        .Where(item => !string.IsNullOrWhiteSpace(item)));
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool SetWhitelist(List<string> instances)
        {
            bool result = false;
            int bytesReturned = 0;
            IntPtr inBuffer =
                StringListToMultiSzPointer(instances, out int inBufferLength);

            result = NativeMethods.DeviceIoControl(hidHideHandle.DangerousGetHandle(),
                IOCTL_SET_WHITELIST,
                inBuffer,
                inBufferLength,
                IntPtr.Zero,
                0,
                ref bytesReturned,
                IntPtr.Zero);

            //int error = Marshal.GetLastWin32Error();
            // Free buffer returned from StringListToMultiSzPointer
            Marshal.FreeHGlobal(inBuffer);

            return result;
        }

        public bool GetWhiteListInverseState()
        {
            return TryGetWhitelistInverseState(out bool state) && state;
        }

        public bool TryGetWhitelistInverseState(out bool state)
        {
            return TryGetBoolean(IOCTL_GET_WL_INVERT, out state);
        }

        public bool SetWhitelistInverseState(bool state)
        {
            bool result = false;

            unsafe
            {
                int bytesReturned = 0;
                NativeMethods.DeviceIoControl(hidHideHandle.DangerousGetHandle(),
                    HidHideAPIDevice.IOCTL_SET_WL_INVERT,
                    new IntPtr(&state),
                    1,
                    IntPtr.Zero,
                    0,
                    ref bytesReturned,
                    IntPtr.Zero);

                //int error = Marshal.GetLastWin32Error();
            }

            return result;
        }

        public bool IsOpen()
        {
            return hidHideHandle != null && (!hidHideHandle.IsClosed && !hidHideHandle.IsInvalid);
        }

        public void Close()
        {
            if (IsOpen())
            {
                hidHideHandle.Close();
                hidHideHandle.Dispose();
                hidHideHandle = null;
            }
        }

        public void Dispose()
        {
            Close();
        }

        private IntPtr StringListToMultiSzPointer(List<string> strList,
            out int length)
        {
            // Temporary byte list
            IEnumerable<byte> multiSz = new List<byte>();

            // Convert each string into wide multi-byte and add NULL-terminator in between
            multiSz = strList.Aggregate(multiSz,
                (current, entry) =>
                {
                    return current.Concat(Encoding.Unicode.GetBytes(entry))
                                    .Concat(Encoding.Unicode.GetBytes(new[] { char.MinValue }));
                });

            // Add another NULL-terminator to signal end of list
            multiSz = multiSz.Concat(Encoding.Unicode.GetBytes(new[] { char.MinValue }));

            // Convert list to array
            byte[] multiSzArray = multiSz.ToArray();

            // Copy array content to allocated buffer
            length = multiSzArray.Length;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            Marshal.Copy(multiSzArray, 0, buffer, length);

            // Return IntPtr to caller. Caller MUST free data when finished with it
            return buffer;
        }
    }
}
