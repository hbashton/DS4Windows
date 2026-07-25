param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$script:ExitCode = 0
$script:RebootRecommended = $false
$script:InstallDir = Join-Path $env:LOCALAPPDATA "VIIPER"
$script:LogPath = Join-Path $script:InstallDir "install.log"
$script:TempDir = Join-Path ([IO.Path]::GetTempPath()) (
    "DS4Windows-VIIPER-Setup-" + [Guid]::NewGuid().ToString("N"))

function Write-SetupLog([string]$message, [ConsoleColor]$color =
        [ConsoleColor]::Gray) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host $message -ForegroundColor $color
    try {
        Add-Content -LiteralPath $script:LogPath -Value (
            "[$timestamp] $message") -Encoding UTF8
    }
    catch { }
}

function Write-Step([string]$message) {
    Write-Host ""
    Write-SetupLog "== $message ==" Cyan
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-UsbipInstalledVersion {
    $driverPath = Join-Path $env:SystemRoot "System32\drivers\usbip2_ude.sys"
    if (Test-Path -LiteralPath $driverPath) {
        try {
            $versionText = (Get-Item -LiteralPath $driverPath).
                VersionInfo.FileVersion
            $version = ConvertTo-VersionFromObject $versionText
            if ($version) { return $version }
        }
        catch { }
    }

    foreach ($root in @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )) {
        $entry = Get-ItemProperty $root -ErrorAction SilentlyContinue |
            Where-Object {
                $displayName = $_.DisplayName
                if ($null -eq $displayName) { return $false }
                $nameText = $displayName -as [string]
                return $nameText -match "USB/IP|USBip"
            } |
            Select-Object -First 1
        if ($entry -and $entry.DisplayVersion) {
            $version = ConvertTo-VersionFromObject $entry.DisplayVersion
            if ($version) { return $version }
        }
    }

    return $null
}

