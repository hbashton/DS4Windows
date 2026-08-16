[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'ViiperWin11Validation.Common.psm1'
$builderPath = Join-Path $PSScriptRoot 'New-ViiperWin11ValidationBundle.ps1'
$orchestratorPath = Join-Path $PSScriptRoot 'Invoke-ViiperWin11Validation.ps1'
$managerPath = Join-Path (Split-Path -Parent $PSScriptRoot) `
    'manage-viiper-native-package.ps1'
$readmePath = Join-Path $PSScriptRoot 'README.md'
$r4FixturePath = Join-Path $PSScriptRoot `
    'fixtures\viiper-r4-failed-install.json'

foreach ($path in @($modulePath, $builderPath, $orchestratorPath,
        $managerPath, $readmePath, $r4FixturePath)) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "Validation contract input is unsafe or empty: '$path'."
    }
}

foreach ($path in @($modulePath, $builderPath, $orchestratorPath, $managerPath)) {
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
$expectedPhases = @('RecoverFailedInstall', 'Preflight', 'Install', 'Repair', 'RebootResume', 'ManualChecks',
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
$manager = Get-Content -LiteralPath $managerPath -Raw -Encoding UTF8
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
    "'RecoverFailedInstall'", 'Test-ViiperFailedInstallRecoveryEvidence',
    "'-Operation', 'Recover'", "'-RecoveryAuthorizationPath'",
    "'viiper.windows11.failed-install-recovery/v1'",
    'lockedTrustCleanupReceipt', 'heldPackageThenServiceMutexes = $true',
    'programDataWasNotDeletedByOrchestrator = $true',
    'This is not ABBA evidence')) {
    if (-not $orchestrator.Contains($fragment)) {
        throw "Orchestrator lost required lifecycle/evidence fragment '$fragment'."
    }
}
$recoveryPhaseStart = $orchestrator.IndexOf(
    "if (`$Phase -ceq 'RecoverFailedInstall')", [StringComparison]::Ordinal)
$preflightPhaseStart = $orchestrator.IndexOf(
    "if (`$Phase -ceq 'Preflight')", [StringComparison]::Ordinal)
if ($recoveryPhaseStart -lt 0 -or $preflightPhaseStart -le $recoveryPhaseStart) {
    throw 'RecoverFailedInstall phase region is malformed.'
}
$recoveryPhase = $orchestrator.Substring(
    $recoveryPhaseStart, $preflightPhaseStart - $recoveryPhaseStart)
if ($recoveryPhase.Contains('Remove-NewLocalTestTrust') -or
    -not $recoveryPhase.Contains('recovery-receipt operation=native-package-recover') -or
    -not $recoveryPhase.Contains('-Resume:$recoveryResume')) {
    throw 'RecoverFailedInstall must verify native locked trust cleanup and must never delete trust itself.'
}
foreach ($fragment in @(
    "[ValidateSet('Install', 'Recover', 'Uninstall')]",
    "if (@('Install', 'Recover') -ccontains `$Operation)",
    "'native-package-recover'", "'--certificate-path'",
    "'--recovery-authorization'", "'--allow-partial-certificate-state'",
    'Assert-ExactR4FailedInstallRecoveryAuthorization',
    'Open-ExactR4FailedInstallEvidenceLeases',
    '-Resume ([bool]$RecoveryResume)',
    "-Role 'local-test-package-lock'",
    '$Authorization.currentBundleManifestSha256',
    'viiper.native.failed-install-recovery-capability/v1',
    'failed-install-recovery-capability.json',
    "'--recovery-capability'", "'--expected-recovery-capability-sha-256'",
    "'--current-package-lock-sha-256'",
    "'--current-bundle-manifest-sha-256'")) {
    if (-not $manager.Contains($fragment)) {
        throw "Package manager lost failed-install recovery fragment '$fragment'."
    }
}
$eligibilityEnd = $manager.IndexOf(
    "Unsupported VIIPER release eligibility", [StringComparison]::Ordinal)
$recoveryCommand = $manager.IndexOf("'native-package-recover'",
    [StringComparison]::Ordinal)
if ($eligibilityEnd -lt 0 -or $recoveryCommand -le $eligibilityEnd) {
    throw 'Recovery operation selection is incorrectly chained into eligibility validation.'
}
$recoveryCapabilityFunctionStart = $manager.IndexOf(
    'function New-ProtectedFailedInstallRecoveryCapability',
    [StringComparison]::Ordinal)
