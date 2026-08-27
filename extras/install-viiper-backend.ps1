param(
    [switch]$NoPause,
    [switch]$Yes,
    [string]$TargetLocalAppData,
    [string]$TargetUserSid,
    [string]$TargetUserName,
    [string]$TargetDs4WindowsPath,
    [string]$PackageExtrasRoot,
    [int]$InstallerHostPid = 0,
    # Backward-compatible name used by older DS4Windows packages. This now
    # means "keep DS4Windows portable" only. The elevated VIIPER service is
    # always installed beneath Program Files.
    [switch]$PortableInstallation,
    [switch]$KeepDs4WindowsPortable,
    [switch]$SkipStartupTasks,
    [switch]$SetupMutexAlreadyHeld,
    [switch]$InstallerMode,
    [string]$CorrelationId
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
$script:KeepDs4WindowsPortable = [bool]$KeepDs4WindowsPortable -or
    [bool]$PortableInstallation
$script:InstallerMode = [bool]$InstallerMode
$script:CorrelationId = if ([string]::IsNullOrWhiteSpace($CorrelationId) -or
        $CorrelationId.Trim() -notmatch '^[0-9A-Fa-f]{32}$') {
    [Guid]::NewGuid().ToString("N")
}
else { $CorrelationId.Trim().ToLowerInvariant() }
$script:RunAtStartupEnabled = -not [bool]$SkipStartupTasks
if ($env:DS4W_SETUP_PORTABLE_INSTALLATION -eq '1') {
    $script:KeepDs4WindowsPortable = $true
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
$script:SetupMutexOwned = $false
$script:SetupTransactionStarted = $false
$script:RequiredUsbipVersion = [Version]"0.9.7.7"
$script:UsbipInstallerSha256 =
    "51620fa5f9f8be5932bc9d786deee557ce06d5407a99cab490dcfac71f185fea"
$script:UsbipExecutableSha256 =
    "fc1660e3759d8af4cede48dbe194285a5a1de85ce6e3216724499afd32be92e8"
$script:UsbipUdeDriverSha256 =
    "51db440065393e588a6b2585508c50eb3e1510b7b06d9afa6c5bde583751ea7d"
$script:UsbipFilterDriverSha256 =
    "c290299ff4d0f6a597db5ce03e15b29a5349cdce7c587ebfbd9ecaeca04f73ed"
$script:BundledViiperPath = Join-Path $script:PackageExtrasRoot `
    "VIIPER-0.1.2-x64.exe"
$script:BundledViiperSha256Path = $script:BundledViiperPath + ".sha256"
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
$script:InstallerLogRoot = Join-Path $env:ProgramData "DS4Windows\Installer"
# Keep diagnostics outside the transaction target so failures that occur
# before Program Files/LocalAppData creation still leave a readable record.
$script:LogPath = Join-Path $script:InstallerLogRoot `
    "infrastructure-actions.log"
$script:UsbipReplacementStatePath = Join-Path $script:InstallDir `
    "usbip-replacement-pending.json"
$script:UsbipUninstallKeyName = `
    "{199505b0-b93d-4521-a8c7-897818e0205a}_is1"
$script:InfrastructureRegistryPath = "HKLM:\SOFTWARE\DS4Windows"
$script:InfrastructureVersion = "VIIPER-0.1.2+USBIP-0.9.7.7"
$script:System32 = [Environment]::SystemDirectory
$script:PnPUtilPath = Join-Path $script:System32 "pnputil.exe"
$script:TaskKillPath = Join-Path $script:System32 "taskkill.exe"
$script:IcaclsPath = Join-Path $script:System32 "icacls.exe"
$script:TempDir = Join-Path ([IO.Path]::GetTempPath()) (
    "DS4Windows-VIIPER-Setup-" + [Guid]::NewGuid().ToString("N"))

function Write-SetupLog([string]$message, [ConsoleColor]$color =
        [ConsoleColor]::Gray) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host $message -ForegroundColor $color
    try {
        $logDirectory = Split-Path -Parent $script:LogPath
        if (-not [string]::IsNullOrWhiteSpace($logDirectory) -and
                -not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $logDirectory -Force |
                Out-Null
        }
        Add-Content -LiteralPath $script:LogPath -Value (
            "[$timestamp] [$($script:CorrelationId)] $message") -Encoding UTF8
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

function Format-NativeFailure([object[]]$output) {
    return (@($output) | ForEach-Object { [string]$_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "; "
}

function Clear-InfrastructureReadiness {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.CreateSubKey(
            "SOFTWARE\DS4Windows", $true)
        try {
            $key.DeleteValue("InfrastructureVersion", $false)
            $key.SetValue("InfrastructureState", "Installing",
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue("InfrastructureStateUtc",
                [DateTime]::UtcNow.ToString("O"),
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.Flush()
        }
        finally {
            if ($key) { $key.Dispose() }
        }
    }
    finally {
        $baseKey.Dispose()
    }
}

function Commit-InfrastructureReadiness {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.CreateSubKey(
            "SOFTWARE\DS4Windows", $true)
        try {
            # Publish Ready last. Readers can never observe Ready paired with
            # an old or missing version from this transaction.
            $key.SetValue("InfrastructureVersion",
                $script:InfrastructureVersion,
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue("InfrastructureStateUtc",
                [DateTime]::UtcNow.ToString("O"),
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue("InfrastructureState", "Ready",
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.Flush()

            $actualVersion = [string]$key.GetValue(
                "InfrastructureVersion", "")
            $actualState = [string]$key.GetValue(
                "InfrastructureState", "")
            if ($actualVersion -cne $script:InfrastructureVersion -or
                    $actualState -cne "Ready") {
                throw (
                    "Infrastructure readiness readback failed: expected " +
                    "$($script:InfrastructureVersion)/Ready, observed " +
                    "$actualVersion/$actualState."
                )
            }
        }
        finally {
            if ($key) { $key.Dispose() }
        }
    }
    finally {
        $baseKey.Dispose()
    }
    Write-SetupLog (
        "Committed verified infrastructure readiness: " +
        $script:InfrastructureVersion
    ) Green
}

function Set-InfrastructureState([string]$state) {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.CreateSubKey(
            "SOFTWARE\DS4Windows", $true)
        try {
            $key.SetValue("InfrastructureState", $state,
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue("InfrastructureStateUtc",
                [DateTime]::UtcNow.ToString("O"),
                [Microsoft.Win32.RegistryValueKind]::String)
            $key.Flush()
        }
        finally {
            if ($key) { $key.Dispose() }
        }
    }
    finally {
        $baseKey.Dispose()
    }
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
        $driverStoreOutput = @(& $script:PnPUtilPath /enum-drivers 2>&1)
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
    $managedViiperPath = Join-Path $script:InstallDir "viiper.exe"
    [void](Remove-ManagedStartupTask "RunVIIPER" $managedViiperPath `
        "server" $script:InstallDir)
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
        throw "Packaged usbip-win2 installer failed SHA256 verification. " +
            "Expected $expectedHash; received $actualHash."
    }

    Write-SetupLog "Verified usbip-win2 installer SHA256: $actualHash" Green
}

function Test-FileSha256([string]$path, [string]$expectedHash) {
    try {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $false
        }
        $actualHash = (Get-FileHash -LiteralPath $path `
            -Algorithm SHA256 -ErrorAction Stop).Hash
        return [string]::Equals($actualHash, $expectedHash,
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
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

function Read-PackagedSha256([string]$manifestPath,
        [string]$expectedFileName) {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The offline DS4Windows package is incomplete: missing " +
            "$(Split-Path -Leaf $manifestPath)."
    }

    $lines = @(Get-Content -LiteralPath $manifestPath -ErrorAction Stop |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -ne 1 -or
        $lines[0] -notmatch '^\s*([0-9A-Fa-f]{64})\s+\*?(.+?)\s*$') {
        throw "The packaged VIIPER SHA256 manifest is malformed."
    }

    $manifestFileName = [IO.Path]::GetFileName($Matches[2])
    if (-not [string]::Equals($manifestFileName, $expectedFileName,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The packaged VIIPER SHA256 manifest names '$manifestFileName' " +
            "instead of '$expectedFileName'."
    }

    return $Matches[1]
}

function Test-UsbipRuntime([string]$usbipPath) {
    if (-not (Test-Path -LiteralPath $usbipPath)) {
        $script:UsbipRuntimeProbeState = "missing"
        Write-SetupLog (
            "usbip-win2 runtime is missing at its canonical path: $usbipPath"
        ) Yellow
        return $false
    }

    if (-not (Test-FileSha256 $usbipPath `
            $script:UsbipExecutableSha256)) {
        $script:UsbipRuntimeProbeState = "executable-mismatch"
        Write-SetupLog (
            "usbip.exe does not match the verified executable from the " +
            "bundled 0.9.7.7 package. Repair is required."
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
        $abiMismatch = $probeText -match (
            "(?i)ABI\s+mismatch|unexpected\s+size|" +
            "specified\s+conversion\s+is\s+not\s+valid|" +
            "invalid\s+structure\s+size")

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

    $unverified = @($processes | Where-Object {
        -not (Test-RecognizedProductExecutable `
            ([string]$_.ExecutablePath) "DS4Windows")
    })
    if ($unverified.Count -gt 0) {
        $details = ($unverified | ForEach-Object {
            "PID=$($_.ProcessId) path=$($_.ExecutablePath)"
        }) -join "; "
        Write-SetupLog (
            "Refusing to terminate an unverified process named " +
            "DS4Windows.exe: $details. Close it manually before setup."
        ) Red
        return $false
    }

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
        $processes = @(Get-CimInstance Win32_Process `
            -Filter "Name='viiper.exe'" -ErrorAction SilentlyContinue)
        $unverified = @($processes | Where-Object {
            -not (Test-RecognizedProductExecutable `
                ([string]$_.ExecutablePath) "VIIPER")
        })
        if ($unverified.Count -gt 0) {
            $details = ($unverified | ForEach-Object {
                "PID=$($_.ProcessId) path=$($_.ExecutablePath)"
            }) -join "; "
            throw "A process named viiper.exe is not a verified VIIPER " +
                "package executable ($details). Close it manually before setup."
        }
        return $processes
    }
    catch {
        throw
    }
}

function Test-RecognizedProductExecutable([string]$path,
        [string]$expectedProduct) {
    if ([string]::IsNullOrWhiteSpace($path) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $false
    }
    try {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo(
            [IO.Path]::GetFullPath($path))
        if ([string]::Equals($expectedProduct, "DS4Windows",
                [StringComparison]::OrdinalIgnoreCase)) {
            return [string]::Equals($version.ProductName, "DS4Windows",
                       [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($version.FileDescription, "DS4Windows",
                    [StringComparison]::OrdinalIgnoreCase)
        }
        return [string]::Equals($version.ProductName, "VIIPER",
                   [StringComparison]::OrdinalIgnoreCase) -or
            ($version.FileDescription -and
             $version.FileDescription.StartsWith("VIIPER",
                 [StringComparison]::OrdinalIgnoreCase))
    }
    catch {
        return $false
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

function Get-PortableViiperDirectory {
    return [IO.Path]::GetFullPath(
        (Join-Path $TargetLocalAppData "VIIPER")).TrimEnd('\', '/')
}

function Test-KnownPortableViiperPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    try {
        $expected = Join-Path (Get-PortableViiperDirectory) "viiper.exe"
        return [string]::Equals([IO.Path]::GetFullPath($path), $expected,
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Remove-ForeignViiperInstallations {
    # There is one elevated backend owner: Program Files. An older
    # LocalAppData copy is foreign too, even when DS4Windows itself remains
    # portable.
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

    if (-not $script:InstallerMode) {
        # The built-in repair flow can ask directly. The all-in-one installer
        # already received one explicit confirmation and runs this child with
        # -NonInteractive, so it follows the standard-install choice instead.
        $answer = Read-Host (
            "Stop these foreign VIIPER processes with administrator rights " +
            "and remove their viiper.exe files? [Y/N]")
        if ($answer -notmatch '^(?i:y|yes)$') {
            $script:UserCanceled = $true
            throw "Setup canceled because a foreign VIIPER instance is " +
                "running. Close or remove it, then run Install / Repair again."
        }
    }
    else {
        Write-SetupLog (
            "The standard installer will replace the foreign VIIPER instance " +
            "with its verified Program Files copy."
        ) Yellow
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
                & $script:TaskKillPath /PID $process.ProcessId /T /F 2>&1 |
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
        if (-not (Test-KnownPortableViiperPath $path) -or
                -not (Test-RecognizedProductExecutable $path "VIIPER")) {
            Write-SetupLog (
                "Stopped but preserved an unmanaged executable outside the " +
                "installer-owned LocalAppData VIIPER path: $path"
            ) Yellow
            continue
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

function Remove-PortableViiperInstallationForStandardMode {
    $portableDirectory = Get-PortableViiperDirectory
    if (-not (Test-Path -LiteralPath $portableDirectory)) { return }

    $expectedParent = [IO.Path]::GetFullPath(
        $TargetLocalAppData).TrimEnd('\', '/')
    $actualParent = [IO.Path]::GetFullPath(
        (Split-Path -Parent $portableDirectory)).TrimEnd('\', '/')
    if (-not [string]::Equals($actualParent, $expectedParent,
            [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Split-Path -Leaf $portableDirectory),
                "VIIPER", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unexpected portable VIIPER path."
    }

    $portableDirectory = Assert-SafeManagedDirectory $portableDirectory `
        "portable VIIPER installation" -RequireExisting
    $installerOwnedFiles = @(
        "viiper.exe",
        "viiper.exe.previous",
        "install.log",
        "usbip-replacement-pending.json"
    )
    foreach ($fileName in $installerOwnedFiles) {
        $path = [IO.Path]::GetFullPath(
            (Join-Path $portableDirectory $fileName))
        $expectedParent = [IO.Path]::GetFullPath(
            (Split-Path -Parent $path)).TrimEnd('\', '/')
        if (-not [string]::Equals($expectedParent, $portableDirectory,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an installer-owned VIIPER file outside " +
                "the verified portable directory: $fileName"
        }
        if (-not (Test-Path -LiteralPath $path)) { continue }

        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.PSIsContainer -or (($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Refusing to remove an unexpected installer-owned VIIPER " +
                "path: $path"
        }
        Remove-Item -LiteralPath $path -Force -ErrorAction Stop
    }

    $portableViiperPath = Join-Path $portableDirectory "viiper.exe"
    if (Test-Path -LiteralPath $portableViiperPath) {
        throw "The old portable VIIPER executable could not be removed."
    }

    if (@(Get-ChildItem -LiteralPath $portableDirectory -Force `
                -ErrorAction Stop).Count -eq 0) {
        Remove-Item -LiteralPath $portableDirectory -Force -ErrorAction Stop
        Write-SetupLog (
            "Removed the empty old portable VIIPER folder after selecting " +
            "Standard: $portableDirectory"
        ) Green
    }
    else {
        Write-SetupLog (
            "Removed only installer-owned portable VIIPER files. Preserved " +
            "unrecognized files in: $portableDirectory"
        ) Yellow
    }
}

function Stop-InstallerHostForStandardMigration {
    if ($script:KeepDs4WindowsPortable -or $script:InstallerHostPid -le 0) {
        return
    }

    $sourceDirectory = [IO.Path]::GetFullPath(
        (Split-Path -Parent $TargetDs4WindowsPath)).TrimEnd('\', '/')
    $managedDirectory = [IO.Path]::GetFullPath(
        $script:Ds4WindowsInstallDir).TrimEnd('\', '/')
    if ([string]::Equals($sourceDirectory, $managedDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $hostProcess = Get-CimInstance Win32_Process -Filter (
        "ProcessId=$script:InstallerHostPid") -ErrorAction SilentlyContinue
    if (-not $hostProcess) { return }

    $expectedPath = [IO.Path]::GetFullPath($TargetDs4WindowsPath)
    $actualPath = if ($hostProcess.ExecutablePath) {
        [IO.Path]::GetFullPath([string]$hostProcess.ExecutablePath)
    }
    else { $null }
    if ($hostProcess.Name -ine "DS4Windows.exe" -or
            -not $actualPath -or
            -not [string]::Equals($actualPath, $expectedPath,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to terminate an unverified installer-host process."
    }

    Write-SetupLog (
        "Closing the old portable DS4Windows installer host before cleanup: " +
        "PID $script:InstallerHostPid"
    ) Yellow
    Stop-Process -Id $script:InstallerHostPid -Force -ErrorAction Stop
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (-not (Get-Process -Id $script:InstallerHostPid `
                -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "The old portable DS4Windows installer host did not exit."
}

function Remove-PortableDs4WindowsPackageForStandardMode {
    if ($script:KeepDs4WindowsPortable) { return }

    $portableDirectory = [IO.Path]::GetFullPath(
        (Split-Path -Parent $TargetDs4WindowsPath)).TrimEnd('\', '/')
    $managedDirectory = [IO.Path]::GetFullPath(
        $script:Ds4WindowsInstallDir).TrimEnd('\', '/')
    if ([string]::Equals($portableDirectory, $managedDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $portableDirectory = Assert-SafeManagedDirectory $portableDirectory `
        "portable DS4Windows package" -RequireExisting
    $manifestPath = Join-Path $portableDirectory `
        ".ds4windows-managed-files.txt"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The old portable DS4Windows package has no managed-file " +
            "manifest, so setup will not guess which files are safe to remove."
    }

    $portablePrefix = $portableDirectory.TrimEnd('\') + '\'
    $managedFiles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $managedDirectories = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-Content -LiteralPath $manifestPath) {
        $relative = ([string]$entry).Trim().Replace('/', '\')
        if (-not (Test-SafePackageRelativePath $relative) -or
                -not $managedFiles.Add($relative)) {
            throw "The old portable DS4Windows manifest contains an unsafe " +
                "or duplicate path: '$entry'."
        }
    }
    if (-not $managedFiles.Contains("DS4Windows.exe")) {
        throw "The old portable package manifest does not own DS4Windows.exe."
    }

    foreach ($relative in $managedFiles) {
        $path = [IO.Path]::GetFullPath(
            (Join-Path $portableDirectory $relative))
        if (-not $path.StartsWith($portablePrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a managed file outside the portable " +
                "DS4Windows folder: $relative"
        }

        $parentDirectory = [IO.Path]::GetFullPath(
            (Split-Path -Parent $path)).TrimEnd('\', '/')
        while (-not [string]::Equals($parentDirectory, $portableDirectory,
                [StringComparison]::OrdinalIgnoreCase)) {
            if (-not ($parentDirectory + '\').StartsWith($portablePrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean a package directory outside the " +
                    "portable DS4Windows folder: $parentDirectory"
            }
            [void]$managedDirectories.Add($parentDirectory)
            $parentDirectory = [IO.Path]::GetFullPath(
                (Split-Path -Parent $parentDirectory)).TrimEnd('\', '/')
        }

        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if (($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to remove a reparse-point package file: $path"
            }
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
    }
    Remove-Item -LiteralPath $manifestPath -Force -ErrorAction Stop

    if (Test-Path -LiteralPath (Join-Path $portableDirectory "DS4Windows.exe")) {
        throw "The old portable DS4Windows executable could not be removed."
    }

    $directories = @($managedDirectories | Sort-Object Length -Descending)
    foreach ($directory in $directories) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and
                @(Get-ChildItem -LiteralPath $directory -Force `
                    -ErrorAction Stop).Count -eq 0) {
            Remove-Item -LiteralPath $directory -Force -ErrorAction Stop
        }
    }

    if (@(Get-ChildItem -LiteralPath $portableDirectory -Force `
                -ErrorAction Stop).Count -eq 0) {
        Remove-Item -LiteralPath $portableDirectory -Force -ErrorAction Stop
        Write-SetupLog (
            "Removed the old portable DS4Windows folder: $portableDirectory"
        ) Green
    }
    else {
        Write-SetupLog (
            "Removed the old portable DS4Windows executable and package " +
            "files. Preserved non-package user files in: $portableDirectory"
        ) Yellow
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
                    & $script:TaskKillPath /PID $process.ProcessId /T /F |
                        Out-Null
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
        $total = 0
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($total -lt $buffer.Length -and
                $deadline.ElapsedMilliseconds -lt $timeoutMilliseconds) {
            $stream.ReadTimeout = [Math]::Max(1,
                $timeoutMilliseconds - [int]$deadline.ElapsedMilliseconds)
            $read = $stream.Read($buffer, $total, $buffer.Length - $total)
            if ($read -le 0) { break }
            $total += $read
            $response = [Text.Encoding]::UTF8.GetString($buffer, 0, $total)
            if ($response.IndexOf("VIIPER",
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
        return $false
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

function Start-AndVerifyViiperDirectly([string]$viiperPath) {
    if (Test-ViiperApi) { return $true }
    try {
        Start-Process -FilePath $viiperPath -ArgumentList "server" `
            -WorkingDirectory (Split-Path -Parent $viiperPath) `
            -WindowStyle Hidden -ErrorAction Stop | Out-Null
    }
    catch {
        Write-SetupLog (
            "Could not start VIIPER for this session: " +
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

function Assert-ManagedStartupTaskName([string]$taskName) {
    $allowed = [string]::Equals($taskName, "RunVIIPER",
            [StringComparison]::Ordinal) -or
        [string]::Equals($taskName, "RunDS4Windows",
            [StringComparison]::Ordinal)
    if (-not $allowed) {
        throw "Refusing an unmanaged root scheduled-task name: '$taskName'."
    }
}

function Get-RootScheduledTask([string]$taskName) {
    Assert-ManagedStartupTaskName $taskName

    # An exact Get-ScheduledTask query turns ordinary absence into a
    # CmdletizationQuery_NotFound error. Enumerate first so a task that has
    # not yet been registered is represented by $null, while real CIM/service
    # failures still terminate and reach the caller's containment logging.
    $matches = @(Get-ScheduledTask -ErrorAction Stop | Where-Object {
        [string]::Equals([string]$_.TaskPath, "\",
            [StringComparison]::Ordinal) -and
        [string]::Equals([string]$_.TaskName, $taskName,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -gt 1) {
        throw "Multiple root scheduled tasks matched '$taskName'."
    }

    return $matches | Select-Object -First 1
}

function Test-HighestLogonTaskDefinition($registered,
        [string]$executablePath, [string]$arguments,
        [string]$workingDirectory, [bool]$requireEnabled = $true) {
    try {
        if (-not $registered -or @($registered.Actions).Count -ne 1 -or
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
        $actualWorkingDirectory = if ([string]::IsNullOrWhiteSpace(
                $registeredAction.WorkingDirectory)) { "" } else {
            [IO.Path]::GetFullPath(
                [string]$registeredAction.WorkingDirectory).TrimEnd('\')
        }
        $principalSid = Convert-AccountToSid `
            ([string]$registered.Principal.UserId)
        $matchingTrigger = @($registered.Triggers | Where-Object {
            if ($_.CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger') {
                return $false
            }
            # A user-neutral logon trigger avoids Task Scheduler's ambiguous
            # UserId name resolution. The exact principal still restricts
            # execution to the intended user's interactive token.
            $triggerUser = [string]$_.UserId
            return [string]::IsNullOrWhiteSpace($triggerUser) -or
                (Convert-AccountToSid $triggerUser) -eq $script:TargetUserSid
        }).Count -gt 0

        return (-not $requireEnabled -or $registered.Settings.Enabled) -and
            [string]::Equals(
                [IO.Path]::GetFullPath([string]$registeredAction.Execute),
                [IO.Path]::GetFullPath($executablePath),
                [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($actualArguments, $expectedArguments,
                [StringComparison]::Ordinal) -and
            [string]::Equals($actualWorkingDirectory,
                $expectedWorkingDirectory,
                [StringComparison]::OrdinalIgnoreCase) -and
            $registered.Principal.RunLevel -eq 'Highest' -and
            $registered.Principal.LogonType -eq 'Interactive' -and
            $principalSid -eq $script:TargetUserSid -and $matchingTrigger
    }
    catch { return $false }
}

function Test-HighestLogonTask([string]$taskName,
        [string]$executablePath, [string]$arguments,
        [string]$workingDirectory, [bool]$requireEnabled = $true) {
    $registered = Get-RootScheduledTask $taskName
    return Test-HighestLogonTaskDefinition $registered $executablePath `
        $arguments $workingDirectory $requireEnabled
}

function Test-ManagedStartupTaskMarker($registered) {
    return $registered -and [string]::Equals(
        [string]$registered.Description,
        "DS4Windows managed startup task v1",
        [StringComparison]::Ordinal)
}

function Test-LegacyManagedStartupTask($registered, [string]$taskName) {
    Assert-ManagedStartupTaskName $taskName
    try {
        # Pre-marker DS4Windows releases did not set Description. Any other
        # description belongs to a different owner and is never migrated.
        if (-not $registered -or -not [string]::IsNullOrWhiteSpace(
                [string]$registered.Description) -or
                @($registered.Actions).Count -ne 1) {
            return $false
        }
        $action = $registered.Actions | Select-Object -First 1
        $executablePath = [IO.Path]::GetFullPath([string]$action.Execute)
        $workingDirectory = [IO.Path]::GetFullPath(
            [string]$action.WorkingDirectory).TrimEnd('\')
        $expectedWorkingDirectory = [IO.Path]::GetDirectoryName(
            $executablePath).TrimEnd('\')
        $isViiper = [string]::Equals($taskName, "RunVIIPER",
            [StringComparison]::Ordinal)
        $expectedFileName = if ($isViiper) { "viiper.exe" } else {
            "DS4Windows.exe"
        }
        $expectedArguments = if ($isViiper) { "server" } else { "-m" }
        $expectedProduct = if ($isViiper) { "VIIPER" } else { "DS4Windows" }

        if (-not [string]::Equals(
                [IO.Path]::GetFileName($executablePath), $expectedFileName,
                [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($workingDirectory,
                    $expectedWorkingDirectory,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-RecognizedProductExecutable $executablePath `
                    $expectedProduct) -or
                ($isViiper -and -not (Test-ManagedViiperPath `
                    $executablePath))) {
            return $false
        }

        # This validates one neutral logon trigger, one exact action, the
        # target SID, Interactive/Highest, and all action fields.
        return Test-HighestLogonTaskDefinition $registered $executablePath `
            $expectedArguments $expectedWorkingDirectory $false
    }
    catch { return $false }
}

function Test-ManagedStartupTaskOwnership($registered, [string]$taskName,
        [string]$legacyExecutablePath = $null,
        [string]$legacyArguments = $null,
        [string]$legacyWorkingDirectory = $null) {
    Assert-ManagedStartupTaskName $taskName
    if (Test-ManagedStartupTaskMarker $registered) { return $true }
    if (-not [string]::IsNullOrWhiteSpace(
            [string]$registered.Description) -or
            [string]::IsNullOrWhiteSpace($legacyExecutablePath)) {
        return $false
    }
    try {
        $isViiperRequest = [string]::Equals($taskName, "RunVIIPER",
            [StringComparison]::Ordinal)
        $expectedRequestFile = if ($isViiperRequest) { "viiper.exe" } else {
            "DS4Windows.exe"
        }
        $expectedRequestArguments = if ($isViiperRequest) { "server" } else {
            "-m"
        }
        $requestedWorkingDirectory = [IO.Path]::GetFullPath(
            $legacyWorkingDirectory).TrimEnd('\')
        $requestedExecutableDirectory = [IO.Path]::GetDirectoryName(
            [IO.Path]::GetFullPath($legacyExecutablePath)).TrimEnd('\')
        if (-not [string]::Equals(
                [IO.Path]::GetFileName($legacyExecutablePath),
                $expectedRequestFile,
                [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($legacyArguments,
                    $expectedRequestArguments,
                    [StringComparison]::Ordinal) -or
                -not [string]::Equals($requestedWorkingDirectory,
                    $requestedExecutableDirectory,
                    [StringComparison]::OrdinalIgnoreCase) -or
                ($isViiperRequest -and -not (Test-ManagedViiperPath `
                    $legacyExecutablePath))) {
            return $false
        }
    }
    catch { return $false }
    # Deliberate one-time migration for pre-marker releases: accept only the
    # tightly recognized previous contract. It may point at an older portable
    # DS4Windows copy, which preserves documented retargeting; VIIPER must
    # still point at the canonical managed executable.
    return Test-LegacyManagedStartupTask $registered $taskName
}

function Assert-StartupTaskMutationAllowed([string]$taskName,
        [string]$legacyExecutablePath = $null,
        [string]$legacyArguments = $null,
        [string]$legacyWorkingDirectory = $null) {
    $registered = Get-RootScheduledTask $taskName
    if ($registered -and -not (Test-ManagedStartupTaskOwnership `
            $registered $taskName $legacyExecutablePath $legacyArguments `
            $legacyWorkingDirectory)) {
        throw "Refusing to overwrite, disable, or remove foreign root task " +
            "'$taskName'. Rename or remove that task manually, then rerun " +
            "Install / Repair."
    }
    return $registered
}

function Remove-ManagedStartupTask([string]$taskName,
        [string]$legacyExecutablePath = $null,
        [string]$legacyArguments = $null,
        [string]$legacyWorkingDirectory = $null) {
    $registered = Assert-StartupTaskMutationAllowed $taskName `
        $legacyExecutablePath $legacyArguments $legacyWorkingDirectory
    if (-not $registered) { return $false }

    Unregister-ScheduledTask -TaskPath "\" -TaskName $taskName `
        -Confirm:$false -ErrorAction Stop
    if (Get-RootScheduledTask $taskName) {
        throw "Managed startup task '$taskName' remained after removal."
    }
    return $true
}

function Remove-ManagedStartupTaskPair([string]$viiperPath,
        [string]$ds4WindowsPath) {
    $viiperDirectory = Split-Path -Parent $viiperPath
    $ds4WindowsDirectory = Split-Path -Parent $ds4WindowsPath
    # Validate the entire ownership set before deleting either fixed name.
    # This prevents a foreign second-name collision from causing a partial
    # deletion of the first DS4Windows-owned task.
    [void](Assert-StartupTaskMutationAllowed "RunVIIPER" $viiperPath `
        "server" $viiperDirectory)
    [void](Assert-StartupTaskMutationAllowed "RunDS4Windows" `
        $ds4WindowsPath "-m" $ds4WindowsDirectory)
    [void](Remove-ManagedStartupTask "RunVIIPER" $viiperPath `
        "server" $viiperDirectory)
    [void](Remove-ManagedStartupTask "RunDS4Windows" $ds4WindowsPath `
        "-m" $ds4WindowsDirectory)
}

function Add-ScheduledTaskXmlElement([Xml.XmlDocument]$document,
        [Xml.XmlNode]$parent, [string]$name,
        [AllowNull()][string]$value = $null) {
    $element = $document.CreateElement($name, $parent.NamespaceURI)
    if ($null -ne $value) { $element.InnerText = $value }
    [void]$parent.AppendChild($element)
    return $element
}

function New-HighestLogonTaskXml([string]$executablePath,
        [string]$arguments, [string]$workingDirectory) {
    if ([string]::IsNullOrWhiteSpace($executablePath)) {
        throw "A scheduled-task executable path is required."
    }

    try {
        $principalSid = ([Security.Principal.SecurityIdentifier]::new(
            $script:TargetUserSid)).Value
    }
    catch {
        throw "The scheduled-task principal SID is invalid."
    }

    # Do not construct the principal through the ScheduledTasks CIM principal
    # object cmdlet.
    # The ScheduledTasks CIM provider can normalize an exact local-account SID
    # to an unqualified account name before registration. On affected Windows
    # account layouts that makes Task Scheduler reject an otherwise valid
    # InteractiveToken task with ERROR_INVALID_PARAMETER. XML registration is
    # part of the public Register-ScheduledTask contract and preserves the
    # already-validated SID byte-for-byte. The neutral logon trigger remains
    # intentional: the exact principal still limits execution to that user's
    # existing interactive token.
    $namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task"
    $document = [Xml.XmlDocument]::new()
    [void]$document.AppendChild(
        $document.CreateXmlDeclaration("1.0", "UTF-16", $null))

    $task = $document.CreateElement("Task", $namespace)
    [void]$task.SetAttribute("version", "1.2")
    [void]$document.AppendChild($task)

    $registrationInfo = Add-ScheduledTaskXmlElement $document $task `
        "RegistrationInfo"
    [void](Add-ScheduledTaskXmlElement $document $registrationInfo `
        "Author" "DS4Windows")
    [void](Add-ScheduledTaskXmlElement $document $registrationInfo `
        "Description" "DS4Windows managed startup task v1")

    $triggers = Add-ScheduledTaskXmlElement $document $task "Triggers"
    $logonTrigger = Add-ScheduledTaskXmlElement $document $triggers `
        "LogonTrigger"
    [void](Add-ScheduledTaskXmlElement $document $logonTrigger `
        "Enabled" "true")

    $principals = Add-ScheduledTaskXmlElement $document $task "Principals"
    $principal = Add-ScheduledTaskXmlElement $document $principals "Principal"
    [void]$principal.SetAttribute("id", "Author")
    [void](Add-ScheduledTaskXmlElement $document $principal `
        "UserId" $principalSid)
    [void](Add-ScheduledTaskXmlElement $document $principal `
        "LogonType" "InteractiveToken")
    [void](Add-ScheduledTaskXmlElement $document $principal `
        "RunLevel" "HighestAvailable")

    # Keep the Settings definition compact and deterministic; omitted settings
    # retain Task Scheduler defaults.
    $settings = Add-ScheduledTaskXmlElement $document $task "Settings"
    [void](Add-ScheduledTaskXmlElement $document $settings `
        "MultipleInstancesPolicy" "IgnoreNew")
    [void](Add-ScheduledTaskXmlElement $document $settings `
        "DisallowStartIfOnBatteries" "false")
    [void](Add-ScheduledTaskXmlElement $document $settings `
        "StopIfGoingOnBatteries" "false")
    [void](Add-ScheduledTaskXmlElement $document $settings `
        "AllowStartOnDemand" "true")
    [void](Add-ScheduledTaskXmlElement $document $settings "Enabled" "true")
    [void](Add-ScheduledTaskXmlElement $document $settings `
        "ExecutionTimeLimit" "PT0S")

    $actions = Add-ScheduledTaskXmlElement $document $task "Actions"
    [void]$actions.SetAttribute("Context", "Author")
    $exec = Add-ScheduledTaskXmlElement $document $actions "Exec"
    [void](Add-ScheduledTaskXmlElement $document $exec `
        "Command" $executablePath)
    if ($null -ne $arguments) {
        [void](Add-ScheduledTaskXmlElement $document $exec `
            "Arguments" $arguments)
    }
    if (-not [string]::IsNullOrWhiteSpace($workingDirectory)) {
        [void](Add-ScheduledTaskXmlElement $document $exec `
            "WorkingDirectory" $workingDirectory)
    }

    return $document.OuterXml
}

function Register-HighestLogonTask([string]$taskName,
        [string]$executablePath, [string]$arguments,
        [string]$workingDirectory) {
    Assert-ManagedStartupTaskName $taskName
    $registeredBefore = Assert-StartupTaskMutationAllowed $taskName `
        $executablePath $arguments $workingDirectory
    if ($registeredBefore -and
            (Test-ManagedStartupTaskMarker $registeredBefore) -and
            (Test-HighestLogonTaskDefinition $registeredBefore `
                $executablePath $arguments $workingDirectory $true)) {
        Write-SetupLog "Startup task '$taskName' is already exact." Green
        return $true
    }

    $replaceExisting = [bool]$registeredBefore
    for ($attempt = 1; $attempt -le 3; $attempt++) {
      $registrationStage = "build exact-SID task XML"
      try {
        $taskXml = New-HighestLogonTaskXml $executablePath $arguments `
            $workingDirectory

        $registrationStage = "register exact-SID task XML"
        $registerParameters = @{
            TaskPath = "\"
            TaskName = $taskName
            Xml = $taskXml
            ErrorAction = "Stop"
        }
        if ($replaceExisting) { $registerParameters.Force = $true }
        Register-ScheduledTask @registerParameters | Out-Null

        $registrationStage = "verify registered task"
        $registeredAfter = Get-RootScheduledTask $taskName
        if (-not (Test-ManagedStartupTaskMarker $registeredAfter) -or
                -not (Test-HighestLogonTaskDefinition $registeredAfter `
                    $executablePath $arguments $workingDirectory $true)) {
            throw "Task registration verification failed."
        }
        Write-SetupLog (
            "Verified startup task '$taskName' on registration attempt " +
            "$attempt."
        ) Green
        return $true
      }
      catch {
        $registrationFailure = $_
        Write-SetupLog (
            "Startup task '$taskName' registration attempt $attempt of 3 " +
            "failed during $registrationStage`: " +
            $registrationFailure.Exception.Message
        ) Yellow

        # Re-enumeration distinguishes a task this attempt created from a
        # foreign same-name collision. Never clean up an unowned task.
        $observed = Get-RootScheduledTask $taskName
        if ($observed -and -not (Test-ManagedStartupTaskOwnership `
                $observed $taskName $executablePath $arguments `
                $workingDirectory)) {
            throw "Startup task '$taskName' became a foreign same-name " +
                "collision during registration. It was left unchanged."
        }
        if ($observed -and (Test-ManagedStartupTaskMarker $observed) -and
                (Test-HighestLogonTaskDefinition $observed `
                    $executablePath $arguments $workingDirectory $true)) {
            Write-SetupLog (
                "Startup task '$taskName' verified after a transient " +
                "registration response."
            ) Green
            return $true
        }
        if (-not $registeredBefore -and $observed) {
            [void](Remove-ManagedStartupTask $taskName $executablePath `
                $arguments $workingDirectory)
            $observed = $null
        }
        $replaceExisting = [bool]$observed
        if ($attempt -lt 3) {
          Start-Sleep -Milliseconds (250 * $attempt)
        }
      }
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

function Register-ManagedStartupTaskPair([string]$viiperPath,
        [string]$ds4WindowsPath) {
    # Detect either fixed-name collision before registering the first task.
    # A foreign RunDS4Windows task must not cause a newly created RunVIIPER
    # task to appear and then require rollback.
    $viiperBefore = Assert-StartupTaskMutationAllowed "RunVIIPER" `
        $viiperPath "server" (Split-Path -Parent $viiperPath)
    [void](Assert-StartupTaskMutationAllowed "RunDS4Windows" `
        $ds4WindowsPath "-m" (Split-Path -Parent $ds4WindowsPath))

    if (-not (Register-ViiperRunTask $viiperPath "RunVIIPER")) {
        throw "Could not create the elevated RunVIIPER startup task."
    }
    try {
        if (-not (Register-Ds4WindowsRunTask $ds4WindowsPath)) {
            throw "Could not register the elevated RunDS4Windows startup task."
        }
    }
    catch {
        $registrationFailure = $_
        # Roll back only RunVIIPER created by this pair transaction. Preserve
        # an exact pre-existing owned task and let outer failure containment
        # disable it if required.
        if (-not $viiperBefore) {
            [void](Remove-ManagedStartupTask "RunVIIPER" $viiperPath `
                "server" (Split-Path -Parent $viiperPath))
        }
        throw $registrationFailure
    }

    return $true
}

function Suspend-StartupTasksUntilInfrastructureReady(
        [string]$viiperPath, [string]$ds4WindowsPath) {
    $contracts = @(
        @("RunVIIPER", $viiperPath, "server",
            (Split-Path -Parent $viiperPath)),
        @("RunDS4Windows", $ds4WindowsPath, "-m",
            (Split-Path -Parent $ds4WindowsPath))
    )

    # Validate the complete ownership set before mutating either task. This
    # avoids a half-suspended pair if one task was replaced concurrently.
    foreach ($contract in $contracts) {
        $taskName = [string]$contract[0]
        if (-not (Test-HighestLogonTask $taskName `
                ([string]$contract[1]) ([string]$contract[2]) `
                ([string]$contract[3]))) {
            throw "Refusing to suspend an unverified startup task: $taskName"
        }
    }

    foreach ($contract in $contracts) {
        $taskName = [string]$contract[0]
        Disable-ScheduledTask -TaskPath "\" -TaskName $taskName `
            -ErrorAction Stop | Out-Null
    }

    foreach ($contract in $contracts) {
        $taskName = [string]$contract[0]
        $disabled = Get-RootScheduledTask $taskName
        if (-not $disabled -or $disabled.Settings.Enabled -or
                -not (Test-HighestLogonTask $taskName `
                    ([string]$contract[1]) ([string]$contract[2]) `
                    ([string]$contract[3]) $false)) {
            throw "Startup task '$taskName' did not enter the verified disabled state."
        }
    }

    Write-SetupLog (
        "Verified startup tasks are registered but disabled until the " +
        "pinned USB-IP package and runtime ABI pass after reboot."
    ) Green
}

function Set-InfrastructureStartupFailClosed(
        [string]$viiperPath, [string]$ds4WindowsPath) {
    $contracts = @(
        @("RunVIIPER", $viiperPath, "server",
            (Split-Path -Parent $viiperPath)),
        @("RunDS4Windows", $ds4WindowsPath, "-m",
            (Split-Path -Parent $ds4WindowsPath))
    )

    foreach ($contract in $contracts) {
        $taskName = [string]$contract[0]
        try {
            $registered = Get-RootScheduledTask $taskName
            if (-not $registered) { continue }
            $owned = Test-ManagedStartupTaskOwnership $registered `
                $taskName ([string]$contract[1]) `
                ([string]$contract[2]) ([string]$contract[3])
            if (-not $owned) {
                Write-SetupLog (
                    "Failure containment preserved foreign same-name task " +
                    "'$taskName'."
                ) Red
                continue
            }

            # The durable marker is sufficient ownership for containment even
            # if a failed -Force update left an action that no longer passes
            # the full expected contract.
            Disable-ScheduledTask -TaskPath "\" -TaskName $taskName `
                -ErrorAction Stop | Out-Null
            $observed = Get-RootScheduledTask $taskName
            if ($observed) {
                $stillOwned = Test-ManagedStartupTaskOwnership $observed `
                    $taskName ([string]$contract[1]) `
                    ([string]$contract[2]) ([string]$contract[3])
                if (-not $stillOwned) {
                    throw "task identity changed during containment"
                }
                if ($observed.Settings.Enabled) {
                    throw "task remained enabled"
                }
            }
            Write-SetupLog (
                "Failure containment verified '$taskName' is disabled or absent."
            ) Yellow
        }
        catch {
            Write-SetupLog (
                "Could not verify failure containment for '$taskName': " +
                $_.Exception.Message
            ) Red
        }
    }
}

function Test-SafePackageRelativePath([string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative)) {
        return $false
    }

    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $components = $relative.Split([char[]]@('\', '/'),
        [StringSplitOptions]::None)
    foreach ($component in $components) {
        if ([string]::IsNullOrWhiteSpace($component) -or
                $component -in @('.', '..') -or
                $component.IndexOfAny($invalid) -ge 0 -or
                $component.EndsWith(' ') -or $component.EndsWith('.')) {
            return $false
        }

        $stem = ($component.Split('.')[0]).ToUpperInvariant()
        if ($stem -in @('CON', 'PRN', 'AUX', 'NUL') -or
                $stem -match '^(?:COM|LPT)[1-9]$') {
            return $false
        }
    }
    return $true
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
        if (-not (Test-SafePackageRelativePath $relative)) {
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
                if (-not (Test-SafePackageRelativePath $oldRelative) -or
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
            # An in-app repair is hosted by the installed DS4Windows process.
            # Its executable and managed DLLs are therefore legitimately open
            # while this verified snapshot is promoted. Never overwrite a
            # byte-identical destination: doing so is unnecessary and fails
            # on Windows for loaded assemblies. Changed package files are
            # still replaced normally after all other DS4Windows processes
            # have been quiesced.
            if (Test-Path -LiteralPath $file.Destination -PathType Leaf) {
                $sourceInfo = Get-Item -LiteralPath $file.Source -Force
                $destinationInfo = Get-Item -LiteralPath $file.Destination `
                    -Force
                if ($sourceInfo.Length -eq $destinationInfo.Length) {
                    $sourceHash = (Get-FileHash -LiteralPath $file.Source `
                        -Algorithm SHA256).Hash
                    $destinationHash = (Get-FileHash `
                        -LiteralPath $file.Destination `
                        -Algorithm SHA256).Hash
                    if ([string]::Equals($sourceHash, $destinationHash,
                            [StringComparison]::OrdinalIgnoreCase)) {
                        continue
                    }
                }
            }
            Copy-Item -LiteralPath $file.Source `
                -Destination $file.Destination -Force
        }
        $destinationManifest = Join-Path $destination $manifestName
        $copyManifest = $true
        if (Test-Path -LiteralPath $destinationManifest -PathType Leaf) {
            $sourceManifestHash = (Get-FileHash -LiteralPath $sourceManifest `
                -Algorithm SHA256).Hash
            $destinationManifestHash = (Get-FileHash `
                -LiteralPath $destinationManifest -Algorithm SHA256).Hash
            $copyManifest = -not [string]::Equals($sourceManifestHash,
                $destinationManifestHash,
                [StringComparison]::OrdinalIgnoreCase)
        }
        if ($copyManifest) {
            Copy-Item -LiteralPath $sourceManifest `
                -Destination $destinationManifest -Force
        }
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
    $aclOutput = @(& $script:IcaclsPath $resolved /reset /Q 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not reset access controls for the $label directory: " +
            (Format-NativeFailure $aclOutput)
    }
    $aclOutput = @(& $script:IcaclsPath $resolved /inheritance:r /grant:r `
        "${system}:(OI)(CI)(F)" `
        "${administrators}:(OI)(CI)(F)" `
        "${targetUser}:(OI)(CI)(RX)" /Q 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not protect the elevated $label startup target: " +
            (Format-NativeFailure $aclOutput)
    }
    $aclOutput = @(& $script:IcaclsPath $resolved /setowner $administrators /Q 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not assign protected ownership for ${label}: " +
            (Format-NativeFailure $aclOutput)
    }

    Write-SetupLog (
        "Protected $label package files from unelevated replacement: " +
        $resolved
    ) Green
}

function Protect-ElevatedTaskTargetFile([string]$filePath, [string]$label) {
    $resolved = [IO.Path]::GetFullPath($filePath)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$label startup target is missing: $resolved"
    }
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$label startup target is a reparse point: $resolved"
    }

    $administrators = '*S-1-5-32-544'
    $system = '*S-1-5-18'
    $targetUser = "*$script:TargetUserSid"
    $aclOutput = @(& $script:IcaclsPath $resolved /inheritance:r /grant:r `
        "${system}:(F)" "${administrators}:(F)" "${targetUser}:(RX)" `
        /Q 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not protect the elevated $label startup executable: " +
            (Format-NativeFailure $aclOutput)
    }
    $aclOutput = @(& $script:IcaclsPath $resolved /setowner $administrators /Q 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not assign protected ownership for the $label startup " +
            "executable: " + (Format-NativeFailure $aclOutput)
    }
    Write-SetupLog "Protected elevated $label startup executable: $resolved" Green
}

try {
    $script:InstallerLogRoot = Assert-SafeManagedDirectory `
        $script:InstallerLogRoot "installer log directory"
    New-Item -ItemType Directory -Path $script:InstallerLogRoot -Force |
        Out-Null
    $script:InstallerLogRoot = Assert-SafeManagedDirectory `
        $script:InstallerLogRoot "installer log directory" -RequireExisting
    if (-not (Test-Administrator)) {
        throw "Administrator permission is required. Launch setup from DS4Windows so Windows can request it automatically."
    }
    $elevatedIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not [string]::Equals($elevatedIdentity.User.Value,
            $script:TargetUserSid,
            [StringComparison]::OrdinalIgnoreCase)) {
        $script:RunAtStartupEnabled = $false
        Write-SetupLog (
            "Setup was elevated with alternate administrator credentials. " +
            "Installation will continue safely, but startup tasks for " +
            "$script:TargetUserName will be deferred until that user launches " +
            "DS4Windows and grants elevation."
        ) Yellow
    }

    if (-not $SetupMutexAlreadyHeld) {
        try {
            $script:SetupMutex = [Threading.Mutex]::new(
                $false, "Global\DS4Windows-VIIPER-Setup")
            if (-not $script:SetupMutex.WaitOne(0)) {
                throw "Another DS4Windows VIIPER setup is already running."
            }
            $script:SetupMutexOwned = $true
        }
        catch [Threading.AbandonedMutexException] {
            # An abandoned mutex is acquired by this thread.
            $script:SetupMutexOwned = $true
        }
    }
    else {
        Write-SetupLog (
            "The all-in-one installer owns the cross-session setup mutex " +
            "through child verification and reboot-resume staging."
        ) Green
    }

    Write-Host ""
    Write-Host "DS4Windows VIIPER virtual controller setup" `
        -ForegroundColor Green
    $installationMode = if ($script:KeepDs4WindowsPortable) {
        "Portable app: keep DS4Windows in place; install VIIPER safely in Program Files."
    }
    else {
        "Standard: install DS4Windows and VIIPER in Program Files."
    }
    Write-Host $installationMode -ForegroundColor Cyan
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

    # Readiness is a transaction commit marker. Start the transaction only
    # after confirmation and while holding the cross-session mutex. It remains
    # absent through mutation, failures, and reboot boundaries, and is
    # republished only after this mutex owner verifies the complete runtime.
    Clear-InfrastructureReadiness
    $script:SetupTransactionStarted = $true

    $script:InstallDir = Assert-SafeManagedDirectory $script:InstallDir `
        "VIIPER installation"
    if (-not $script:KeepDs4WindowsPortable) {
        $script:Ds4WindowsInstallDir = Assert-SafeManagedDirectory `
            $script:Ds4WindowsInstallDir "managed DS4Windows installation"
        New-Item -ItemType Directory -Path $script:Ds4WindowsInstallDir `
            -Force | Out-Null
        Protect-ElevatedTaskTargetDirectory $script:Ds4WindowsInstallDir `
            "DS4Windows"
    }
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

    Write-Step "Step 1 of 4 - Installing VIIPER 0.1.2"
    Remove-ForeignViiperInstallations
    $viiperPath = Join-Path $script:InstallDir "viiper.exe"
    $candidatePath = Join-Path $script:TempDir "viiper.exe"
    if (-not (Test-Path -LiteralPath $script:BundledViiperPath `
            -PathType Leaf)) {
        throw "The offline DS4Windows package is incomplete: missing " +
            "$(Split-Path -Leaf $script:BundledViiperPath)."
    }
    $bundledViiperSha256 = Read-PackagedSha256 `
        $script:BundledViiperSha256Path `
        (Split-Path -Leaf $script:BundledViiperPath)
    Write-SetupLog "Using packaged VIIPER 0.1.2 x64 binary." Green
    Assert-ViiperFileSha256 $script:BundledViiperPath $bundledViiperSha256
    Copy-Item -LiteralPath $script:BundledViiperPath `
        -Destination $candidatePath -Force
    Assert-ViiperFileSha256 $candidatePath $bundledViiperSha256
    if (-not (Stop-Ds4WindowsProcesses "VIIPER backend replacement")) {
        throw "Unable to quiesce DS4Windows before replacing VIIPER."
    }
    if (-not (Stop-ViiperProcesses "VIIPER backend replacement")) {
        throw "Unable to stop the existing VIIPER backend before replacement."
    }
    Disable-ViiperStartup
    Remove-PortableViiperInstallationForStandardMode
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
    # A Burn/MSI install has already atomically placed and verified the managed
    # application payload. The legacy in-app installer still promotes its
    # protected package snapshot itself, so retain that verification there.
    if ($script:InstallerMode) {
        $expectedManagedPath = Join-Path $script:Ds4WindowsInstallDir `
            "DS4Windows.exe"
        $resolvedRestartPath = [IO.Path]::GetFullPath(
            $script:Ds4WindowsRestartPath)
        $resolvedManagedPath = [IO.Path]::GetFullPath($expectedManagedPath)
        if (-not [string]::Equals($resolvedRestartPath,
                $resolvedManagedPath,
                [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $resolvedManagedPath `
                    -PathType Leaf)) {
            throw "The Windows Installer managed DS4Windows payload is " +
                "missing or outside the protected Program Files location."
        }
        $script:Ds4WindowsRestartPath = $resolvedManagedPath
        Write-SetupLog (
            "Windows Installer managed DS4Windows copy and startup target: " +
            $script:Ds4WindowsRestartPath
        ) Green
    }
    else {
        # Verify that the executable which launched setup belongs to this
        # package before copying the protected snapshot into Program Files.
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
            throw "The currently running DS4Windows executable changed " +
                "while setup was starting. Close it, extract a complete " +
                "release ZIP, and run Install / Repair again."
        }

    if ($script:KeepDs4WindowsPortable) {
        # Keep the exact package executable that initiated setup. No
        # DS4Windows files are copied into Program Files in portable mode.
        Write-SetupLog (
            "Portable DS4Windows retained; elevated startup target: " +
            $script:Ds4WindowsRestartPath
        ) Yellow
    }
    else {
        $managedDs4WindowsPath = Install-Ds4WindowsPackage `
            $sourceDs4WindowsDirectory $script:Ds4WindowsInstallDir
        # Standard Install / Repair promotes the verified managed copy.
        $script:Ds4WindowsRestartPath = $managedDs4WindowsPath
        Write-SetupLog (
            "Managed DS4Windows copy and elevated startup target: " +
            $script:Ds4WindowsRestartPath
        ) Green
    }
    }

    # VIIPER is always an elevated Program Files component. Lock its task
    # target regardless of where the unprivileged DS4Windows UI lives.
    Protect-ElevatedTaskTargetFile $viiperPath "VIIPER"
    if (-not $script:KeepDs4WindowsPortable) {
        # Program Files inheritance protects the managed app package tree.
        Protect-ElevatedTaskTargetFile $script:Ds4WindowsRestartPath `
            "DS4Windows"
    }

    # Preserve the setting that launched the built-in installer. The standard
    # Burn installer retains its existing startup-enabled default because it
    # does not pass -SkipStartupTasks.
    if ($script:RunAtStartupEnabled) {
        [void](Register-ManagedStartupTaskPair $viiperPath `
            $script:Ds4WindowsRestartPath)
        Write-SetupLog (
            "Registered and verified enabled elevated RunVIIPER and " +
            "RunDS4Windows tasks for $script:TargetUserName before driver setup."
        ) Green
    }
    else {
        Remove-ManagedStartupTaskPair $viiperPath `
            $script:Ds4WindowsRestartPath
        Write-SetupLog (
            "Run at Startup is disabled; no DS4Windows or VIIPER logon " +
            "task was retained."
        ) Green
    }

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
    $usbipExecutableSafe = $canonicalUsbipPresent -and
        (Test-FileSha256 $script:CanonicalUsbipPath `
            $script:UsbipExecutableSha256)
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
    $usbipPackageReady = $usbipVersionReady -and
        $usbipExecutableSafe -and $usbipDriverFilesSafe
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
        elseif (-not $usbipExecutableSafe) {
            "installed with an unverified usbip.exe"
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
                -not $usbipExecutableSafe -or
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
                $requiredUsbipVersion -and
                (-not $usbipExecutableSafe -or
                 -not $usbipDriverFilesSafe))

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
            if (-not (Test-Path -LiteralPath `
                    $script:BundledUsbipInstallerPath -PathType Leaf)) {
                $missingUsbipName = Split-Path -Leaf `
                    $script:BundledUsbipInstallerPath
                throw "The offline DS4Windows package is incomplete: " +
                    "missing $missingUsbipName."
            }
            Write-SetupLog (
                "Using the bundled usbip-win2 0.9.7.7 installer."
            ) Green
            Assert-FileSha256 $script:BundledUsbipInstallerPath `
                $script:UsbipInstallerSha256
            Copy-Item -LiteralPath $script:BundledUsbipInstallerPath `
                -Destination $usbipInstaller -Force
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
            $usbipExecutableSafe = $canonicalUsbipPresent -and
                (Test-FileSha256 $script:CanonicalUsbipPath `
                    $script:UsbipExecutableSha256)
            $usbipDriverIntegrity = Get-UsbipDriverIntegrity
            $usbipDriverFilesSafe = [bool]$usbipDriverIntegrity.Safe
            $usbipVersionReady = $canonicalUsbipPresent -and $usbipVersion -and
                $usbipVersion -eq $requiredUsbipVersion
            $usbipPackageReady = $usbipVersionReady -and
                $usbipExecutableSafe -and $usbipDriverFilesSafe
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
        [void](Stop-ViiperProcesses "pending usbip-win2 replacement reboot")
        if ($script:RunAtStartupEnabled) {
            Suspend-StartupTasksUntilInfrastructureReady $viiperPath `
                $script:Ds4WindowsRestartPath
        }
        $script:ExitCode = 3010
        Write-Host ""
        Write-SetupLog (
            "VIIPER is installed and the old USBIP package removal was " +
            "started. Packaged 0.9.7.7 was not installed. Restart Windows, " +
            "then run Install / Repair again to complete phase 2."
        ) Yellow
    }
    else {
    Write-Step "Step 3 of 4 - Verifying launch policy"
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended) {
        if (-not (Stop-ViiperProcesses "install registration")) {
            throw "VIIPER registration could not proceed because a VIIPER process could not be closed automatically. Please close viiper.exe manually, then run Install / Repair again."
        }

        if ($script:RunAtStartupEnabled) {
            if (-not (Test-HighestLogonTask "RunVIIPER" $viiperPath `
                        "server" (Split-Path -Parent $viiperPath)) -or
                    -not (Test-HighestLogonTask "RunDS4Windows" `
                        $script:Ds4WindowsRestartPath "-m" `
                        (Split-Path -Parent $script:Ds4WindowsRestartPath))) {
                throw "A verified startup task changed during setup."
            }
            Write-SetupLog "Both elevated startup tasks remain verified." Green
        }
        else {
            $unexpectedTasks = @(
                Get-RootScheduledTask "RunVIIPER"
                Get-RootScheduledTask "RunDS4Windows"
            ) | Where-Object { $null -ne $_ }
            if ($unexpectedTasks.Count -gt 0) {
                throw "A startup task was recreated while Run at Startup is disabled."
            }
            Write-SetupLog (
                "Run at Startup remains disabled; one-time launch only."
            ) Green
        }
    }
    else {
        [void](Stop-ViiperProcesses "pending usbip-win2 reboot")
        if ($script:RunAtStartupEnabled) {
            Suspend-StartupTasksUntilInfrastructureReady $viiperPath `
                $script:Ds4WindowsRestartPath
            Write-SetupLog (
                "Both startup tasks are preserved but disabled across the " +
                "reboot boundary. Repair will re-enable them only after " +
                "usbip-win2 passes its runtime ABI check."
            ) Yellow
        }
        else {
            Write-SetupLog (
                "Run at Startup remains disabled. Restart Windows, then " +
                "launch DS4Windows manually to finish readiness checks."
            ) Yellow
        }
    }

    Write-Step "Step 4 of 4 - Verifying runtime readiness"
    $viiperStarted = $false
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended) {
        $viiperStarted = if ($script:RunAtStartupEnabled) {
            $startedFromTask = Start-AndVerifyViiper "RunVIIPER"
            if (-not $startedFromTask) {
                Write-SetupLog (
                    "The verified startup task did not start VIIPER in this " +
                    "session; retrying the same packaged executable directly."
                ) Yellow
                Start-AndVerifyViiperDirectly $viiperPath
            }
            else {
                $true
            }
        }
        else {
            Start-AndVerifyViiperDirectly $viiperPath
        }
    }
    if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended -and
            $viiperStarted) {
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
        Commit-InfrastructureReadiness
        Write-SetupLog $finish Green
        if ($script:Ds4WindowsRestartPath -and -not $script:InstallerMode) {
            Write-SetupLog "SUCCESSFUL: restarting DS4Windows in 2 seconds." Green
            Start-Sleep -Seconds 2
            if (-not $script:KeepDs4WindowsPortable) {
                Stop-InstallerHostForStandardMigration
                Remove-PortableDs4WindowsPackageForStandardMode
            }
            if ($script:RunAtStartupEnabled) {
                Start-ScheduledTask -TaskPath "\" `
                    -TaskName "RunDS4Windows" -ErrorAction Stop
            }
            else {
                Start-Process -FilePath $script:Ds4WindowsRestartPath `
                    -ArgumentList "-m" `
                    -WorkingDirectory (Split-Path -Parent `
                        $script:Ds4WindowsRestartPath) `
                    -WindowStyle Hidden -ErrorAction Stop | Out-Null
            }
        }
    }
    else {
        if ($script:SetupTransactionStarted) {
            Set-InfrastructureState "RebootPending"
        }
        Write-SetupLog $finish Yellow
        if ($script:ExitCode -eq 0) {
            $script:ExitCode = 3010
        }
    }
    }
}
catch {
    $setupFailure = $_
    Write-Host ""
    if ($script:SetupTransactionStarted) {
        # A failed transaction must never leave an auto-start path capable of
        # launching VIIPER against an unverified or half-replaced USB/IP ABI.
        # Only tasks whose complete action/principal contract still belongs
        # to this package are touched.
        try {
            [void](Stop-ViiperProcesses "failed infrastructure transaction")
        }
        catch {
            Write-SetupLog (
                "Failure containment could not stop VIIPER: " +
                $_.Exception.Message
            ) Red
        }
        if ($script:RunAtStartupEnabled -and $script:InstallDir -and
                $script:Ds4WindowsRestartPath) {
            $failureViiperPath = Join-Path $script:InstallDir "viiper.exe"
            Set-InfrastructureStartupFailClosed $failureViiperPath `
                $script:Ds4WindowsRestartPath
        }
    }
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
        if ($script:SetupTransactionStarted) {
            try { Set-InfrastructureState "RebootPending" } catch { }
        }
        Write-SetupLog $setupFailure.Exception.Message Yellow
        Write-SetupLog "Restart required; no install changes were made." Yellow
        Write-SetupLog "Details were saved to $script:LogPath" Yellow
    }
    else {
        $script:ExitCode = 1
        if ($script:SetupTransactionStarted) {
            try { Set-InfrastructureState "Failed" } catch { }
        }
        Write-SetupLog (
            "Setup could not finish: " + $setupFailure.Exception.Message
        ) Red
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
    if ($script:SetupMutexOwned -and $script:SetupMutex) {
        try { $script:SetupMutex.ReleaseMutex() } catch { }
    }
    if ($script:SetupMutex) {
        try { $script:SetupMutex.Dispose() } catch { }
    }
}

exit $script:ExitCode
