param(
    [switch]$NoPause,
    [switch]$Yes,
    [string]$TargetLocalAppData,
    [string]$TargetUserSid,
    [string]$TargetUserName,
    [string]$TargetDs4WindowsPath,
    [string]$PackageExtrasRoot,
    [int]$InstallerHostPid = 0
)

# DS4Windows normally streams this script from an embedded resource into the
# absolute system PowerShell executable. Environment values carry the original
# signed-in user and package paths without executing a mutable .ps1 file after
# the UAC boundary. Direct/manual script execution keeps the normal parameters.
if ($env:DS4W_SETUP_TARGET_LOCALAPPDATA) {
    $TargetLocalAppData = $env:DS4W_SETUP_TARGET_LOCALAPPDATA
}
if ($env:DS4W_SETUP_TARGET_USER_SID) {
    $TargetUserSid = $env:DS4W_SETUP_TARGET_USER_SID
}
if ($env:DS4W_SETUP_TARGET_USER_NAME) {
    $TargetUserName = $env:DS4W_SETUP_TARGET_USER_NAME
}
if ($env:DS4W_SETUP_TARGET_EXE) {
    $TargetDs4WindowsPath = $env:DS4W_SETUP_TARGET_EXE
}
if ($env:DS4W_SETUP_NO_PAUSE -eq '1') {
    $NoPause = $true
}
$script:InstallerHostPid = $InstallerHostPid
if ($env:DS4W_SETUP_HOST_PID) {
    $parsedHostPid = 0
    if ([int]::TryParse($env:DS4W_SETUP_HOST_PID,
            [ref]$parsedHostPid)) {
        $script:InstallerHostPid = $parsedHostPid
    }
}
$script:PackageExtrasRoot = if ($PackageExtrasRoot) {
    [IO.Path]::GetFullPath($PackageExtrasRoot)
}
elseif ($env:DS4W_SETUP_PACKAGE_EXTRAS) {
    [IO.Path]::GetFullPath($env:DS4W_SETUP_PACKAGE_EXTRAS)
}
else {
    $PSScriptRoot
}

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$script:ExitCode = 0
$script:RebootRecommended = $false
$script:UsbipRuntimeReady = $false
$script:UsbipRuntimeProbeState = "not-run"
$script:Ds4WindowsRestartPath = $TargetDs4WindowsPath
$script:UserCanceled = $false
$script:RebootBoundaryPending = $false
$script:SafetyRestartPending = $false
$script:UsbipReplacementPhaseOne = $false
$script:SetupMutex = $null
$script:RequiredUsbipVersion = [Version]"0.9.7.7"
$script:UsbipInstallerUrl =
    "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe"
$script:UsbipInstallerSha256 =
    "51620fa5f9f8be5932bc9d786deee557ce06d5407a99cab490dcfac71f185fea"
$script:UsbipUdeDriverSha256 =
    "51db440065393e588a6b2585508c50eb3e1510b7b06d9afa6c5bde583751ea7d"
$script:UsbipFilterDriverSha256 =
    "c290299ff4d0f6a597db5ce03e15b29a5349cdce7c587ebfbd9ecaeca04f73ed"
$script:BundledViiperPath = Join-Path $script:PackageExtrasRoot `
    "VIIPER-0.0.6-x64.exe"
$script:BundledViiperSha256 =
    "ae03b04db7a59075706fb13dcbfcf5bc58ff986191e3b0c56e4221f556542016"
$script:BundledUsbipInstallerPath = Join-Path $script:PackageExtrasRoot `
    "USBip-0.9.7.7-x64.exe"
$programFilesRoot = if ($env:ProgramW6432) {
    $env:ProgramW6432
}
else {
    $env:ProgramFiles
}
$script:CanonicalUsbipPath = Join-Path $programFilesRoot "USBip\usbip.exe"
if ([string]::IsNullOrWhiteSpace($TargetLocalAppData)) {
    $TargetLocalAppData = $env:LOCALAPPDATA
}
if ([string]::IsNullOrWhiteSpace($TargetUserSid)) {
    $TargetUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}
if ([string]::IsNullOrWhiteSpace($TargetUserName)) {
    $TargetUserName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
}
$TargetLocalAppData = [IO.Path]::GetFullPath($TargetLocalAppData)
$validatedTargetSid = $null
try {
    $validatedTargetSid = [Security.Principal.SecurityIdentifier]::new(
        $TargetUserSid)
}
catch { }
if (-not [IO.Path]::IsPathRooted($TargetLocalAppData) -or
        -not $validatedTargetSid) {
    throw "Invalid target Windows user identity supplied to VIIPER setup."
}
$script:TargetUserSid = $TargetUserSid
$script:TargetUserName = $TargetUserName
$script:TargetRunKeyPath = "Registry::HKEY_USERS\$TargetUserSid\Software\Microsoft\Windows\CurrentVersion\Run"
$script:ManagedRoot = Join-Path $programFilesRoot "DS4Windows"
$script:InstallDir = Join-Path $script:ManagedRoot "VIIPER"
$script:Ds4WindowsInstallDir = $script:ManagedRoot
$script:LogPath = Join-Path $script:InstallDir "install.log"
$script:UsbipReplacementStatePath = Join-Path $script:InstallDir `
    "usbip-replacement-pending.json"
$script:UsbipUninstallKeyName = `
    "{199505b0-b93d-4521-a8c7-897818e0205a}_is1"
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

