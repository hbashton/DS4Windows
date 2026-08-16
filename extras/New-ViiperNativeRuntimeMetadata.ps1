[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [ValidateSet('production', 'local-test-evidence-only')]
    [string]$ReleaseEligibility = 'production',
    [string]$ControllerContractPath =
        (Join-Path $PSScriptRoot 'ViiperControllerApiContract.json'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'ViiperNativeRuntimeMetadata.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $PackageRoot -ErrorAction Stop).Path.TrimEnd('\')
$rootItem = Get-Item -LiteralPath $root -Force
if (-not $rootItem.PSIsContainer -or
    ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PackageRoot must be an ordinary directory.'
}

$manifestPath = Join-Path $root 'submission-manifest.json'
$submission = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$sourceRevision = [string]$submission.sourceRevision
$packageVersion = [string]$submission.driverPackageVersion
$abiMajor = [int]$submission.driverABIMajor
$abiMinor = [int]$submission.driverABIMinor
$capabilitiesHex = ([string]$submission.driverCapabilities).ToLowerInvariant()
$buildIdentity = ([string]$submission.driverBuildIdentity).ToLowerInvariant()
if ($sourceRevision -cnotmatch '^[0-9a-f]{40}$|^[0-9a-f]{64}$' -or
    $packageVersion -cnotmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
    $abiMajor -le 0 -or $abiMajor -gt 65535 -or
    $abiMinor -lt 0 -or $abiMinor -gt 65535 -or
    $capabilitiesHex -cnotmatch '^0x[0-9a-f]{8}$' -or
    $buildIdentity -cnotmatch '^[0-9a-f]{64}$') {
    throw 'The source-bound submission manifest has invalid native identity fields.'
}
$capabilities = [Convert]::ToUInt32($capabilitiesHex.Substring(2), 16)
if ($capabilities -eq 0) {
    throw 'The source-bound submission manifest advertises no native capabilities.'
}

$controllerContractFile = (Resolve-Path -LiteralPath $ControllerContractPath -ErrorAction Stop).Path
$controllerContractItem = Get-Item -LiteralPath $controllerContractFile -Force
if ($controllerContractItem.PSIsContainer -or
    ($controllerContractItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'ControllerContractPath must be an ordinary JSON file.'
}
$controllerContractInput = Get-Content -LiteralPath $controllerContractFile -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
if ([int]$controllerContractInput.schemaVersion -ne 1 -or
    [string]$controllerContractInput.sourceRevision -cne $sourceRevision -or
    [string]::IsNullOrWhiteSpace(
        [string]$controllerContractInput.implementation)) {
    throw 'The controller API contract is not bound to this VIIPER source revision.'
}

$registrations = [Collections.Generic.List[object]]::new()
$seenTypes = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$seenPersonas = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($registration in @($controllerContractInput.registrations)) {
    $type = [string]$registration.type
    $persona = [string]$registration.persona
    $defaultVid = [string]$registration.defaultVid
    $defaultPid = [string]$registration.defaultPid
    $ds4WindowsPid = [string]$registration.ds4WindowsPid
    $interfaceProfile = [string]$registration.interfaceProfile
    $streamProtocol = [string]$registration.streamProtocol
    if ($type -cnotmatch '^[a-z0-9]+$' -or -not $seenTypes.Add($type) -or
        $persona -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
        $defaultVid -cnotmatch '^0x[0-9a-f]{4}$' -or
        $defaultPid -cnotmatch '^0x[0-9a-f]{4}$' -or
        $ds4WindowsPid -cnotmatch '^0x[0-9a-f]{4}$' -or
        $interfaceProfile -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
        $streamProtocol -cnotmatch '^(?:fixed|framed-v[1-9][0-9]*)$') {
        throw "Invalid or duplicate controller registration '$type'."
    }
    [void]$seenPersonas.Add($persona)
    $registrations.Add([ordered]@{
        type = $type
        persona = $persona
        defaultVid = $defaultVid
        defaultPid = $defaultPid
        ds4WindowsPid = $ds4WindowsPid
        interfaceProfile = $interfaceProfile
        streamProtocol = $streamProtocol
    })
}
foreach ($requiredPersona in @('xbox360', 'dualshock4', 'dualsense',
    'dualsense-edge', 'switch2-pro')) {
    if (-not $seenPersonas.Contains($requiredPersona)) {
        throw "Controller API contract omits required DS4Windows persona '$requiredPersona'."
    }
}
$controllerContract = [ordered]@{
    schemaVersion = 1
    sourceRevision = $sourceRevision
    implementation = [string]$controllerContractInput.implementation
    registrations = $registrations
}

if ($ReleaseEligibility -ceq 'production') {
    if ($submission.releaseEligible -ne $true -or
        [string]$submission.signingRoute -notmatch 'HLK|WHCP|Microsoft') {
        throw 'Production metadata requires a release-eligible HLK/WHCP submission manifest.'
    }
    if (Test-Path -LiteralPath (Join-Path $root 'ViiperUdeTest.cer')) {
        throw 'Production package roots must not contain the local-test certificate.'
    }
} elseif ($submission.releaseEligible -ne $false -or
    [string]$submission.signingRoute -cne 'LocalTest') {
    throw 'Local-test metadata requires the non-release-eligible LocalTest submission manifest.'
}

$definitions = @(
    @{ role = 'broker'; path = 'viiper.exe'; required = $true },
    @{ role = 'driver-helper'; path = 'ViiperUdeCtl.exe'; required = $true },
    @{ role = 'media-probe'; path = 'ViiperUdeMediaProbe.exe'; required = $false },
    @{ role = 'input-probe'; path = 'ViiperUdeInputProbe.exe'; required = $false },
    @{ role = 'live-probe-manifest'; path = 'ViiperUdeLiveProbes.manifest.json'; required = $false },
    @{ role = 'submission-manifest'; path = 'submission-manifest.json'; required = $true },
    @{ role = 'driver-inf'; path = 'driver/ViiperUde.inf'; required = $true },
    @{ role = 'driver-sys'; path = 'driver/ViiperUde.sys'; required = $true },
    @{ role = 'driver-cat'; path = 'driver/ViiperUde.cat'; required = $true },
    @{ role = 'driver-pdb'; path = 'signed-package/ViiperUde.pdb'; required = $false },
    @{ role = 'signed-driver-inf'; path = 'signed-package/ViiperUde.inf'; required = $false },
    @{ role = 'signed-driver-sys'; path = 'signed-package/ViiperUde.sys'; required = $false },
    @{ role = 'signed-driver-cat'; path = 'signed-package/ViiperUde.cat'; required = $false }
)
if ($ReleaseEligibility -ceq 'local-test-evidence-only') {
    $definitions += @(
        @{
            role = 'local-test-package-lock'
            path = 'local-test-package.lock.json'
            required = $true
        },
        @{
            role = 'local-test-certificate-evidence'
            path = 'ViiperUdeTest.cer'
            required = $true
        }
    )
}

$artifacts = [Collections.Generic.List[object]]::new()
foreach ($definition in $definitions) {
    $relative = [string]$definition.path
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if ([bool]$definition.required) {
            throw "Required native package artifact is missing: '$path'."
        }
        continue
    }
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Native package artifacts must not be reparse points: '$path'."
    }
    $artifacts.Add([ordered]@{
        role = [string]$definition.role
        relativePath = 'viiper-native-package/' + $relative.Replace('\', '/')
        length = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

if ($ReleaseEligibility -ceq 'local-test-evidence-only') {
    $lockPath = Join-Path $root 'local-test-package.lock.json'
    $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    $certificateArtifact = @($artifacts | Where-Object {
        [string]$_.role -ceq 'local-test-certificate-evidence'
    })
    if ([int]$lock.schema -ne 1 -or
        [string]$lock.sourceRevision -cne $sourceRevision -or
        [string]$lock.driverPackageVersion -cne $packageVersion -or
        [string]$lock.driverBuildIdentity -cne $buildIdentity -or
        $certificateArtifact.Count -ne 1 -or
        [string]$lock.testSignerCertificateSha256 -cne
            [string]$certificateArtifact[0].sha256) {
        throw 'Local-test package lock disagrees with the source-bound submission metadata.'
    }
    $lockFiles = @($lock.files)
    $boundArtifacts = @($artifacts | Where-Object {
        [string]$_.role -cne 'local-test-package-lock'
    })
    if ($lockFiles.Count -ne $boundArtifacts.Count) {
        throw 'Local-test package lock does not bind the complete package inventory.'
    }
    foreach ($artifact in $boundArtifacts) {
        $relativePath = ([string]$artifact.relativePath).Substring(
            'viiper-native-package/'.Length)
        $matches = @($lockFiles | Where-Object {
            [string]$_.path -ceq $relativePath
        })
        if ($matches.Count -ne 1 -or
            [long]$matches[0].length -ne [long]$artifact.length -or
            [string]$matches[0].sha256 -cne [string]$artifact.sha256) {
            throw "Local-test package lock disagrees with '$relativePath'."
        }
    }
}

$metadata = [ordered]@{
    schemaVersion = 1
    releaseEligibility = $ReleaseEligibility
    localTestOptInEnvironment = 'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST'
    sourceRevision = $sourceRevision
    driverPackageVersion = $packageVersion
    driverAbi = [ordered]@{ major = $abiMajor; minor = $abiMinor }
    requiredCapabilities = $capabilities
    requiredCapabilitiesHex = $capabilitiesHex
    loadedDriverBuildIdentity = $buildIdentity
    productionSigningRoute = 'HLK/WHCP dashboard signing'
    managedBroker = [ordered]@{
        serviceName = 'VIIPERNativeBroker'
        serviceAccount = 'LocalSystem'
        startMode = 'automatic'
        transport = 'native-ude'
        apiHost = '127.0.0.1'
        apiPort = 3242
        credentialPath = '%ProgramData%/VIIPER/viiper.key.txt'
    }
    controllerApiContract = $controllerContract
    artifacts = $artifacts
}

$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $output
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$temporary = Join-Path $outputDirectory (
    '.' + [IO.Path]::GetFileName($output) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $json = $metadata | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $output -Force
} finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
Write-Host "Wrote exact VIIPER runtime metadata: $output"
