[CmdletBinding()]
param(
    [int]$DurationSeconds = 60,
    [string]$LogPath = "",
    [switch]$IncludeUnchangedHid
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    $script = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($script)) {
        throw "This script must be saved to disk before it can self-elevate."
    }

    $argsList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$script`"",
        "-DurationSeconds", $DurationSeconds
    )
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        $argsList += @("-LogPath", "`"$LogPath`"")
    }
    if ($IncludeUnchangedHid) {
        $argsList += "-IncludeUnchangedHid"
    }

    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argsList
    return
}

if ($DurationSeconds -lt 5) {
    $DurationSeconds = 5
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $LogPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "ds4windows-windows-input-debugger-$stamp.log"
}

$LogPath = [IO.Path]::GetFullPath($LogPath)
$logDir = Split-Path -Parent $LogPath
if (-not [string]::IsNullOrWhiteSpace($logDir) -and -not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

function Add-LogLine {
    param([string]$Message)
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

Add-LogLine "WINDOWS_INPUT_DEBUGGER_START pid=$PID admin=True durationSeconds=$DurationSeconds includeUnchangedHid=$($IncludeUnchangedHid.IsPresent)"
Add-LogLine "OS=$([Environment]::OSVersion.VersionString) user=$env:USERNAME computer=$env:COMPUTERNAME"
Add-LogLine "PowerShell=$($PSVersionTable.PSVersion)"

Add-LogLine "PNP_SNAPSHOT_BEGIN"
Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FriendlyName -match "DualSense|Wireless Controller|VIIPER|HBashton|Virtual|Gamepad|Controller|Keyboard|Mouse|HID"
    } |
    Sort-Object Class,FriendlyName,InstanceId |
    ForEach-Object {
        Add-LogLine ("PNP status={0} class={1} friendly=""{2}"" instance=""{3}""" -f $_.Status, $_.Class, $_.FriendlyName, $_.InstanceId)
    }
Add-LogLine "PNP_SNAPSHOT_END"

$source = @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DS4Windows.Diagnostics
{
    public sealed class RawInputDebugger : NativeWindow, IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        private const int GIDC_ARRIVAL = 1;
        private const int GIDC_REMOVAL = 2;
        private const int RIM_TYPEMOUSE = 0;
        private const int RIM_TYPEKEYBOARD = 1;
        private const int RIM_TYPEHID = 2;
        private const int RIDEV_INPUTSINK = 0x00000100;
        private const int RIDEV_DEVNOTIFY = 0x00002000;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;

        private readonly object gate = new object();
        private readonly Dictionary<string, DeviceStats> stats = new Dictionary<string, DeviceStats>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IntPtr, string> deviceNameCache = new Dictionary<IntPtr, string>();
        private readonly StreamWriter log;
        private readonly bool includeUnchangedHid;
        private readonly System.Threading.Timer summaryTimer;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public int dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public int dwType;
            public int dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice,
            uint uiCommand,
            StringBuilder pData,
            ref uint pcbSize);

        public static void Run(string logPath, int durationSeconds, bool includeUnchangedHid)
        {
            using (var debugger = new RawInputDebugger(logPath, includeUnchangedHid))
            {
                debugger.WriteLine("RAW_INPUT_LOOP_START durationSeconds=" + durationSeconds);
                using (var stopTimer = new System.Windows.Forms.Timer())
                {
                    stopTimer.Interval = Math.Max(5, durationSeconds) * 1000;
                    stopTimer.Tick += delegate
                    {
                        stopTimer.Stop();
                        Application.ExitThread();
                    };
                    stopTimer.Start();
                    Application.Run(new ApplicationContext());
                }
                debugger.WriteSummary(true);
                debugger.WriteLine("RAW_INPUT_LOOP_END");
            }
        }

        private RawInputDebugger(string logPath, bool includeUnchangedHid)
        {
            this.includeUnchangedHid = includeUnchangedHid;
            log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
            log.AutoFlush = true;

            CreateParams cp = new CreateParams();
            cp.Caption = "DS4Windows Windows Input Debugger";
            cp.X = 0;
            cp.Y = 0;
            cp.Width = 1;
            cp.Height = 1;
            CreateHandle(cp);

            RegisterDevices();
            summaryTimer = new System.Threading.Timer(_ => WriteSummary(false), null, 1000, 1000);
        }

        public void Dispose()
        {
            summaryTimer.Dispose();
            DestroyHandle();
            log.Dispose();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }
            else if (m.Msg == WM_INPUT_DEVICE_CHANGE)
            {
                string action = m.WParam.ToInt32() == GIDC_ARRIVAL ? "arrival" :
                    m.WParam.ToInt32() == GIDC_REMOVAL ? "removal" : "unknown";
                WriteLine("RAW_DEVICE_CHANGE action=" + action + " hDevice=0x" + m.LParam.ToInt64().ToString("X") +
                    " name=\"" + Escape(GetDeviceName(m.LParam)) + "\"");
            }

            base.WndProc(ref m);
        }

        private void RegisterDevices()
        {
            int flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[]
            {
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = flags, hwndTarget = Handle }, // Mouse
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = flags, hwndTarget = Handle }, // Keyboard
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = flags, hwndTarget = Handle }, // Joystick
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = flags, hwndTarget = Handle }, // Gamepad
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x08, dwFlags = flags, hwndTarget = Handle }, // Multi-axis
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x80, dwFlags = flags, hwndTarget = Handle }, // System control
                new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 0x01, dwFlags = flags, hwndTarget = Handle }, // Consumer control
            };

            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed");
            }

            WriteLine("RAW_REGISTERED usages=mouse,keyboard,joystick,gamepad,multiaxis,system,consumer hwnd=0x" + Handle.ToInt64().ToString("X"));
        }

        private void HandleRawInput(IntPtr rawInputHandle)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint probe = GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (probe != 0 || size == 0)
            {
                WriteLine("RAW_ERROR stage=size win32=" + Marshal.GetLastWin32Error() + " size=" + size);
                return;
            }

            byte[] buffer = new byte[size];
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = pinned.AddrOfPinnedObject();
                uint read = GetRawInputData(rawInputHandle, RID_INPUT, ptr, ref size, headerSize);
                if (read == UInt32.MaxValue || read != size)
                {
                    WriteLine("RAW_ERROR stage=read win32=" + Marshal.GetLastWin32Error() + " read=" + read + " size=" + size);
                    return;
                }

                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(ptr, typeof(RAWINPUTHEADER));
                string name = GetDeviceName(header.hDevice);
                int body = (int)headerSize;
                if (header.dwType == RIM_TYPEKEYBOARD)
                {
                    HandleKeyboard(buffer, body, header, name);
                }
                else if (header.dwType == RIM_TYPEMOUSE)
                {
                    HandleMouse(buffer, body, header, name);
                }
                else if (header.dwType == RIM_TYPEHID)
                {
                    HandleHid(buffer, body, header, name);
                }
                else
                {
                    WriteLine("RAW_UNKNOWN type=" + header.dwType + " size=" + header.dwSize +
                        " hDevice=0x" + header.hDevice.ToInt64().ToString("X") + " name=\"" + Escape(name) + "\"");
                }
            }
            finally
            {
                pinned.Free();
            }
        }

        private void HandleKeyboard(byte[] buffer, int body, RAWINPUTHEADER header, string name)
        {
            if (buffer.Length < body + 16)
            {
                WriteLine("RAW_KEYBOARD_SHORT len=" + buffer.Length + " name=\"" + Escape(name) + "\"");
                return;
            }

            ushort makeCode = BitConverter.ToUInt16(buffer, body);
            ushort flags = BitConverter.ToUInt16(buffer, body + 2);
            ushort vkey = BitConverter.ToUInt16(buffer, body + 6);
            uint message = BitConverter.ToUInt32(buffer, body + 8);
            uint extra = BitConverter.ToUInt32(buffer, body + 12);
            string key = DeviceKey("keyboard", name);
            RecordEvent(key, "keyboard", name, true, "");
            WriteLine("RAW_KEYBOARD hDevice=0x" + header.hDevice.ToInt64().ToString("X") +
                " name=\"" + Escape(name) + "\" make=0x" + makeCode.ToString("X4") +
                " flags=0x" + flags.ToString("X4") + " vkey=0x" + vkey.ToString("X4") +
                " msg=0x" + message.ToString("X4") + " extra=0x" + extra.ToString("X8"));
        }

        private void HandleMouse(byte[] buffer, int body, RAWINPUTHEADER header, string name)
        {
            if (buffer.Length < body + 24)
            {
                WriteLine("RAW_MOUSE_SHORT len=" + buffer.Length + " name=\"" + Escape(name) + "\"");
                return;
            }

            ushort flags = BitConverter.ToUInt16(buffer, body);
            uint buttons = BitConverter.ToUInt32(buffer, body + 4);
            int dx = BitConverter.ToInt32(buffer, body + 12);
            int dy = BitConverter.ToInt32(buffer, body + 16);
            uint extra = BitConverter.ToUInt32(buffer, body + 20);
            string key = DeviceKey("mouse", name);
            RecordEvent(key, "mouse", name, true, "");
            WriteLine("RAW_MOUSE hDevice=0x" + header.hDevice.ToInt64().ToString("X") +
                " name=\"" + Escape(name) + "\" flags=0x" + flags.ToString("X4") +
                " buttons=0x" + buttons.ToString("X8") + " dx=" + dx + " dy=" + dy +
                " extra=0x" + extra.ToString("X8"));
        }

        private void HandleHid(byte[] buffer, int body, RAWINPUTHEADER header, string name)
        {
            if (buffer.Length < body + 8)
            {
                WriteLine("RAW_HID_SHORT len=" + buffer.Length + " name=\"" + Escape(name) + "\"");
                return;
            }

            int sizeHid = BitConverter.ToInt32(buffer, body);
            int count = BitConverter.ToInt32(buffer, body + 4);
            int dataOffset = body + 8;
            int rawLength = Math.Max(0, Math.Min(buffer.Length - dataOffset, sizeHid * count));
            string hex = ToHex(buffer, dataOffset, Math.Min(rawLength, 96));
            string key = DeviceKey("hid", name);
            bool changed = RecordEvent(key, "hid", name, false, hex);
            if (includeUnchangedHid || changed)
            {
                WriteLine("RAW_HID" + (changed ? "_CHANGE" : "") +
                    " hDevice=0x" + header.hDevice.ToInt64().ToString("X") +
                    " name=\"" + Escape(name) + "\" sizeHid=" + sizeHid + " count=" + count +
                    " rawLen=" + rawLength + " first96=" + hex);
            }
        }

        private bool RecordEvent(string key, string type, string name, bool forceChange, string lastHex)
        {
            lock (gate)
            {
                DeviceStats stat;
                if (!stats.TryGetValue(key, out stat))
                {
                    stat = new DeviceStats { Type = type, Name = name };
                    stats[key] = stat;
                }

                stat.Events++;
                bool changed = forceChange || !String.Equals(stat.LastHex, lastHex, StringComparison.Ordinal);
                if (changed)
                {
                    stat.Changes++;
                    stat.LastChangeUtc = DateTime.UtcNow;
                    stat.LastChangeHex = lastHex;
                }

                stat.LastHex = lastHex;
                return changed;
            }
        }

        private void WriteSummary(bool final)
        {
            lock (gate)
            {
                foreach (DeviceStats stat in stats.Values)
                {
                    long eventDelta = stat.Events - stat.LastSummaryEvents;
                    long changeDelta = stat.Changes - stat.LastSummaryChanges;
                    if (!final && eventDelta == 0 && changeDelta == 0)
                    {
                        continue;
                    }

                    stat.LastSummaryEvents = stat.Events;
                    stat.LastSummaryChanges = stat.Changes;
                    WriteLineLocked("RAW_SUMMARY final=" + final +
                        " type=" + stat.Type +
                        " events=" + stat.Events +
                        " changes=" + stat.Changes +
                        " eventDelta=" + eventDelta +
                        " changeDelta=" + changeDelta +
                        " lastChangeUtc=" + (stat.LastChangeUtc == DateTime.MinValue ? "<none>" : stat.LastChangeUtc.ToString("O")) +
                        " name=\"" + Escape(stat.Name) + "\"" +
                        " lastChangeFirst96=" + stat.LastChangeHex);
                }
            }
        }

        private string GetDeviceName(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero)
            {
                return "<zero>";
            }

            lock (gate)
            {
                string cached;
                if (deviceNameCache.TryGetValue(hDevice, out cached))
                {
                    return cached;
                }
            }

            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, null, ref size);
            if (size == 0)
            {
                return "<unknown:" + hDevice.ToInt64().ToString("X") + ">";
            }

            StringBuilder builder = new StringBuilder((int)size + 1);
            uint result = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, builder, ref size);
            string name = result == UInt32.MaxValue ? "<error:" + Marshal.GetLastWin32Error() + ">" : builder.ToString();
            lock (gate)
            {
                deviceNameCache[hDevice] = name;
            }
            return name;
        }

        private static string DeviceKey(string type, string name)
        {
            return type + "|" + (name ?? "");
        }

        private void WriteLine(string message)
        {
            lock (gate)
            {
                WriteLineLocked(message);
            }
        }

        private void WriteLineLocked(string message)
        {
            log.WriteLine(DateTime.UtcNow.ToString("O") + " " + message);
        }

        private static string ToHex(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0 || offset >= data.Length)
            {
                return "";
            }

            int end = Math.Min(data.Length, offset + count);
            StringBuilder builder = new StringBuilder((end - offset) * 3);
            for (int i = offset; i < end; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(data[i].ToString("X2"));
            }
            return builder.ToString();
        }

        private static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private sealed class DeviceStats
        {
            public string Type;
            public string Name;
            public long Events;
            public long Changes;
            public long LastSummaryEvents;
            public long LastSummaryChanges;
            public DateTime LastChangeUtc;
            public string LastHex = "";
            public string LastChangeHex = "";
        }
    }
}
"@

