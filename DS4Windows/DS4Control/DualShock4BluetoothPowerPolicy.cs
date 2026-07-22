using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Keeps the USB-hosted Bluetooth radio out of selective suspend while a
    /// physical DualShock 4 audio lane is being armed. The DS4 speaker has a
    /// very small decoder FIFO; a radio resume pause is audible even when the
    /// application submitted every SBC frame on time.
    /// </summary>
    internal static class DualShock4BluetoothPowerPolicy
    {
        private static readonly Guid UsbSettingsSubgroup =
            new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid UsbSelectiveSuspendSetting =
            new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        private const uint Disabled = 0;
        private const uint ErrorSuccess = 0;
        private static int changedLogWritten;
        private static int failureLogWritten;

        internal static bool EnsureDisabledForActivePowerScheme()
        {
            IntPtr schemePointer = IntPtr.Zero;
            try
            {
                uint result = PowerGetActiveScheme(IntPtr.Zero,
                    out schemePointer);
                if (result != ErrorSuccess || schemePointer == IntPtr.Zero)
                {
                    LogFailure($"PowerGetActiveScheme returned {result}");
                    return false;
                }

                Guid scheme = Marshal.PtrToStructure<Guid>(schemePointer);
                Guid subgroup = UsbSettingsSubgroup;
                Guid setting = UsbSelectiveSuspendSetting;
                result = PowerReadACValueIndex(IntPtr.Zero, ref scheme,
                    ref subgroup, ref setting, out uint acValue);
                if (result != ErrorSuccess)
                {
                    LogFailure($"PowerReadACValueIndex returned {result}");
                    return false;
                }

                result = PowerReadDCValueIndex(IntPtr.Zero, ref scheme,
                    ref subgroup, ref setting, out uint dcValue);
                if (result != ErrorSuccess)
                {
                    LogFailure($"PowerReadDCValueIndex returned {result}");
                    return false;
                }

                if (!ShouldApply(acValue, dcValue))
                {
                    return true;
                }

                result = PowerWriteACValueIndex(IntPtr.Zero, ref scheme,
                    ref subgroup, ref setting, Disabled);
                if (result != ErrorSuccess)
                {
                    LogFailure($"PowerWriteACValueIndex returned {result}");
                    return false;
                }

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme,
                    ref subgroup, ref setting, Disabled);
                if (result != ErrorSuccess)
                {
                    LogFailure($"PowerWriteDCValueIndex returned {result}");
                    return false;
                }

                result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                if (result != ErrorSuccess)
                {
                    LogFailure($"PowerSetActiveScheme returned {result}");
                    return false;
                }

                if (Interlocked.Exchange(ref changedLogWritten, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "Disabled USB selective suspend for the active power plan to keep DualShock 4 Bluetooth audio uninterrupted.",
                        false);
                }
                return true;
            }
            catch (Exception ex)
            {
                LogFailure(ex.Message);
                return false;
            }
            finally
            {
                if (schemePointer != IntPtr.Zero)
                {
                    LocalFree(schemePointer);
                }
            }
        }

        internal static bool ShouldApply(uint acValue, uint dcValue)
        {
            return acValue != Disabled || dcValue != Disabled;
        }

        private static void LogFailure(string reason)
        {
            if (Interlocked.Exchange(ref failureLogWritten, 1) == 0)
            {
                AppLogger.LogToGui(
                    $"Could not disable USB selective suspend for DualShock 4 Bluetooth audio ({reason}). Audio will continue, but the Bluetooth radio may introduce brief gaps.",
                    true);
            }
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey,
            out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey,
            ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid,
            out uint valueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey,
            ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid,
            out uint valueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey,
            ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid,
            uint valueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey,
            ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid,
            uint valueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey,
            ref Guid schemeGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
