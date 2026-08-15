[CmdletBinding()]
param(
    [switch]$RequireProduction,
    [switch]$RequirePackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$metadataPath = Join-Path $PSScriptRoot 'ViiperNativeRuntimeMetadata.json'
$managerPath = Join-Path $PSScriptRoot 'manage-viiper-native-package.ps1'
$generatorPath = Join-Path $PSScriptRoot 'New-ViiperNativeRuntimeMetadata.ps1'
$controllerContractPath = Join-Path $PSScriptRoot 'ViiperControllerApiContract.json'
foreach ($path in @($metadataPath, $managerPath, $generatorPath,
    $controllerContractPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing native UDE integration input '$path'."
    }
}

foreach ($scriptPath in @($managerPath, $generatorPath)) {
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $scriptPath, [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Native package script '$scriptPath' has PowerShell syntax errors: $($parseErrors -join '; ')"
    }
}

$managerSource = Get-Content -LiteralPath $managerPath -Raw
foreach ($required in @(
    'native-package-install',
    "'uninstall', '--yes'",
    '--expected-broker-sha-256',
    '--expected-helper-sha-256',
    '--expected-manifest-sha-256',
    '--expected-inf-sha-256',
    '--expected-sys-sha-256',
    '--expected-cat-sha-256',
    '--target-user-sid',
    '--driver-validation-mode',
    'DS4WINDOWS_VIIPER_NATIVE_RESULT',
    "'not-started'",
    "'safely-settled'",
    "'unverified-see-transaction-log'",
    'AcknowledgeDisposableTestMachine',
    'local-test-certificate-evidence',
    'Assert-LocalTestBootAdmission',
    'testsigning\s+Yes',
    'Get-ExactMachineCertificateCount',
    'Ensure-ExactLocalTestTrust',
    'Remove-NewLocalTestTrust',
    "@('Root', 'TrustedPublisher')",
    'Invoke-JoinedNativeProcess',
    'Started ([ref]$processStarted)',
    '$script:transactionStarted = $processStarted',
    '$script:trustCleanupFailed = $true',
    'AggregateException'
)) {
    if ($managerSource.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Native package manager omitted required contract token '$required'."
    }
}
if ($managerSource.IndexOf('& $stagedBroker @arguments',
        [StringComparison]::Ordinal) -ge 0) {
    throw 'Native package manager must distinguish process creation from transaction admission.'
}
foreach ($forbidden in @('usbip-win2', 'RunVIIPER', 'Invoke-WebRequest',
    'Invoke-RestMethod', 'DownloadFile', 'api.github.com')) {
    if ($managerSource.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Native package manager contains forbidden legacy/download token '$forbidden'."
    }
}

$generatorSource = Get-Content -LiteralPath $generatorPath -Raw
foreach ($required in @(
    '[string]$submission.driverPackageVersion',
    '[int]$submission.driverABIMajor',
    '[int]$submission.driverABIMinor',
    '[string]$submission.driverCapabilities',
    '[string]$submission.driverBuildIdentity',
    '$ControllerContractPath',
    'local-test-package.lock.json'
)) {
    if ($generatorSource.IndexOf($required,
            [StringComparison]::Ordinal) -lt 0) {
        throw "Native metadata generator omitted dynamic contract token '$required'."
    }
}
if ($generatorSource -match '(?<![0-9])0\.1\.0\.[0-9]+(?![0-9])') {
    throw 'Native metadata generator must not freeze a specific driver package version.'
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$capabilities = [uint32]$metadata.requiredCapabilities
$capabilitiesHex = '0x{0:x8}' -f $capabilities
if ([int]$metadata.schemaVersion -ne 1 -or
    [string]$metadata.localTestOptInEnvironment -cne
        'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST' -or
    [string]$metadata.sourceRevision -cnotmatch '^[0-9a-f]{40}$|^[0-9a-f]{64}$' -or
    [string]$metadata.driverPackageVersion -cnotmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
    [int]$metadata.driverAbi.major -le 0 -or
    [int]$metadata.driverAbi.major -gt 65535 -or
    [int]$metadata.driverAbi.minor -lt 0 -or
    [int]$metadata.driverAbi.minor -gt 65535 -or
    $capabilities -eq 0 -or
    [string]$metadata.requiredCapabilitiesHex -cne $capabilitiesHex -or
    [string]$metadata.loadedDriverBuildIdentity -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Native runtime metadata has an invalid source/package/ABI/capability/build-identity contract.'
}
if ([string]$metadata.managedBroker.serviceName -cne 'VIIPERNativeBroker' -or
    [string]$metadata.managedBroker.serviceAccount -cne 'LocalSystem' -or
    [string]$metadata.managedBroker.startMode -cne 'automatic' -or
    [string]$metadata.managedBroker.transport -cne 'native-ude' -or
    [string]$metadata.managedBroker.apiHost -cne '127.0.0.1' -or
    [int]$metadata.managedBroker.apiPort -ne 3242 -or
    [string]$metadata.managedBroker.credentialPath -cne
        '%ProgramData%/VIIPER/viiper.key.txt') {
    throw 'Native runtime metadata has an invalid managed LocalSystem broker contract.'
}

$controllerTemplate = Get-Content -LiteralPath $controllerContractPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$controllerContract = $metadata.controllerApiContract
if ([int]$controllerContract.schemaVersion -ne 1 -or
    [string]$controllerContract.sourceRevision -cne
        [string]$metadata.sourceRevision -or
    (($controllerContract | ConvertTo-Json -Depth 8 -Compress) -cne
        ($controllerTemplate | ConvertTo-Json -Depth 8 -Compress))) {
    throw 'Native runtime metadata does not contain the checked-in source-bound controller API contract.'
}
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
foreach ($registration in @($controllerContract.registrations)) {
    $type = [string]$registration.type
    if (-not $actualControllerTypes.Add($type) -or
        -not $expectedControllerRegistrations.Contains($type)) {
        throw "Native controller API contract has unexpected or duplicate type '$type'."
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
        throw "Native controller API type '$type' diverges from its VIIPER HID/interface implementation."
    }
}
if ($actualControllerTypes.Count -ne $expectedControllerRegistrations.Count) {
    throw 'Native controller API contract omits one or more DS4Windows controller personas.'
}

$runtimeSourcePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'DS4Windows/DS4Control/Viiper/ViiperOutDevice.cs'
$runtimeSource = Get-Content -LiteralPath $runtimeSourcePath -Raw
foreach ($type in $expectedControllerRegistrations.Keys) {
    $quotedType = '"' + [string]$type + '"'
    if ($runtimeSource.IndexOf($quotedType, [StringComparison]::Ordinal) -lt 0) {
        throw "DS4Windows runtime does not reference authoritative VIIPER controller type '$type'."
    }
}
foreach ($obsoleteType in @(
    'dualsense', 'dualsenseext', 'dualsensecombinedext',
    'dualsensecombinedmicv2', 'dualsenseaudioonlyduplexv3',
    'dualsenseaudioonlyduplexv4', 'dualsensecombinedaudioduplexv3',
    'dualsensecombinedaudioduplexv4', 'dualsenseedge',
    'dualsenseedgeext', 'dualsenseedgecombinedext',
    'dualsenseedgecombinedmicv2',
    'dualsenseedgecombinedaudioduplexv3',
    'dualsenseedgecombinedaudioduplexv4', 'dualshock4micv2'
)) {
    $quotedType = '"' + $obsoleteType + '"'
    if ($runtimeSource.IndexOf($quotedType, [StringComparison]::Ordinal) -ge 0) {
        throw "DS4Windows runtime still references obsolete/unregistered VIIPER type '$obsoleteType'."
    }
}

$roles = @($metadata.artifacts | ForEach-Object { [string]$_.role })
foreach ($role in @('broker', 'driver-helper', 'submission-manifest',
    'driver-inf', 'driver-sys', 'driver-cat')) {
    if (@($roles | Where-Object { $_ -ceq $role }).Count -ne 1) {
        throw "Native metadata must contain exactly one '$role' artifact."
    }
}
if (@($roles | Select-Object -Unique).Count -ne $roles.Count) {
    throw 'Native metadata artifact roles must be unique.'
}
$metadataPackageRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot 'viiper-native-package'))
$metadataPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($artifact in @($metadata.artifacts)) {
    $resolvedMetadataPath = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot ([string]$artifact.relativePath)))
    if ([string]$artifact.role -cnotmatch '^[a-z0-9-]+$' -or
        [string]$artifact.relativePath -notlike 'viiper-native-package/*' -or
        [IO.Path]::IsPathRooted([string]$artifact.relativePath) -or
        -not $resolvedMetadataPath.StartsWith(
            $metadataPackageRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $metadataPaths.Add($resolvedMetadataPath) -or
        [long]$artifact.length -le 0 -or
        [string]$artifact.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Invalid native artifact metadata for role '$([string]$artifact.role)'."
    }
}

