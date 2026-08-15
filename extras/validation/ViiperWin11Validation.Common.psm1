Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ViiperSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function Resolve-ViiperRegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "$Label is not a non-empty regular file: '$Path'."
    }
    return $item.FullName
}

function Resolve-ViiperSafeDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label is not a non-reparse directory: '$Path'."
    }
    return $item.FullName
}

function Write-ViiperJsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [void][IO.Directory]::CreateDirectory($parent)
    }
    $temporary = $fullPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText(
            $temporary, ($Value | ConvertTo-Json -Depth 20),
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            [IO.File]::Replace($temporary, $fullPath, $null, $true)
        }
        else {
            [IO.File]::Move($temporary, $fullPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Test-ViiperGitIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedRevision,
        [Parameter(Mandatory = $true)][string]$GitExecutable,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $repository = Resolve-ViiperSafeDirectory -Path $RepositoryRoot -Label $Label
    $git = Resolve-ViiperRegularFile -Path $GitExecutable -Label 'Git executable'
    $headOutput = @(& $git -C $repository rev-parse --verify HEAD 2>&1)
    if ($LASTEXITCODE -ne 0 -or $headOutput.Count -eq 0) {
        throw "$Label is not an exact Git checkout.`n$($headOutput -join [Environment]::NewLine)"
    }
    $head = ([string]$headOutput[0]).Trim().ToLowerInvariant()
    if ($head -cne $ExpectedRevision.ToLowerInvariant()) {
        throw "$Label is revision '$head', not '$ExpectedRevision'."
    }
    $status = @(& $git -C $repository status --porcelain=v1 --untracked-files=all 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect $Label.`n$($status -join [Environment]::NewLine)"
    }
    if ($status.Count -ne 0) {
        throw "$Label is dirty; refusing unbound source:`n$($status -join [Environment]::NewLine)"
    }
    $submodules = @(& $git -C $repository submodule status --recursive 2>&1)
    if ($LASTEXITCODE -ne 0 -or
        @($submodules | Where-Object { $_ -match '^[\-+U]' }).Count -ne 0) {
        throw "$Label has an unbound submodule state.`n$($submodules -join [Environment]::NewLine)"
    }
    return [ordered]@{
        root = $repository
        revision = $head
        submodules = @($submodules | ForEach-Object { ([string]$_).Trim() })
    }
}

function Test-ViiperLocalTestPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceRevision,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageLockSHA256
    )

    $root = Resolve-ViiperSafeDirectory -Path $PackageRoot -Label 'Local-test package root'
    $unsafeDirectories = @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($unsafeDirectories.Count -ne 0) {
        throw "The local-test package contains reparse directory '$($unsafeDirectories[0].FullName)'."
    }
    $lockPath = Resolve-ViiperRegularFile -Path (Join-Path $root 'local-test-package.lock.json') `
        -Label 'Local-test package lock'
    $actualLockHash = Get-ViiperSha256 -Path $lockPath
    if ($actualLockHash -cne $ExpectedPackageLockSHA256.ToLowerInvariant()) {
        throw "The local-test package lock SHA-256 is '$actualLockHash', not the explicit expected digest."
    }
    $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ([int]$lock.schema -ne 1 -or
        [string]$lock.sourceRevision -cne $ExpectedSourceRevision.ToLowerInvariant() -or
        [string]$lock.driverBuildIdentity -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The local-test package lock has the wrong schema, source revision, or build identity.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($lock.files)) {
        $relative = [string]$entry.path
        if ($relative -cnotmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or
            -not $seen.Add($relative) -or [long]$entry.length -le 0 -or
            [string]$entry.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw "The local-test package lock has an unsafe or duplicate entry '$relative'."
        }
        $path = Join-Path $root $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $item.Length -ne [long]$entry.length -or
            (Get-ViiperSha256 -Path $item.FullName) -cne [string]$entry.sha256) {
            throw "The local-test package entry '$relative' does not match its lock."
        }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Where-Object {
        $_.FullName -cne $lockPath
    })
    if ($actualFiles.Count -ne $seen.Count) {
        throw "The local-test package has $($actualFiles.Count) payload files but the lock binds $($seen.Count)."
    }
    foreach ($file in $actualFiles) {
        $relative = $file.FullName.Substring($root.TrimEnd('\').Length + 1).Replace('\', '/')
        if (-not $seen.Contains($relative)) {
            throw "The local-test package contains unbound file '$relative'."
        }
    }
    return [ordered]@{
        root = $root
        lockPath = $lockPath
        lockSha256 = $actualLockHash
        sourceRevision = [string]$lock.sourceRevision
        driverPackageVersion = [string]$lock.driverPackageVersion
        driverBuildIdentity = [string]$lock.driverBuildIdentity
        fileCount = $seen.Count
    }
}

function Get-ViiperBootIdentity {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $rawBoot = $os.LastBootUpTime
    if ($rawBoot -is [DateTime]) {
        $boot = [DateTime]$rawBoot
    }
    else {
        $boot = [Management.ManagementDateTimeConverter]::ToDateTime([string]$rawBoot)
    }
    return $boot.ToUniversalTime().ToString('o')
}

function Invoke-ViiperReadOnlyCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    catch {
        $lines = @($_.Exception.Message)
        $exitCode = -1
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    return [ordered]@{
        exitCode = [int]$exitCode
        output = @($lines | ForEach-Object { [string]$_ })
    }
}

function Resolve-ViiperServiceImage {
    param([string]$ImagePath)

    if ([string]::IsNullOrWhiteSpace($ImagePath)) { return $null }
    $expanded = [Environment]::ExpandEnvironmentVariables($ImagePath.Trim())
    if ($expanded.StartsWith('"')) {
        $match = [regex]::Match($expanded, '^"(?<path>[^"]+)"')
    }
    else {
        $match = [regex]::Match($expanded, '^(?<path>\S+)')
    }
    if (-not $match.Success) { return $null }
    $path = $match.Groups['path'].Value
    if ($path.StartsWith('\??\', [StringComparison]::Ordinal)) {
        $path = $path.Substring(4)
    }
    if ($path.StartsWith('\SystemRoot\', [StringComparison]::OrdinalIgnoreCase)) {
        $path = Join-Path $env:SystemRoot $path.Substring('\SystemRoot\'.Length)
    }
    elseif ($path.StartsWith('System32\', [StringComparison]::OrdinalIgnoreCase)) {
        $path = Join-Path $env:SystemRoot $path
    }
    if (-not [IO.Path]::IsPathRooted($path) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Resolve-Path -LiteralPath $path -ErrorAction Stop).Path
}

function Get-ViiperFileProvenance {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
    return [ordered]@{
        path = $item.FullName
        length = [long]$item.Length
        sha256 = Get-ViiperSha256 -Path $item.FullName
        signatureStatus = [string]$signature.Status
        signerSubject = if ($null -ne $signature.SignerCertificate) {
            [string]$signature.SignerCertificate.Subject
        } else { $null }
        fileVersion = [string]$item.VersionInfo.FileVersion
        productVersion = [string]$item.VersionInfo.ProductVersion
    }
}

function Get-ViiperMachineEvidenceSnapshot {
    $errors = [Collections.Generic.List[object]]::new()
    function Capture-Section {
        param([string]$Name, [scriptblock]$Action)
        try { return & $Action }
        catch {
            $errors.Add([ordered]@{ section = $Name; message = $_.Exception.Message })
            return $null
        }
    }

    $os = Capture-Section 'operatingSystem' {
        $value = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $capturedUtc = [DateTime]::UtcNow
        $bootIdentity = Get-ViiperBootIdentity
        $bootUtc = [DateTime]::Parse($bootIdentity, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
        [ordered]@{
            caption = [string]$value.Caption
            version = [string]$value.Version
            buildNumber = [string]$value.BuildNumber
            productType = [uint32]$value.ProductType
            osArchitecture = [string]$value.OSArchitecture
            bootIdentity = $bootIdentity
            lastBootUpUtc = $bootIdentity
            uptimeSeconds = [uint64][Math]::Floor(($capturedUtc - $bootUtc).TotalSeconds)
            localDateTimeUtc = $capturedUtc.ToString('o')
        }
    }
    $computer = Capture-Section 'computerSystem' {
        $value = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        [ordered]@{
            manufacturer = [string]$value.Manufacturer
            model = [string]$value.Model
            hypervisorPresent = [bool]$value.HypervisorPresent
            totalPhysicalMemoryBytes = [uint64]$value.TotalPhysicalMemory
        }
    }
    $powerCfg = Capture-Section 'activePowerPlan' {
        Invoke-ViiperReadOnlyCommand -FilePath (Join-Path $env:SystemRoot 'System32\powercfg.exe') `
            -Arguments @('/getactivescheme')
    }
    $battery = Capture-Section 'battery' {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $status = [Windows.Forms.SystemInformation]::PowerStatus
        [ordered]@{
            powerLineStatus = [string]$status.PowerLineStatus
            batteryChargeStatus = [string]$status.BatteryChargeStatus
            batteryLifePercent = [double]$status.BatteryLifePercent
            batteryLifeRemainingSeconds = [int]$status.BatteryLifeRemaining
            systemBatteryDevices = @((Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue) |
                ForEach-Object {
                    [ordered]@{
                        name = [string]$_.Name
                        status = [string]$_.Status
                        batteryStatus = [uint16]$_.BatteryStatus
                        estimatedChargeRemainingPercent = [uint16]$_.EstimatedChargeRemaining
                    }
                })
        }
    }
    $bcd = Capture-Section 'bootConfiguration' {
        $result = Invoke-ViiperReadOnlyCommand `
            -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') `
            -Arguments @('/enum', '{current}')
        [ordered]@{
            testSigning = [bool](($result.output -join [Environment]::NewLine) -match
                '(?im)^\s*testsigning\s+Yes\s*$')
            command = $result
        }
    }
    $deviceGuard = Capture-Section 'deviceGuard' {
        $value = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard `
            -ClassName Win32_DeviceGuard -ErrorAction Stop
        [ordered]@{
            virtualizationBasedSecurityStatus = [uint32]$value.VirtualizationBasedSecurityStatus
            securityServicesConfigured = @($value.SecurityServicesConfigured | ForEach-Object { [uint32]$_ })
            securityServicesRunning = @($value.SecurityServicesRunning | ForEach-Object { [uint32]$_ })
            requiredSecurityProperties = @($value.RequiredSecurityProperties | ForEach-Object { [uint32]$_ })
            availableSecurityProperties = @($value.AvailableSecurityProperties | ForEach-Object { [uint32]$_ })
        }
    }
    $hvciRegistry = Capture-Section 'hvciRegistry' {
        $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity'
        if (Test-Path -LiteralPath $path) {
            $value = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
            $enabledProperty = $value.PSObject.Properties['Enabled']
            $lockedProperty = $value.PSObject.Properties['Locked']
            [ordered]@{
                present = $true
                enabled = if ($null -ne $enabledProperty) { [int]$enabledProperty.Value } else { $null }
                locked = if ($null -ne $lockedProperty) { [int]$lockedProperty.Value } else { $null }
            }
        }
        else { [ordered]@{ present = $false; enabled = $null; locked = $null } }
    }
    $pendingReboot = Capture-Section 'pendingReboot' {
        $pendingRename = (Get-ItemProperty `
            -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' `
            -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations
        $activeName = (Get-ItemProperty `
            -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' `
            -Name ComputerName -ErrorAction SilentlyContinue).ComputerName
        $pendingName = (Get-ItemProperty `
            -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' `
            -Name ComputerName -ErrorAction SilentlyContinue).ComputerName
        [ordered]@{
            componentBasedServicing = Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
            windowsUpdate = Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
            pendingFileRenameOperations = @($pendingRename).Count -gt 0
            pendingComputerRename = -not [string]::Equals([string]$activeName, [string]$pendingName,
                [StringComparison]::OrdinalIgnoreCase)
        }
    }
    $disks = Capture-Section 'disks' {
        @((Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop) |
            ForEach-Object {
                [ordered]@{
                    deviceId = [string]$_.DeviceID
                    volumeName = [string]$_.VolumeName
                    sizeBytes = [uint64]$_.Size
                    freeBytes = [uint64]$_.FreeSpace
                }
            })
    }
    $processes = Capture-Section 'backgroundProcesses' {
        @((Get-CimInstance Win32_Process -ErrorAction Stop) | Sort-Object Name, ProcessId |
            ForEach-Object {
                [ordered]@{
                    name = [string]$_.Name
                    processId = [uint32]$_.ProcessId
                    parentProcessId = [uint32]$_.ParentProcessId
                    threadCount = [uint32]$_.ThreadCount
                    workingSetBytes = [uint64]$_.WorkingSetSize
                    executablePath = [string]$_.ExecutablePath
                }
            })
    }

    $usbipServices = Capture-Section 'usbipServices' {
        $candidates = @(
            @(Get-CimInstance Win32_Service -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    Kind = 'Win32_Service'; Name = $_.Name; DisplayName = $_.DisplayName
                    State = $_.State; StartMode = $_.StartMode; StartName = $_.StartName
                    PathName = $_.PathName
                }
            })
            @(Get-CimInstance Win32_SystemDriver -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    Kind = 'Win32_SystemDriver'; Name = $_.Name; DisplayName = $_.DisplayName
                    State = $_.State; StartMode = $_.StartMode; StartName = $_.StartName
                    PathName = $_.PathName
                }
            })
        )
        @(($candidates | Where-Object {
            (@($_.Name, $_.DisplayName, $_.PathName) -join ' ') -match '(?i)usbip|usb/ip|vhci'
        }) | Sort-Object Kind, Name -Unique | ForEach-Object {
            $resolvedImage = Resolve-ViiperServiceImage -ImagePath ([string]$_.PathName)
            [ordered]@{
                kind = [string]$_.Kind
                name = [string]$_.Name
                displayName = [string]$_.DisplayName
                state = [string]$_.State
                startMode = [string]$_.StartMode
                startName = [string]$_.StartName
                rawPathName = [string]$_.PathName
                image = Get-ViiperFileProvenance -Path $resolvedImage
            }
        })
    }
    $usbipDrivers = Capture-Section 'usbipSignedDrivers' {
        @((Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Where-Object {
            (@($_.DeviceName, $_.DriverProviderName, $_.InfName, $_.DeviceID, $_.Signer) -join ' ') -match
                '(?i)usbip|usb/ip|vhci'
        }) | Sort-Object DeviceID | ForEach-Object {
            $infPath = if ([string]$_.InfName -match '^oem[0-9]+\.inf$') {
                Join-Path (Join-Path $env:SystemRoot 'INF') ([string]$_.InfName)
            } else { $null }
            [ordered]@{
                deviceName = [string]$_.DeviceName
                deviceId = [string]$_.DeviceID
                infName = [string]$_.InfName
                publishedInf = if ($null -ne $infPath -and (Test-Path -LiteralPath $infPath -PathType Leaf)) {
                    Get-ViiperFileProvenance -Path $infPath
                } else { $null }
                provider = [string]$_.DriverProviderName
                driverVersion = [string]$_.DriverVersion
                driverDate = [string]$_.DriverDate
                signer = [string]$_.Signer
                isSigned = [bool]$_.IsSigned
            }
        })
    }
    $usbipDevices = Capture-Section 'usbipDeviceInstances' {
        @((Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {
            (@($_.Name, $_.Description, $_.PNPDeviceID,
                (@($_.HardwareID) -join ' ')) -join ' ') -match
                '(?i)usbip|usb/ip|vhci'
        }) | Sort-Object PNPDeviceID | ForEach-Object {
            [ordered]@{
                name = [string]$_.Name
                description = [string]$_.Description
                instanceId = [string]$_.PNPDeviceID
                hardwareIds = @($_.HardwareID | ForEach-Object { [string]$_ })
                service = [string]$_.Service
                status = [string]$_.Status
                problemCode = [uint32]$_.ConfigManagerErrorCode
            }
        })
    }
    $driverStoreEnumeration = Capture-Section 'driverStoreEnumeration' {
        Invoke-ViiperReadOnlyCommand -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') `
            -Arguments @('/enum-drivers', '/files')
    }

    return [ordered]@{
        schema = 'viiper.windows11.machine-snapshot/v1'
        capturedUtc = [DateTime]::UtcNow.ToString('o')
        observationalOnly = $true
        operatingSystem = $os
        computerSystem = $computer
        activePowerPlan = $powerCfg
        battery = $battery
        bootConfiguration = $bcd
        deviceGuard = $deviceGuard
        hvciRegistry = $hvciRegistry
        pendingReboot = $pendingReboot
        fixedDisks = @($disks)
        backgroundProcesses = @($processes)
        usbipComparator = [ordered]@{
            claim = 'provenance-only; no ABBA or latency-superiority claim'
            services = @($usbipServices)
            signedDrivers = @($usbipDrivers)
            deviceInstances = @($usbipDevices)
            driverStoreEnumeration = $driverStoreEnumeration
        }
        collectionErrors = @($errors)
    }
}

