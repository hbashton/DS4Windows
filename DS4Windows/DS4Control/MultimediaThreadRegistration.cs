using System;
using System.Runtime.InteropServices;

namespace DS4Windows
{
    /// <summary>
    /// Registers a short-lived, event-driven transport worker with MMCSS.
    /// Only the isolated physical HID presenter uses Critical; every upstream
    /// producer/dispatcher uses High so UI and lifecycle work cannot consume a
    /// media deadline without turning the process into a realtime process.
    /// </summary>
    // A ref struct cannot be boxed, captured by a worker, or survive an await.
    // That restriction is intentional: Windows requires the AVRT handle to be
    // reverted by the same native thread that registered it.
    internal ref struct MultimediaThreadRegistration
    {
        private IntPtr handle;
        private readonly int error;

        private MultimediaThreadRegistration(IntPtr handle, int error)
        {
            this.handle = handle;
            this.error = error;
        }

        internal bool IsActive => handle != IntPtr.Zero;
        internal int Error => error;

        internal static MultimediaThreadRegistration EnterProAudio(
            bool critical = false)
        {
            return Enter("Pro Audio", critical ? AvrtPriority.Critical :
                AvrtPriority.High);
        }

        internal static MultimediaThreadRegistration EnterGames()
        {
            return Enter("Games", AvrtPriority.High);
        }

        private static MultimediaThreadRegistration Enter(string taskName,
            AvrtPriority priority)
        {
            try
            {
                uint taskIndex = 0;
                IntPtr handle = AvSetMmThreadCharacteristicsW(taskName,
                    ref taskIndex);
                if (handle == IntPtr.Zero)
                {
                    return new MultimediaThreadRegistration(IntPtr.Zero,
                        Marshal.GetLastWin32Error());
                }

                int error = AvSetMmThreadPriority(handle, priority) ? 0 :
                    Marshal.GetLastWin32Error();
                return new MultimediaThreadRegistration(handle, error);
            }
            catch (DllNotFoundException)
            {
                return new MultimediaThreadRegistration(IntPtr.Zero, -1);
            }
            catch (EntryPointNotFoundException)
            {
                return new MultimediaThreadRegistration(IntPtr.Zero, -2);
            }
        }

        public void Dispose()
        {
            IntPtr activeHandle = handle;
            handle = IntPtr.Zero;
            if (activeHandle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(activeHandle);
            }
        }

        private enum AvrtPriority
        {
            Normal = 0,
            High = 1,
            Critical = 2,
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(
            string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle,
            AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(
            IntPtr avrtHandle);
    }
}