if ($RequireProduction) {
    if ([string]$metadata.releaseEligibility -cne 'production') {
        throw 'Release publication is blocked: ViiperNativeRuntimeMetadata.json is not production eligible.'
    }
    if (@($roles | Where-Object { $_ -like '*local-test*' }).Count -ne 0) {
        throw 'Release publication is blocked: production metadata references local-test media.'
    }
}

$packageRoot = $metadataPackageRoot
if ($RequireProduction -or $RequirePackage -or
    (Test-Path -LiteralPath $packageRoot)) {
    $packageRootItem = Get-Item -LiteralPath $packageRoot -Force -ErrorAction Stop
    if (-not $packageRootItem.PSIsContainer -or
        ($packageRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Native package root is missing or unsafe: '$packageRoot'."
    }
    $listedPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($artifact in @($metadata.artifacts)) {
        $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ([string]$artifact.relativePath)))
        if (-not $path.StartsWith(
            $packageRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
            -not $listedPaths.Add($path)) {
            throw "Invalid or duplicate native package path '$path'."
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Native package artifact is missing: '$path'."
        }
        $item = Get-Item -LiteralPath $path -Force
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long]$artifact.length -or
            $hash -cne [string]$artifact.sha256) {
            throw "Native package artifact '$path' differs from metadata."
        }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force)
    if ($actualFiles.Count -ne $listedPaths.Count) {
        throw "Native package inventory has $($actualFiles.Count) files but metadata binds $($listedPaths.Count)."
    }
    foreach ($item in $actualFiles) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $listedPaths.Contains($item.FullName)) {
            throw "Native package has unbound or unsafe file '$($item.FullName)'."
        }
    }
}