function Get-ViiperValidationPhaseModel {
    return @(
        [ordered]@{ phase = 'Preflight'; predecessor = 'new'; mutatesMachine = $false; next = 'preflight-complete' },
        [ordered]@{ phase = 'Install'; predecessor = 'preflight-complete'; mutatesMachine = $true; next = 'installed-or-reboot' },
        [ordered]@{ phase = 'Repair'; predecessor = 'installed'; mutatesMachine = $true; next = 'installed-or-reboot' },
        [ordered]@{ phase = 'RebootResume'; predecessor = 'awaiting-transaction-reboot-or-interrupted-running-transaction'; mutatesMachine = $true; next = 'installed-or-reboot' },
        [ordered]@{ phase = 'ManualChecks'; predecessor = 'installed'; mutatesMachine = $false; next = 'manual-complete-or-reboot' },
        [ordered]@{ phase = 'EnableVerifier'; predecessor = 'manual-complete'; mutatesMachine = $true; next = 'awaiting-verifier-reboot' },
        [ordered]@{ phase = 'VerifierResume'; predecessor = 'awaiting-verifier-reboot'; mutatesMachine = $false; next = 'verifier-ready' },
        [ordered]@{ phase = 'Live'; predecessor = 'verifier-ready'; mutatesMachine = $true; next = 'live-complete'; includes = 'VIIPER reference plus DS4Windows HID/media/reconnect runner' },
        [ordered]@{ phase = 'Performance'; predecessor = 'live-complete'; mutatesMachine = $true; next = 'performance-complete' },
        [ordered]@{ phase = 'LatencyMatrix'; predecessor = 'performance-complete'; mutatesMachine = $true; next = 'latency-complete'; claim = 'descriptive exact-machine-session only' },
        [ordered]@{ phase = 'CollectDumps'; predecessor = 'any-after-install'; mutatesMachine = $false; next = 'unchanged' },
        [ordered]@{ phase = 'Uninstall'; predecessor = 'any-after-install'; mutatesMachine = $true; next = 'uninstalled-or-reboot' },
        [ordered]@{ phase = 'Status'; predecessor = 'any'; mutatesMachine = $false; next = 'unchanged' }
    )
}

Export-ModuleMember -Function @(
    'Get-ViiperSha256', 'Resolve-ViiperRegularFile', 'Resolve-ViiperSafeDirectory',
    'Write-ViiperJsonAtomic', 'Test-ViiperGitIdentity', 'Test-ViiperLocalTestPackage',
    'Get-ViiperBootIdentity', 'Get-ViiperMachineEvidenceSnapshot',
    'Get-ViiperValidationPhaseModel'
)
