[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Operation = 'Install',

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^S-[0-9]+(?:-[0-9]+)+$')]
    [string]$TargetUserSID,

    [switch]$AllowLocalTest,
    [switch]$AcknowledgeDisposableTestMachine
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSchema = 1
$localTestOptInEnvironment = 'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST'
$structuredOutcomeWritten = $false
$transactionStarted = $false
$trustCleanupFailed = $false
$localTestCertificate = $null
$addedLocalTestTrustStores = [Collections.Generic.List[string]]::new()

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-LocalTestBootAdmission {
    $bcdeditPath = Join-Path ([Environment]::SystemDirectory) 'bcdedit.exe'
    $bcdOutput = (& $bcdeditPath /enum '{current}' 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        $bcdOutput -notmatch '(?im)^\s*testsigning\s+Yes\s*$') {
        throw "The current boot entry does not report 'testsigning Yes'. Enable TESTSIGNING and reboot before local-test installation.`n$bcdOutput"
    }
}

function Get-ExactMachineCertificateCount {
    param(
        [Parameter(Mandatory = $true)][string]$StoreName,
        [Parameter(Mandatory = $true)]$Certificate
    )

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $matches = $null
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Certificate.Thumbprint, $false)
        $expectedBytes = [Convert]::ToBase64String($Certificate.RawData)
        $exact = @($matches | Where-Object {
            [Convert]::ToBase64String($_.RawData) -ceq $expectedBytes
        })
        if ($matches.Count -ne $exact.Count -or $exact.Count -gt 1) {
            throw "Certificate collision in LocalMachine\$StoreName."
        }
        return [int]$exact.Count
    }
    finally {
        if ($null -ne $matches) {
            foreach ($match in $matches) {
                $match.Dispose()
            }
        }
        $store.Close()
    }
}

function Ensure-ExactLocalTestTrust {
    param(
        [Parameter(Mandatory = $true)][string]$StoreName,
        [Parameter(Mandatory = $true)]$Certificate
    )

    if ((Get-ExactMachineCertificateCount -StoreName $StoreName `
            -Certificate $Certificate) -eq 1) {
        Write-Host "local-test-trust store=$StoreName action=add result=preexisting"
        return
    }

    # Record cleanup authority before the mutation. If Add throws after
    # persisting the certificate, the outer trap still removes only these
    # exact bytes while no driver transaction has begun.
    $script:addedLocalTestTrustStores.Add($StoreName)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($Certificate)
    }
    finally {
        $store.Close()
    }
    if ((Get-ExactMachineCertificateCount -StoreName $StoreName `
            -Certificate $Certificate) -ne 1) {
        throw "Exact local-test certificate was not installed in LocalMachine\$StoreName."
    }
    Write-Host "local-test-trust store=$StoreName action=add result=added"
}

function Remove-NewLocalTestTrust {
    if ($null -eq $script:localTestCertificate) {
        return
    }
    $cleanupErrors = [Collections.Generic.List[Exception]]::new()
    foreach ($storeName in $script:addedLocalTestTrustStores) {
        try {
            $count = Get-ExactMachineCertificateCount -StoreName $storeName `
                -Certificate $script:localTestCertificate
            if ($count -eq 1) {
                $store = [Security.Cryptography.X509Certificates.X509Store]::new(
                    $storeName,
                    [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
                try {
                    $store.Open(
                        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    $store.Remove($script:localTestCertificate)
                }
                finally {
                    $store.Close()
                }
            }
            if ((Get-ExactMachineCertificateCount -StoreName $storeName `
                    -Certificate $script:localTestCertificate) -ne 0) {
                throw "Exact local-test certificate remained in LocalMachine\$StoreName."
            }
            Write-Host "local-test-trust store=$StoreName action=cleanup result=absent"
        }
        catch {
            Write-Host "local-test-trust store=$StoreName action=cleanup result=error"
            $cleanupErrors.Add([InvalidOperationException]::new(
                "LocalMachine\$StoreName trust cleanup failed.",
                $_.Exception))
        }
    }
    if ($cleanupErrors.Count -ne 0) {
        throw [AggregateException]::new(
            'Failed to remove one or more newly added local-test trust anchors.',
            [Exception[]]$cleanupErrors.ToArray())
    }
}

