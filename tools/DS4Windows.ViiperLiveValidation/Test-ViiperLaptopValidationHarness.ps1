#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ViiperLaptopValidation.Common.psm1') `
    -Force -ErrorAction Stop

function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action)
    try {
        & $Action
    }
    catch {
        return
    }
    throw 'Expected the adversarial harness case to fail closed.'
}

function New-EvidenceFileBinding {
    param($Binding)
    return [pscustomobject]@{
        role = $Binding.Role
        path = $Binding.Path
        length = $Binding.Length
        sha256 = $Binding.Sha256
        expectedLength = $null
        expectedSha256 = $null
        exactMatch = $true
    }
}

function New-InstalledFileBinding {
    return [pscustomobject]@{
        role = 'installed'
        path = 'C:\Windows\bound.bin'
        length = 1
        sha256 = ('a' * 64)
        expectedLength = 1
        expectedSha256 = ('a' * 64)
        exactMatch = $true
    }
}

$root = Join-Path ([IO.Path]::GetTempPath()) `
    ('DS4Windows-harness-contract-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$runnerBinding = $null
$metadataBinding = $null
$jsonBinding = $null
try {
    $runnerPath = Join-Path $root 'DS4Windows.ViiperLiveValidation.exe'
    $metadataPath = Join-Path $root 'ViiperNativeRuntimeMetadata.json'
    [IO.File]::WriteAllBytes($runnerPath, [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllText($metadataPath,
        '{"releaseEligibility":"production"}',
        [Text.UTF8Encoding]::new($false))
    $runnerBinding = New-ViiperLockedFileBinding $runnerPath 'runner executable'
    $metadataBinding = New-ViiperLockedFileBinding $metadataPath 'runtime metadata'
    Assert-ViiperLockedFileBinding $runnerBinding
    Assert-ViiperLockedFileBinding $metadataBinding
    Assert-Throws { [IO.File]::WriteAllBytes($runnerPath, [byte[]](9)) }

    foreach ($malformed in @(
            '{"schemaVersion":2,"schemaVersion":2}',
            '{"schemaVersion":2,"\u0073chemaVersion":2}',
            '{"schemaVersion":2,}')) {
        $jsonPath = Join-Path $root ([guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::WriteAllText($jsonPath, $malformed,
            [Text.UTF8Encoding]::new($false))
        $jsonBinding = New-ViiperLockedFileBinding $jsonPath 'adversarial JSON'
        Assert-Throws { Read-ViiperStrictEvidence $jsonBinding }
        Close-ViiperLockedFileBinding $jsonBinding
        $jsonBinding = $null
    }

    $childLine = '{"schemaVersion":2,"status":"pass"}'
    $childReceipt = New-ViiperStdoutEvidenceReceipt -Lines @($childLine)
    $successorPath = Join-Path $root 'successor-evidence.json'
    [IO.File]::WriteAllText($successorPath, $childLine + "`n",
        [Text.UTF8Encoding]::new($false))
    $jsonBinding = New-ViiperLockedFileBinding $successorPath `
        'success evidence'
    Assert-ViiperStdoutEvidenceContinuity $childReceipt $jsonBinding
    Close-ViiperLockedFileBinding $jsonBinding
    $jsonBinding = $null

    # Reproduce the child-close/parent-open gap with a real successor file.
    [IO.File]::WriteAllText($successorPath,
        '{"schemaVersion":2,"status":"forged"}' + "`n",
        [Text.UTF8Encoding]::new($false))
    $jsonBinding = New-ViiperLockedFileBinding $successorPath `
        'replacement evidence'
    Assert-Throws {
        Assert-ViiperStdoutEvidenceContinuity $childReceipt $jsonBinding
    }
    Close-ViiperLockedFileBinding $jsonBinding
    $jsonBinding = $null

    $now = [DateTimeOffset]::UtcNow
    $installedFile = New-InstalledFileBinding
    $evidence = [pscustomobject]@{
        schemaVersion = 2
        tool = 'DS4Windows.ViiperLiveValidation'
        status = 'pass'
        finalized = $true
        startedUtc = $now.ToString('O')
        endedUtc = $now.AddSeconds(1).ToString('O')
        outputPath = (Join-Path $root 'fresh-evidence.json')
        consentNonceSha256 = ('b' * 64)
        failureStage = $null
        failures = @()
        controllers = @(
            [pscustomobject]@{ status = 'pass' },
            [pscustomobject]@{ status = 'pass' },
            [pscustomobject]@{ status = 'pass' })
        bindings = [pscustomobject]@{
            runnerExecutable = New-EvidenceFileBinding $runnerBinding
            metadata = New-EvidenceFileBinding $metadataBinding
            packageArtifacts = @(
                [pscustomobject]@{ exactMatch = $true })
            inputProbeExecution = [pscustomobject]@{
                allLaunchesExact = $true; launchCount = 1
            }
            mediaProbeExecution = [pscustomobject]@{
                allLaunchesExact = $true; launchCount = 1
            }
            installedRuntime = [pscustomobject]@{
                exactPackageMatch = $true
                broker = [pscustomobject]@{
                    exactPackageMatch = $true
                    configuredImageIsRunningImage = $true
                    state = 'running'
                    runningImage = $installedFile
                }
                driver = [pscustomobject]@{
                    exactPackageMatch = $true
                    started = $true
                    serviceState = 'running'
                    problemCode = 0
                    publishedInf = $installedFile
                    driverStoreInf = $installedFile
                    driverStoreCat = $installedFile
                    driverStoreSys = $installedFile
                    loadedServiceImage = $installedFile
                }
            }
        }
    }
    $assert = @{
        Evidence = $evidence
        ExpectedNonceSha256 = ('b' * 64)
        ExpectedOutputPath = $evidence.outputPath
        RunnerBinding = $runnerBinding
        MetadataBinding = $metadataBinding
        LaunchWindowStartUtc = $now.AddSeconds(-1)
        LaunchWindowEndUtc = $now.AddSeconds(2)
    }
    Assert-ViiperLiveEvidence @assert

    $evidence.consentNonceSha256 = ('c' * 64)
    Assert-Throws { Assert-ViiperLiveEvidence @assert }
    $evidence.consentNonceSha256 = ('b' * 64)
    $evidence.startedUtc = $now.AddHours(-1).ToString('O')
    Assert-Throws { Assert-ViiperLiveEvidence @assert }
    $evidence.startedUtc = $now.ToString('O')
    $evidence.bindings.runnerExecutable.sha256 = ('d' * 64)
    Assert-Throws { Assert-ViiperLiveEvidence @assert }

    [pscustomobject]@{
        status = 'pass'
        runnerSha256 = $runnerBinding.Sha256
        metadataSha256 = $metadataBinding.Sha256
    } | ConvertTo-Json -Compress
}
finally {
    Close-ViiperLockedFileBinding $jsonBinding
    Close-ViiperLockedFileBinding $metadataBinding
    Close-ViiperLockedFileBinding $runnerBinding
    if ($root.StartsWith([IO.Path]::GetTempPath(),
            [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($root).StartsWith(
            'DS4Windows-harness-contract-',
            [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($root, $true)
    }
}