$recoveryCapabilityFunctionEnd = $manager.IndexOf(
    'function Remove-ProtectedStage', $recoveryCapabilityFunctionStart,
    [StringComparison]::Ordinal)
if ($recoveryCapabilityFunctionStart -lt 0 -or
    $recoveryCapabilityFunctionEnd -le $recoveryCapabilityFunctionStart) {
    throw 'Parent-bound failed-install recovery capability function is malformed.'
}
$recoveryCapabilityRegion = $manager.Substring(
    $recoveryCapabilityFunctionStart,
    $recoveryCapabilityFunctionEnd - $recoveryCapabilityFunctionStart)
$capabilityValueStart = $recoveryCapabilityRegion.IndexOf(
    '$value = [ordered]@{', [StringComparison]::Ordinal)
if ($capabilityValueStart -lt 0) {
    throw 'Recovery capability is not built from one ordered value.'
}
$recoveryCapabilityValueRegion =
    $recoveryCapabilityRegion.Substring($capabilityValueStart)
$capabilityPosition = -1
foreach ($field in @(
        "schema = 'viiper.native.failed-install-recovery-capability/v1'",
        'nonce =', 'parentPid =', 'parentCreationFileTime =', 'leasePath =',
        'sourceRevision =', 'helperSha256 =', 'certificateSha256 =',
        'recoveryAuthorizationSha256 =', 'recoveryRootAuthorizationSha256 =',
        'packageLockSha256 =', 'bundleManifestSha256 =',
        'allowPartialCertificateState =')) {
    $next = $recoveryCapabilityValueRegion.IndexOf(
        $field, [StringComparison]::Ordinal)
    if ($next -le $capabilityPosition) {
        throw "Recovery capability lost canonical ordered field '$field'."
    }
    $capabilityPosition = $next
}

foreach ($fragment in @(
        'viiper.native.local-test-trust-capability/v1',
        'viiper.native.local-test-trust-ownership/v1',
        'certificatePath = [IO.Path]::GetFullPath($CertificatePath)',
        'certificateSha256 = $CertificateSHA256.ToLowerInvariant()',
        'packageLockSha256 = $PackageLockSHA256.ToLowerInvariant()',
        'trustJournalSchema =', 'trustJournalDirectory =',
        "'--local-test-certificate-path'",
        "'--expected-local-test-certificate-sha-256'",
        "'--expected-local-test-package-lock-sha-256'")) {
    if (-not $manager.Contains($fragment)) {
        throw "Package manager lost native-owned local-test trust binding '$fragment'."
    }
}
$localCapabilityFunctionStart = $manager.IndexOf(
    'function New-ProtectedLocalTestTrustCapability',
    [StringComparison]::Ordinal)
$localCapabilityFunctionEnd = $manager.IndexOf(
    'function New-ProtectedFailedInstallRecoveryCapability',
    $localCapabilityFunctionStart, [StringComparison]::Ordinal)
if ($localCapabilityFunctionStart -lt 0 -or
    $localCapabilityFunctionEnd -le $localCapabilityFunctionStart) {
    throw 'Parent-bound local-test trust capability function is malformed.'
}
$localCapabilityRegion = $manager.Substring(
    $localCapabilityFunctionStart,
    $localCapabilityFunctionEnd - $localCapabilityFunctionStart)
$localCapabilityValueStart = $localCapabilityRegion.IndexOf(
    '$value = [ordered]@{', [StringComparison]::Ordinal)
if ($localCapabilityValueStart -lt 0) {
    throw 'Local-test trust capability is not built from one ordered value.'
}
$localCapabilityValueRegion =
    $localCapabilityRegion.Substring($localCapabilityValueStart)
$localCapabilityPosition = -1
foreach ($field in @(
        "schema = 'viiper.native.local-test-trust-capability/v1'",
        'nonce =', 'parentPid =', 'parentCreationFileTime =',
        'sourceRevision =', 'certificatePath =', 'certificateSha256 =',
        'packageLockSha256 =', 'trustJournalSchema =',
        'trustJournalDirectory =')) {
    $next = $localCapabilityValueRegion.IndexOf(
        $field, [StringComparison]::Ordinal)
    if ($next -le $localCapabilityPosition) {
        throw "Local-test trust capability lost canonical ordered field '$field'."
    }
    $localCapabilityPosition = $next
}
$managerMainIndex = $manager.IndexOf('$programDataRoot =', [StringComparison]::Ordinal)
$managerMain = $manager.Substring($managerMainIndex)
$localCapabilityCreation = $managerMain.IndexOf(
    '$trustCapability = New-ProtectedLocalTestTrustCapability',
    [StringComparison]::Ordinal)
