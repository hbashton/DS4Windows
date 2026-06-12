param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

function Write-Step($message) {
    Write-Host ""
    Write-Host "== $message ==" -ForegroundColor Cyan
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-UsbipInstalledVersion {
    $driverPath = Join-Path $env:SystemRoot "System32\drivers\usbip2_ude.sys"
    if (Test-Path $driverPath) {
        try {
            return [Version](Get-Item $driverPath).VersionInfo.FileVersion
        }
        catch { }
    }

    foreach ($root in @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )) {
        $entry = Get-ItemProperty $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -match "USB/IP|USBip" } |
            Select-Object -First 1
        if ($entry -and $entry.DisplayVersion) {
            try {
                return [Version]$entry.DisplayVersion
            }
            catch { }
        }
    }

    return $null
}

function Invoke-Download($url, $outFile) {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing
}

function Get-GithubLatestAsset($repo, $assetPattern) {
    $apiUrl = "https://api.github.com/repos/$repo/releases/latest"
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "DS4Windows-VIIPER-Setup" }
    $asset = $release.assets |
        Where-Object { $_.name -match $assetPattern } |
        Select-Object -First 1

    if (-not $asset) {
        throw "Could not find a release asset matching '$assetPattern' in $repo latest release."
    }

    return $asset.browser_download_url
}

function Install-ViiperAsset($assetUrl, $installPath, $tempDir) {
    $extension = [IO.Path]::GetExtension(([Uri]$assetUrl).AbsolutePath)
    $downloadPath = Join-Path $tempDir ("viiper-download" + $extension)
    Invoke-Download $assetUrl $downloadPath

    if ($extension -ieq ".exe") {
        Copy-Item $downloadPath $installPath -Force
        return
    }

    if ($extension -ieq ".zip") {
        $extractDir = Join-Path $tempDir "viiper-extract"
        if (Test-Path $extractDir) {
            Remove-Item $extractDir -Recurse -Force
        }

        Expand-Archive -LiteralPath $downloadPath -DestinationPath $extractDir -Force
        $executable = Get-ChildItem -Path $extractDir -Recurse -Filter "viiper.exe" |
            Select-Object -First 1

        if (-not $executable) {
            throw "Downloaded VIIPER archive did not contain viiper.exe."
        }

        Copy-Item $executable.FullName $installPath -Force
        return
    }

    throw "Unsupported VIIPER release asset type '$extension' from $assetUrl"
}

if (-not (Test-Administrator)) {
    throw "Please run this script as Administrator. DS4Windows normally launches it elevated for you."
}

$tempDir = Join-Path $env:TEMP "DS4Windows-VIIPER-Setup"
$installDir = Join-Path $env:LOCALAPPDATA "VIIPER"
$viiperPath = Join-Path $installDir "viiper.exe"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
New-Item -ItemType Directory -Path $installDir -Force | Out-Null

Write-Host "DS4Windows VIIPER virtual controller setup" -ForegroundColor Green
Write-Host "This installs/repairs VIIPER and usbip-win2 for local virtual USB controller output."

Write-Step "Checking usbip-win2"
$requiredUsbipVersion = [Version]"0.9.7.7"
$usbipVersion = Get-UsbipInstalledVersion
if ($usbipVersion -and $usbipVersion -ge $requiredUsbipVersion) {
    Write-Host "usbip-win2 already installed: $usbipVersion" -ForegroundColor Green
}
else {
    if ($usbipVersion) {
        Write-Host "usbip-win2 is installed but old: $usbipVersion. Updating to $requiredUsbipVersion." -ForegroundColor Yellow
    }
    else {
        Write-Host "usbip-win2 driver was not found. Installing $requiredUsbipVersion." -ForegroundColor Yellow
    }

    $usbipUrl = "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe"
    $usbipInstaller = Join-Path $tempDir "USBip-0.9.7.7-x64.exe"
    Invoke-Download $usbipUrl $usbipInstaller
    Write-Host "Launching usbip-win2 installer. Windows may briefly restart USB hub devices." -ForegroundColor Yellow
    Start-Process -FilePath $usbipInstaller -ArgumentList "/S" -Wait
}

Write-Step "Installing VIIPER"
$viiperAssetUrl = Get-GithubLatestAsset "Alia5/VIIPER" "(?i)(^viiper\.exe$|^viiper-windows-amd64\.zip$|^(?!.*libviiper).*(windows|win).*(x64|amd64).*\.(exe|zip)$)"
Install-ViiperAsset $viiperAssetUrl $viiperPath $tempDir
Write-Host "VIIPER installed to $viiperPath" -ForegroundColor Green

Write-Step "Registering and starting VIIPER server"
try {
    Start-Process -FilePath $viiperPath -ArgumentList "install" -WindowStyle Hidden -Wait
}
catch {
    Write-Host "VIIPER install command failed: $($_.Exception.Message)" -ForegroundColor Yellow
}

try {
    Start-Process -FilePath $viiperPath -ArgumentList "server" -WindowStyle Hidden
    Start-Sleep -Seconds 2
}
catch {
    Write-Host "VIIPER server start failed: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Step "Verification"
$client = $null
try {
    $client = [Net.Sockets.TcpClient]::new()
    $connect = $client.BeginConnect("127.0.0.1", 3242, $null, $null)
    if (-not $connect.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(3))) {
        throw "Timed out connecting to VIIPER API."
    }
    $client.EndConnect($connect)
    $stream = $client.GetStream()
    $bytes = [Text.Encoding]::UTF8.GetBytes("ping`0")
    $stream.Write($bytes, 0, $bytes.Length)
    $buffer = New-Object byte[] 512
    $read = $stream.Read($buffer, 0, $buffer.Length)
    $response = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
    Write-Host "VIIPER API response: $response" -ForegroundColor Green
}
catch {
    Write-Host "VIIPER was installed, but the API did not respond yet: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "A reboot may be required after installing usbip-win2." -ForegroundColor Yellow
}
finally {
    if ($client) { $client.Dispose() }
}

Write-Host ""
Write-Host "Setup complete. If DS4Windows still reports usbip-win2 missing, reboot Windows once." -ForegroundColor Green

if (-not $NoPause) {
    Write-Host ""
    Read-Host "Press Enter to close"
}
