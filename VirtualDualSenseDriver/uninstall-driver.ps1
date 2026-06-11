param(
    [switch]$Force,
    [switch]$DisableTestSigning
)

$ErrorActionPreference = "Stop"

$hardwareId = "Root\HBashtonVirtualDualSense"
$originalInfName = "HBashtonVirtualDualSense.inf"
$serviceName = "HBashtonVirtualDualSense"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window."
    }
}

function Invoke-PnPUtil {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$IgnoreFailure
    )

    $output = & pnputil.exe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }

    if ($exitCode -ne 0 -and -not $IgnoreFailure) {
        throw "pnputil failed with exit code $exitCode. Arguments: $($Arguments -join ' ')"
    }

    return @($output)
}

function Get-RootDeviceInstanceIds {
    $output = & pnputil.exe /enum-devices /deviceid $hardwareId /deviceids 2>&1
    $text = $output | Out-String
    if ($LASTEXITCODE -ne 0 -or $text -match "No devices were found") {
        return @()
    }

    $ids = @()
    foreach ($line in $output) {
        if ($line -match "^\s*Instance ID:\s*(.+?)\s*$") {
            $ids += $matches[1].Trim()
        }
    }

    return @($ids | Select-Object -Unique)
}

function Get-HBashtonDriverPackages {
    $output = & pnputil.exe /enum-drivers 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "pnputil failed while enumerating driver packages. Exit code: $LASTEXITCODE"
    }

    $records = @()
    $current = $null

    foreach ($line in $output) {
        if ($line -match "^\s*Published Name\s*:\s*(.+?)\s*$") {
            if ($current -ne $null) {
                $records += [pscustomobject]$current
            }

            $current = [ordered]@{
                PublishedName = $matches[1].Trim()
                OriginalName = ""
                ProviderName = ""
            }
            continue
        }

        if ($current -eq $null) {
            continue
        }

        if ($line -match "^\s*Original Name\s*:\s*(.+?)\s*$") {
            $current.OriginalName = $matches[1].Trim()
        } elseif ($line -match "^\s*Provider Name\s*:\s*(.+?)\s*$") {
            $current.ProviderName = $matches[1].Trim()
        }
    }

    if ($current -ne $null) {
        $records += [pscustomobject]$current
    }

    return @($records | Where-Object { $_.OriginalName -ieq $originalInfName })
}

Assert-Administrator

Write-Host "Close DS4Windows before uninstalling the virtual DualSense driver."
Write-Host ""

$deviceIds = Get-RootDeviceInstanceIds
if ($deviceIds.Count -eq 0) {
    Write-Host "No root-enumerated HBashton Virtual DualSense device was found."
} else {
    foreach ($deviceId in $deviceIds) {
        Write-Host "Removing root-enumerated device $deviceId..."
        $arguments = @("/remove-device", $deviceId, "/subtree")
        if ($Force.IsPresent) {
            $arguments += "/force"
        }

        Invoke-PnPUtil -Arguments $arguments -IgnoreFailure | Out-Null
    }
}

$driverPackages = Get-HBashtonDriverPackages
if ($driverPackages.Count -eq 0) {
    Write-Host "No staged HBashton Virtual DualSense driver packages were found."
} else {
    foreach ($driverPackage in $driverPackages) {
        Write-Host "Deleting driver package $($driverPackage.PublishedName) ($($driverPackage.OriginalName))..."
        Invoke-PnPUtil -Arguments @("/delete-driver", $driverPackage.PublishedName, "/uninstall", "/force") | Out-Null
    }
}

Write-Host "Refreshing Plug and Play device tree..."
Invoke-PnPUtil -Arguments @("/scan-devices") -IgnoreFailure | Out-Null

Write-Host ""
$remainingPackages = Get-HBashtonDriverPackages
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($remainingPackages.Count -eq 0 -and -not $service) {
    Write-Host "Uninstalled. Restart DS4Windows if it was open."
} else {
    if ($remainingPackages.Count -ne 0) {
        Write-Host "Driver package still staged:"
        $remainingPackages | ForEach-Object { Write-Host "  $($_.PublishedName) ($($_.OriginalName))" }
    }

    if ($service) {
        Write-Host "Service still present: $($service.Status). A reboot may be required."
    }
}

if ($DisableTestSigning.IsPresent) {
    Write-Host ""
    Write-Host "Disabling Windows test-signing mode..."
    & bcdedit.exe /set testsigning off | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "bcdedit failed while disabling test-signing mode. Exit code: $LASTEXITCODE"
    }

    Write-Host "Reboot Windows for the test-signing change to take effect."
}