if ($RequireProduction) {
    $manifestArtifact = @($metadata.artifacts | Where-Object {
        [string]$_.role -ceq 'submission-manifest'
    })[0]
    $catalogArtifact = @($metadata.artifacts | Where-Object {
        [string]$_.role -ceq 'driver-cat'
    })[0]
    $manifestRelativePath = [string]$manifestArtifact.relativePath
    $catalogRelativePath = [string]$catalogArtifact.relativePath
    $manifestPath = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot $manifestRelativePath))
    $catalogPath = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot $catalogRelativePath))
    $submission = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    if ($submission.releaseEligible -ne $true -or
        [string]$submission.signingRoute -notmatch 'HLK|WHCP|Microsoft' -or
        [string]$submission.sourceRevision -cne [string]$metadata.sourceRevision -or
        [string]$submission.driverPackageVersion -cne [string]$metadata.driverPackageVersion -or
        [int]$submission.driverABIMajor -ne [int]$metadata.driverAbi.major -or
        [int]$submission.driverABIMinor -ne [int]$metadata.driverAbi.minor -or
        [string]$submission.driverCapabilities -cne [string]$metadata.requiredCapabilitiesHex -or
        [string]$submission.driverBuildIdentity -cne [string]$metadata.loadedDriverBuildIdentity) {
        throw 'Release publication is blocked: the HLK/WHCP submission manifest disagrees with runtime metadata.'
    }
    $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalogPath
    if ($catalogSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $catalogSignature.SignerCertificate -or
        $catalogSignature.SignerCertificate.Subject -notmatch
            'Microsoft Windows Hardware Compatibility Publisher') {
        throw 'Release publication is blocked: ViiperUde.cat does not have the required Microsoft Windows Hardware Compatibility Publisher signature.'
    }
}

Write-Host "VIIPER native package contract passed (eligibility=$([string]$metadata.releaseEligibility), requireProduction=$([bool]$RequireProduction))."
