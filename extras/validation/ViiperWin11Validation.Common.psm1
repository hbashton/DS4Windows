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
            [IO.File]::Replace(
                $temporary, $fullPath,
                [Management.Automation.Language.NullString]::Value, $true)
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

function Test-ViiperFailedInstallRecoveryEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$PredecessorEvidenceRoot,
        [Parameter(Mandatory = $true)][string]$PredecessorInstallStepDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedStateSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedInstallCommandSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedInstallResultSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedInstallStdoutSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedInstallStderrSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedBundleManifestSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedViiperSourceRevision,
        [Parameter(Mandatory = $true)][string]$ExpectedDS4WindowsSourceRevision,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageLockSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedMachine,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetUserSID
    )

    $digests = @(
        $ExpectedStateSHA256, $ExpectedInstallCommandSHA256,
        $ExpectedInstallResultSHA256, $ExpectedInstallStdoutSHA256,
        $ExpectedInstallStderrSHA256, $ExpectedBundleManifestSHA256,
        $ExpectedPackageLockSHA256
    )
    if (@($digests | Where-Object { $_ -cnotmatch '^[0-9a-fA-F]{64}$' }).Count -ne 0 -or
        $ExpectedViiperSourceRevision -cnotmatch '^[0-9a-fA-F]{40,64}$' -or
        $ExpectedDS4WindowsSourceRevision -cnotmatch '^[0-9a-fA-F]{40,64}$' -or
        $ExpectedTargetUserSID -cnotmatch '^S-1-5-21-(?:[0-9]+-){3}[0-9]+$') {
        throw 'Failed-install recovery identities are not canonical hashes, revisions, and a user SID.'
    }

    $predecessorRoot = Resolve-ViiperSafeDirectory `
        -Path $PredecessorEvidenceRoot -Label 'Predecessor evidence root'
    $predecessorSteps = Resolve-ViiperSafeDirectory `
        -Path (Join-Path $predecessorRoot 'steps') `
        -Label 'Predecessor evidence steps directory'
    $installStep = Resolve-ViiperSafeDirectory `
        -Path $PredecessorInstallStepDirectory `
        -Label 'Predecessor Install evidence directory'
    if ((Split-Path -Parent $installStep) -ine $predecessorSteps) {
        throw 'Predecessor Install evidence must be one direct child of its retained steps directory.'
    }

    $statePath = Resolve-ViiperRegularFile `
        -Path (Join-Path $predecessorRoot 'state\validation-state.json') `
        -Label 'Predecessor validation state'
    $commandPath = Resolve-ViiperRegularFile `
        -Path (Join-Path $installStep 'command.json') `
        -Label 'Predecessor Install command evidence'
    $resultPath = Resolve-ViiperRegularFile `
        -Path (Join-Path $installStep 'result.json') `
        -Label 'Predecessor Install result evidence'
    $stdoutPath = Resolve-ViiperRegularFile `
        -Path (Join-Path $installStep 'stdout.log') `
        -Label 'Predecessor Install stdout evidence'
    $stderrPath = Resolve-ViiperRegularFile `
        -Path (Join-Path $installStep 'stderr.log') `
        -Label 'Predecessor Install stderr evidence'

    $expectedFiles = [ordered]@{
        $statePath = $ExpectedStateSHA256
        $commandPath = $ExpectedInstallCommandSHA256
        $resultPath = $ExpectedInstallResultSHA256
        $stdoutPath = $ExpectedInstallStdoutSHA256
        $stderrPath = $ExpectedInstallStderrSHA256
    }
    $lockedStreams = [Collections.Generic.List[IO.FileStream]]::new()
    $lockedEvidence = @{}
    try {
        foreach ($entry in $expectedFiles.GetEnumerator()) {
            $path = [string]$entry.Key
            $stream = [IO.FileStream]::new(
                $path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                [IO.FileShare]::Read)
            $lockedStreams.Add($stream)
            if ($stream.Length -le 0 -or $stream.Length -gt 16777216) {
                throw "Predecessor recovery evidence length is outside its bound: '$path'."
            }
            $bytes = [byte[]]::new([int]$stream.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($read -le 0) {
                    throw "Predecessor recovery evidence ended before its locked length: '$path'."
                }
                $offset += $read
            }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $digest = ([BitConverter]::ToString(
                    $algorithm.ComputeHash($bytes))).Replace(
                        '-', '').ToLowerInvariant()
            }
            finally {
                $algorithm.Dispose()
            }
            if ($digest -cne ([string]$entry.Value).ToLowerInvariant()) {
                throw "Predecessor recovery evidence hash mismatch: '$path'."
            }
            $textOffset = if ($bytes.Length -ge 3 -and
                $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and
                $bytes[2] -eq 0xbf) { 3 } else { 0 }
            $text = [Text.UTF8Encoding]::new($false, $true).GetString(
                $bytes, $textOffset, $bytes.Length - $textOffset)
            $lockedEvidence[$path] = [pscustomobject]@{
                Hash = $digest
                Text = $text
            }
        }

    $state = $lockedEvidence[$statePath].Text |
        ConvertFrom-Json -ErrorAction Stop
    $history = @($state.history)
    if ([string]$state.schema -cne 'viiper.windows11.validation-state/v1' -or
        [string]$state.bundleManifestSha256 -cne
            $ExpectedBundleManifestSHA256.ToLowerInvariant() -or
        [string]$state.viiperSourceRevision -cne
            $ExpectedViiperSourceRevision.ToLowerInvariant() -or
        [string]$state.ds4WindowsSourceRevision -cne
            $ExpectedDS4WindowsSourceRevision.ToLowerInvariant() -or
        [string]$state.packageLockSha256 -cne
            $ExpectedPackageLockSHA256.ToLowerInvariant() -or
        [string]$state.machine -cne $ExpectedMachine -or
        [string]$state.targetUserSid -cne $ExpectedTargetUserSID -or
        [string]$state.lifecycle -cne 'transaction-failed' -or
        [string]$state.pendingTransaction -cne 'Install' -or
        [int]$state.trustBeforeInstall.Root -ne 0 -or
        [int]$state.trustBeforeInstall.TrustedPublisher -ne 0 -or
        $history.Count -eq 0 -or
        [string]$history[-1].phase -cne 'Install' -or
        [string]$history[-1].lifecycle -cne 'transaction-failed') {
        throw 'Predecessor state is not the exact zero-prior-trust failed Install authorized for recovery.'
    }

    $command = $lockedEvidence[$commandPath].Text |
        ConvertFrom-Json -ErrorAction Stop
    $arguments = @($command.arguments | ForEach-Object { [string]$_ })
    if ([string]$command.schema -cne 'viiper.windows11.captured-command/v1' -or
        [string]$command.name -cne 'install') {
        throw 'Predecessor command evidence is not the captured Install command.'
    }
    function Assert-UniquePredecessorArgument {
        param([string]$Name, [string]$ExpectedValue)
        $indexes = @()
        for ($index = 0; $index -lt $arguments.Count; ++$index) {
            if ($arguments[$index] -ceq $Name) { $indexes += $index }
        }
        if ($indexes.Count -ne 1 -or $indexes[0] + 1 -ge $arguments.Count -or
            $arguments[$indexes[0] + 1] -cne $ExpectedValue) {
            throw "Predecessor Install command lost its unique '$Name' identity."
        }
    }
    Assert-UniquePredecessorArgument '-ExpectedSourceRevision' `
        $ExpectedViiperSourceRevision.ToLowerInvariant()
    Assert-UniquePredecessorArgument '-ExpectedPackageLockSHA256' `
        $ExpectedPackageLockSHA256.ToLowerInvariant()
    Assert-UniquePredecessorArgument '-TargetUserSID' $ExpectedTargetUserSID
    if (@($arguments | Where-Object {
            $_ -ceq '-AcknowledgeDisposableTestMachine'
        }).Count -ne 1) {
        throw 'Predecessor Install command lacks its unique disposable-machine acknowledgement.'
    }

    $result = $lockedEvidence[$resultPath].Text |
        ConvertFrom-Json -ErrorAction Stop
    if ([string]$result.schema -cne 'viiper.windows11.captured-result/v1' -or
        [string]$result.name -cne 'install' -or
        $result.started -ne $true -or [int]$result.exitCode -ne 1 -or
        $result.success -ne $false -or $null -ne $result.launchFailure -or
        [string]$result.evidenceDirectory -ine $installStep) {
        throw 'Predecessor result does not prove one launched, failed Install child.'
    }

    $stdout = [string]$lockedEvidence[$stdoutPath].Text
    # EmitOutcome always emits this canonical prefix. Depending on how far
    # journal initialization progressed, it may append either or both bounded
    # recovery diagnostics in this one fixed field order. The caller still
    # binds the entire stdout file by SHA-256 above; accepting the diagnostics
    # here only prevents a truthful retained-journal path from invalidating the
    # exact zero-change failure proof.
    $quotedRecoveryPath = '"(?:[^"\\\r\n]|\\["\\]){1,32767}"'
    $quotedRecoveryText = '"(?:[^"\\\r\n]|\\["\\]){0,4096}"'
    $recoveryRecordProof = '(?: recoveryRecord=' + $quotedRecoveryPath +
        ' recoveryRecordWritten=1| recoveryRecord=' + $quotedRecoveryPath +
        ' recoveryRecordWritten=0(?: recoveryRecordPhase=' +
        $quotedRecoveryText + ' recoveryRecordWin32Error=[0-9]{1,10} ' +
        'recoveryRecordMessage=' + $quotedRecoveryText + ')?)?'
    $recoveryBackupProof = '(?: recoveryBackup=' + $quotedRecoveryPath +
        ' recoveryBackupRetained=(?:0|1))?'
    $failureProof = '(?m)^result=error operation=install changed=0 ' +
        'rebootRequired=0 rollback=not-needed exitCode=4 ' +
        'phase="install-journal-broker-image-hash" win32Error=23 ' +
        'message="protected broker evidence differs from its immutable digest"' +
        $recoveryRecordProof + $recoveryBackupProof + '\r?$'
    if ([regex]::Matches($stdout, $failureProof).Count -ne 1 -or
        [regex]::Matches($stdout,
            '(?m)^result=(?:success|error) operation=install ').Count -ne 1) {
        throw 'Predecessor stdout does not prove the exact settled, zero-change broker-image digest rejection.'
    }
    foreach ($storeName in @('Root', 'TrustedPublisher')) {
        foreach ($proof in @(
                "local-test-trust store=$storeName action=add result=added",
                "local-test-trust store=$storeName action=verify-add result=present")) {
            if ([regex]::Matches($stdout,
                    '(?m)^' + [regex]::Escape($proof) + '\r?$').Count -ne 1) {
                throw "Predecessor stdout does not prove exact new trust in LocalMachine\$storeName."
            }
        }
    }

    return [ordered]@{
        predecessorEvidenceRoot = $predecessorRoot
        installEvidenceDirectory = $installStep
        statePath = $statePath
        stateSha256 = [string]$lockedEvidence[$statePath].Hash
        commandSha256 = [string]$lockedEvidence[$commandPath].Hash
        resultSha256 = [string]$lockedEvidence[$resultPath].Hash
        stdoutSha256 = [string]$lockedEvidence[$stdoutPath].Hash
        stderrSha256 = [string]$lockedEvidence[$stderrPath].Hash
        bundleManifestSha256 = $ExpectedBundleManifestSHA256.ToLowerInvariant()
        viiperSourceRevision = $ExpectedViiperSourceRevision.ToLowerInvariant()
        ds4WindowsSourceRevision = $ExpectedDS4WindowsSourceRevision.ToLowerInvariant()
        packageLockSha256 = $ExpectedPackageLockSHA256.ToLowerInvariant()
    }
    }
    finally {
        for ($index = $lockedStreams.Count - 1; $index -ge 0; --$index) {
            $lockedStreams[$index].Dispose()
        }
    }
}

function Get-ViiperValidationPhaseModel {
    return @(
        [ordered]@{ phase = 'RecoverFailedInstall'; predecessor = 'exact predecessor transaction-failed Install evidence'; mutatesMachine = $true; next = 'fresh-preflight-ready-or-reboot' },
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
    'Test-ViiperFailedInstallRecoveryEvidence',
    'Get-ViiperBootIdentity', 'Get-ViiperMachineEvidenceSnapshot',
    'Get-ViiperValidationPhaseModel'
)