function Get-CitrixUsbMonitorState {
    $service = Get-CimInstance Win32_SystemDriver `
        -Filter "Name='ctxusbm'" -ErrorAction SilentlyContinue
    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\ctxusbm"
    $registry = Get-ItemProperty -LiteralPath $keyPath `
        -ErrorAction SilentlyContinue
    if (-not $service -and -not $registry) {
        return $null
    }

    $imagePath = if ($service.PathName) {
        [string]$service.PathName
    }
    elseif ($registry.ImagePath) {
        [string]$registry.ImagePath
    }
    else { "" }
    if ($imagePath -and
            $imagePath.IndexOf("ctxusbmon.sys",
                [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The ctxusbm service exists but does not point to the known " +
            "Citrix ctxusbmon.sys driver. Refusing to change it."
    }

    [pscustomobject]@{
        State = [string]$service.State
        Start = if ($null -ne $registry.Start) {
            [int]$registry.Start
        } else { $null }
        ImagePath = $imagePath
    }
}

function Disable-ConflictingCitrixUsbMonitor {
    $state = Get-CitrixUsbMonitorState
    if (-not $state) { return $false }
    if ($state.State -ne "Running" -and $state.Start -eq 4) {
        return $false
    }

    Write-SetupLog (
        "Citrix USB Monitor (ctxusbmon.sys) is enabled. A verified " +
        "IRQL_NOT_LESS_OR_EQUAL crash occurs when it races USB/IP virtual " +
        "controller enumeration. VIIPER will not start while it is loaded."
    ) Red
    Write-SetupLog (
        "This repair disables only Citrix generic USB redirection; the " +
        "rest of Citrix Workspace remains installed."
    ) Yellow

    if (-not $Yes) {
        $answer = Read-Host (
            "Disable the conflicting Citrix USB monitor and require a " +
            "restart? [Y/N]")
        if ($answer -notmatch '^(?i:y|yes)$') {
            $script:UserCanceled = $true
            throw "Setup canceled before the Citrix USB monitor was changed."
        }
    }

    Set-ItemProperty -LiteralPath `
        "HKLM:\SYSTEM\CurrentControlSet\Services\ctxusbm" `
        -Name Start -Type DWord -Value 4
    $verified = Get-ItemPropertyValue -LiteralPath `
        "HKLM:\SYSTEM\CurrentControlSet\Services\ctxusbm" `
        -Name Start
    if ([int]$verified -ne 4) {
        throw "Windows did not disable the Citrix USB monitor service."
    }

    $script:RebootRecommended = $true
    Write-SetupLog (
        "Citrix generic USB redirection is disabled. Its kernel driver " +
        "remains loaded until Windows restarts, so VIIPER stays stopped."
    ) Green
    return $true
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

    $records = @(Get-UsbipUninstallRecords)
    if ($records.Count -gt 1) {
        throw "Multiple exact usbip-win2 package records exist. Refusing " +
            "an ambiguous driver transition."
    }
    if ($records.Count -eq 1 -and $records[0].DisplayVersion) {
        $version = ConvertTo-VersionFromObject $records[0].DisplayVersion
        if ($version) { return $version }
    }

    return $null
}

function Resolve-SystemDriverPath([string]$pathName) {
    if ([string]::IsNullOrWhiteSpace($pathName)) { return $null }

    $path = [Environment]::ExpandEnvironmentVariables($pathName.Trim())
    if ($path.StartsWith('"')) {
        $closingQuote = $path.IndexOf('"', 1)
        if ($closingQuote -le 1) { return $null }
        $path = $path.Substring(1, $closingQuote - 1)
    }
    else {
        $extension = $path.IndexOf('.sys',
            [StringComparison]::OrdinalIgnoreCase)
        if ($extension -ge 0) {
            $path = $path.Substring(0, $extension + 4)
        }
    }

    if ($path.StartsWith('\??\') -or $path.StartsWith('\\?\')) {
        $path = $path.Substring(4)
    }
    if ($path.StartsWith('\SystemRoot\',
            [StringComparison]::OrdinalIgnoreCase)) {
        $path = Join-Path $env:SystemRoot `
            $path.Substring('\SystemRoot\'.Length)
    }
    elseif (-not [IO.Path]::IsPathRooted($path)) {
        $path = Join-Path $env:SystemRoot $path
    }

    try { return [IO.Path]::GetFullPath($path) }
    catch { return $null }
}

function Get-UsbipDriverIntegrity {
    $expected = [ordered]@{
        usbip2_ude = $script:UsbipUdeDriverSha256
        usbip2_filter = $script:UsbipFilterDriverSha256
    }
    $details = [Collections.Generic.List[string]]::new()
    $installedServiceCount = 0

    foreach ($serviceName in $expected.Keys) {
        try {
            $drivers = @(Get-CimInstance Win32_SystemDriver `
                -Filter "Name='$serviceName'" -ErrorAction Stop)
        }
        catch {
            return [pscustomobject]@{
                Safe = $false
                InstalledServiceCount = -1
                Message = "Could not inspect $serviceName`: " +
                    $_.Exception.Message
            }
        }

        $installedServiceCount += $drivers.Count
        if ($drivers.Count -ne 1) {
            $details.Add($(if ($drivers.Count -eq 0) {
                "$serviceName service is missing"
            } else {
                "multiple $serviceName services were returned"
            }))
            continue
        }

        $driverPath = Resolve-SystemDriverPath `
            ([string]$drivers[0].PathName)
        if (-not $driverPath -or
                -not (Test-Path -LiteralPath $driverPath -PathType Leaf)) {
            $details.Add("$serviceName driver file is missing")
            continue
        }

        try {
            $hash = (Get-FileHash -LiteralPath $driverPath `
                -Algorithm SHA256 -ErrorAction Stop).Hash
        }
        catch {
            $details.Add("$serviceName driver could not be hashed")
            continue
        }
        if (-not [string]::Equals($hash, $expected[$serviceName],
                [StringComparison]::OrdinalIgnoreCase)) {
            $details.Add("$serviceName does not match signed 0.9.7.7")
            continue
        }

        $details.Add("$serviceName verified at $driverPath")
    }

    $safe = $details.Count -eq 2 -and
        @($details | Where-Object { $_ -match ' verified at ' }).Count -eq 2
    return [pscustomobject]@{
        Safe = $safe
        InstalledServiceCount = $installedServiceCount
        Message = ($details -join '; ')
    }
}

function Get-WindowsBootSessionId {
    $bootTime = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).
        LastBootUpTime
    if ($bootTime -isnot [DateTime]) {
        $bootTime = [Management.ManagementDateTimeConverter]::ToDateTime(
            [string]$bootTime)
    }
    return $bootTime.ToUniversalTime().Ticks.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}

function Get-UsbipUninstallRecords {
    $records = @()
    foreach ($basePath in @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )) {
        $path = Join-Path $basePath $script:UsbipUninstallKeyName
        if (Test-Path -LiteralPath $path) {
            $records += Get-ItemProperty -LiteralPath $path `
                -ErrorAction Stop
        }
    }
    return $records
}

function Set-UsbipReplacementBoundary([Version]$installedVersion,
        [Version]$requiredVersion) {
    $state = [ordered]@{
        BootSessionId = Get-WindowsBootSessionId
        RemovedVersion = $installedVersion.ToString()
        RequiredVersion = $requiredVersion.ToString()
        StartedUtc = [DateTime]::UtcNow.ToString("o")
    }
    $state | ConvertTo-Json | Set-Content `
        -LiteralPath $script:UsbipReplacementStatePath -Encoding UTF8
}

function Assert-UsbipPostRebootState {
    $activeDevices = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object {
            $_.Service -eq 'usbip2_ude'
        })
    if ($activeDevices.Count -gt 0) {
        throw "A usbip2_ude root device is still present after " +
            "the reboot. No replacement driver was installed."
    }

    $runningDrivers = @(Get-CimInstance Win32_SystemDriver -ErrorAction Stop |
        Where-Object {
            $_.State -eq "Running" -and
            $_.Name -match '(?i)^usbip2_(?:ude|filter)$'
        })
    if ($runningDrivers.Count -gt 0) {
        $names = ($runningDrivers | ForEach-Object { $_.Name }) -join ", "
        throw "Old USBIP driver service(s) are still running after reboot: " +
            "$names. No replacement driver was installed."
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $driverStoreOutput = @(& pnputil.exe /enum-drivers 2>&1)
        $driverStoreExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $driverStoreText = ($driverStoreOutput | ForEach-Object { [string]$_ }) `
        -join [Environment]::NewLine
    if ($driverStoreExitCode -ne 0) {
        throw "Could not verify the DriverStore after reboot " +
            "(pnputil exit=$driverStoreExitCode): $driverStoreText"
    }
    if ($driverStoreText -match '(?im)\busbip2_(?:ude|filter)\.inf\b') {
        throw "An old usbip2_ude.inf or usbip2_filter.inf package remains " +
            "in the DriverStore after reboot. Remove it safely, reboot, then " +
            "run Install / Repair again."
    }
}

function Resolve-UsbipReplacementBoundary {
    if (-not (Test-Path -LiteralPath $script:UsbipReplacementStatePath)) {
        return
    }

    try {
        $state = Get-Content -LiteralPath $script:UsbipReplacementStatePath `
            -Raw | ConvertFrom-Json
    }
    catch {
        throw "The USBIP replacement state file is unreadable: " +
            "$script:UsbipReplacementStatePath. Refusing to cross the " +
            "driver reboot boundary automatically."
    }

    $currentBootSessionId = Get-WindowsBootSessionId
    if (-not $state.BootSessionId -or
            $state.BootSessionId -eq $currentBootSessionId) {
        $script:RebootBoundaryPending = $true
        throw "USBIP replacement phase 1 is complete. Restart Windows before " +
            "running Install / Repair again; 0.9.7.7 was not installed."
    }

    Write-SetupLog (
        "Detected the required reboot after USBIP removal; validating old " +
        "driver state before phase 2."
    ) Cyan
    Assert-UsbipPostRebootState
    Remove-Item -LiteralPath $script:UsbipReplacementStatePath -Force
    Write-SetupLog "USBIP reboot boundary validated; phase 2 may proceed." Green
}

