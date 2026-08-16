[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ViiperSourceRoot,
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$DS4WindowsSourceRoot,
    [Parameter(Mandatory = $true)][string]$DS4WindowsArtifactRoot,
    [Parameter(Mandatory = $true)][string]$DS4WindowsExecutableRelativePath,
    [Parameter(Mandatory = $true)][string]$DS4WindowsLiveRunnerRelativePath,
    [Parameter(Mandatory = $true)][string]$DS4WindowsLiveHarnessRelativePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedViiperSourceRevision,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedDS4WindowsSourceRevision,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedPackageLockSHA256,
    [Parameter(Mandatory = $true)][string]$GitExecutable,
    [Parameter(Mandatory = $true)][string]$GoExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'ViiperWin11Validation.Common.psm1'
Import-Module -Name $modulePath -Force -ErrorAction Stop

function Assert-NoReparseEntries {
    param([Parameter(Mandatory = $true)][string]$Root, [string]$Label)

    $unsafe = @(Get-ChildItem -LiteralPath $Root -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($unsafe.Count -ne 0) {
        throw "$Label contains reparse entries; first unsafe path is '$($unsafe[0].FullName)'."
    }
}

function Copy-DirectoryContents {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)

    [void][IO.Directory]::CreateDirectory($Destination)
    Get-ChildItem -LiteralPath $Source -Force -ErrorAction Stop | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
    }
}