function ConvertTo-WindowsProcessArgument {
    param([AllowEmptyString()][Parameter(Mandatory = $true)][string]$Value)

    if ($Value.IndexOf([char]0) -ge 0) {
        throw 'Native process argument contains NUL.'
    }
    if ($Value.Length -ne 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append([char]34)
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            ++$slashes
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $slashes) + 1)
            [void]$builder.Append([char]34)
            $slashes = 0
            continue
        }
        if ($slashes -ne 0) {
            [void]$builder.Append([char]92, $slashes)
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -ne 0) {
        [void]$builder.Append([char]92, 2 * $slashes)
    }
    [void]$builder.Append([char]34)
    return $builder.ToString()
}

function Set-ExactProcessArguments {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.ProcessStartInfo]$StartInfo,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    if ($null -ne $StartInfo.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $Arguments) {
            $StartInfo.ArgumentList.Add($argument)
        }
        return
    }
    $StartInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-WindowsProcessArgument -Value $_
    }) -join ' ')
}

function Invoke-JoinedNativeProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][ref]$Started
    )

    $Started.Value = $false
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    Set-ExactProcessArguments -StartInfo $startInfo -Arguments $Arguments
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $joined = $false
    try {
        if (-not $process.Start()) {
            throw 'The protected native broker process was not created.'
        }
        $Started.Value = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $joined) {
            try {
                $process.WaitForExit()
                $joined = $true
            }
            catch {
                # Never unwind while the exact mutating child may remain alive.
                Start-Sleep -Milliseconds 250
            }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $combined = @($stdout, $stderr) -join [Environment]::NewLine
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = @($combined -split '\r?\n' | Where-Object {
                $_.Length -ne 0
            })
        }
    }
    finally {
        if ($Started.Value -and -not $joined) {
            while (-not $joined) {
                try {
                    $process.WaitForExit()
                    $joined = $true
                }
                catch {
                    Start-Sleep -Milliseconds 250
                }
            }
        }
        $process.Dispose()
    }
}

function Resolve-SingleMetadataPath {
    $outputRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $outputRoot 'ViiperNativeRuntimeMetadata.json'),
        (Join-Path $PSScriptRoot 'ViiperNativeRuntimeMetadata.json')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

    $resolved = @($candidates | ForEach-Object {
        (Resolve-Path -LiteralPath $_ -ErrorAction Stop).Path
    } | Select-Object -Unique)
    if ($resolved.Count -ne 1) {
        throw "Expected exactly one bundled ViiperNativeRuntimeMetadata.json; found $($resolved.Count)."
    }
    $item = Get-Item -LiteralPath $resolved[0] -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Runtime metadata must not be a reparse point: '$($item.FullName)'."
    }
    return $item.FullName
}

function Get-UniqueArtifact {
    param(
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $matches = @($Metadata.artifacts | Where-Object { [string]$_.role -ceq $Role })
    if ($matches.Count -ne 1) {
        throw "Runtime metadata must contain exactly one '$Role' artifact; found $($matches.Count)."
    }
    return $matches[0]
}

function Assert-NoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$FilePath
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $cursor = Split-Path -Parent ([IO.Path]::GetFullPath($FilePath))
    while ($cursor.Length -ge $rootPath.Length) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Native package directory chain is not an ordinary directory: '$cursor'."
        }
        if ($cursor -ceq $rootPath) {
            return
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -ceq $cursor) {
            break
        }
        $cursor = $parent
    }
    throw "Artifact escaped the native package root: '$FilePath'."
}

