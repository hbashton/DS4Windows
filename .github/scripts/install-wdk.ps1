$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [int[]]$AllowedExitCodes = @(0)
    )

    Write-Host "Running: $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "Command failed with exit code $exitCode`: $FilePath $($Arguments -join ' ')"
    }
}

function Install-WinGetPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageId
    )

    Invoke-CheckedCommand -FilePath $script:WinGetPath -Arguments @(
        "install",
        "--id", $PackageId,
        "--exact",
        "--source", "winget",
        "--silent",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )
}

$wingetCommand = Get-Command winget.exe -ErrorAction SilentlyContinue
if (-not $wingetCommand) {
    throw "winget.exe was not found. The Windows Driver Kit cannot be installed on this runner."
}

$script:WinGetPath = $wingetCommand.Source

Invoke-CheckedCommand -FilePath $script:WinGetPath -Arguments @("source", "update")

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vs_installer.exe"
if ((Test-Path $vswhere) -and (Test-Path $vsInstaller)) {
    $vsInstallPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsInstallPath) {
        Invoke-CheckedCommand -FilePath $vsInstaller -AllowedExitCodes @(0, 3010) -Arguments @(
            "modify",
            "--installPath", $vsInstallPath,
            "--quiet",
            "--norestart",
            "--nocache",
            "--add", "Component.Microsoft.Windows.DriverKit",
            "--add", "Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre"
        )
    }
}

# Use the VS2022-compatible WDK family. Microsoft recommends WDK 26100 for
# VS2022 while the newest 28000 line targets VS2026.
Install-WinGetPackage -PackageId "Microsoft.WindowsSDK.10.0.26100"
Install-WinGetPackage -PackageId "Microsoft.WindowsWDK.10.0.26100"

$kitIncludeRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Include"
$ntddk = Get-ChildItem -Path $kitIncludeRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName "km\ntddk.h" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $ntddk) {
    throw "WDK installation completed, but ntddk.h was not found under $kitIncludeRoot."
}

Write-Host "WDK header found: $ntddk"