function ConvertTo-VersionFromObject([object]$value) {
    if ($null -eq $value) { return $null }
    if ($value -is [Version]) { return $value }

    try {
        if ($value -is [string]) {
            $text = $value.Trim()
        }
        else {
            $text = [string]$value
            if ($null -eq $text) { return $null }
            $text = $text.Trim()
        }
    }
    catch { return $null }

    if ($text.Length -eq 0) { return $null }

    $parsed = $null
    if ([Version]::TryParse($text, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

function Invoke-Download([string]$url, [string]$outFile) {
    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Write-SetupLog "Downloading $url (attempt $attempt of 3)"
            Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing `
                -TimeoutSec 60 -Headers @{ "User-Agent" =
                    "DS4Windows-VIIPER-Setup" }
            if (-not (Test-Path -LiteralPath $outFile) -or
                (Get-Item -LiteralPath $outFile).Length -le 0) {
                throw "The downloaded file was empty."
            }
            return
        }
        catch {
            $lastError = $_.Exception
            if ($attempt -lt 3) { Start-Sleep -Seconds $attempt }
        }
    }

    throw "Download failed after three attempts: $($lastError.Message)"
}

function Get-GithubReleaseAsset([string]$repo, [string]$assetPattern) {
    $apiUrl = "https://api.github.com/repos/$repo/releases?per_page=20"
    $releases = Invoke-RestMethod -Uri $apiUrl -TimeoutSec 30 -Headers @{
        "User-Agent" = "DS4Windows-VIIPER-Setup"
        "Accept" = "application/vnd.github+json"
    }
    if (-not $releases) { throw "No releases were found in $repo." }

    foreach ($release in @($releases | Where-Object { -not $_.draft })) {
        $asset = @($release.assets) |
            Where-Object { $_.name -match $assetPattern } |
            Sort-Object @{ Expression = {
                if ($_.name -match
                    '(?i)^viiper-(windows|win)-(amd64|x64)\.zip$') { 0 }
                elseif ($_.name -match '(?i)^viiper\.exe$') { 1 }
                elseif ($_.name -match
                    '(?i)(windows|win).*(amd64|x64).*\.(exe|zip)$') { 2 }
                elseif ($_.name -match '(?i)\.(exe|zip)$') { 3 }
                else { 4 }
            }}, name | Select-Object -First 1
        if ($asset) {
            $label = if ($release.tag_name) { $release.tag_name }
                elseif ($release.name) { $release.name } else { $release.id }
            Write-SetupLog (
                "Using '$($asset.name)' from $repo release '$label'.")
            return $asset.browser_download_url
        }
    }

    $names = @($releases | ForEach-Object { $_.assets } |
        ForEach-Object { $_.name }) -join ", "
    throw "No supported Windows VIIPER asset was found. Assets seen: $names"
}

function Get-ViiperAssetUrl {
    $errors = @()
    foreach ($repo in @("hbashton/VIIPER", "Alia5/VIIPER")) {
        try {
            Write-SetupLog "Checking release assets in $repo"
            return Get-GithubReleaseAsset $repo (
                "(?i)^(?!.*(libviiper|client|headers|linux|arm64|\.nupkg|" +
                "\.crate|\.tgz)).*\.(exe|zip)$")
        }
        catch {
            $errors += "${repo}: $($_.Exception.Message)"
            Write-SetupLog "Could not use ${repo}: $($_.Exception.Message)" Yellow
        }
    }
    throw "Could not locate VIIPER. $($errors -join '; ')"
}

function Expand-ViiperAsset([string]$assetUrl, [string]$candidatePath) {
    $extension = [IO.Path]::GetExtension(([Uri]$assetUrl).AbsolutePath)
    $downloadPath = Join-Path $script:TempDir ("viiper-download" + $extension)
    Invoke-Download $assetUrl $downloadPath

    if ($extension -ieq ".exe") {
        Copy-Item -LiteralPath $downloadPath -Destination $candidatePath -Force
    }
    elseif ($extension -ieq ".zip") {
        $extractDir = Join-Path $script:TempDir "viiper-extract"
        Expand-Archive -LiteralPath $downloadPath -DestinationPath $extractDir `
            -Force
        $executable = Get-ChildItem -LiteralPath $extractDir -Recurse `
            -Filter "viiper.exe" | Select-Object -First 1
        if (-not $executable) {
            throw "The VIIPER archive did not contain viiper.exe."
        }
        Copy-Item -LiteralPath $executable.FullName `
            -Destination $candidatePath -Force
    }
    else {
        throw "Unsupported VIIPER asset type '$extension'."
    }

    $candidate = Get-Item -LiteralPath $candidatePath
    if ($candidate.Length -lt 65536) {
        throw "The downloaded VIIPER executable is unexpectedly small."
    }
    if ($candidate.Extension -ine ".exe") {
        throw "The downloaded VIIPER payload is not a Windows executable."
    }
}

function Install-ViiperAtomically([string]$candidatePath,
        [string]$viiperPath) {
    $newPath = "$viiperPath.new"
    $backupPath = "$viiperPath.previous"
    Copy-Item -LiteralPath $candidatePath -Destination $newPath -Force

    # An explicit repair/update may replace a running backend. Stop only the
    # VIIPER process and leave DS4Windows and every physical Bluetooth device
    # alone.
    Get-Process -Name "viiper" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300

    try {
        if (Test-Path -LiteralPath $viiperPath) {
            [IO.File]::Replace($newPath, $viiperPath, $backupPath, $true)
        }
        else {
            Move-Item -LiteralPath $newPath -Destination $viiperPath -Force
        }
    }
    catch {
        if (Test-Path -LiteralPath $backupPath) {
            Copy-Item -LiteralPath $backupPath -Destination $viiperPath -Force
        }
        throw
    }
}

function Test-ViiperApi([int]$timeoutMilliseconds = 1000) {
    $client = $null
    try {
        $client = [Net.Sockets.TcpClient]::new()
        $client.NoDelay = $true
        $client.SendTimeout = $timeoutMilliseconds
        $client.ReceiveTimeout = $timeoutMilliseconds
        $connect = $client.BeginConnect("127.0.0.1", 3242, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($timeoutMilliseconds)) {
            return $false
        }
        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $bytes = [Text.Encoding]::UTF8.GetBytes("ping`0")
        $stream.Write($bytes, 0, $bytes.Length)
        $buffer = New-Object byte[] 512
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { return $false }
        $response = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
        return $response.IndexOf("VIIPER",
            [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    catch { return $false }
    finally { if ($client) { $client.Dispose() } }
}

function Start-AndVerifyViiper([string]$viiperPath) {
    if (Test-ViiperApi) { return $true }
    Start-Process -FilePath $viiperPath -ArgumentList "server" `
        -WindowStyle Hidden | Out-Null
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        Start-Sleep -Milliseconds 500
        if (Test-ViiperApi) { return $true }
    }
    return $false
}

function Register-ViiperRunTask([string]$viiperPath, [string]$taskName) {
    try {
        $taskAction = New-ScheduledTaskAction -Execute $viiperPath `
            -Argument "server"
        $taskTrigger = New-ScheduledTaskTrigger -AtLogOn
        $taskPrincipal = New-ScheduledTaskPrincipal `
            -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
            -RunLevel Highest -LogonType Interactive
        $taskSettings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -MultipleInstances IgnoreNew

        Register-ScheduledTask -TaskName $taskName -Action $taskAction `
            -Trigger $taskTrigger -Principal $taskPrincipal -Settings $taskSettings `
            -Force | Out-Null
        return $true
    }
    catch {
        Write-SetupLog "Failed modern scheduled task registration: $($_.Exception.Message)" Yellow
    }

    try {
        $runCommand = '"{0}" server' -f $viiperPath
        $scheduledResult = Start-Process -FilePath "schtasks.exe" `
            -ArgumentList "/Create /F /TN `"$taskName`" /SC ONLOGON /RL HIGHEST /IT /TR `"$runCommand`"" `
            -WindowStyle Hidden -PassThru -Wait
        if ($scheduledResult.ExitCode -eq 0) {
            return $true
        }

        Write-SetupLog "Fallback schtasks command exited with code $($scheduledResult.ExitCode)." Yellow
    }
    catch {
        Write-SetupLog "Failed fallback scheduled task registration: $($_.Exception.Message)" Yellow
    }

    return $false
}

try {
    if (-not (Test-Administrator)) {
        throw "Administrator permission is required. Launch setup from DS4Windows so Windows can request it automatically."
    }

    New-Item -ItemType Directory -Path $script:InstallDir -Force | Out-Null
    New-Item -ItemType Directory -Path $script:TempDir -Force | Out-Null
    Write-SetupLog ""
    Write-SetupLog "DS4Windows VIIPER virtual controller setup" Green
    Write-SetupLog "Installing or repairing VIIPER and usbip-win2."

    Write-Step "Checking usbip-win2"
    $requiredUsbipVersion = [Version]"0.9.7.7"
    try {
        $usbipVersion = Get-UsbipInstalledVersion
    }
    catch {
        Write-SetupLog "usbip-win2 version check failed: $($_.Exception.Message)" Yellow
        $usbipVersion = $null
    }
    if ($usbipVersion -and $usbipVersion -ge $requiredUsbipVersion) {
        Write-SetupLog "usbip-win2 is ready: $usbipVersion" Green
    }
    else {
        $state = if ($usbipVersion) { "old ($usbipVersion)" } else { "missing" }
        Write-SetupLog "usbip-win2 is $state; installing $requiredUsbipVersion." Yellow
        $usbipUrl = "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe"
        $usbipInstaller = Join-Path $script:TempDir "USBip-0.9.7.7-x64.exe"
        Invoke-Download $usbipUrl $usbipInstaller
        Write-SetupLog "Windows may briefly restart USB hub devices." Yellow
        $installer = Start-Process -FilePath $usbipInstaller `
            -ArgumentList "/S" -PassThru -Wait
        if ($installer.ExitCode -notin @(0, 1641, 3010)) {
            throw "usbip-win2 setup failed with exit code $($installer.ExitCode)."
        }
        if ($installer.ExitCode -in @(1641, 3010)) {
            $script:RebootRecommended = $true
        }
        $usbipVersion = Get-UsbipInstalledVersion
        if (-not $usbipVersion) {
            $script:RebootRecommended = $true
            Write-SetupLog "The driver will finish registering after a Windows restart." Yellow
        }
    }

    Write-Step "Installing VIIPER"
    $viiperPath = Join-Path $script:InstallDir "viiper.exe"
    $candidatePath = Join-Path $script:TempDir "viiper.exe"
    Expand-ViiperAsset (Get-ViiperAssetUrl) $candidatePath
    Install-ViiperAtomically $candidatePath $viiperPath
    Write-SetupLog "VIIPER installed to $viiperPath" Green

    Write-Step "Registering VIIPER"
    $registration = Start-Process -FilePath $viiperPath `
        -ArgumentList "install" -WindowStyle Hidden -PassThru -Wait
    if ($registration.ExitCode -ne 0) {
        throw "VIIPER registration failed with exit code $($registration.ExitCode)."
    }

    $taskName = "RunVIIPER"
    if (Register-ViiperRunTask $viiperPath $taskName) {
        Write-SetupLog "Registered hidden logon task '$taskName'." Green
    }
    else {
        Write-SetupLog "Could not create hidden logon task. Setup will continue; VIIPER can still be started by DS4Windows when needed." Yellow
    }

    Write-Step "Verification"
    if (Start-AndVerifyViiper $viiperPath) {
        Write-SetupLog "VIIPER API is ready." Green
        $backupPath = "$viiperPath.previous"
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
    elseif ($script:RebootRecommended) {
        Write-SetupLog "VIIPER is installed; restart Windows to finish driver setup." Yellow
    }
    else {
        throw "VIIPER installed, but its local API did not start. See $script:LogPath"
    }

    Write-Host ""
    $finish = if ($script:RebootRecommended) {
        "Setup complete. Restart Windows once before using a virtual controller."
    } else {
        "Setup complete. VIIPER is ready for DS4Windows."
    }
    Write-SetupLog $finish Green
}
catch {
    $script:ExitCode = 1
    Write-Host ""
    Write-SetupLog "Setup could not finish: $($_.Exception.Message)" Red
    Write-SetupLog "Details were saved to $script:LogPath" Yellow
}
finally {
    if (Test-Path -LiteralPath $script:TempDir) {
        Remove-Item -LiteralPath $script:TempDir -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
    if (-not $NoPause) {
        Write-Host ""
        Read-Host "Press Enter to close"
    }
}

exit $script:ExitCode