function Resolve-VerifiedArtifact {
    param(
        [Parameter(Mandatory = $true)]$Artifact,
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    $relativePath = [string]$Artifact.relativePath
    $sha256 = ([string]$Artifact.sha256).ToLowerInvariant()
    $expectedLength = [long]$Artifact.length
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.IndexOf([char]0) -ge 0 -or
        $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $expectedLength -le 0) {
        throw "Artifact '$([string]$Artifact.role)' has invalid path, length, or SHA-256 metadata."
    }

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
    $candidate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relativePath))
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact '$relativePath' escapes '$root'."
    }
    Assert-NoReparseDirectoryChain -Root $root -FilePath $candidate
    $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Artifact must be an ordinary file: '$candidate'."
    }
    if ($item.Length -ne $expectedLength) {
        throw "Artifact '$relativePath' length is $($item.Length); expected $expectedLength."
    }
    $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $sha256) {
        throw "Artifact '$relativePath' SHA-256 is $actualHash; expected $sha256."
    }
    return $item.FullName
}

function Write-StructuredOutcome {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedOperation,
        [Parameter(Mandatory = $true)][int]$ExitCode
    )

    $outcome = [ordered]@{
        schemaVersion = 1
        operation = $RequestedOperation.ToLowerInvariant()
        exitCode = $ExitCode
        succeeded = ($ExitCode -eq 0)
        rebootRequired = ($ExitCode -eq 3010)
        rollbackStatus = if ($ExitCode -eq 0) {
            'not-required'
        } elseif ($ExitCode -eq 3010) {
            'safely-settled'
        } elseif ($script:trustCleanupFailed) {
            'unverified-see-transaction-log'
        } elseif (-not $script:transactionStarted) {
            'not-started'
        } else {
            'unverified-see-transaction-log'
        }
        manualRecoveryRequired = $script:trustCleanupFailed -or
            ($script:transactionStarted -and $ExitCode -notin @(0, 3010))
    }
    Write-Host ('DS4WINDOWS_VIIPER_NATIVE_RESULT ' +
        ($outcome | ConvertTo-Json -Compress))
    $script:structuredOutcomeWritten = $true
}