$recoveryCapabilityCreation = $managerMain.IndexOf(
    '$recoveryCapability = New-ProtectedFailedInstallRecoveryCapability',
    [StringComparison]::Ordinal)
$recoveryEvidenceLease = $managerMain.IndexOf(
    '$script:recoveryPredecessorLeases = @(',
    [StringComparison]::Ordinal)
$uninstallTrustBinding = $managerMain.IndexOf(
    "'--local-test-certificate-path', `$certificatePath",
    [StringComparison]::Ordinal)
$joinedChild = $managerMain.IndexOf(
    '$processResult = Invoke-JoinedNativeProcess',
    [StringComparison]::Ordinal)
$recoveryEvidenceRelease = $managerMain.IndexOf(
    'Close-ExactR4FailedInstallEvidenceLeases', $joinedChild,
    [StringComparison]::Ordinal)
if ($managerMainIndex -lt 0 -or $localCapabilityCreation -lt 0 -or
    $recoveryCapabilityCreation -lt 0 -or $recoveryEvidenceLease -lt 0 -or
    $uninstallTrustBinding -lt 0 -or
    $joinedChild -le $localCapabilityCreation -or
    $joinedChild -le $recoveryCapabilityCreation -or
    $joinedChild -le $recoveryEvidenceLease -or
    $joinedChild -le $uninstallTrustBinding -or
    $recoveryEvidenceRelease -le $joinedChild) {
    throw 'Package manager lost capability/uninstall identity staging before the joined native trust owner.'
}
foreach ($forbiddenMutation in @(
        'Open-ProtectedTrustManagerLease',
        'Enter-LocalTestTrustInstallJournal',
        'Enter-LocalTestTrustUninstallJournal',
        'Complete-LocalTestTrustJournal',
        'Ensure-ExactLocalTestTrust', 'Remove-NewLocalTestTrust',
        '[Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite',
        '$store.Add(', '$store.Remove(')) {
    if ($managerMain.Contains($forbiddenMutation)) {
        throw "Package manager main flow retained parent-side trust mutation '$forbiddenMutation'."
    }
}
if ($orchestrator.Contains('Remove-NewLocalTestTrust')) {
    throw 'Validation orchestrator must not mutate trust after package-manager Uninstall releases its lease.'
}
$cleanupStart = $orchestrator.IndexOf(
    'function Complete-UninstallCleanup', [StringComparison]::Ordinal)
$cleanupEnd = $orchestrator.IndexOf(
    '# Verify every critical file before importing', $cleanupStart,
    [StringComparison]::Ordinal)
