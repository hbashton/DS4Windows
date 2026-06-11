param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$rootDeviceName = "HBashtonVirtualDualSense"
$hardwareId = "Root\HBashtonVirtualDualSense"
$infPath = Join-Path $PSScriptRoot "HBashtonVirtualDualSense.inf"
$sysPath = Join-Path $PSScriptRoot "HBashtonVirtualDualSense.sys"
$catPath = Join-Path $PSScriptRoot "HBashtonVirtualDualSense.cat"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window."
    }
}

function Assert-DriverPackage {
    foreach ($path in @($infPath, $sysPath, $catPath)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing driver package file: $path"
        }
    }
}

function Test-RootDevicePresent {
    $output = & pnputil.exe /enum-devices /deviceid $hardwareId 2>&1
    $text = $output | Out-String
    return ($LASTEXITCODE -eq 0) -and ($text -notmatch "No devices were found")
}

Assert-Administrator
Assert-DriverPackage

$infPath = [string](Resolve-Path -LiteralPath $infPath).ProviderPath
$sysPath = [string](Resolve-Path -LiteralPath $sysPath).ProviderPath
$catPath = [string](Resolve-Path -LiteralPath $catPath).ProviderPath

Write-Host "Staging HBashton Virtual DualSense driver package..."
& pnputil.exe /add-driver $infPath | Write-Host
if ($LASTEXITCODE -ne 0) {
    throw "pnputil failed while staging the driver package. Exit code: $LASTEXITCODE"
}

$helperTypeName = "RootDeviceInstaller_" + [Guid]::NewGuid().ToString("N")
$source = @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class $helperTypeName
{
    private const int DICD_GENERATE_ID = 0x00000001;
    private const int DIF_REGISTERDEVICE = 0x00000019;
    private const int SPDRP_HARDWAREID = 0x00000001;
    private const int INSTALLFLAG_FORCE = 0x00000001;
    private const int INSTALLFLAG_NONINTERACTIVE = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    private static readonly Guid SystemClassGuid = new Guid("4d36e97d-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    public static void CreateRootDevice(string rootDeviceName, string hardwareId)
    {
        Guid classGuid = SystemClassGuid;
        IntPtr deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
        {
            ThrowLastWin32("SetupDiCreateDeviceInfoList failed");
        }

        try
        {
            SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA))
            };

            if (!SetupDiCreateDeviceInfo(deviceInfoSet, rootDeviceName, ref classGuid, null,
                    IntPtr.Zero, DICD_GENERATE_ID, ref deviceInfoData))
            {
                ThrowLastWin32("SetupDiCreateDeviceInfo failed");
            }

            byte[] hardwareIdBuffer = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData,
                    SPDRP_HARDWAREID, hardwareIdBuffer, hardwareIdBuffer.Length))
            {
                ThrowLastWin32("SetupDiSetDeviceRegistryProperty(SPDRP_HARDWAREID) failed");
            }

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, deviceInfoSet, ref deviceInfoData))
            {
                ThrowLastWin32("SetupDiCallClassInstaller(DIF_REGISTERDEVICE) failed");
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public static bool UpdateDriver(string hardwareId, string infPath, bool force)
    {
        int flags = INSTALLFLAG_NONINTERACTIVE;
        if (force)
        {
            flags |= INSTALLFLAG_FORCE;
        }

        bool rebootRequired;
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, flags, out rebootRequired))
        {
            ThrowLastWin32("UpdateDriverForPlugAndPlayDevices failed");
        }

        return rebootRequired;
    }

    private static void ThrowLastWin32(string message)
    {
        int error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, message + ". Win32 error " + error);
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData, int Property, byte[] PropertyBuffer,
        int PropertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int InstallFunction,
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent,
        string HardwareId, string FullInfPath, int InstallFlags, out bool bRebootRequired);
}
"@

$installerType = Add-Type -TypeDefinition $source -PassThru | Where-Object { $_.Name -eq $helperTypeName } | Select-Object -First 1
if ($installerType -eq $null) {
    throw "Failed to compile root device installer helper."
}

if (-not (Test-RootDevicePresent)) {
    Write-Host "Creating root-enumerated device $hardwareId..."
    $createRootArgs = New-Object 'object[]' 2
    $createRootArgs[0] = [string]$rootDeviceName
    $createRootArgs[1] = [string]$hardwareId
    $installerType.GetMethod("CreateRootDevice").Invoke($null, $createRootArgs)
} else {
    Write-Host "Root-enumerated device already exists."
}

Write-Host "Binding driver package to $hardwareId..."
$updateDriverArgs = New-Object 'object[]' 3
$updateDriverArgs[0] = [string]$hardwareId
$updateDriverArgs[1] = [string]$infPath
$updateDriverArgs[2] = [bool]$Force.IsPresent
$rebootRequired = [bool]$installerType.GetMethod("UpdateDriver").Invoke($null, $updateDriverArgs)

Write-Host "Refreshing Plug and Play device tree..."
& pnputil.exe /scan-devices | Write-Host

Write-Host ""
if ($rebootRequired) {
    Write-Host "Installed. Windows reported that a reboot is required."
} else {
    Write-Host "Installed. Restart DS4Windows before creating a virtual DualSense output."
}