function Initialize-ProtectedStage {
    param([Parameter(Mandatory = $true)][string]$ProgramDataRoot)

    $programData = [IO.Path]::GetFullPath($ProgramDataRoot).TrimEnd('\')
    $programDataItem = Get-Item -LiteralPath $programData -Force
    if (-not $programDataItem.PSIsContainer -or
        ($programDataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ProgramData is not a safe staging parent: '$programData'."
    }
    $stage = Join-Path $programData ('VIIPER.DS4WindowsStage.' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($stage) | Out-Null

    $administrators = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $system = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($administrators, $system)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $stage -AclObject $acl
    return $stage
}

function Remove-ProtectedStage {
    param(
        [Parameter(Mandatory = $true)][string]$StagePath,
        [Parameter(Mandatory = $true)][string]$ProgramDataRoot
    )

    $stage = [IO.Path]::GetFullPath($StagePath).TrimEnd('\')
    $programData = [IO.Path]::GetFullPath($ProgramDataRoot).TrimEnd('\')
    $prefix = $programData + [IO.Path]::DirectorySeparatorChar + 'VIIPER.DS4WindowsStage.'
    if (-not $stage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $stage) -ine $programData) {
        throw "Refusing to remove an unverified staging directory: '$stage'."
    }
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

trap {
    $primaryFailure = $_.Exception
    $trustCleanupFailure = $null
    if (-not $script:transactionStarted -and
        $script:addedLocalTestTrustStores.Count -ne 0) {
        try {
            Remove-NewLocalTestTrust
        }
        catch {
            $trustCleanupFailure = $_.Exception
            $script:trustCleanupFailed = $true
        }
    }
    if (-not $script:structuredOutcomeWritten) {
        Write-StructuredOutcome -RequestedOperation $Operation -ExitCode 1
    }
    Write-Host ("VIIPER native UDE setup stopped: " +
        $primaryFailure.Message) -ForegroundColor Red
    if ($null -ne $trustCleanupFailure) {
        Write-Host ("VIIPER local-test trust cleanup also failed: " +
            $trustCleanupFailure.Message) -ForegroundColor Red
    }
    if ($null -ne $script:localTestCertificate) {
        $script:localTestCertificate.Dispose()
        $script:localTestCertificate = $null
    }
    exit 1
}

if (-not [Environment]::Is64BitOperatingSystem -or
    -not [Environment]::Is64BitProcess) {
    throw 'VIIPER native UDE setup requires a 64-bit DS4Windows process on 64-bit Windows.'
}
if (-not (Test-IsAdministrator)) {
    throw 'VIIPER native UDE setup must run from the administrator prompt started by DS4Windows.'
}

$sid = [Security.Principal.SecurityIdentifier]::new($TargetUserSID)
if ($sid.IsWellKnown([Security.Principal.WellKnownSidType]::LocalSystemSid) -or
    $sid.IsWellKnown([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)) {
    throw 'TargetUserSID must name the interactive DS4Windows user, not a system principal.'
}

$metadataPath = Resolve-SingleMetadataPath
$metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$sourceRevision = [string]$metadata.sourceRevision
$driverPackageVersion = [string]$metadata.driverPackageVersion
$driverABIMajor = [int]$metadata.driverAbi.major
$driverABIMinor = [int]$metadata.driverAbi.minor
$driverCapabilities = [uint32]$metadata.requiredCapabilities
$driverCapabilitiesHex = '0x{0:x8}' -f $driverCapabilities
$driverBuildIdentity = [string]$metadata.loadedDriverBuildIdentity
if ([int]$metadata.schemaVersion -ne $expectedSchema -or
    [string]$metadata.localTestOptInEnvironment -cne
        $localTestOptInEnvironment -or
    $sourceRevision -cnotmatch '^[0-9a-f]{40}$|^[0-9a-f]{64}$' -or
    $driverPackageVersion -cnotmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
    $driverABIMajor -le 0 -or $driverABIMajor -gt 65535 -or
    $driverABIMinor -lt 0 -or $driverABIMinor -gt 65535 -or
    $driverCapabilities -eq 0 -or
    [string]$metadata.requiredCapabilitiesHex -cne $driverCapabilitiesHex -or
    $driverBuildIdentity -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Bundled VIIPER metadata has an invalid source/package/ABI/capability/build-identity contract.'
}
if ([string]$metadata.managedBroker.serviceName -cne 'VIIPERNativeBroker' -or
    [string]$metadata.managedBroker.serviceAccount -cne 'LocalSystem' -or
    [string]$metadata.managedBroker.startMode -cne 'automatic' -or
    [string]$metadata.managedBroker.transport -cne 'native-ude' -or
    [string]$metadata.managedBroker.apiHost -cne '127.0.0.1' -or
    [int]$metadata.managedBroker.apiPort -ne 3242 -or
    [string]$metadata.managedBroker.credentialPath -cne
        '%ProgramData%/VIIPER/viiper.key.txt') {
    throw 'Bundled VIIPER metadata has an invalid managed LocalSystem broker contract.'
}

$controllerContract = $metadata.controllerApiContract
$expectedControllerRegistrations = [ordered]@{
    xbox360 = 'xbox360|0x045e|0x028e|0x028e|xusb-composite|fixed'
    dualshock4 = 'dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|fixed'
    dualshock4audioduplexv3 = 'dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|framed-v3'
    dualshock4audioonlyduplexv3 = 'dualshock4|0x054c|0x09cc|0x05c4|audio-duplex-only|framed-v3'
    dualsensecombinedaudioduplexv5 = 'dualsense|0x054c|0x0ce6|0x0ce6|hid-audio-duplex|framed-v5'
    dualsenseaudioonlyduplexv5 = 'dualsense|0x054c|0x0ce6|0x0ce6|audio-duplex-only|framed-v5'
    dualsensegamepadv5 = 'dualsense|0x054c|0x0ce6|0x0ce6|hid-gamepad-only|framed-v5'
    dualsenseedgecombinedaudioduplexv5 = 'dualsense-edge|0x054c|0x0df2|0x0df2|hid-audio-duplex|framed-v5'
    dualsenseedgegamepadv5 = 'dualsense-edge|0x054c|0x0df2|0x0df2|hid-gamepad-only|framed-v5'
    ns2pro = 'switch2-pro|0x057e|0x2069|0x2069|hid-vendor-bulk|fixed'
}
$actualControllerTypes = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
if ([int]$controllerContract.schemaVersion -ne 1 -or
    [string]$controllerContract.sourceRevision -cne $sourceRevision) {
    throw 'Bundled VIIPER metadata has an invalid source-bound controller API contract.'
}
foreach ($registration in @($controllerContract.registrations)) {
    $type = [string]$registration.type
    if (-not $actualControllerTypes.Add($type) -or
        -not $expectedControllerRegistrations.Contains($type)) {
        throw "Bundled controller API contract has unexpected or duplicate type '$type'."
    }
    $signature = @(
        [string]$registration.persona,
        [string]$registration.defaultVid,
        [string]$registration.defaultPid,
        [string]$registration.ds4WindowsPid,
        [string]$registration.interfaceProfile,
        [string]$registration.streamProtocol
    ) -join '|'
    if ($signature -cne [string]$expectedControllerRegistrations[$type]) {
        throw "Bundled controller API type '$type' diverges from its VIIPER HID/interface implementation."
    }
}
if ($actualControllerTypes.Count -ne $expectedControllerRegistrations.Count) {
    throw 'Bundled controller API contract omits a DS4Windows controller persona.'
}

$eligibility = [string]$metadata.releaseEligibility
$driverValidationMode = 'production'
if ($eligibility -ceq 'production') {
    if ($AllowLocalTest -or $AcknowledgeDisposableTestMachine) {
        throw 'Local-test switches cannot be combined with production VIIPER metadata.'
    }
} elseif ($eligibility -ceq 'local-test-evidence-only') {
    if (-not $AllowLocalTest -or -not $AcknowledgeDisposableTestMachine -or
        [Environment]::GetEnvironmentVariable($localTestOptInEnvironment) -cne '1') {
        throw "This bundle is local-test evidence only. A developer must set $localTestOptInEnvironment=1 and pass both -AllowLocalTest and -AcknowledgeDisposableTestMachine on a disposable VM."
    }
    $driverValidationMode = 'local-test'
} else {
    throw "Unsupported VIIPER release eligibility '$eligibility'."
}

$packageRoot = Join-Path $PSScriptRoot 'viiper-native-package'
$packageRootItem = Get-Item -LiteralPath $packageRoot -Force -ErrorAction Stop
if (-not $packageRootItem.PSIsContainer -or
    ($packageRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The bundled VIIPER native package root is missing or unsafe: '$packageRoot'."
}

if ($Operation -ceq 'Install') {
    $boundPackageFiles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $boundRoles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($artifact in @($metadata.artifacts)) {
        $role = [string]$artifact.role
        if ($role -cnotmatch '^[a-z0-9-]+$' -or -not $boundRoles.Add($role)) {
            throw "Native metadata has invalid or duplicate artifact role '$role'."
        }
        $verifiedPath = Resolve-VerifiedArtifact -Artifact $artifact -PackageRoot $packageRoot
        if (-not $boundPackageFiles.Add($verifiedPath)) {
            throw "Native metadata binds duplicate package path '$verifiedPath'."
        }
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $packageRoot -Directory -Recurse -Force)) {
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Native package contains unsafe directory '$($directory.FullName)'."
        }
    }
    $actualPackageFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force)
    if ($actualPackageFiles.Count -ne $boundPackageFiles.Count) {
        throw "Native package inventory has $($actualPackageFiles.Count) files but metadata binds $($boundPackageFiles.Count)."
    }
    foreach ($file in $actualPackageFiles) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $boundPackageFiles.Contains($file.FullName)) {
            throw "Native package contains unbound or unsafe file '$($file.FullName)'."
        }
    }
}

$brokerArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'broker'
$helperArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-helper'
$brokerPath = Resolve-VerifiedArtifact -Artifact $brokerArtifact -PackageRoot $packageRoot
$helperPath = Resolve-VerifiedArtifact -Artifact $helperArtifact -PackageRoot $packageRoot
$helperHash = ([string]$helperArtifact.sha256).ToLowerInvariant()
if ((Split-Path -Leaf $brokerPath) -cne 'viiper.exe' -or
    (Split-Path -Leaf $helperPath) -cne 'ViiperUdeCtl.exe') {
    throw 'Native metadata must bind viiper.exe and ViiperUdeCtl.exe by their canonical names.'
}

$arguments = @()
if ($Operation -ceq 'Install') {
    $manifestArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'submission-manifest'
    $infArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-inf'
    $sysArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-sys'
    $catArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-cat'
    $manifestPath = Resolve-VerifiedArtifact -Artifact $manifestArtifact -PackageRoot $packageRoot
    $infPath = Resolve-VerifiedArtifact -Artifact $infArtifact -PackageRoot $packageRoot
    $sysPath = Resolve-VerifiedArtifact -Artifact $sysArtifact -PackageRoot $packageRoot
    $catPath = Resolve-VerifiedArtifact -Artifact $catArtifact -PackageRoot $packageRoot
    $driverDirectory = Split-Path -Parent $infPath
    if ((Split-Path -Leaf $manifestPath) -cne 'submission-manifest.json' -or
        (Split-Path -Leaf $infPath) -cne 'ViiperUde.inf' -or
        (Split-Path -Leaf $sysPath) -cne 'ViiperUde.sys' -or
        (Split-Path -Leaf $catPath) -cne 'ViiperUde.cat' -or
        (Split-Path -Parent $sysPath) -ine $driverDirectory -or
        (Split-Path -Parent $catPath) -ine $driverDirectory) {
        throw 'Native metadata must bind the canonical submission manifest and one co-located INF/SYS/CAT driver package.'
    }

    $submission = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    if ([string]$submission.sourceRevision -cne $sourceRevision -or
        [string]$submission.driverPackageVersion -cne $driverPackageVersion -or
        [int]$submission.driverABIMajor -ne $driverABIMajor -or
        [int]$submission.driverABIMinor -ne $driverABIMinor -or
        [string]$submission.driverCapabilities -cne $driverCapabilitiesHex -or
        [string]$submission.driverBuildIdentity -cne $driverBuildIdentity) {
        throw 'The source-bound submission manifest disagrees with bundled runtime metadata.'
    }
    if ($driverValidationMode -ceq 'production') {
        if ($submission.releaseEligible -ne $true -or
            [string]$submission.signingRoute -notmatch 'HLK|WHCP|Microsoft') {
            throw 'Production installation requires a release-eligible HLK/WHCP submission manifest.'
        }
        if (@($metadata.artifacts | Where-Object {
            [string]$_.role -eq 'local-test-certificate-evidence'
        }).Count -ne 0) {
            throw 'Production runtime metadata must not reference a local-test certificate.'
        }
        $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catPath
        if ($catalogSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $catalogSignature.SignerCertificate -or
            $catalogSignature.SignerCertificate.Subject -notmatch
                'Microsoft Windows Hardware Compatibility Publisher') {
            throw 'Production ViiperUde.cat is not signed by Microsoft Windows Hardware Compatibility Publisher.'
        }
    } elseif ($submission.releaseEligible -ne $false -or
        [string]$submission.signingRoute -cne 'LocalTest') {
        throw 'Local-test installation requires the non-release-eligible LocalTest submission manifest.'
    }

    if ($driverValidationMode -ceq 'local-test') {
        $certificateArtifact = Get-UniqueArtifact -Metadata $metadata `
            -Role 'local-test-certificate-evidence'
        $certificatePath = Resolve-VerifiedArtifact `
            -Artifact $certificateArtifact -PackageRoot $packageRoot
        if ((Split-Path -Leaf $certificatePath) -cne 'ViiperUdeTest.cer' -or
            [string]$submission.testSignerCertificateSha256 -cne
                ([string]$certificateArtifact.sha256).ToLowerInvariant()) {
            throw 'The local-test signer certificate disagrees with the source-bound package evidence.'
        }
        Assert-LocalTestBootAdmission
        $script:localTestCertificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $certificatePath)
        if ($script:localTestCertificate.HasPrivateKey) {
            throw 'The local-test package must contain only the public signer certificate.'
        }
        $certificateAlgorithm =
            [Security.Cryptography.SHA256]::Create()
        try {
            $certificateSha256 = ([BitConverter]::ToString(
                $certificateAlgorithm.ComputeHash(
                    $script:localTestCertificate.RawData))).Replace(
                        '-', '').ToLowerInvariant()
        }
        finally {
            $certificateAlgorithm.Dispose()
        }
        if ($certificateSha256 -cne
            ([string]$certificateArtifact.sha256).ToLowerInvariant()) {
            throw 'The parsed local-test signer certificate bytes differ from their package hash.'
        }
        foreach ($storeName in @('Root', 'TrustedPublisher')) {
            Ensure-ExactLocalTestTrust -StoreName $storeName `
                -Certificate $script:localTestCertificate
        }
    }

    $arguments = @(
        'native-package-install',
        '--package-directory', $driverDirectory,
        '--submission-manifest', $manifestPath,
        '--source-revision', $sourceRevision,
        '--driver-helper', $helperPath,
        '--expected-broker-sha-256', ([string]$brokerArtifact.sha256).ToLowerInvariant(),
        '--expected-helper-sha-256', $helperHash,
        '--expected-manifest-sha-256', ([string]$manifestArtifact.sha256).ToLowerInvariant(),
        '--expected-inf-sha-256', ([string]$infArtifact.sha256).ToLowerInvariant(),
        '--expected-sys-sha-256', ([string]$sysArtifact.sha256).ToLowerInvariant(),
        '--expected-cat-sha-256', ([string]$catArtifact.sha256).ToLowerInvariant(),
        '--target-user-sid', $TargetUserSID,
        '--driver-validation-mode', $driverValidationMode
    )
} else {
    $arguments = @(
        'uninstall', '--yes',
        '--target-user-sid', $TargetUserSID,
        '--driver-helper', $helperPath,
        '--expected-helper-sha-256', $helperHash
    )
}

$programDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$stagePath = $null
$exitCode = 1
try {
    $stagePath = Initialize-ProtectedStage -ProgramDataRoot $programDataRoot
    $stagedBroker = Join-Path $stagePath 'viiper.exe'
    Copy-Item -LiteralPath $brokerPath -Destination $stagedBroker -ErrorAction Stop
    $stagedItem = Get-Item -LiteralPath $stagedBroker -Force
    $stagedHash = (Get-FileHash -LiteralPath $stagedBroker -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($stagedItem.Length -ne [long]$brokerArtifact.length -or
        $stagedHash -cne ([string]$brokerArtifact.sha256).ToLowerInvariant()) {
        throw 'Protected staged VIIPER broker did not preserve the manifest-bound bytes.'
    }

    Write-Host "Running VIIPER native UDE $($Operation.ToLowerInvariant()) transaction..."
    $processStarted = $false
    try {
        $processResult = Invoke-JoinedNativeProcess `
            -FileName $stagedBroker -Arguments $arguments `
            -WorkingDirectory $stagePath -Started ([ref]$processStarted)
    }
    finally {
        $script:transactionStarted = $processStarted
    }
    $output = @($processResult.Output)
    $exitCode = [int]$processResult.ExitCode
    $output | ForEach-Object { Write-Host ([string]$_) }
    Write-StructuredOutcome -RequestedOperation $Operation -ExitCode $exitCode
} finally {
    if ($null -ne $stagePath) {
        Remove-ProtectedStage -StagePath $stagePath -ProgramDataRoot $programDataRoot
    }
}
if ($null -ne $script:localTestCertificate) {
    $script:localTestCertificate.Dispose()
    $script:localTestCertificate = $null
}

if ($exitCode -eq 0) {
    Write-Host 'VIIPER native UDE transaction completed and authenticated service readiness was verified by the package transaction.'
} elseif ($exitCode -eq 3010) {
    Write-Warning 'VIIPER stopped at a safe reboot boundary before mutation or after successful rollback. Restart Windows, then rerun this identical transaction.'
} else {
    Write-Warning "VIIPER native UDE transaction failed with exit code $exitCode. Review the protected transaction/recovery logs before retrying."
}
exit $exitCode