if ($cleanupStart -lt 0 -or $cleanupEnd -le $cleanupStart) {
    throw 'Validation Uninstall cleanup region is malformed.'
}
$cleanupRegion = $orchestrator.Substring($cleanupStart, $cleanupEnd - $cleanupStart)
if ($cleanupRegion.Contains('ReadWrite') -or $cleanupRegion.Contains('.Remove(') -or
    -not $cleanupRegion.Contains('verified-baseline-')) {
    throw 'Validation Uninstall cleanup must be trust-verification-only.'
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

# Preserve the exact operator-reported R4 identities and path spellings that
# this recovery phase was introduced to admit. The fixture is evidence input,
# not a substitute for the laptop's independently hashed retained files.
$r4Fixture = Get-Content -LiteralPath $r4FixturePath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$expectedR4 = [ordered]@{
    schema = 'viiper.windows11.failed-install-fixture/v1'
    predecessorEvidenceRoot = 'C:\Users\hbash\Documents\Codex\2026-08-15\the\outputs\VIIPER-Win11-9481f9d-272f6a0-r4'
    predecessorInstallStepDirectory = 'C:\Users\hbash\Documents\Codex\2026-08-15\the\outputs\VIIPER-Win11-9481f9d-272f6a0-r4\steps\20260816T034608909Z-install-27fffa05b7e544feb3c5a415ebd1f6c4'
    stateSha256 = 'e13c686a0cddcf66620940005568b3a7a9a41abb277f61977dd88994863d8cda'
    installCommandSha256 = 'c38579b1504c8851dd72317d49f4439d14b7878b4e19907ebe864c8ad986e3f7'
    installResultSha256 = '1095194f448455f746b5af92b89ae4f08f8f69a7ba9fac1d17a90d73e8a971b0'
    installStdoutSha256 = 'ca95fac3b8bd6fe7871a7f42400031f01ea946dc88786e9e9a746084144c205b'
    installStderrSha256 = '2610d56f76be3c1aea4f6b3dd4e4b38d134a1d311133ac46f389a28f8faeb520'
    bundleManifestSha256 = '765de4fe822004e97940fa66ba73602dafd68194d14fd64e20b388444cd4c247'
    viiperSourceRevision = '9481f9dbfde64af99905fa325546e50b5ea03d6e'
    ds4WindowsSourceRevision = '272f6a05f1476d5aa9c055a234e61c292d3c1556'
    packageLockSha256 = '16e08c31bb1c240a3612a6c4ddc8219b040d0e2dec5773e39f363d045113ab8c'
    certificateSha256 = '09ca0c2d4d3da29268eff59cf85b6c1347d4a28ddc098b8640381694ad74c517'
}
foreach ($entry in $expectedR4.GetEnumerator()) {
    if ([string]$r4Fixture.($entry.Key) -cne [string]$entry.Value) {
        throw "R4 failed-install fixture lost exact '$($entry.Key)'."
    }
}
if ([string]$r4Fixture.failure.phase -cne
        'install-journal-broker-image-hash' -or
    [int]$r4Fixture.failure.exitCode -ne 4 -or
    [int]$r4Fixture.failure.changed -ne 0 -or
    [int]$r4Fixture.failure.rebootRequired -ne 0 -or
    [string]$r4Fixture.failure.rollback -cne 'not-needed') {
    throw 'R4 failed-install fixture lost its exact zero-change failure tuple.'
}

# Execute the manager's pure authorization functions without invoking its main
# flow. These tests are non-elevated and never open a certificate store, driver,
# service, mutex, or protected ProgramData path.
$managerTokens = $null
$managerParseErrors = $null
$managerAst = [Management.Automation.Language.Parser]::ParseFile(
    $managerPath, [ref]$managerTokens, [ref]$managerParseErrors)
$authorizationFunctionNames = @(
    'Assert-ExactRecoveryJsonObjectProperties',
    'Assert-ExactR4FailedInstallRecoveryAuthorization',
    'Open-ExactR4FailedInstallEvidenceLeases'
)
foreach ($functionName in $authorizationFunctionNames) {
    $definitions = @($managerAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq $functionName
    }, $true))
    if ($definitions.Count -ne 1) {
        throw "Manager lost unique pure recovery function '$functionName'."
    }
    Invoke-Expression ([string]$definitions[0].Extent.Text)
}

