[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'ViiperWin11Validation.Common.psm1'
$builderPath = Join-Path $PSScriptRoot 'New-ViiperWin11ValidationBundle.ps1'
$orchestratorPath = Join-Path $PSScriptRoot 'Invoke-ViiperWin11Validation.ps1'
$readmePath = Join-Path $PSScriptRoot 'README.md'

foreach ($path in @($modulePath, $builderPath, $orchestratorPath, $readmePath)) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "Validation contract input is unsafe or empty: '$path'."
    }
}

foreach ($path in @($modulePath, $builderPath, $orchestratorPath)) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $path, [ref]$tokens, [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        throw "PowerShell parse failed for '$path': $(@($parseErrors | ForEach-Object Message) -join '; ')"
    }
}

Import-Module -Name $modulePath -Force -ErrorAction Stop
$phaseModel = @(Get-ViiperValidationPhaseModel)
$expectedPhases = @('Preflight', 'Install', 'Repair', 'RebootResume', 'ManualChecks',
    'EnableVerifier', 'VerifierResume', 'Live', 'Performance', 'LatencyMatrix',
    'CollectDumps', 'Uninstall', 'Status')
if ($phaseModel.Count -ne $expectedPhases.Count -or
    @(Compare-Object -ReferenceObject $expectedPhases `
        -DifferenceObject @($phaseModel | ForEach-Object { [string]$_.phase }) `
        -CaseSensitive).Count -ne 0) {
    throw 'Validation phase model is missing a boot-resume, repair, evidence, or uninstall phase.'
}

$builder = Get-Content -LiteralPath $builderPath -Raw -Encoding UTF8
$orchestrator = Get-Content -LiteralPath $orchestratorPath -Raw -Encoding UTF8
$module = Get-Content -LiteralPath $modulePath -Raw -Encoding UTF8
$readme = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8

foreach ($fragment in @(
    '[string]$ViiperSourceRoot', '[string]$PackageRoot',
    '[string]$DS4WindowsSourceRoot', '[string]$ExpectedViiperSourceRevision',
    '[string]$DS4WindowsArtifactRoot', '[string]$DS4WindowsExecutableRelativePath',
    '[string]$DS4WindowsLiveRunnerRelativePath', '[string]$DS4WindowsLiveHarnessRelativePath',
    '[string]$ExpectedDS4WindowsSourceRevision', '[string]$ExpectedPackageLockSHA256',
    '[string]$GitExecutable', '[string]$GoExecutable',
    "schema = 'viiper.windows11.validation-bundle/v1'",
    'endToEndValidated = $false', 'artifactRelativePath', 'executableSha256',
    'liveRunnerSha256', 'liveHarnessSha256', 'sdlBinarySha256',
    'nativeVersusUsbipAbba = $false',
    'nativeLatencySuperiority = $false', 'noWebDownload = $true',
    'Test-ViiperGitIdentity', 'Test-ViiperLocalTestPackage')) {
    if (-not $builder.Contains($fragment)) {
        throw "Bundle builder lost required fail-closed fragment '$fragment'."
    }
}

$manifestCheck = $orchestrator.IndexOf('actualManifestHash', [StringComparison]::Ordinal)
$moduleImport = $orchestrator.IndexOf('Import-Module -Name', [StringComparison]::Ordinal)
if ($manifestCheck -lt 0 -or $moduleImport -le $manifestCheck) {
    throw 'Orchestrator imports mutable bundle code before checking the out-of-band manifest digest.'
}
foreach ($fragment in @(
    "'-SignatureValidationMode', 'LocalTest'",
    "'-LocalTestCertificatePath'", "'-DisposableTestMachine'",
    "'-ManageInstalledBrokerService'", "'-RequireDriverVerifier'",
    "'-RestartRootDevice'", "'-PreflightOnly'",
    "'stdout.log'", "'stderr.log'", "'result.json'", "'command.json'",
    'viiper-localtest-performance.etl', 'crash-policy-backup.json',
    'MANUAL HOTPLUG PROMPT', 'MANUAL SLEEP PROMPT',
    'MANUAL HIBERNATE PROMPT', 'MANUAL REBOOT PROMPT',
    'MANUAL VERIFIER REBOOT PROMPT', 'MANUAL FINAL REBOOT PROMPT',
    "'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST'", "'-Operation', 'Uninstall'",
    'ds4WindowsLiveEvidence', 'latencyMatrixEvidence',
    '$ds4LiveHarnessPath',
    'Invoke-ViiperE2ELatencyMatrix.ps1',
    "'-PackageValidationMode', 'LocalTest'",
    'This is not ABBA evidence')) {
    if (-not $orchestrator.Contains($fragment)) {
        throw "Orchestrator lost required lifecycle/evidence fragment '$fragment'."
    }
}
$liveStateInitialization = $orchestrator.IndexOf('ds4WindowsLiveEvidence = $null',
    [StringComparison]::Ordinal)