function Get-UsbipUninstallEntry([Version]$installedVersion) {
    if (-not $installedVersion) { return $null }

    $matches = @(Get-UsbipUninstallRecords | Where-Object {
        $entryVersion = ConvertTo-VersionFromObject $_.DisplayVersion
        $entryVersion -and $entryVersion -eq $installedVersion
    })

    if ($matches.Count -gt 1) {
        $locations = ($matches | ForEach-Object {
            $_.PSPath -as [string]
        }) -join "; "
        throw "Multiple uninstall records exactly match usbip-win2 " +
            "$installedVersion ($locations). Refusing an ambiguous driver " +
            "transition. Remove stale records manually, then run Repair again."
    }

    $entry = $matches | Select-Object -First 1
    if (-not $entry) { return $null }

    $publisher = $entry.Publisher -as [string]
    $displayName = $entry.DisplayName -as [string]
    $installLocation = $entry.InstallLocation -as [string]
    $expectedDisplayName = "USBip version $installedVersion"
    if (-not [string]::Equals($publisher, "usbip-win2",
            [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($displayName, $expectedDisplayName,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact usbip-win2 AppId record has unexpected package " +
            "identity metadata. No driver transition was started."
    }

    if ([string]::IsNullOrWhiteSpace($installLocation)) {
        throw "The exact usbip-win2 AppId record has no install location. " +
            "No driver transition was started."
    }
    $canonicalUsbipDir = Split-Path -Parent $script:CanonicalUsbipPath
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath($installLocation).TrimEnd('\', '/'),
            [IO.Path]::GetFullPath($canonicalUsbipDir).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact usbip-win2 AppId record points outside the " +
            "canonical install directory: $installLocation"
    }

    return $entry
}

function Get-UsbipPortBlocks([string]$portText) {
    $blocks = @()
    $currentPort = $null
    $currentLines = [Collections.Generic.List[string]]::new()

    foreach ($line in ($portText -split "`r?`n")) {
        $header = [regex]::Match($line, '(?i)^\s*Port\s+(\d+):')
        if ($header.Success) {
            if ($null -ne $currentPort) {
                $blocks += [pscustomobject]@{
                    Port = $currentPort
                    Text = $currentLines -join [Environment]::NewLine
                }
            }
            $currentPort = [int]$header.Groups[1].Value
            $currentLines = [Collections.Generic.List[string]]::new()
        }

        if ($null -ne $currentPort) {
            $currentLines.Add($line)
        }
    }

    if ($null -ne $currentPort) {
        $blocks += [pscustomobject]@{
            Port = $currentPort
            Text = $currentLines -join [Environment]::NewLine
        }
    }

    return $blocks
}

function Test-Ds4WindowsOwnedUsbipPort([string]$block) {
    $location = [regex]::Match(
        $block, '(?im)^\s*->\s+(?<uri>usbip://\S+)\s*$')
    if (-not $location.Success) { return $false }

    $uri = $null
    if (-not [Uri]::TryCreate($location.Groups['uri'].Value,
            [UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    $uriHost = $uri.Host.Trim('[', ']')
    $hostAddress = $null
    $isLocalHost = $uriHost -ieq "localhost"
    if ([Net.IPAddress]::TryParse($uriHost, [ref]$hostAddress)) {
        $isLocalHost = $hostAddress.Equals([Net.IPAddress]::Loopback) -or
            $hostAddress.Equals([Net.IPAddress]::IPv6Loopback)
    }
    $remoteBusId = $uri.AbsolutePath.Trim('/')
    if ($uri.Scheme -ine "usbip" -or $uri.Port -ne 3241 -or
            -not $isLocalHost -or
            $remoteBusId -notmatch '^\d+-\d+$') {
        return $false
    }

    $serialLine = [regex]::Match(
        $block, '(?im)^\s*->\s+serial\b(?<value>.*)$')
    if (-not $serialLine.Success) {
        # usbip-win2 0.9.7.7 cannot assign an attach-time owner serial. The
        # exact localhost VIIPER endpoint and bus/device tuple are its identity.
        return $true
    }

    $serial = [regex]::Match(
        $serialLine.Groups['value'].Value, "^\s*'(?<serial>[^']*)'\s*$")
    if (-not $serial.Success) { return $false }

    $serialValue = $serial.Groups['serial'].Value
    return [string]::IsNullOrEmpty($serialValue) -or
        $serialValue -cmatch '^DS4W[0-9A-Fa-f]{11}$'
}

function Get-UsbipImportedPortBlocks([string]$usbipPath) {
    if (-not (Test-Path -LiteralPath $usbipPath -PathType Leaf)) {
        throw "Cannot safely inspect USBIP imports because canonical " +
            "usbip.exe is missing: $usbipPath"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $portOutput = @(& $usbipPath port 2>&1)
        $portExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $portText = ($portOutput | ForEach-Object { [string]$_ }) -join `
        [Environment]::NewLine
    if ($portExitCode -ne 0) {
        $detail = if ($portText) { $portText.Trim() }
            else { "no diagnostic output" }
        throw "Cannot safely inspect USBIP imports (exit=$portExitCode): " +
            "$detail. No driver transition was started."
    }

    return @(Get-UsbipPortBlocks $portText)
}

function Disconnect-UsbipImports([string]$usbipPath) {
    $blocks = @(Get-UsbipImportedPortBlocks $usbipPath)
    $owned = @($blocks | Where-Object {
        Test-Ds4WindowsOwnedUsbipPort $_.Text
    })
    $foreign = @($blocks | Where-Object {
        -not (Test-Ds4WindowsOwnedUsbipPort $_.Text)
    })
    if ($foreign.Count -gt 0) {
        $ports = ($foreign | ForEach-Object { $_.Port }) -join ", "
        throw "USBIP port(s) $ports are not exact DS4Windows-owned local " +
            "VIIPER imports. Close the owning application or detach those " +
            "imports manually, then run Repair again. No imports were changed."
    }

    foreach ($block in $owned) {
        $port = $block.Port
        Write-SetupLog (
            "Detaching exact DS4Windows-owned local VIIPER import on port " +
            "$port."
        ) Yellow
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $detachOutput = @(& $usbipPath detach -p $port 2>&1)
            $detachExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($detachExitCode -ne 0) {
            $detail = ($detachOutput | ForEach-Object { [string]$_ }) -join `
                [Environment]::NewLine
            throw "Could not detach DS4Windows-owned USBIP port $port " +
                "(exit=$detachExitCode): $detail. No driver transition was started."
        }
    }

    for ($attempt = 1; $attempt -le 10; $attempt++) {
        $remaining = @(Get-UsbipImportedPortBlocks $usbipPath)
        $remainingOwned = @($remaining | Where-Object {
            Test-Ds4WindowsOwnedUsbipPort $_.Text
        })
        $remainingForeign = @($remaining | Where-Object {
            -not (Test-Ds4WindowsOwnedUsbipPort $_.Text)
        })
        if ($remainingForeign.Count -gt 0) {
            $ports = ($remainingForeign | ForEach-Object { $_.Port }) -join ", "
            throw "Foreign USBIP port(s) $ports appeared while confirming " +
                "detach convergence. No driver transition was started."
        }
        if ($remainingOwned.Count -eq 0) {
            if ($owned.Count -gt 0) {
                Write-SetupLog (
                    "Confirmed all exact DS4Windows-owned USBIP imports " +
                    "are detached."
                ) Green
            }
            return
        }

        if ($attempt -lt 10) { Start-Sleep -Milliseconds 200 }
    }

    $ports = ($remainingOwned | ForEach-Object { $_.Port }) -join ", "
    throw "DS4Windows-owned USBIP port(s) $ports did not detach within " +
        "the convergence window. No driver transition was started."
}

function Remove-MismatchedUsbipPackage($entry, [Version]$installedVersion,
        [Version]$requiredVersion, [switch]$ForceUnsafeReplacement) {
    if (-not $installedVersion -or
            ($installedVersion -eq $requiredVersion -and
            -not $ForceUnsafeReplacement)) {
        return $false
    }
    if (-not $entry) {
        throw "usbip-win2 $installedVersion requires safe replacement, " +
            "but no exact uninstall record for that version exists. " +
            "Refusing to overlay $requiredVersion. Repair or remove the " +
            "installed package manually, reboot, then run setup again."
    }

    $entryVersion = ConvertTo-VersionFromObject $entry.DisplayVersion
    if (-not $entryVersion -or $entryVersion -ne $installedVersion) {
        throw "The selected usbip-win2 uninstall record does not exactly " +
            "match installed version $installedVersion. No driver transition " +
            "was started."
    }

    $uninstallCommand = $entry.QuietUninstallString -as [string]
    if (-not $uninstallCommand) {
        $uninstallCommand = $entry.UninstallString -as [string]
    }
    if (-not $uninstallCommand) {
        throw "usbip-win2 $installedVersion must be replaced with " +
            "$requiredVersion, but its uninstall command is unavailable."
    }

    $match = [regex]::Match($uninstallCommand,
        '^\s*(?:"(?<exe>[^"]+)"|(?<exe>\S+))(?:\s+(?<args>.*))?$')
    if (-not $match.Success) {
        throw "Could not parse the installed usbip-win2 uninstall command."
    }
    $uninstaller = $match.Groups['exe'].Value
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "The installed usbip-win2 uninstaller is missing: $uninstaller"
    }
    $uninstaller = (Resolve-Path -LiteralPath $uninstaller).Path
    if ([IO.Path]::GetFileName($uninstaller) -notmatch
            '(?i)^unins\d+\.exe$') {
        throw "The exact usbip-win2 uninstall record does not point to an " +
            "Inno Setup uninstaller: $uninstaller"
    }
    $canonicalUsbipDir = Split-Path -Parent $script:CanonicalUsbipPath
    $uninstallerDir = Split-Path -Parent $uninstaller
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath($uninstallerDir).TrimEnd('\'),
            [IO.Path]::GetFullPath($canonicalUsbipDir).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact-version usbip-win2 uninstall record points outside " +
            "the canonical install directory: $uninstaller"
    }

    $reason = if ($ForceUnsafeReplacement) {
        "unsafe or mixed usbip-win2 $installedVersion driver files"
    } else {
        "unsupported usbip-win2 $installedVersion"
    }
    Write-SetupLog (
        "Removing $reason before " +
        "a required reboot boundary. Pinned $requiredVersion will not be " +
        "installed in this Windows session."
    ) Yellow
    Set-UsbipReplacementBoundary $installedVersion $requiredVersion
    try {
        $uninstall = Start-Process -FilePath $uninstaller `
            -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" `
            -PassThru
    }
    catch {
        $startFailure = $_.Exception.Message
        Remove-Item -LiteralPath $script:UsbipReplacementStatePath -Force `
            -ErrorAction Stop
        throw "Could not start the usbip-win2 uninstaller: $startFailure"
    }
    $script:UsbipReplacementPhaseOne = $true
    if (-not $uninstall.WaitForExit(30000)) {
        # The uninstaller can be waiting for driver teardown. Never kill it:
        # terminating an in-flight driver uninstall risks leaving HID devices
        # disabled or package metadata half-written.
        $script:RebootRecommended = $true
        $script:UsbipRuntimeReady = $false
        Write-SetupLog (
            "The usbip-win2 uninstaller is still running after 30 seconds. " +
            "Setup left it running and will not install $requiredVersion in " +
            "this boot. Restart Windows when ready, then run Install / " +
            "Repair again."
        ) Yellow
        return $true
    }

    $uninstall.Refresh()
    if ($uninstall.ExitCode -ne 0) {
        $script:UsbipReplacementPhaseOne = $false
        Remove-Item -LiteralPath $script:UsbipReplacementStatePath -Force `
            -ErrorAction Stop
        throw "usbip-win2 uninstall failed with exit code " +
            "$($uninstall.ExitCode)."
    }

    # Never install a different userspace/driver ABI in the same boot. The old
    # kernel image and root-hub filter can remain active after Inno completes.
    $script:RebootRecommended = $true
    $script:UsbipRuntimeReady = $false
    Write-SetupLog (
        "usbip-win2 $installedVersion was removed. Restart Windows, then run " +
        "Install / Repair again to install packaged $requiredVersion."
    ) Yellow
    return $true
}

function Disable-ViiperStartup {
    try {
        Unregister-ScheduledTask -TaskPath "\" -TaskName "RunVIIPER" `
            -Confirm:$false `
            -ErrorAction SilentlyContinue
    }
    catch { }
    try {
        Remove-ItemProperty `
            -LiteralPath $script:TargetRunKeyPath `
            -Name "VIIPER" -ErrorAction SilentlyContinue
    }
    catch { }
    Write-SetupLog (
        "Disabled existing VIIPER startup until setup verifies the driver ABI."
    ) Green
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

function Assert-ViiperFileSha256([string]$path, [string]$expectedHash) {
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $expectedHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Packaged VIIPER failed SHA256 verification. Expected " +
            "$expectedHash; received $actualHash."
    }

    Write-SetupLog "Verified packaged VIIPER SHA256: $actualHash" Green
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
                "usbip-win2 0.9.7.7 is installed, but Windows still has a " +
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

function Stop-Ds4WindowsProcesses([string]$operation) {
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -ieq "DS4Windows.exe" -and
            $_.ProcessId -ne $script:InstallerHostPid
        })
    if ($processes.Count -eq 0) { return $true }

    $restartPath = $script:Ds4WindowsRestartPath
    if (-not $restartPath) {
        $restartPath = $processes |
            Where-Object { $_.ExecutablePath -and
                (Test-Path -LiteralPath $_.ExecutablePath) } |
            Select-Object -First 1 -ExpandProperty ExecutablePath
    }
    if (-not $restartPath) {
        $bundledPath = Join-Path `
            (Split-Path -Parent $script:PackageExtrasRoot) "DS4Windows.exe"
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

    $remaining = @(Get-Process -Name "DS4Windows" `
        -ErrorAction SilentlyContinue | Where-Object {
            $_.Id -ne $script:InstallerHostPid
        })
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

function Get-ViiperAssetUrl {
    return "https://github.com/hbashton/VIIPER/releases/download/" +
        "v0.0.6/viiper-windows-amd64.zip"
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

function Test-ManagedViiperPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    try {
        $candidate = [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
        $managed = [IO.Path]::GetFullPath(
            (Join-Path $script:InstallDir "viiper.exe")).TrimEnd('\', '/')
        return [string]::Equals($candidate, $managed,
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Get-ForeignViiperProcesses {
    return @(Get-RunningViiperProcesses | Where-Object {
        -not (Test-ManagedViiperPath ($_.ExecutablePath -as [string]))
    })
}

function Remove-ForeignViiperInstallations {
    $foreign = @(Get-ForeignViiperProcesses)
    if ($foreign.Count -eq 0) { return }

    Write-SetupLog (
        "Detected running VIIPER process(es) outside the managed install " +
        "path '$script:InstallDir'. DS4Windows will never use them."
    ) Yellow
    foreach ($process in $foreign) {
        $displayPath = if ($process.ExecutablePath) {
            $process.ExecutablePath
        }
        else { "<path unavailable>" }
        Write-SetupLog (
            "  Foreign VIIPER PID=$($process.ProcessId): $displayPath"
        ) Yellow
    }

    # This destructive choice is always interactive, even when -Yes was used
    # for unattended confirmation of the normal managed setup.
    $answer = Read-Host (
        "Stop these foreign VIIPER processes with administrator rights and " +
        "remove their viiper.exe files? [Y/N]")
    if ($answer -notmatch '^(?i:y|yes)$') {
        $script:UserCanceled = $true
        throw "Setup canceled because a foreign VIIPER instance is running. " +
            "Close or remove it, then run Install / Repair again."
    }

    $unknownPath = @($foreign | Where-Object {
        [string]::IsNullOrWhiteSpace($_.ExecutablePath -as [string])
    })
    $foreignPaths = @($foreign | Where-Object { $_.ExecutablePath } |
        ForEach-Object {
            [IO.Path]::GetFullPath([string]$_.ExecutablePath)
        } | Sort-Object -Unique)

    Disable-ViiperStartup
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $remaining = @(Get-ForeignViiperProcesses)
        if ($remaining.Count -eq 0) { break }
        foreach ($process in $remaining) {
            try {
                & taskkill.exe /PID $process.ProcessId /T /F 2>&1 |
                    Out-Null
            }
            catch { }
        }
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(Get-ForeignViiperProcesses)
    if ($remaining.Count -gt 0) {
        $details = ($remaining | ForEach-Object {
            "PID=$($_.ProcessId) path=$($_.ExecutablePath)"
        }) -join "; "
        throw "Administrator termination of foreign VIIPER failed: " +
            "$details. No managed VIIPER was started."
    }
    if ($unknownPath.Count -gt 0) {
        throw "The foreign VIIPER process was stopped, but Windows did not " +
            "reveal its executable path, so setup could not safely remove " +
            "the binary. No managed VIIPER was started."
    }

    foreach ($path in $foreignPaths) {
        if (Test-ManagedViiperPath $path) {
            throw "Refusing to remove the managed VIIPER binary: $path"
        }
        try {
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
        catch {
            throw "Foreign VIIPER was stopped, but its executable could not " +
                "be removed from '$path': $($_.Exception.Message). No " +
                "managed VIIPER was started."
        }
        if (Test-Path -LiteralPath $path) {
            throw "Foreign VIIPER executable still exists at '$path'. No " +
                "managed VIIPER was started."
        }
        Write-SetupLog "Removed foreign VIIPER executable: $path" Green
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

function Start-AndVerifyViiper([string]$taskName) {
    if (Test-ViiperApi) { return $true }
    try {
        Start-ScheduledTask -TaskPath "\" -TaskName $taskName `
            -ErrorAction Stop
    }
    catch {
        Write-SetupLog (
            "Could not start managed VIIPER task '$taskName': " +
            $_.Exception.Message
        ) Red
        return $false
    }
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        Start-Sleep -Milliseconds 500
        if (Test-ViiperApi) { return $true }
    }
    return $false
}

function Convert-AccountToSid([string]$identity) {
    if ([string]::IsNullOrWhiteSpace($identity)) { return $null }
    try {
        return ([Security.Principal.SecurityIdentifier]::new($identity)).Value
    }
    catch { }
    try {
        return ([Security.Principal.NTAccount]::new($identity)).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch { return $null }
}

function Test-HighestLogonTask([string]$taskName,
        [string]$executablePath, [string]$arguments,
        [string]$workingDirectory) {
    $registered = Get-ScheduledTask -TaskPath "\" -TaskName $taskName `
        -ErrorAction Stop
    if (@($registered.Actions).Count -ne 1 -or
            @($registered.Triggers).Count -ne 1) {
        return $false
    }
    $registeredAction = $registered.Actions | Select-Object -First 1
    $expectedArguments = if ($null -eq $arguments) { "" } else {
        $arguments.Trim()
    }
    $actualArguments = if ($null -eq $registeredAction.Arguments) { "" }
        else { ([string]$registeredAction.Arguments).Trim() }
    $expectedWorkingDirectory = if (
            [string]::IsNullOrWhiteSpace($workingDirectory)) { "" }
        else { [IO.Path]::GetFullPath($workingDirectory).TrimEnd('\') }
    $actualWorkingDirectory = if (
            [string]::IsNullOrWhiteSpace($registeredAction.WorkingDirectory)) {
            ""
        }
        else {
            [IO.Path]::GetFullPath(
                [string]$registeredAction.WorkingDirectory).TrimEnd('\')
        }
    $principalSid = Convert-AccountToSid `
        ([string]$registered.Principal.UserId)
    $matchingTrigger = @($registered.Triggers | Where-Object {
        $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and
        (Convert-AccountToSid ([string]$_.UserId)) -eq $script:TargetUserSid
    }).Count -gt 0

    return $registered.Settings.Enabled -and
        [string]::Equals(
            [IO.Path]::GetFullPath([string]$registeredAction.Execute),
            [IO.Path]::GetFullPath($executablePath),
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($actualArguments, $expectedArguments,
            [StringComparison]::Ordinal) -and
        [string]::Equals($actualWorkingDirectory, $expectedWorkingDirectory,
            [StringComparison]::OrdinalIgnoreCase) -and
        $registered.Principal.RunLevel -eq 'Highest' -and
        $registered.Principal.LogonType -eq 'Interactive' -and
        $principalSid -eq $script:TargetUserSid -and $matchingTrigger
}

function Register-HighestLogonTask([string]$taskName,
        [string]$executablePath, [string]$arguments,
        [string]$workingDirectory) {
    try {
        $taskActionParameters = @{
            Execute = $executablePath
            Argument = $arguments
        }
        if (-not [string]::IsNullOrWhiteSpace($workingDirectory)) {
            $taskActionParameters.WorkingDirectory = $workingDirectory
        }
        $taskAction = New-ScheduledTaskAction @taskActionParameters
        $taskTrigger = New-ScheduledTaskTrigger -AtLogOn `
            -User $script:TargetUserSid
        $taskPrincipal = New-ScheduledTaskPrincipal `
            -UserId $script:TargetUserSid `
            -RunLevel Highest -LogonType Interactive
        $taskSettings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -MultipleInstances IgnoreNew

        Register-ScheduledTask -TaskPath "\" -TaskName $taskName `
            -Action $taskAction -Trigger $taskTrigger `
            -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null
        Enable-ScheduledTask -TaskPath "\" -TaskName $taskName `
            -ErrorAction Stop | Out-Null

        if (-not (Test-HighestLogonTask $taskName $executablePath `
                $arguments $workingDirectory)) {
            throw "Task registration verification failed."
        }
        return $true
    }
    catch {
        Write-SetupLog "Failed modern scheduled task registration: $($_.Exception.Message)" Yellow
        try {
            Unregister-ScheduledTask -TaskPath "\" -TaskName $taskName `
                -Confirm:$false `
                -ErrorAction SilentlyContinue
        }
        catch { }
    }

    return $false
}

function Register-ViiperRunTask([string]$viiperPath, [string]$taskName) {
    return Register-HighestLogonTask $taskName $viiperPath "server" `
        (Split-Path -Parent $viiperPath)
}

function Register-Ds4WindowsRunTask([string]$ds4WindowsPath) {
    return Register-HighestLogonTask "RunDS4Windows" $ds4WindowsPath "-m" `
        (Split-Path -Parent $ds4WindowsPath)
}

function Assert-SafeManagedDirectory([string]$directory, [string]$label,
        [switch]$RequireExisting) {
    $resolved = [IO.Path]::GetFullPath($directory).TrimEnd('\', '/')
    $root = [IO.Path]::GetPathRoot($resolved).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($resolved) -or
            [string]::Equals($resolved, $root,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use an unsafe $label path."
    }

    $rootWithSeparator = [IO.Path]::GetPathRoot($resolved)
    $relative = $resolved.Substring($rootWithSeparator.Length)
    $cursor = $rootWithSeparator
    foreach ($component in $relative.Split([char[]]@('\', '/'),
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $component
        if (-not (Test-Path -LiteralPath $cursor)) { continue }
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$label path traverses a reparse point: $cursor"
        }
        if (-not $item.PSIsContainer) {
            throw "$label path component is not a directory: $cursor"
        }
    }

    if ($RequireExisting -and
            -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$label directory is missing: $resolved"
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        $reparsePoint = Get-ChildItem -LiteralPath $resolved -Force `
            -Recurse -ErrorAction Stop | Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            } | Select-Object -First 1
        if ($reparsePoint) {
            throw "$label contains a reparse point: $($reparsePoint.FullName)"
        }
    }

    return $resolved
}

function Install-Ds4WindowsPackage([string]$sourceDirectory,
        [string]$destinationDirectory) {
    $source = Assert-SafeManagedDirectory $sourceDirectory `
        "DS4Windows source package" -RequireExisting
    $destination = Assert-SafeManagedDirectory $destinationDirectory `
        "managed DS4Windows installation"
    $sourcePrefix = $source.TrimEnd('\') + '\'
    $destinationPrefix = $destination.TrimEnd('\') + '\'
    if (-not [string]::Equals($source, $destination,
            [StringComparison]::OrdinalIgnoreCase) -and
            ($sourcePrefix.StartsWith($destinationPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            $destinationPrefix.StartsWith($sourcePrefix,
                [StringComparison]::OrdinalIgnoreCase))) {
        throw "The DS4Windows source and managed destination may not contain one another."
    }

    $manifestName = ".ds4windows-managed-files.txt"
    $sourceManifest = Join-Path $source $manifestName
    if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) {
        throw "The DS4Windows package manifest is missing. Extract and run the complete release ZIP instead of a raw build folder."
    }

    $managedFiles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $copyPlan = [Collections.Generic.List[object]]::new()
    foreach ($entry in Get-Content -LiteralPath $sourceManifest) {
        $relative = ([string]$entry).Trim().Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or
                [IO.Path]::IsPathRooted($relative) -or
                $relative.Split([char]'\') -contains '..') {
            throw "The DS4Windows package manifest contains an unsafe path: '$entry'."
        }
        if (-not $managedFiles.Add($relative)) {
            throw "The DS4Windows package manifest contains a duplicate path: '$relative'."
        }

        $sourcePath = [IO.Path]::GetFullPath((Join-Path $source $relative))
        $destinationPath = [IO.Path]::GetFullPath(
            (Join-Path $destination $relative))
        if (-not $sourcePath.StartsWith($sourcePrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
                -not $destinationPath.StartsWith($destinationPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "The DS4Windows package manifest does not resolve to a packaged file: '$relative'."
        }
        $copyPlan.Add([pscustomobject]@{
            Relative = $relative
            Source = $sourcePath
            Destination = $destinationPath
        })
    }
    if ($copyPlan.Count -eq 0) {
        throw "The DS4Windows package manifest is empty."
    }

    if (-not [string]::Equals($source, $destination,
            [StringComparison]::OrdinalIgnoreCase)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null

        $oldManifest = Join-Path $destination $manifestName
        if (Test-Path -LiteralPath $oldManifest -PathType Leaf) {
            foreach ($oldEntry in Get-Content -LiteralPath $oldManifest) {
                $oldRelative = ([string]$oldEntry).Trim().Replace('/', '\')
                if ([string]::IsNullOrWhiteSpace($oldRelative) -or
                        [IO.Path]::IsPathRooted($oldRelative) -or
                        $oldRelative.Split([char]'\') -contains '..' -or
                        $managedFiles.Contains($oldRelative)) {
                    continue
                }
                $obsoletePath = [IO.Path]::GetFullPath(
                    (Join-Path $destination $oldRelative))
                if ($obsoletePath.StartsWith($destinationPrefix,
                        [StringComparison]::OrdinalIgnoreCase) -and
                        (Test-Path -LiteralPath $obsoletePath -PathType Leaf)) {
                    Remove-Item -LiteralPath $obsoletePath -Force `
                        -ErrorAction Stop
                    Write-SetupLog "Removed obsolete packaged file: $oldRelative"
                }
            }
        }

        foreach ($file in $copyPlan) {
            $parent = Split-Path -Parent $file.Destination
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            Copy-Item -LiteralPath $file.Source `
                -Destination $file.Destination -Force
        }
        Copy-Item -LiteralPath $sourceManifest `
            -Destination (Join-Path $destination $manifestName) -Force
    }

    $installedExecutable = Join-Path $destination "DS4Windows.exe"
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw "The managed DS4Windows installation is missing DS4Windows.exe."
    }
    Write-SetupLog "DS4Windows installed to $destination" Green
    return $installedExecutable
}

function Protect-ElevatedTaskTargetDirectory([string]$directory,
        [string]$label) {
    $resolved = Assert-SafeManagedDirectory $directory $label `
        -RequireExisting

    $administrators = '*S-1-5-32-544'
    $system = '*S-1-5-18'
    $targetUser = "*$script:TargetUserSid"
    & icacls.exe $resolved /reset /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not reset access controls for the $label directory."
    }
    & icacls.exe $resolved /inheritance:r /grant:r `
        "${system}:(OI)(CI)(F)" `
        "${administrators}:(OI)(CI)(F)" `
        "${targetUser}:(OI)(CI)(RX)" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not protect the elevated $label startup target."
    }

    $children = @(Get-ChildItem -LiteralPath $resolved -Force `
        -ErrorAction Stop)
    if ($children.Count -gt 0) {
        & icacls.exe (Join-Path $resolved '*') /reset /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not apply protected inheritance inside $label."
        }
    }
    & icacls.exe $resolved /setowner $administrators /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not assign protected ownership for $label."
    }

    Write-SetupLog (
        "Protected $label package files from unelevated replacement: " +
        $resolved
    ) Green
}

try {
    if (-not (Test-Administrator)) {
        throw "Administrator permission is required. Launch setup from DS4Windows so Windows can request it automatically."
    }
    $elevatedIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not [string]::Equals($elevatedIdentity.User.Value,
            $script:TargetUserSid,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The signed-in Windows account must itself be an administrator " +
            "to receive highest-privilege startup tasks. Setup will not " +
            "register those tasks under alternate administrator credentials."
    }

    try {
        $script:SetupMutex = [Threading.Mutex]::new(
            $false, "Local\DS4Windows-VIIPER-Setup")
        if (-not $script:SetupMutex.WaitOne(0)) {
            throw "Another DS4Windows VIIPER setup is already running."
        }
    }
    catch [Threading.AbandonedMutexException] {
        # An abandoned mutex is acquired by this thread.
    }

    Write-Host ""
    Write-Host "DS4Windows VIIPER virtual controller setup" `
        -ForegroundColor Green
    Write-Host "Planned order:" -ForegroundColor Cyan
    Write-Host "  1. Install VIIPER and register both elevated startup tasks." `
        -ForegroundColor Cyan
    Write-Host "  2. Verify or install packaged usbip-win2 0.9.7.7." `
        -ForegroundColor Cyan
    Write-Host "  3. Verify the startup tasks remain exact and enabled." `
        -ForegroundColor Cyan
    Write-Host "  4. Start and verify the local VIIPER API." `
        -ForegroundColor Cyan
    Write-Host (
        "USB hub devices may restart during driver replacement, and Windows " +
        "may require a reboot."
    ) -ForegroundColor Yellow
    if (-not $Yes) {
        Write-Host "VIIPER is bundled and installs first. An incompatible USBIP driver is removed only after VIIPER is in place, and 0.9.7.7 is installed only after a separate reboot." -ForegroundColor Yellow
        Write-Host "Save work before continuing; a driver replacement can require you to restart Windows and run Repair again." -ForegroundColor Yellow
        $answer = Read-Host "Install bundled VIIPER first and continue? [Y/N]"
        if ($answer -notmatch '^(?i:y|yes)$') {
            $script:UserCanceled = $true
            throw "Setup canceled by the user before any changes were made."
        }
    }

    $script:InstallDir = Assert-SafeManagedDirectory $script:InstallDir `
        "VIIPER installation"
    $script:Ds4WindowsInstallDir = Assert-SafeManagedDirectory `
        $script:Ds4WindowsInstallDir "managed DS4Windows installation"
    New-Item -ItemType Directory -Path $script:Ds4WindowsInstallDir `
        -Force | Out-Null
    Protect-ElevatedTaskTargetDirectory $script:Ds4WindowsInstallDir `
        "DS4Windows"
    New-Item -ItemType Directory -Path $script:InstallDir -Force | Out-Null
    Protect-ElevatedTaskTargetDirectory $script:InstallDir "VIIPER"
    New-Item -ItemType Directory -Path $script:TempDir -Force | Out-Null
    Write-SetupLog ""
    Write-SetupLog "Setup authorized; beginning verified installation." Green
    if (Disable-ConflictingCitrixUsbMonitor) {
        # Do not enumerate, detach, uninstall, or replace any USB/IP device
        # while the crashing Citrix kernel filter remains loaded. Changing
        # Start only affects the next boot; crossing that boundary is the
        # only safe continuation.
        Disable-ViiperStartup
        $script:SafetyRestartPending = $true
        throw "Citrix generic USB redirection was disabled. Restart Windows " +
            "before running Install / Repair again; no USB/IP operation was " +
            "attempted in this unsafe kernel session."
    }
    Resolve-UsbipReplacementBoundary

    Write-Step "Step 1 of 4 - Installing VIIPER 0.0.6"
    Remove-ForeignViiperInstallations
    $viiperPath = Join-Path $script:InstallDir "viiper.exe"
    $candidatePath = Join-Path $script:TempDir "viiper.exe"
    if (Test-Path -LiteralPath $script:BundledViiperPath -PathType Leaf) {
        Write-SetupLog "Using packaged VIIPER 0.0.6 x64 binary." Green
        Assert-ViiperFileSha256 $script:BundledViiperPath `
            $script:BundledViiperSha256
        Copy-Item -LiteralPath $script:BundledViiperPath `
            -Destination $candidatePath -Force
    }
    else {
        Write-SetupLog (
            "Packaged VIIPER is unavailable; using the GitHub recovery " +
            "download."
        ) Yellow
        Expand-ViiperAsset (Get-ViiperAssetUrl) $candidatePath
    }
    Assert-ViiperFileSha256 $candidatePath $script:BundledViiperSha256
    if (-not (Stop-Ds4WindowsProcesses "VIIPER backend replacement")) {
        throw "Unable to quiesce DS4Windows before replacing VIIPER."
    }
    if (-not (Stop-ViiperProcesses "VIIPER backend replacement")) {
        throw "Unable to stop the existing VIIPER backend before replacement."
    }
    Disable-ViiperStartup
    Install-ViiperAtomically $candidatePath $viiperPath
    Write-SetupLog "VIIPER installed to $viiperPath" Green

    if (-not $script:Ds4WindowsRestartPath) {
        $bundledDs4Windows = Join-Path `
            (Split-Path -Parent $script:PackageExtrasRoot) "DS4Windows.exe"
        if (Test-Path -LiteralPath $bundledDs4Windows -PathType Leaf) {
            $script:Ds4WindowsRestartPath = $bundledDs4Windows
        }
    }
    if (-not $script:Ds4WindowsRestartPath -or
            -not (Test-Path -LiteralPath $script:Ds4WindowsRestartPath `
                -PathType Leaf)) {
        throw "DS4Windows.exe could not be located for the elevated startup task."
    }
    # The protected package snapshot is what gets copied into Program Files,
    # but portable users deliberately keep the executable that launched this
    # setup as their startup target. The normal startup path also repairs this
    # exact choice whenever a different portable copy is opened.
    $sourceDs4WindowsDirectory = [IO.Path]::GetFullPath(
        (Split-Path -Parent $script:PackageExtrasRoot)).TrimEnd('\', '/')
    $sourceDs4WindowsPath = Join-Path $sourceDs4WindowsDirectory `
        "DS4Windows.exe"
    if (-not (Test-Path -LiteralPath $sourceDs4WindowsPath -PathType Leaf)) {
        throw "The protected DS4Windows package snapshot is incomplete."
    }
    $taskTargetHash = (Get-FileHash -LiteralPath `
        $script:Ds4WindowsRestartPath -Algorithm SHA256).Hash
    $sourceTargetHash = (Get-FileHash -LiteralPath `
        $sourceDs4WindowsPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($taskTargetHash, $sourceTargetHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The currently running DS4Windows executable changed while " +
            "setup was starting. Close it, extract a complete release ZIP, " +
            "and run Install / Repair again."
    }

    $managedDs4WindowsPath = Install-Ds4WindowsPackage `
        $sourceDs4WindowsDirectory $script:Ds4WindowsInstallDir
    Write-SetupLog (
        "Managed DS4Windows copy: $managedDs4WindowsPath; startup target: " +
        $script:Ds4WindowsRestartPath
    ) Green

    # Register both tasks before any driver operation can require a reboot.
    # VIIPER itself fails closed until its exact USBIP ABI probe succeeds.
    if (-not (Register-ViiperRunTask $viiperPath "RunVIIPER")) {
        throw "Could not create the elevated RunVIIPER startup task."
    }
    if (-not (Register-Ds4WindowsRunTask $script:Ds4WindowsRestartPath)) {
        Unregister-ScheduledTask -TaskPath "\" -TaskName "RunVIIPER" `
            -Confirm:$false `
            -ErrorAction SilentlyContinue
        throw "Could not register the elevated RunDS4Windows startup task."
    }
    Write-SetupLog (
        "Registered and verified enabled elevated RunVIIPER and " +
        "RunDS4Windows tasks for $script:TargetUserName before driver setup."
    ) Green

    Write-Step "Step 2 of 4 - Checking usbip-win2 0.9.7.7"
    $requiredUsbipVersion = $script:RequiredUsbipVersion
    try {
        $usbipVersion = Get-UsbipInstalledVersion
    }
    catch {
        Write-SetupLog "usbip-win2 version check failed: $($_.Exception.Message)" Yellow
        $usbipVersion = $null
    }

    $canonicalUsbipPresent = Test-Path -LiteralPath $script:CanonicalUsbipPath
    $usbipDriverIntegrity = Get-UsbipDriverIntegrity
    if ($usbipDriverIntegrity.InstalledServiceCount -lt 0) {
        throw "USBIP driver integrity inspection failed: " +
            $usbipDriverIntegrity.Message
    }
    $usbipDriverPresent =
        $usbipDriverIntegrity.InstalledServiceCount -gt 0
    $usbipDriverFilesSafe = [bool]$usbipDriverIntegrity.Safe
    if (-not $canonicalUsbipPresent -and
            ($usbipDriverPresent -or $usbipVersion)) {
        throw "USBIP driver or package artifacts exist, but canonical " +
            "usbip.exe is missing. Refusing to change a driver whose active " +
            "imports cannot be inspected safely. Repair or remove USBIP " +
            "manually, reboot, then run setup again."
    }
    if (($canonicalUsbipPresent -or $usbipDriverPresent) -and
            -not $usbipVersion) {
        throw "USBIP files are present, but their installed version cannot " +
            "be read. Refusing an unknown driver ABI. Repair or remove the " +
            "existing USBIP package manually, reboot, then run setup again."
    }
    $usbipVersionReady = $canonicalUsbipPresent -and $usbipVersion -and
        $usbipVersion -eq $requiredUsbipVersion
    $usbipPackageReady = $usbipVersionReady -and $usbipDriverFilesSafe
    if ($usbipPackageReady) {
        $script:UsbipRuntimeReady = Test-UsbipRuntime $script:CanonicalUsbipPath
    }

    if ($usbipPackageReady -and $script:UsbipRuntimeReady) {
        Write-SetupLog (
            "usbip-win2 is ready: $usbipVersion with exact signed driver " +
            "files at $script:CanonicalUsbipPath"
        ) Green
    }
    elseif ($usbipPackageReady) {
        $script:RebootRecommended = $true
        Write-SetupLog (
            "The supported usbip-win2 $requiredUsbipVersion package is " +
            "already installed, but its runtime probe failed " +
            "($script:UsbipRuntimeProbeState). Setup will not overlay a " +
            "live driver. Restart Windows, then repair USBIP manually if " +
            "the probe still fails."
        ) Yellow
    }
    else {
        $state = if (-not $canonicalUsbipPresent) {
            "missing from the canonical Program Files location"
        }
        elseif (-not $usbipVersion) {
            "present with an unreadable version"
        }
        elseif ($usbipVersion -ne $requiredUsbipVersion) {
            "unsupported ($usbipVersion)"
        }
        elseif (-not $usbipDriverFilesSafe) {
            "installed with unsafe or mixed driver files (" +
                $usbipDriverIntegrity.Message + ")"
        }
        else {
            "installed but its userspace/driver ABI probe failed"
        }
        Write-SetupLog (
            "usbip-win2 is $state; installing or repairing " +
            "$requiredUsbipVersion."
        ) Yellow
        $uninstallEntry = $null
        if ($usbipVersion -and
                ($usbipVersion -ne $requiredUsbipVersion -or
                -not $usbipDriverFilesSafe)) {
            $uninstallEntry = Get-UsbipUninstallEntry $usbipVersion
            if (-not $uninstallEntry) {
                throw "usbip-win2 $usbipVersion requires safe replacement, " +
                    "but no exact uninstall record for that version exists. " +
                    "Refusing to overlay $requiredUsbipVersion. Remove it " +
                    "manually, reboot, then run Repair again."
            }
        }

        if (-not (Stop-Ds4WindowsProcesses "usbip-win2 driver upgrade")) {
            throw "Unable to quiesce DS4Windows before the usbip-win2 driver upgrade."
        }
        if (-not (Stop-ViiperProcesses "usbip-win2 driver upgrade")) {
            throw "Unable to quiesce VIIPER before the usbip-win2 driver upgrade. " +
                "Close viiper.exe manually and run Install / Repair again."
        }
        if ($canonicalUsbipPresent) {
            Disconnect-UsbipImports $script:CanonicalUsbipPath
        }
        elseif ($usbipDriverPresent -or $usbipVersion) {
            throw "Cannot inspect USBIP imports because canonical usbip.exe " +
                "is missing. No driver transition was started."
        }
        $removedMismatchedPackage = Remove-MismatchedUsbipPackage `
            $uninstallEntry $usbipVersion `
            $requiredUsbipVersion `
            -ForceUnsafeReplacement:($usbipVersion -eq `
                $requiredUsbipVersion -and -not $usbipDriverFilesSafe)

        if ($removedMismatchedPackage) {
            Write-SetupLog (
                "USBIP replacement phase 1 of 2 is complete. Packaged " +
                "$requiredUsbipVersion was intentionally not installed in " +
                "this boot. Restart Windows, then run Install / Repair again."
            ) Yellow
        }
        else {
            $usbipInstaller = Join-Path $script:TempDir `
                "USBip-0.9.7.7-x64.exe"
            if (Test-Path -LiteralPath $script:BundledUsbipInstallerPath) {
                Write-SetupLog (
                    "Using the bundled usbip-win2 0.9.7.7 installer."
                ) Green
                Assert-FileSha256 $script:BundledUsbipInstallerPath `
                    $script:UsbipInstallerSha256
                Copy-Item -LiteralPath $script:BundledUsbipInstallerPath `
                    -Destination $usbipInstaller -Force
            }
            else {
                Write-SetupLog (
                    "Bundled usbip-win2 installer is unavailable; using " +
                    "the verified upstream recovery download."
                ) Yellow
                Invoke-Download $script:UsbipInstallerUrl $usbipInstaller
            }
            Assert-FileSha256 $usbipInstaller $script:UsbipInstallerSha256

            Write-SetupLog "Windows may briefly restart USB hub devices." Yellow
            $installer = Start-Process -FilePath $usbipInstaller `
                -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010" `
                -PassThru -Wait
            if ($installer.ExitCode -notin @(0, 3010)) {
                throw "usbip-win2 setup failed with exit code $($installer.ExitCode)."
            }
            if ($installer.ExitCode -eq 3010) {
                $script:RebootRecommended = $true
                Write-SetupLog (
                    "usbip-win2 setup requested a restart (exit=" +
                    "$($installer.ExitCode)). VIIPER will remain stopped."
                ) Yellow
            }

            $usbipVersion = Get-UsbipInstalledVersion
            $canonicalUsbipPresent = Test-Path -LiteralPath $script:CanonicalUsbipPath
            $usbipDriverIntegrity = Get-UsbipDriverIntegrity
            $usbipDriverFilesSafe = [bool]$usbipDriverIntegrity.Safe
            $usbipVersionReady = $canonicalUsbipPresent -and $usbipVersion -and
                $usbipVersion -eq $requiredUsbipVersion
            $usbipPackageReady = $usbipVersionReady -and
                $usbipDriverFilesSafe
            if (-not $usbipPackageReady) {
                $script:RebootRecommended = $true
                Write-SetupLog (
                    "usbip-win2 $requiredUsbipVersion and its exact signed " +
                    "driver files are not active yet (" +
                    $usbipDriverIntegrity.Message + "). A reboot or repair " +
                    "is required."
                ) Yellow
            }
            elseif (-not $script:RebootRecommended) {
                $script:UsbipRuntimeReady = Test-UsbipRuntime `
                    $script:CanonicalUsbipPath
                if (-not $script:UsbipRuntimeReady) {
                    $script:RebootRecommended = $true
                    Write-SetupLog (
                        "usbip.exe port did not confirm a compatible driver ABI. " +
                        "A reboot or repair is required; setup will not report Ready."
                    ) Yellow
                }
            }
        }
    }

    if ($script:UsbipReplacementPhaseOne) {
        $script:ExitCode = 3010
        Write-Host ""
        Write-SetupLog (
            "VIIPER is installed and the old USBIP package removal was " +
            "started. Packaged 0.9.7.7 was not installed. Restart Windows, " +
            "then run Install / Repair again to complete phase 2."
        ) Yellow
    }
    else {
    Write-Step "Step 3 of 4 - Verifying startup tasks"
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended) {
        if (-not (Stop-ViiperProcesses "install registration")) {
            throw "VIIPER registration could not proceed because a VIIPER process could not be closed automatically. Please close viiper.exe manually, then run Install / Repair again."
        }

        if (-not (Test-HighestLogonTask "RunVIIPER" $viiperPath "server" `
                    (Split-Path -Parent $viiperPath)) -or
                -not (Test-HighestLogonTask "RunDS4Windows" `
                    $script:Ds4WindowsRestartPath "-m" `
                    (Split-Path -Parent $script:Ds4WindowsRestartPath))) {
            throw "A verified startup task changed during setup."
        }
        Write-SetupLog "Both elevated startup tasks remain verified." Green
    }
    else {
        [void](Stop-ViiperProcesses "pending usbip-win2 reboot")
        Write-SetupLog (
            "Both startup tasks are registered for the next sign-in. " +
            "VIIPER startup is deferred in this session until usbip-win2 " +
            "passes its runtime ABI check."
        ) Yellow
    }

    Write-Step "Step 4 of 4 - Verifying runtime readiness"
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended -and
            (Start-AndVerifyViiper "RunVIIPER")) {
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
            Start-ScheduledTask -TaskPath "\" -TaskName "RunDS4Windows" `
                -ErrorAction Stop
        }
    }
    else {
        Write-SetupLog $finish Yellow
        if ($script:ExitCode -eq 0) {
            $script:ExitCode = 3010
        }
    }
    }
}
catch {
    Write-Host ""
    if ($script:UserCanceled) {
        $script:ExitCode = 1223
        Write-SetupLog (
            "Setup canceled. No USBIP driver or foreign executable was " +
            "changed."
        ) Yellow
    }
    elseif ($script:RebootBoundaryPending -or
            $script:SafetyRestartPending) {
        $script:ExitCode = 3010
        Write-SetupLog $_.Exception.Message Yellow
        Write-SetupLog "Restart required; no install changes were made." Yellow
        Write-SetupLog "Details were saved to $script:LogPath" Yellow
    }
    else {
        $script:ExitCode = 1
        Write-SetupLog "Setup could not finish: $($_.Exception.Message)" Red
        Write-SetupLog "Details were saved to $script:LogPath" Yellow
    }
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
    if ($script:SetupMutex) {
        try { $script:SetupMutex.ReleaseMutex() } catch { }
        try { $script:SetupMutex.Dispose() } catch { }
    }
}

exit $script:ExitCode
