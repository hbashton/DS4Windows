#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunnerPath,

    [Parameter(Mandatory = $true)]
    [string]$MetadataPath,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [ValidateRange(32, 512)]
    [int]$Samples = 256,

    [ValidateRange(1, 300)]
    [int]$MediaSeconds = 10,

    [switch]$AllowLocalTestPackage,

    [switch]$IUnderstandThisExercisesLiveControllers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IUnderstandThisExercisesLiveControllers) {
    throw 'Explicit -IUnderstandThisExercisesLiveControllers consent is required.'
}
if (-not [Environment]::Is64BitOperatingSystem -or
    [Environment]::OSVersion.Version.Build -lt 22000) {
    throw 'The DS4Windows VIIPER live gate requires 64-bit Windows 11.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this laptop validation from an elevated PowerShell session.'
}

$commonModule = Join-Path $PSScriptRoot 'ViiperLaptopValidation.Common.psm1'
Import-Module -Name $commonModule -Force -ErrorAction Stop

$runnerBinding = $null
$metadataBinding = $null
$evidenceBinding = $null
try {
    $runnerBinding = New-ViiperLockedFileBinding -Path $RunnerPath `
        -Role 'runner executable'
    if ([IO.Path]::GetFileName($runnerBinding.Path) -cne
            'DS4Windows.ViiperLiveValidation.exe') {
        throw 'RunnerPath must retain the exact DS4Windows.ViiperLiveValidation.exe apphost name.'
    }
    $metadataBinding = New-ViiperLockedFileBinding -Path $MetadataPath `
        -Role 'runtime metadata'
    $artifactDirectory = Get-Item -LiteralPath $ArtifactRoot -Force `
        -ErrorAction Stop
    if (-not ($artifactDirectory -is [IO.DirectoryInfo]) -or
        ($artifactDirectory.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ArtifactRoot must be an existing non-reparse directory: '$ArtifactRoot'."
    }

    $metadataObject = Read-ViiperStrictEvidence -Binding $metadataBinding
    $eligibility = [string]$metadataObject.releaseEligibility
    if ($eligibility -cnotin @('production', 'local-test-evidence-only')) {
        throw "Runtime metadata has unsupported releaseEligibility '$eligibility'."
    }
    $localTest = $eligibility -ceq 'local-test-evidence-only'
    if ($localTest -and -not $AllowLocalTestPackage) {
        throw 'This is a local-test-only package. Re-run with -AllowLocalTestPackage only on the disposable test laptop/VM.'
    }
    if (-not $localTest -and $AllowLocalTestPackage) {
        throw '-AllowLocalTestPackage contradicts production-eligible metadata.'
    }

    $output = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $output) {
        throw "Refusing to overwrite existing evidence or input: '$output'."
    }
    $outputDirectory = Split-Path -Parent $output
    if ([string]::IsNullOrWhiteSpace($outputDirectory) -or
        -not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        throw "The evidence output directory must already exist: '$outputDirectory'."
    }

    $nonceBytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($nonceBytes)
    }
    finally {
        $rng.Dispose()
    }
    $nonce = -join ($nonceBytes | ForEach-Object { $_.ToString('x2') })
    $nonceAscii = [Text.Encoding]::ASCII.GetBytes($nonce)
    $nonceHasher = [Security.Cryptography.SHA256]::Create()
    try {
        $nonceSha256 = -join ($nonceHasher.ComputeHash($nonceAscii) |
            ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $nonceHasher.Dispose()
        [Array]::Clear($nonceAscii, 0, $nonceAscii.Length)
        [Array]::Clear($nonceBytes, 0, $nonceBytes.Length)
    }

    Assert-ViiperLockedFileBinding -Binding $runnerBinding
    Assert-ViiperLockedFileBinding -Binding $metadataBinding
    $nonceVariable = 'DS4WINDOWS_VIIPER_LIVE_VALIDATION_NONCE'
    $localVariable = 'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST'
    $oldNonce = [Environment]::GetEnvironmentVariable(
        $nonceVariable, 'Process')
    $oldLocal = [Environment]::GetEnvironmentVariable(
        $localVariable, 'Process')
    $launchWindowStartUtc = [DateTimeOffset]::UtcNow
    try {
        [Environment]::SetEnvironmentVariable(
            $nonceVariable, $nonce, 'Process')
        [Environment]::SetEnvironmentVariable(
            $localVariable,
            $(if ($localTest) { '1' } else { $null }), 'Process')
        $runnerStdout = @(& $runnerBinding.Path `
            --nonce $nonce `
            --output $output `
            --metadata $metadataBinding.Path `
            --artifact-root $artifactDirectory.FullName `
            --samples ([string]$Samples) `
            --media-seconds ([string]$MediaSeconds))
        $runnerExitCode = $LASTEXITCODE
        $launchWindowEndUtc = [DateTimeOffset]::UtcNow
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $nonceVariable, $oldNonce, 'Process')
        [Environment]::SetEnvironmentVariable(
            $localVariable, $oldLocal, 'Process')
        $nonce = $null
    }

    if ($runnerExitCode -ne 0) {
        throw "DS4Windows VIIPER live validation failed with exit code $runnerExitCode. Preserve the new evidence file and stdout for diagnosis."
    }
    $stdoutReceipt = New-ViiperStdoutEvidenceReceipt `
        -Lines @($runnerStdout)
    $evidenceBinding = New-ViiperLockedFileBinding -Path $output `
        -Role 'final live-validation evidence'
    Assert-ViiperStdoutEvidenceContinuity -Receipt $stdoutReceipt `
        -EvidenceBinding $evidenceBinding
    $evidence = Read-ViiperStrictEvidence -Binding $evidenceBinding
    Assert-ViiperLiveEvidence -Evidence $evidence `
        -ExpectedNonceSha256 $nonceSha256 `
        -ExpectedOutputPath $output `
        -RunnerBinding $runnerBinding `
        -MetadataBinding $metadataBinding `
        -LaunchWindowStartUtc $launchWindowStartUtc `
        -LaunchWindowEndUtc $launchWindowEndUtc
    Assert-ViiperLockedFileBinding -Binding $runnerBinding
    Assert-ViiperLockedFileBinding -Binding $metadataBinding
    Assert-ViiperLockedFileBinding -Binding $evidenceBinding

    [pscustomobject]@{
        status = 'pass'
        evidencePath = $output
        evidenceSha256 = $evidenceBinding.Sha256
        childEvidenceLength = $stdoutReceipt.Length
        childEvidenceSha256 = $stdoutReceipt.Sha256
        consentNonceSha256 = $nonceSha256
        runnerPath = $runnerBinding.Path
        runnerLength = $runnerBinding.Length
        runnerSha256 = $runnerBinding.Sha256
        metadataPath = $metadataBinding.Path
        metadataLength = $metadataBinding.Length
        metadataSha256 = $metadataBinding.Sha256
        installedBrokerSha256 =
            [string]$evidence.bindings.installedRuntime.broker.runningImage.sha256
        driverStoreInfSha256 =
            [string]$evidence.bindings.installedRuntime.driver.driverStoreInf.sha256
        driverStoreCatSha256 =
            [string]$evidence.bindings.installedRuntime.driver.driverStoreCat.sha256
        driverStoreSysSha256 =
            [string]$evidence.bindings.installedRuntime.driver.driverStoreSys.sha256
        loadedDriverSha256 =
            [string]$evidence.bindings.installedRuntime.driver.loadedServiceImage.sha256
    }
}
finally {
    Close-ViiperLockedFileBinding -Binding $evidenceBinding
    Close-ViiperLockedFileBinding -Binding $metadataBinding
    Close-ViiperLockedFileBinding -Binding $runnerBinding
}