$liveStateAssignment = $orchestrator.IndexOf('$script:state.ds4WindowsLiveEvidence =',
    [StringComparison]::Ordinal)
$latencyStateInitialization = $orchestrator.IndexOf('latencyMatrixEvidence = $null',
    [StringComparison]::Ordinal)
$latencyStateAssignment = $orchestrator.IndexOf('$script:state.latencyMatrixEvidence =',
    [StringComparison]::Ordinal)
if ($liveStateInitialization -lt 0 -or $liveStateInitialization -ge $liveStateAssignment -or
    $latencyStateInitialization -lt 0 -or
    $latencyStateInitialization -ge $latencyStateAssignment) {
    throw 'Strict validation state does not declare live/latency evidence before assignment.'
}
if ([regex]::Matches($orchestrator, 'MANUAL FINAL REBOOT PROMPT').Count -ne 2) {
    throw 'Direct and reboot-resumed uninstall must both emit the final reboot prompt.'
}
foreach ($fragment in @(
    "schema = 'viiper.windows11.machine-snapshot/v1'", 'activePowerPlan',
    'battery = $battery', 'lastBootUpUtc', 'hypervisorPresent',
    'virtualizationBasedSecurityStatus', 'hvciRegistry', 'testSigning',
    'pendingReboot', 'fixedDisks', 'backgroundProcesses', 'usbipComparator',
    'usbipServices', 'usbipSignedDrivers', 'usbipDeviceInstances',
    'driverStoreEnumeration', "@('/enum-drivers', '/files')",
    'publishedInf', 'hardwareIds', 'instanceId', 'signer', 'driverVersion',
    'provenance-only; no ABBA or latency-superiority claim')) {
    if (-not $module.Contains($fragment)) {
        throw "Machine provenance snapshot lost required fragment '$fragment'."
    }
}
foreach ($forbidden in @('Invoke-WebRequest', 'Invoke-RestMethod', 'Start-BitsTransfer',
    'System.Net.WebClient', 'Verb = "runas"')) {
    if (($builder + $orchestrator + $module).IndexOf($forbidden,
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Validation bundle contains forbidden download/elevation/claim path '$forbidden'."
    }
}
foreach ($fragment in @('Building the bundle makes no runtime claim',
    'DS4Windows HID/media/reconnect',
    'lower in every observed balanced cycle on that exact machine session',
    'no iid, confidence, population, or cross-machine claim',
    'No step downloads anything')) {
    if ($readme.IndexOf($fragment, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Validation guide lost scope boundary '$fragment'."
    }
}

# Exercise the package/hash model with deterministic synthetic bytes. This is
# intentionally non-elevated and performs no driver, service, registry, BCD,
# verifier, WPR, power, or device mutation.
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'viiper-validation-contract-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporaryRoot)
try {
    $payloadPath = Join-Path $temporaryRoot 'payload.bin'
    [IO.File]::WriteAllBytes($payloadPath, [byte[]](1, 2, 3, 4, 5))
    $sourceRevision = '0123456789abcdef0123456789abcdef01234567'
    $payload = Get-Item -LiteralPath $payloadPath
    $lock = [ordered]@{
        schema = 1
        sourceRevision = $sourceRevision
        driverPackageVersion = '1.2.3.4'
        driverBuildIdentity = ('a' * 64)
        testSignerCertificateSha256 = ('b' * 64)
        installerScriptSha256 = ('c' * 64)
        files = @([ordered]@{
            path = 'payload.bin'
            length = [long]$payload.Length
            sha256 = Get-ViiperSha256 -Path $payloadPath
        })
    }
    $lockPath = Join-Path $temporaryRoot 'local-test-package.lock.json'
    Write-ViiperJsonAtomic -Path $lockPath -Value $lock
    $lockHash = Get-ViiperSha256 -Path $lockPath
    $identity = Test-ViiperLocalTestPackage -PackageRoot $temporaryRoot `
        -ExpectedSourceRevision $sourceRevision -ExpectedPackageLockSHA256 $lockHash
    if ([string]$identity.lockSha256 -cne $lockHash -or [int]$identity.fileCount -ne 1) {
        throw 'Synthetic exact-package model returned the wrong identity.'
    }
    [IO.File]::WriteAllBytes($payloadPath, [byte[]](1, 2, 3, 4, 6))
    $tamperRejected = $false
    try {
        [void](Test-ViiperLocalTestPackage -PackageRoot $temporaryRoot `
            -ExpectedSourceRevision $sourceRevision -ExpectedPackageLockSHA256 $lockHash)
    }
    catch { $tamperRejected = $true }
    if (-not $tamperRejected) { throw 'Synthetic package tamper was not rejected.' }
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporary.StartsWith($systemTemporary,
        [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemporary) -like 'viiper-validation-contract-*') {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

Write-Host 'VIIPER Windows 11 validation source contract and deterministic package model passed.'
