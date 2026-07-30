param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$script:ExitCode = 0
$script:RebootRecommended = $false
$script:UsbipRuntimeReady = $false
$script:UsbipRuntimeProbeState = "not-run"
$script:Ds4WindowsRestartPath = $null
$script:RequiredUsbipVersion = [Version]"0.9.7.8"
$script:UsbipInstallerUrl =
    "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.8/USBip-0.9.7.8-x64.exe"
$script:UsbipInstallerSha256 =
    "44451fe06f4186125c2a5ecd25b099c5560a61a60b1e56f5a0758e77a60afa44"
$script:BundledUsbipInstallerPath = Join-Path $PSScriptRoot `
    "USBip-0.9.7.8-x64.exe"
$programFilesRoot = if ($env:ProgramW6432) {
    $env:ProgramW6432
}
else {
    $env:ProgramFiles
}
$script:CanonicalUsbipPath = Join-Path $programFilesRoot "USBip\usbip.exe"
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
    # The official x64 installer owns this canonical executable. Its product
    # version is the authoritative userspace ABI version; check it before
    # driver metadata or historical uninstall records.
    if (Test-Path -LiteralPath $script:CanonicalUsbipPath) {
        try {
            $versionText = (Get-Item -LiteralPath $script:CanonicalUsbipPath).VersionInfo.ProductVersion
            return ConvertTo-VersionFromObject $versionText
        }
        catch { return $null }
    }

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

function Assert-FileSha256([string]$path, [string]$expectedHash) {
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $expectedHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Downloaded usbip-win2 installer failed SHA256 verification. " +
            "Expected $expectedHash; received $actualHash."
    }

    Write-SetupLog "Verified usbip-win2 installer SHA256: $actualHash" Green
}

function Test-UsbipRuntime([string]$usbipPath) {
    if (-not (Test-Path -LiteralPath $usbipPath)) {
        $script:UsbipRuntimeProbeState = "missing"
        Write-SetupLog (
            "usbip-win2 runtime is missing at its canonical path: $usbipPath"
        ) Yellow
        return $false
    }

    try {
        # Windows PowerShell turns native stderr into ErrorRecord objects. Do
        # not let the script-wide Stop policy hide the text we need to
        # distinguish a pending-reboot ABI mismatch from a broken install.
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $probeOutput = @(& $usbipPath port 2>&1)
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        $probeExitCode = $LASTEXITCODE
        $probeText = ($probeOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        $abiMismatch = $probeText -match "(?i)ABI\s+mismatch|unexpected\s+size.*(?:input|structure)"

        if ($abiMismatch) {
            $script:UsbipRuntimeProbeState = "abi-mismatch"
            Write-SetupLog (
                "usbip-win2 0.9.7.8 is installed, but Windows still has a " +
                "different driver ABI loaded. A reboot is required."
            ) Yellow
            return $false
        }

        if ($probeExitCode -ne 0) {
            $script:UsbipRuntimeProbeState = "failed"
            $summary = if ($probeText) { $probeText.Trim() }
                else { "no diagnostic output" }
            Write-SetupLog (
                "usbip-win2 runtime probe failed (exit=$probeExitCode): " +
                $summary
            ) Yellow
            return $false
        }

        $script:UsbipRuntimeProbeState = "ready"
        Write-SetupLog "usbip-win2 runtime ABI probe succeeded." Green
        return $true
    }
    catch {
        $script:UsbipRuntimeProbeState = "failed"
        Write-SetupLog (
            "usbip-win2 runtime probe could not run: $($_.Exception.Message)"
        ) Yellow
        return $false
    }
}

function Get-RunningPadSenseProcesses {
    try {
        return @(Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object { $_.Name -match "^PadSense.*\.exe$" })
    }
    catch {
        return @(Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -match "^PadSense" } |
            ForEach-Object {
                [pscustomobject]@{
                    Name = "$($_.ProcessName).exe"
                    ProcessId = $_.Id
                }
            })
    }
}

function Assert-PadSenseNotRunning([string]$operation) {
    $processes = @(Get-RunningPadSenseProcesses)
    if ($processes.Count -eq 0) { return }

    $owners = ($processes | ForEach-Object {
        "$($_.Name) PID=$($_.ProcessId)"
    }) -join ", "
    $message = "PadSense is running ($owners) and may own active USBIP " +
        "imports. Close PadSense completely, including its tray/background " +
        "process, then run Install / Repair again. Setup did not begin the " +
        "$operation."
    Write-SetupLog $message Red
    throw $message
}

function Stop-Ds4WindowsProcesses([string]$operation) {
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ieq "DS4Windows.exe" })
    if ($processes.Count -eq 0) { return $true }

    $restartPath = $processes |
        Where-Object { $_.ExecutablePath -and
            (Test-Path -LiteralPath $_.ExecutablePath) } |
        Select-Object -First 1 -ExpandProperty ExecutablePath
    if (-not $restartPath) {
        $bundledPath = Join-Path (Split-Path -Parent $PSScriptRoot) "DS4Windows.exe"
        if (Test-Path -LiteralPath $bundledPath) {
            $restartPath = $bundledPath
        }
    }
    $script:Ds4WindowsRestartPath = $restartPath

    Write-SetupLog "Stopping DS4Windows output owners for $operation..." Yellow
    foreach ($entry in $processes) {
        try {
            $process = Get-Process -Id $entry.ProcessId -ErrorAction Stop
            [void]$process.CloseMainWindow()
        }
        catch { }
    }
    Start-Sleep -Milliseconds 1500
    foreach ($entry in $processes) {
        try {
            Stop-Process -Id $entry.ProcessId -Force -ErrorAction SilentlyContinue
        }
        catch { }
    }
    Start-Sleep -Milliseconds 750

    $remaining = @(Get-Process -Name "DS4Windows" -ErrorAction SilentlyContinue)
    if ($remaining.Count -eq 0) { return $true }

    Write-SetupLog (
        "DS4Windows could not be stopped safely for $operation. " +
        "Close it manually and run Install / Repair again."
    ) Red
    return $false
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
    foreach ($repo in @("hbashton/VIIPER")) {
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
    $stopped = Stop-ViiperProcesses "backend replacement"
    if (-not $stopped) {
        throw "Unable to stop the currently running VIIPER process automatically during install. " +
              "Please close viiper.exe manually and try again."
    }
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

function Get-RunningViiperProcesses {
    try {
        Get-CimInstance Win32_Process -Filter "Name='viiper.exe'" -ErrorAction SilentlyContinue
    }
    catch {
        @()
    }
}

function Stop-ViiperProcesses([string]$operation) {
    $attempts = 12
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $processes = @(Get-RunningViiperProcesses)
        if ($processes.Count -eq 0) { return $true }

        if ($attempt -eq 1) {
            Write-SetupLog "Stopping VIIPER process(es) for $operation..." Yellow
        }

        foreach ($process in $processes) {
            if ($process.ProcessId -eq $PID) { continue }
            try {
                $identifier = if ($process.ExecutablePath) {
                    $process.ExecutablePath
                }
                else {
                    $process.ProcessId
                }
                Write-SetupLog "Stopping viiper PID=$($process.ProcessId) ($identifier)." Yellow
                Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
            }
            catch { }
        }

        Start-Sleep -Milliseconds 300

        $remaining = @(Get-RunningViiperProcesses)
        if ($remaining.Count -eq 0) { return $true }

        if ($attempt -ge 3) {
            foreach ($process in $remaining) {
                if ($process.ProcessId -eq $PID) { continue }
                try {
                    & taskkill.exe /PID $process.ProcessId /T /F | Out-Null
                }
                catch { }
            }
            Start-Sleep -Milliseconds 200
        }
    }

    Write-SetupLog (
        "A VIIPER process is still running after stop attempts. " +
        "Please close viiper.exe manually and rerun Install/Repair."
    ) Yellow
    return $false
}

function Invoke-ViiperInstallRegistration([string]$viiperPath) {
    # viiper.exe install intentionally launches the long-running server before
    # its short-lived registration process exits. Start-Process -Wait follows
    # the whole descendant tree on Windows and therefore waits on that server
    # forever. Wait on the registration PID itself and bound genuine hangs.
    $registration = Start-Process -FilePath $viiperPath `
        -ArgumentList "install" -WindowStyle Hidden -PassThru
    if (-not $registration.WaitForExit(15000)) {
        try {
            & taskkill.exe /PID $registration.Id /T /F | Out-Null
        }
        catch { }
        throw "VIIPER registration did not finish within 15 seconds. Its process tree was stopped safely; run Install / Repair again."
    }

    $registration.Refresh()
    if ($registration.ExitCode -ne 0) {
        throw "VIIPER registration failed with exit code $($registration.ExitCode)."
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
    Write-SetupLog "Installing VIIPER first, then checking usbip-win2."

    Write-Step "Installing VIIPER"
    $viiperPath = Join-Path $script:InstallDir "viiper.exe"
    $candidatePath = Join-Path $script:TempDir "viiper.exe"
    Expand-ViiperAsset (Get-ViiperAssetUrl) $candidatePath
    if (-not (Stop-Ds4WindowsProcesses "VIIPER backend replacement")) {
        throw "Unable to quiesce DS4Windows before replacing VIIPER."
    }
    Install-ViiperAtomically $candidatePath $viiperPath
    Write-SetupLog "VIIPER installed to $viiperPath" Green

    Write-Step "Registering VIIPER"
    if (-not (Stop-ViiperProcesses "install registration")) {
        throw "VIIPER registration could not proceed because a VIIPER process could not be closed automatically. Please close viiper.exe manually, then run Install / Repair again."
    }

    Invoke-ViiperInstallRegistration $viiperPath

    $taskName = "RunVIIPER"
    if (Register-ViiperRunTask $viiperPath $taskName) {
        Write-SetupLog "Registered hidden logon task '$taskName'." Green
    }
    else {
        Write-SetupLog "Could not create hidden logon task. Setup will continue; VIIPER can still be started by DS4Windows when needed." Yellow
    }

    Write-Step "Checking usbip-win2"
    $requiredUsbipVersion = $script:RequiredUsbipVersion
    try {
        $usbipVersion = Get-UsbipInstalledVersion
    }
    catch {
        Write-SetupLog "usbip-win2 version check failed: $($_.Exception.Message)" Yellow
        $usbipVersion = $null
    }

    $canonicalUsbipPresent = Test-Path -LiteralPath $script:CanonicalUsbipPath
    $usbipVersionReady = $canonicalUsbipPresent -and $usbipVersion -and $usbipVersion -ge $requiredUsbipVersion
    if ($usbipVersionReady) {
        $script:UsbipRuntimeReady = Test-UsbipRuntime $script:CanonicalUsbipPath
    }

    if ($usbipVersionReady -and $script:UsbipRuntimeReady) {
        Write-SetupLog (
            "usbip-win2 is ready: $usbipVersion at " +
            $script:CanonicalUsbipPath
        ) Green
    }
    elseif ($usbipVersionReady -and
            $script:UsbipRuntimeProbeState -eq "abi-mismatch") {
        $script:RebootRecommended = $true
        Write-SetupLog (
            "The supported usbip-win2 $requiredUsbipVersion package is " +
            "already installed. Skipping a redundant reinstall; restart " +
            "Windows to load its matching driver."
        ) Yellow
    }
    else {
        $state = if (-not $canonicalUsbipPresent) {
            "missing from the canonical Program Files location"
        }
        elseif (-not $usbipVersion) {
            "present with an unreadable version"
        }
        elseif ($usbipVersion -lt $requiredUsbipVersion) {
            "old ($usbipVersion)"
        }
        else {
            "installed but its userspace/driver ABI probe failed"
        }
        Write-SetupLog (
            "usbip-win2 is $state; installing or repairing " +
            "$requiredUsbipVersion."
        ) Yellow
        Assert-PadSenseNotRunning "usbip-win2 driver transition"

        $usbipInstaller = Join-Path $script:TempDir "USBip-0.9.7.8-x64.exe"
        if (Test-Path -LiteralPath $script:BundledUsbipInstallerPath) {
            Write-SetupLog (
                "Using the bundled usbip-win2 0.9.7.8 installer."
            ) Green
            Assert-FileSha256 $script:BundledUsbipInstallerPath `
                $script:UsbipInstallerSha256
            Copy-Item -LiteralPath $script:BundledUsbipInstallerPath `
                -Destination $usbipInstaller -Force
        }
        else {
            Write-SetupLog (
                "Bundled usbip-win2 installer is unavailable; using the " +
                "verified upstream recovery download."
            ) Yellow
            Invoke-Download $script:UsbipInstallerUrl $usbipInstaller
        }
        Assert-FileSha256 $usbipInstaller $script:UsbipInstallerSha256

        if (-not (Stop-Ds4WindowsProcesses "usbip-win2 driver upgrade")) {
            throw "Unable to quiesce DS4Windows before the usbip-win2 driver upgrade."
        }
        if (-not (Stop-ViiperProcesses "usbip-win2 driver upgrade")) {
            throw "Unable to quiesce VIIPER before the usbip-win2 driver upgrade. " +
                "Close viiper.exe manually and run Install / Repair again."
        }
        Assert-PadSenseNotRunning "usbip-win2 driver transition"

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
        $canonicalUsbipPresent = Test-Path -LiteralPath $script:CanonicalUsbipPath
        $usbipVersionReady = $canonicalUsbipPresent -and $usbipVersion -and $usbipVersion -ge $requiredUsbipVersion
        if (-not $usbipVersionReady) {
            $script:RebootRecommended = $true
            Write-SetupLog (
                "usbip-win2 $requiredUsbipVersion is not active at its " +
                "canonical Program Files path yet. A reboot or repair is required."
            ) Yellow
        }
        else {
            $script:UsbipRuntimeReady = Test-UsbipRuntime $script:CanonicalUsbipPath
            if (-not $script:UsbipRuntimeReady) {
                $script:RebootRecommended = $true
                Write-SetupLog (
                    "usbip.exe port did not confirm a compatible driver ABI. " +
                    "A reboot or repair is required; setup will not report Ready."
                ) Yellow
            }
        }
    }

    Write-Step "Verification"
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended -and
            (Start-AndVerifyViiper $viiperPath)) {
        Write-SetupLog "VIIPER API is ready." Green
        $backupPath = "$viiperPath.previous"
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
    elseif (-not $script:UsbipRuntimeReady -or $script:RebootRecommended) {
        [void](Stop-ViiperProcesses "pending usbip-win2 reboot")
        Write-SetupLog (
            "VIIPER is installed, but usbip-win2 is not runtime-ready. " +
            "Restart Windows; if usbip.exe port still fails, run Repair again."
        ) Yellow
    }
    else {
        throw "VIIPER installed, but its local API did not start. See $script:LogPath"
    }

    Write-Host ""
    $finish = if (-not $script:UsbipRuntimeReady -or
            $script:RebootRecommended) {
        "Setup complete, but not Ready. Restart Windows before using a virtual controller; run Repair if the usbip ABI probe still fails."
    } else {
        "Setup complete. VIIPER is ready for DS4Windows."
    }
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended) {
        Write-SetupLog $finish Green
        if ($script:Ds4WindowsRestartPath) {
            Write-SetupLog "SUCCESSFUL: restarting DS4Windows in 2 seconds." Green
            Start-Sleep -Seconds 2
            Start-Process -FilePath $script:Ds4WindowsRestartPath
        }
    }
    else {
        Write-SetupLog $finish Yellow
    }
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
