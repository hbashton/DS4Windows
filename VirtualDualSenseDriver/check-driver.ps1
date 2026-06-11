param()

$ErrorActionPreference = "Stop"
$hardwareId = "Root\HBashtonVirtualDualSense"
$serviceName = "HBashtonVirtualDualSense"
$controlSymbolicLink = "\\.\HBashtonVirtualDualSenseControl"
$symbolicLink = "\\.\HBashtonVirtualDualSense"

Write-Host "HBashton Virtual DualSense driver status"
Write-Host "-----------------------------------------"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Service: $($service.Status)"
} else {
    Write-Host "Service: not installed"
}

Write-Host ""
Write-Host "PnP device:"
$deviceText = & pnputil.exe /enum-devices /deviceid $hardwareId /deviceids 2>&1
$deviceText | ForEach-Object { Write-Host $_ }

Write-Host ""
Write-Host "Published DOS device links:"
foreach ($link in @($controlSymbolicLink, $symbolicLink)) {
    try {
        $stream = [System.IO.File]::Open($link, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        $stream.Dispose()
        Write-Host "$link opened successfully."
    } catch {
        Write-Host "$link could not be opened: $($_.Exception.Message)"
    }
}