if (-not ("DS4Windows.Diagnostics.RawInputDebugger" -as [type])) {
    Add-Type -TypeDefinition $source -ReferencedAssemblies @("System.Windows.Forms.dll", "System.Drawing.dll")
}

Write-Host "DS4Windows Windows input debugger is running as Administrator."
Write-Host "Log: $LogPath"
Write-Host "Duration: $DurationSeconds seconds"
Write-Host "Start DS4Windows and reproduce the issue without touching the controller until the timer exits."

[DS4Windows.Diagnostics.RawInputDebugger]::Run($LogPath, $DurationSeconds, [bool]$IncludeUnchangedHid)

Add-LogLine "EVENTLOG_SNAPSHOT_BEGIN"
$start = (Get-Date).AddSeconds(-1 * ($DurationSeconds + 30))
Get-WinEvent -FilterHashtable @{LogName = "System"; StartTime = $start} -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ProviderName -match "HidBth|BTHUSB|Kernel-PnP|HIDCLASS|UserPnp|Kernel-Input" -or
        $_.Message -match "Wireless Controller|DualSense|Bluetooth HID|HID device|USB\\VID_054C|HBashton|VIIPER"
    } |
    Sort-Object TimeCreated |
    ForEach-Object {
        $message = (($_.Message -replace "`r?`n", " ") -replace "\s+", " ")
        Add-LogLine ("EVENTLOG_SYSTEM time={0:o} provider={1} id={2} level={3} message=""{4}""" -f $_.TimeCreated, $_.ProviderName, $_.Id, $_.LevelDisplayName, ($message -replace '"', '\"'))
    }
Get-WinEvent -FilterHashtable @{LogName = "Application"; StartTime = $start} -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ProviderName -match "Application Error|.NET Runtime|Windows Error Reporting|DS4Windows|VIIPER" -or
        $_.Message -match "DS4Windows|VIIPER|HBashton"
    } |
    Sort-Object TimeCreated |
    ForEach-Object {
        $message = (($_.Message -replace "`r?`n", " ") -replace "\s+", " ")
        Add-LogLine ("EVENTLOG_APPLICATION time={0:o} provider={1} id={2} level={3} message=""{4}""" -f $_.TimeCreated, $_.ProviderName, $_.Id, $_.LevelDisplayName, ($message -replace '"', '\"'))
    }
Add-LogLine "EVENTLOG_SNAPSHOT_END"
Add-LogLine "WINDOWS_INPUT_DEBUGGER_END"

Write-Host "Done. Log written to: $LogPath"