$contractCurrentSource = ('a' * 40)
$contractCurrentLock = ('b' * 64)
$contractCurrentManifest = ('c' * 64)
$contractMachine = 'VIIPER-R4-CONTRACT'
$contractTargetSid = 'S-1-5-21-1-2-3-1001'
$contractAuthorizationValue = [ordered]@{
    schema = 'viiper.windows11.failed-install-recovery-progress/v1'
    status = 'native-attempt'
    retryPermitted = $true
    firstAuthorizedUtc = [DateTime]::UtcNow.ToString('o')
    currentBundleManifestSha256 = $contractCurrentManifest
    currentViiperSourceRevision = $contractCurrentSource
    currentPackageLockSha256 = $contractCurrentLock
    predecessor = [ordered]@{
        predecessorEvidenceRoot = [string]$r4Fixture.predecessorEvidenceRoot
        installEvidenceDirectory = [string]$r4Fixture.predecessorInstallStepDirectory
        statePath = Join-Path ([string]$r4Fixture.predecessorEvidenceRoot) `
            'state\validation-state.json'
        stateSha256 = [string]$r4Fixture.stateSha256
        commandSha256 = [string]$r4Fixture.installCommandSha256
        resultSha256 = [string]$r4Fixture.installResultSha256
        stdoutSha256 = [string]$r4Fixture.installStdoutSha256
        stderrSha256 = [string]$r4Fixture.installStderrSha256
        bundleManifestSha256 = [string]$r4Fixture.bundleManifestSha256
        viiperSourceRevision = [string]$r4Fixture.viiperSourceRevision
        ds4WindowsSourceRevision = [string]$r4Fixture.ds4WindowsSourceRevision
        packageLockSha256 = [string]$r4Fixture.packageLockSha256
    }
    predecessorCertificateSha256 = [string]$r4Fixture.certificateSha256
    machine = $contractMachine
    targetUserSid = $contractTargetSid
    trustBeforeNativeAttempt = [ordered]@{ Root = 1; TrustedPublisher = 1 }
    resume = $false
    updatedUtc = [DateTime]::UtcNow.ToString('o')
}
$contractAuthorizationText = $contractAuthorizationValue |
    ConvertTo-Json -Depth 20 -Compress
$contractAuthorization = $contractAuthorizationText |
    ConvertFrom-Json -ErrorAction Stop
$contractAuthorizationArguments = @{
    CurrentViiperSourceRevision = $contractCurrentSource
    CurrentPackageLockSHA256 = $contractCurrentLock
    CurrentBundleManifestSHA256 = $contractCurrentManifest
    CurrentCertificateSHA256 = [string]$r4Fixture.certificateSha256
    ExpectedMachine = $contractMachine
    ExpectedTargetUserSID = $contractTargetSid
    Resume = $false
}
Assert-ExactR4FailedInstallRecoveryAuthorization `
    -AuthorizationText $contractAuthorizationText `
    -Authorization $contractAuthorization @contractAuthorizationArguments
$contractResumeAuthorization = $contractAuthorizationText |
    ConvertFrom-Json -ErrorAction Stop
$contractResumeAuthorization.resume = $true
$contractResumeAuthorization.trustBeforeNativeAttempt.Root = 0
$contractResumeAuthorization | Add-Member `
    -NotePropertyName 'recoveryRootAuthorizationSha256' `
    -NotePropertyValue ('d' * 64)
$contractResumeAuthorizationText = $contractResumeAuthorization |
    ConvertTo-Json -Depth 20 -Compress
$contractResumeArguments = $contractAuthorizationArguments.Clone()
$contractResumeArguments.Resume = $true
Assert-ExactR4FailedInstallRecoveryAuthorization `
    -AuthorizationText $contractResumeAuthorizationText `
    -Authorization $contractResumeAuthorization @contractResumeArguments

function Assert-ContractRecoveryAuthorizationRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $rejected = $false
    try {
        Assert-ExactR4FailedInstallRecoveryAuthorization `
            -AuthorizationText $Text -Authorization $Value `
            @contractAuthorizationArguments
    }
    catch { $rejected = $true }
    if (-not $rejected) {
        throw "Manager admitted adversarial recovery authorization: $Label."
    }
}

$forgedPredecessor = $contractAuthorizationText |
    ConvertFrom-Json -ErrorAction Stop
$forgedPredecessor.predecessor.stateSha256 = ('f' * 64)
$forgedPredecessorText = $forgedPredecessor |
    ConvertTo-Json -Depth 20 -Compress
Assert-ContractRecoveryAuthorizationRejected -Text $forgedPredecessorText `
    -Value $forgedPredecessor -Label 'fabricated predecessor'

$missingField = $contractAuthorizationText | ConvertFrom-Json -ErrorAction Stop
$missingField.predecessor.PSObject.Properties.Remove('stdoutSha256')
$missingFieldText = $missingField | ConvertTo-Json -Depth 20 -Compress
Assert-ContractRecoveryAuthorizationRejected -Text $missingFieldText `
    -Value $missingField -Label 'missing predecessor field'

$unknownField = $contractAuthorizationText | ConvertFrom-Json -ErrorAction Stop
$unknownField | Add-Member -NotePropertyName 'unboundAuthority' `
    -NotePropertyValue ('d' * 64)
$unknownFieldText = $unknownField | ConvertTo-Json -Depth 20 -Compress
Assert-ContractRecoveryAuthorizationRejected -Text $unknownFieldText `
    -Value $unknownField -Label 'unknown field'

$duplicateFieldText = $contractAuthorizationText.Replace(
    '"status":"native-attempt"',
    '"status":"native-attempt","status":"native-attempt"')
Assert-ContractRecoveryAuthorizationRejected -Text $duplicateFieldText `
    -Value $contractAuthorization -Label 'duplicate field'

$otherMachine = $contractAuthorizationText | ConvertFrom-Json -ErrorAction Stop
$otherMachine.machine = 'OTHER-MACHINE'
$otherMachineText = $otherMachine | ConvertTo-Json -Depth 20 -Compress
Assert-ContractRecoveryAuthorizationRejected -Text $otherMachineText `
    -Value $otherMachine -Label 'other machine'

$missingEvidence = $contractAuthorizationText | ConvertFrom-Json -ErrorAction Stop
$missingEvidence.predecessor.statePath = Join-Path ([IO.Path]::GetTempPath()) `
    ('viiper-r4-missing-' + [Guid]::NewGuid().ToString('N') + '.json')
$missingEvidence.predecessor.installEvidenceDirectory = Join-Path `
    ([IO.Path]::GetTempPath()) ('viiper-r4-missing-' +
        [Guid]::NewGuid().ToString('N'))
$missingEvidenceRejected = $false
try {
    [void](Open-ExactR4FailedInstallEvidenceLeases `
        -Authorization $missingEvidence -ExpectedMachine $contractMachine `
        -ExpectedTargetUserSID $contractTargetSid)
}
catch { $missingEvidenceRejected = $true }
if (-not $missingEvidenceRejected) {
    throw 'Manager admitted R4 recovery without retained predecessor evidence.'
}

# Exercise the package/hash model with deterministic synthetic bytes. This is
# intentionally non-elevated and performs no driver, service, registry, BCD,
# verifier, WPR, power, or device mutation.
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'viiper-validation-contract-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporaryRoot)
try {
    # Exercise both publication branches. Windows PowerShell converts a literal
    # $null string argument to String.Empty, so an existing destination is the
    # required regression cut for File.Replace.
    $atomicPath = Join-Path $temporaryRoot 'atomic-state.json'
    Write-ViiperJsonAtomic -Path $atomicPath -Value ([ordered]@{ sequence = 1 })
    Write-ViiperJsonAtomic -Path $atomicPath -Value ([ordered]@{ sequence = 2 })
    $atomicState = Get-Content -LiteralPath $atomicPath -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    if ([int]$atomicState.sequence -ne 2) {
        throw 'Atomic JSON replacement did not publish the second state.'
    }
    $atomicResidue = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Force |
        Where-Object { $_.Name -like 'atomic-state.json.*.tmp' })
    if ($atomicResidue.Count -ne 0) {
        throw 'Atomic JSON replacement retained a temporary state file.'
    }
    Remove-Item -LiteralPath $atomicPath -Force

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

    # Prove that recovery admission is bound to the predecessor state plus the
    # exact failed child evidence, not merely to a caller-selected directory.
    # This remains read-only with respect to drivers, services, trust, BCD, and
    # ProgramData and therefore runs identically under Windows PowerShell 5.1
    # and PowerShell 7.
    $predecessorRoot = Join-Path $temporaryRoot 'predecessor-evidence'
    $predecessorStateDirectory = Join-Path $predecessorRoot 'state'
    $predecessorStepsDirectory = Join-Path $predecessorRoot 'steps'
    $predecessorInstallStep = Join-Path $predecessorStepsDirectory `
        '20260816T034608909Z-install-contract'
    [void][IO.Directory]::CreateDirectory($predecessorStateDirectory)
    [void][IO.Directory]::CreateDirectory($predecessorInstallStep)
    $predecessorManifestHash = ('d' * 64)
    $predecessorPackageLockHash = ('e' * 64)
    $predecessorDs4Revision = ('f' * 40)
    $predecessorTargetSid = 'S-1-5-21-1-2-3-1001'
    $predecessorMachine = 'VIIPER-CONTRACT-MACHINE'
    $predecessorStatePath = Join-Path $predecessorStateDirectory `
        'validation-state.json'
    Write-ViiperJsonAtomic -Path $predecessorStatePath -Value ([ordered]@{
        schema = 'viiper.windows11.validation-state/v1'
        machine = $predecessorMachine
        targetUserSid = $predecessorTargetSid
        bundleManifestSha256 = $predecessorManifestHash
        viiperSourceRevision = $sourceRevision
        ds4WindowsSourceRevision = $predecessorDs4Revision
        packageLockSha256 = $predecessorPackageLockHash
        lifecycle = 'transaction-failed'
        pendingTransaction = 'Install'
        trustBeforeInstall = [ordered]@{ Root = 0; TrustedPublisher = 0 }
        history = @([ordered]@{
            phase = 'Install'; lifecycle = 'transaction-failed'
        })
    })
    $predecessorCommandPath = Join-Path $predecessorInstallStep 'command.json'
    Write-ViiperJsonAtomic -Path $predecessorCommandPath -Value ([ordered]@{
        schema = 'viiper.windows11.captured-command/v1'
        name = 'install'
        arguments = @(
            '-NoProfile', '-File', 'Install-ViiperUdeLocalTest.ps1',
            '-PackageRoot', 'C:\contract\package',
            '-ExpectedSourceRevision', $sourceRevision,
            '-ExpectedPackageLockSHA256', $predecessorPackageLockHash,
            '-TargetUserSID', $predecessorTargetSid,
            '-AcknowledgeDisposableTestMachine'
        )
    })
    $predecessorResultPath = Join-Path $predecessorInstallStep 'result.json'
    Write-ViiperJsonAtomic -Path $predecessorResultPath -Value ([ordered]@{
        schema = 'viiper.windows11.captured-result/v1'
        name = 'install'
        started = $true
        exitCode = 1
        success = $false
        launchFailure = $null
        evidenceDirectory = $predecessorInstallStep
    })
    $predecessorStdoutPath = Join-Path $predecessorInstallStep 'stdout.log'
    [IO.File]::WriteAllLines($predecessorStdoutPath, [string[]]@(
        'local-test-trust store=Root action=add result=added',
        'local-test-trust store=Root action=verify-add result=present',
        'local-test-trust store=TrustedPublisher action=add result=added',
        'local-test-trust store=TrustedPublisher action=verify-add result=present',
        'result=error operation=install changed=0 rebootRequired=0 rollback=not-needed exitCode=4 phase="install-journal-broker-image-hash" win32Error=23 message="protected broker evidence differs from its immutable digest" recoveryRecord="C:\\ProgramData\\VIIPER\\UdeCx\\active-v2" recoveryRecordWritten=0 recoveryRecordPhase="journal-write" recoveryRecordWin32Error=112 recoveryRecordMessage="full" recoveryBackup="C:\\ProgramData\\VIIPER\\UdeCx\\backup" recoveryBackupRetained=1'
    ), [Text.UTF8Encoding]::new($false))
    $predecessorStderrPath = Join-Path $predecessorInstallStep 'stderr.log'
    [IO.File]::WriteAllText($predecessorStderrPath,
        'Local VIIPER driver transaction failed with exit code 4.',
        [Text.UTF8Encoding]::new($false))
    $recoveryEvidenceArguments = @{
        PredecessorEvidenceRoot = $predecessorRoot
        PredecessorInstallStepDirectory = $predecessorInstallStep
        ExpectedStateSHA256 = Get-ViiperSha256 -Path $predecessorStatePath
        ExpectedInstallCommandSHA256 = Get-ViiperSha256 -Path $predecessorCommandPath
        ExpectedInstallResultSHA256 = Get-ViiperSha256 -Path $predecessorResultPath
        ExpectedInstallStdoutSHA256 = Get-ViiperSha256 -Path $predecessorStdoutPath
        ExpectedInstallStderrSHA256 = Get-ViiperSha256 -Path $predecessorStderrPath
        ExpectedBundleManifestSHA256 = $predecessorManifestHash
        ExpectedViiperSourceRevision = $sourceRevision
        ExpectedDS4WindowsSourceRevision = $predecessorDs4Revision
        ExpectedPackageLockSHA256 = $predecessorPackageLockHash
        ExpectedMachine = $predecessorMachine
        ExpectedTargetUserSID = $predecessorTargetSid
    }
    $recoveryIdentity = Test-ViiperFailedInstallRecoveryEvidence `
        @recoveryEvidenceArguments
    if ([string]$recoveryIdentity.stateSha256 -cne
            [string]$recoveryEvidenceArguments.ExpectedStateSHA256 -or
        [string]$recoveryIdentity.stdoutSha256 -cne
            [string]$recoveryEvidenceArguments.ExpectedInstallStdoutSHA256) {
        throw 'Synthetic failed-install recovery evidence returned the wrong identity.'
    }
    Add-Content -LiteralPath $predecessorStdoutPath -Value 'tampered' `
        -Encoding UTF8
    $recoveryTamperRejected = $false
    try {
        [void](Test-ViiperFailedInstallRecoveryEvidence `
            @recoveryEvidenceArguments)
    }
    catch { $recoveryTamperRejected = $true }
    if (-not $recoveryTamperRejected) {
        throw 'Failed-install recovery admitted changed predecessor evidence.'
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