function New-BoundFileEntry {
    param([Parameter(Mandatory = $true)][string]$BundleRoot, [Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Join-Path $BundleRoot $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "Bundle-bound file is unsafe or empty: '$RelativePath'."
    }
    return [ordered]@{
        path = $RelativePath
        length = [long]$item.Length
        sha256 = Get-ViiperSha256 -Path $item.FullName
    }
}

$viiperRevision = $ExpectedViiperSourceRevision.ToLowerInvariant()
$ds4Revision = $ExpectedDS4WindowsSourceRevision.ToLowerInvariant()
$packageLockSha256 = $ExpectedPackageLockSHA256.ToLowerInvariant()
$gitPath = Resolve-ViiperRegularFile -Path $GitExecutable -Label 'Git executable'
$goPath = Resolve-ViiperRegularFile -Path $GoExecutable -Label 'Go executable'
if ((Split-Path -Leaf $gitPath) -ine 'git.exe' -or
    (Split-Path -Leaf $goPath) -ine 'go.exe') {
    throw 'Explicit source-bound tools must retain the canonical names git.exe and go.exe.'
}
$gitVersionOutput = @(& $gitPath --version 2>&1)
if ($LASTEXITCODE -ne 0 -or $gitVersionOutput.Count -ne 1) {
    throw "Explicit Git executable did not return one version line.`n$($gitVersionOutput -join [Environment]::NewLine)"
}
$oldGoToolchain = [Environment]::GetEnvironmentVariable('GOTOOLCHAIN', 'Process')
$goVersionExitCode = -1
$goVersionOutput = @()
try {
    $env:GOTOOLCHAIN = 'local'
    $goVersionOutput = @(& $goPath version 2>&1)
    $goVersionExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable('GOTOOLCHAIN', $oldGoToolchain, 'Process')
}
if ($goVersionExitCode -ne 0 -or $goVersionOutput.Count -ne 1) {
    throw "Explicit Go executable did not return one version line.`n$($goVersionOutput -join [Environment]::NewLine)"
}
$viiperIdentity = Test-ViiperGitIdentity -RepositoryRoot $ViiperSourceRoot `
    -ExpectedRevision $viiperRevision -GitExecutable $gitPath -Label 'VIIPER source checkout'
$ds4Identity = Test-ViiperGitIdentity -RepositoryRoot $DS4WindowsSourceRoot `
    -ExpectedRevision $ds4Revision -GitExecutable $gitPath -Label 'DS4Windows source checkout'
$packageIdentity = Test-ViiperLocalTestPackage -PackageRoot $PackageRoot `
    -ExpectedSourceRevision $viiperRevision -ExpectedPackageLockSHA256 $packageLockSha256
$ds4Artifact = Resolve-ViiperSafeDirectory -Path $DS4WindowsArtifactRoot `
    -Label 'DS4Windows published artifact root'
if ($DS4WindowsExecutableRelativePath -cnotmatch
    '^[A-Za-z0-9_.-]+(?:[\\/][A-Za-z0-9_.-]+)*$') {
    throw "DS4Windows executable relative path is unsafe: '$DS4WindowsExecutableRelativePath'."
}
$ds4EntrypointRelative = $DS4WindowsExecutableRelativePath.Replace('\', '/')
$ds4Entrypoint = Resolve-ViiperRegularFile `
    -Path (Join-Path $ds4Artifact $DS4WindowsExecutableRelativePath) `
    -Label 'DS4Windows published executable'
$ds4LiveRunnerRelative = $DS4WindowsLiveRunnerRelativePath.Replace('\', '/')
$ds4LiveHarnessRelative = $DS4WindowsLiveHarnessRelativePath.Replace('\', '/')
foreach ($relative in @($ds4LiveRunnerRelative, $ds4LiveHarnessRelative)) {
    if ($relative -cnotmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$') {
        throw "DS4Windows live-validation relative path is unsafe: '$relative'."
    }
}
$ds4LiveRunner = Resolve-ViiperRegularFile `
    -Path (Join-Path $ds4Artifact $DS4WindowsLiveRunnerRelativePath) `
    -Label 'DS4Windows live-validation runner'
$ds4LiveHarness = Resolve-ViiperRegularFile `
    -Path (Join-Path $ds4Artifact $DS4WindowsLiveHarnessRelativePath) `
    -Label 'DS4Windows live-validation harness'
if ((Split-Path -Leaf $ds4LiveRunner) -cne 'DS4Windows.ViiperLiveValidation.exe' -or
    (Split-Path -Leaf $ds4LiveHarness) -cne 'Invoke-ViiperDs4WindowsLaptopValidation.ps1') {
    throw 'DS4Windows live-validation inputs do not retain their canonical names.'
}
$sdlBinaryRelative = '_testing/e2e/deps/SDL/build/Debug/SDL3.dll'
$sdlBinary = Resolve-ViiperRegularFile `
    -Path (Join-Path $viiperIdentity.root $sdlBinaryRelative.Replace('/', '\')) `
    -Label 'source-built SDL3 latency binary'

$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Refusing to overwrite validation bundle output '$output'."
}
$outputParent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "Validation bundle parent must already exist: '$outputParent'."
}
$outputParent = Resolve-ViiperSafeDirectory -Path $outputParent -Label 'Validation bundle parent'
foreach ($sourceRoot in @($viiperIdentity.root, $ds4Identity.root, $packageIdentity.root,
        $ds4Artifact)) {
    $sourcePrefix = $sourceRoot.TrimEnd('\') + '\'
    if ($output.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $output -ieq $sourceRoot) {
        throw "Validation bundle output must be outside every bound input: '$output'."
    }
}

Assert-NoReparseEntries -Root $viiperIdentity.root -Label 'VIIPER source checkout'
Assert-NoReparseEntries -Root $packageIdentity.root -Label 'Local-test package'
Assert-NoReparseEntries -Root $ds4Artifact -Label 'DS4Windows published artifact'
$ds4ArtifactFiles = @(Get-ChildItem -LiteralPath $ds4Artifact -File -Recurse -Force)
if ($ds4ArtifactFiles.Count -eq 0) {
    throw 'DS4Windows published artifact root is empty.'
}

$installerSource = Join-Path $viiperIdentity.root 'native\udecx\tools\Install-ViiperUdeLocalTest.ps1'
$packageLock = Get-Content -LiteralPath $packageIdentity.lockPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$installerHash = Get-ViiperSha256 -Path $installerSource
if ($installerHash -cne [string]$packageLock.installerScriptSha256) {
    throw "The bound VIIPER checkout's local-test installer is not the one bound by the package lock."
}

$ds4ManagerSource = Join-Path $ds4Identity.root 'extras\manage-viiper-native-package.ps1'
$ds4MetadataSource = Join-Path $ds4Identity.root 'extras\ViiperNativeRuntimeMetadata.json'
$ds4ManagerSource = Resolve-ViiperRegularFile -Path $ds4ManagerSource -Label 'DS4Windows package manager'
$ds4MetadataSource = Resolve-ViiperRegularFile -Path $ds4MetadataSource -Label 'DS4Windows runtime metadata'
$metadata = Get-Content -LiteralPath $ds4MetadataSource -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
if ([int]$metadata.schemaVersion -ne 1 -or
    [string]$metadata.releaseEligibility -cne 'local-test-evidence-only' -or
    [string]$metadata.sourceRevision -cne $viiperRevision -or
    [string]$metadata.loadedDriverBuildIdentity -cne $packageIdentity.driverBuildIdentity) {
    throw 'DS4Windows runtime metadata is not bound to the explicit local-test VIIPER identity.'
}
$metadataLock = @($metadata.artifacts | Where-Object {
    [string]$_.role -ceq 'local-test-package-lock'
})
if ($metadataLock.Count -ne 1 -or
    [string]$metadataLock[0].sha256 -cne $packageLockSha256) {
    throw 'DS4Windows runtime metadata is not bound to the explicit package lock digest.'
}

$runtimeScriptSource = Resolve-ViiperRegularFile `
    -Path (Join-Path $PSScriptRoot 'Invoke-ViiperWin11Validation.ps1') `
    -Label 'Validation orchestrator'
$readmeSource = Resolve-ViiperRegularFile -Path (Join-Path $PSScriptRoot 'README.md') `
    -Label 'Validation bundle guide'
$commonSource = Resolve-ViiperRegularFile -Path $modulePath -Label 'Validation common module'

$staging = Join-Path $outputParent (
    ([IO.Path]::GetFileName($output)) + '.incomplete.' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($staging)
try {
    Copy-Item -LiteralPath $runtimeScriptSource -Destination (Join-Path $staging 'Invoke-ViiperWin11Validation.ps1')
    Copy-Item -LiteralPath $commonSource -Destination (Join-Path $staging 'ViiperWin11Validation.Common.psm1')
    Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $staging 'README.md')

    $ds4ArtifactDestination = Join-Path $staging 'ds4-artifact'
    Copy-DirectoryContents -Source $ds4Artifact -Destination $ds4ArtifactDestination
    [void](Resolve-ViiperRegularFile `
        -Path (Join-Path $ds4ArtifactDestination $DS4WindowsExecutableRelativePath) `
        -Label 'Copied DS4Windows published executable')
    $copiedDs4Files = @(Get-ChildItem -LiteralPath $ds4ArtifactDestination -File -Recurse -Force)
    if ($copiedDs4Files.Count -ne $ds4ArtifactFiles.Count) {
        throw 'Copied DS4Windows artifact has a different file count from its explicit input.'
    }
    foreach ($sourceFile in $ds4ArtifactFiles) {
        $relative = $sourceFile.FullName.Substring($ds4Artifact.TrimEnd('\').Length + 1)
        $copiedFile = Get-Item -LiteralPath (Join-Path $ds4ArtifactDestination $relative) `
            -Force -ErrorAction Stop
        if ($copiedFile.PSIsContainer -or $copiedFile.Length -ne $sourceFile.Length -or
            (Get-ViiperSha256 -Path $copiedFile.FullName) -cne
                (Get-ViiperSha256 -Path $sourceFile.FullName)) {
            throw "Copied DS4Windows artifact differs at '$relative'."
        }
    }

    $viiperDestination = Join-Path $staging 'viiper-source'
    Copy-DirectoryContents -Source $viiperIdentity.root -Destination $viiperDestination
    [void](Test-ViiperGitIdentity -RepositoryRoot $viiperDestination `
        -ExpectedRevision $viiperRevision -GitExecutable $gitPath -Label 'Copied VIIPER source checkout')

    $managerDestination = Join-Path $staging 'ds4-manager'
    [void][IO.Directory]::CreateDirectory($managerDestination)
    Copy-Item -LiteralPath $ds4ManagerSource -Destination (Join-Path $managerDestination 'manage-viiper-native-package.ps1')
    Copy-Item -LiteralPath $ds4MetadataSource -Destination (Join-Path $managerDestination 'ViiperNativeRuntimeMetadata.json')
    Copy-DirectoryContents -Source $packageIdentity.root `
        -Destination (Join-Path $managerDestination 'viiper-native-package')
    [void](Test-ViiperLocalTestPackage `
        -PackageRoot (Join-Path $managerDestination 'viiper-native-package') `
        -ExpectedSourceRevision $viiperRevision `
        -ExpectedPackageLockSHA256 $packageLockSha256)

    $criticalPaths = @(
        'Invoke-ViiperWin11Validation.ps1',
        'ViiperWin11Validation.Common.psm1',
        'README.md',
        'ds4-manager/manage-viiper-native-package.ps1',
        'ds4-manager/ViiperNativeRuntimeMetadata.json',
        'ds4-manager/viiper-native-package/local-test-package.lock.json',
        'viiper-source/native/udecx/tools/Install-ViiperUdeLocalTest.ps1',
        'viiper-source/native/udecx/tools/Invoke-ViiperUdeLiveValidation.ps1',
        'viiper-source/native/udecx/tools/Invoke-ViiperUdePerformanceValidation.ps1',
        'viiper-source/_testing/e2e/scripts/Invoke-ViiperE2ELatencyGate.ps1',
        'viiper-source/_testing/e2e/scripts/Invoke-ViiperE2ELatencyMatrix.ps1',
        'viiper-source/_testing/e2e/cmd/verifylatencymatrix/main.go',
        'viiper-source/native/udecx/tools/Enable-ViiperUdeVerifierForNextBoot.ps1',
        'viiper-source/native/udecx/tools/Set-ViiperCrashDiagnostics.ps1',
        'viiper-source/native/udecx/tools/Copy-ViiperCrashDumps.ps1',
        'viiper-source/native/udecx/tools/Test-ViiperUdeSignedPackage.ps1',
        'viiper-source/_testing/e2e/deps/SDL/build/Debug/SDL3.dll'
    )
    $boundFiles = @($criticalPaths | ForEach-Object {
        New-BoundFileEntry -BundleRoot $staging -RelativePath $_
    })
    $boundFiles += @(Get-ChildItem -LiteralPath $ds4ArtifactDestination -File -Recurse -Force |
        Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($staging.TrimEnd('\').Length + 1).Replace('\', '/')
            New-BoundFileEntry -BundleRoot $staging -RelativePath $relative
        })
    $manifest = [ordered]@{
        schema = 'viiper.windows11.validation-bundle/v1'
        createdUtc = [DateTime]::UtcNow.ToString('o')
        localTestOnly = $true
        disposableWindows11MachineRequired = $true
        noWebDownload = $true
        viiper = [ordered]@{
            sourceRevision = $viiperRevision
            repositoryRelativePath = 'viiper-source'
            submodules = @($viiperIdentity.submodules)
        }
        package = [ordered]@{
            relativePath = 'ds4-manager/viiper-native-package'
            lockSha256 = $packageLockSha256
            driverPackageVersion = $packageIdentity.driverPackageVersion
            driverBuildIdentity = $packageIdentity.driverBuildIdentity
        }
        ds4Windows = [ordered]@{
            sourceRevision = $ds4Revision
            packageManagerRelativePath = 'ds4-manager/manage-viiper-native-package.ps1'
            runtimeMetadataRelativePath = 'ds4-manager/ViiperNativeRuntimeMetadata.json'
            artifactRelativePath = 'ds4-artifact'
            executableRelativePath = $ds4EntrypointRelative
            executableSha256 = Get-ViiperSha256 -Path $ds4Entrypoint
            liveRunnerRelativePath = $ds4LiveRunnerRelative
            liveRunnerSha256 = Get-ViiperSha256 -Path $ds4LiveRunner
            liveHarnessRelativePath = $ds4LiveHarnessRelative
            liveHarnessSha256 = Get-ViiperSha256 -Path $ds4LiveHarness
            artifactFileCount = $ds4ArtifactFiles.Count
            integrationEvidenceOnly = $true
            endToEndValidated = $false
        }
        latency = [ordered]@{
            sdlBinaryRelativePath = $sdlBinaryRelative
            sdlBinarySha256 = Get-ViiperSha256 -Path $sdlBinary
            packageValidationMode = 'LocalTest'
            cyclesPerPriority = 8
            samplePairsPerTransition = 10000
            claim = 'descriptive for this exact source-bound machine session only'
        }
        tools = [ordered]@{
            git = [ordered]@{
                sha256 = Get-ViiperSha256 -Path $gitPath
                version = ([string]$gitVersionOutput[0]).Trim()
            }
            go = [ordered]@{
                sha256 = Get-ViiperSha256 -Path $goPath
                version = ([string]$goVersionOutput[0]).Trim()
            }
        }
        claims = [ordered]@{
            ds4WindowsEndToEnd = $false
            nativeVersusUsbipAbba = $false
            nativeLatencySuperiority = $false
        }
        files = $boundFiles
    }
    $manifestPath = Join-Path $staging 'bundle-manifest.json'
    Write-ViiperJsonAtomic -Path $manifestPath -Value $manifest
    $manifestSha256 = Get-ViiperSha256 -Path $manifestPath
    [IO.Directory]::Move($staging, $output)
    $staging = $null

    $receipt = [ordered]@{
        result = 'success'
        bundle = $output
        manifest = Join-Path $output 'bundle-manifest.json'
        manifestSha256 = $manifestSha256
        viiperSourceRevision = $viiperRevision
        ds4WindowsSourceRevision = $ds4Revision
        packageLockSha256 = $packageLockSha256
        next = 'Transfer the bundle plus this out-of-band manifest SHA-256 to the disposable Windows 11 laptop.'
    }
    $receipt | ConvertTo-Json -Depth 6
}
catch {
    if ($null -ne $staging) {
        Write-Warning "Incomplete bundle was retained for forensic inspection at '$staging'."
    }
    throw
}
