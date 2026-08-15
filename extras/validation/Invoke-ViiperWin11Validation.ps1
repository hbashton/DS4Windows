[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'Preflight', 'Install', 'Repair', 'RebootResume',
        'ManualChecks', 'EnableVerifier', 'VerifierResume', 'Live',
        'Performance', 'LatencyMatrix', 'CollectDumps', 'Uninstall')]
    [string]$Phase,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedBundleManifestSHA256,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^S-1-5-21-(?:[0-9]+-){3}[0-9]+$')]
    [string]$TargetUserSID,
    [Parameter(Mandatory = $true)][string]$GitExecutable,
    [Parameter(Mandatory = $true)][string]$GoExecutable,
    [ValidateRange(1, 100)][int]$Iterations = 3,
    [ValidateRange(1, 300)][int]$MediaDurationSeconds = 180,
    [switch]$AcknowledgePhysicalHotplug,
    [switch]$AcknowledgeSleepWake,
    [switch]$AcknowledgeHibernateWake,
    [switch]$AcknowledgeManualReboot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$bundleRoot = (Resolve-Path -LiteralPath $PSScriptRoot -ErrorAction Stop).Path
$manifestPath = Join-Path $bundleRoot 'bundle-manifest.json'
$manifestItem = Get-Item -LiteralPath $manifestPath -Force -ErrorAction Stop
if ($manifestItem.PSIsContainer -or
    ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $manifestItem.Length -le 0) {
    throw "Bundle manifest is not a non-empty regular file: '$manifestPath'."
}
$actualManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualManifestHash -cne $ExpectedBundleManifestSHA256.ToLowerInvariant()) {
    throw "Bundle manifest SHA-256 '$actualManifestHash' does not match the explicit out-of-band digest."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
if ([string]$manifest.schema -cne 'viiper.windows11.validation-bundle/v1' -or
    $manifest.localTestOnly -ne $true -or $manifest.noWebDownload -ne $true -or
    $manifest.ds4Windows.integrationEvidenceOnly -ne $true -or
    $manifest.ds4Windows.endToEndValidated -ne $false -or
    $manifest.claims.ds4WindowsEndToEnd -ne $false -or
    $manifest.claims.nativeVersusUsbipAbba -ne $false -or
    $manifest.claims.nativeLatencySuperiority -ne $false) {
    throw 'Bundle manifest does not have the exact local-test, no-download, no-claim contract.'
}

$preImportFiles = @(
    'Invoke-ViiperWin11Validation.ps1',
    'ViiperWin11Validation.Common.psm1'
)
foreach ($relative in $preImportFiles) {
    $matches = @($manifest.files | Where-Object { [string]$_.path -ceq $relative })
    if ($matches.Count -ne 1) { throw "Bundle manifest must bind exactly one '$relative'." }
    $path = Join-Path $bundleRoot $relative
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -ne [long]$matches[0].length -or
        (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            [string]$matches[0].sha256) {
        throw "Bundle bootstrap file '$relative' does not match the out-of-band-bound manifest."
    }
}
Import-Module -Name (Join-Path $bundleRoot 'ViiperWin11Validation.Common.psm1') -Force -ErrorAction Stop

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This disposable-machine validation phase requires an elevated 64-bit PowerShell session.'
    }
    if (-not [Environment]::Is64BitProcess -or -not [Environment]::Is64BitOperatingSystem) {
        throw 'Validation requires 64-bit PowerShell on 64-bit Windows 11.'
    }
}

function ConvertTo-WindowsCommandLineArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
        }
        else {
            if ($backslashes -gt 0) { [void]$builder.Append(('\' * $backslashes)) }
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) { [void]$builder.Append(('\' * ($backslashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function New-StepDirectory {
    param([Parameter(Mandatory = $true)][string]$Name)

    $safe = $Name.ToLowerInvariant() -replace '[^a-z0-9-]', '-'
    $path = Join-Path (Join-Path $script:evidence 'steps') (
        ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')) + '-' + $safe + '-' +
        [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Invoke-CapturedPowerShell {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$StepDirectory
    )

    $scriptFile = Resolve-ViiperRegularFile -Path $ScriptPath -Label "$Name script"
    if ([string]::IsNullOrWhiteSpace($StepDirectory)) {
        $StepDirectory = New-StepDirectory -Name $Name
    }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $powershell = Resolve-ViiperRegularFile -Path $powershell -Label 'Inbox Windows PowerShell'
    $childArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy',
        'Bypass', '-File', $scriptFile) + $Arguments
    $command = [ordered]@{
        schema = 'viiper.windows11.captured-command/v1'
        name = $Name
        startedUtc = [DateTime]::UtcNow.ToString('o')
        executable = [ordered]@{
            path = $powershell
            sha256 = Get-ViiperSha256 -Path $powershell
        }
        script = [ordered]@{
            path = $scriptFile
            sha256 = Get-ViiperSha256 -Path $scriptFile
        }
        arguments = $childArguments
    }
    Write-ViiperJsonAtomic -Path (Join-Path $StepDirectory 'command.json') -Value $command

    $joinedArguments = (($childArguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Value ([string]$_)
    }) -join ' ')
    $stdoutPath = Join-Path $StepDirectory 'stdout.log'
    $stderrPath = Join-Path $StepDirectory 'stderr.log'
    $started = $false
    $exitCode = -1
    $failure = $null
    try {
        Write-Host "Starting $Name; live output is retained in '$StepDirectory'."
        $process = Start-Process -FilePath $powershell -ArgumentList $joinedArguments `
            -WorkingDirectory $StepDirectory -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
            -ErrorAction Stop
        $started = $true
        $exitCode = $process.ExitCode
    }
    catch {
        $failure = $_.Exception.Message
    }
    finally {
        if (-not (Test-Path -LiteralPath $stdoutPath -PathType Leaf)) {
            [IO.File]::WriteAllText($stdoutPath, '', [Text.UTF8Encoding]::new($false))
        }
        if (-not (Test-Path -LiteralPath $stderrPath -PathType Leaf)) {
            [IO.File]::WriteAllText($stderrPath, '', [Text.UTF8Encoding]::new($false))
        }
    }
    $result = [ordered]@{
        schema = 'viiper.windows11.captured-result/v1'
        name = $Name
        completedUtc = [DateTime]::UtcNow.ToString('o')
        started = $started
        exitCode = [int]$exitCode
        success = ($started -and $exitCode -eq 0 -and $null -eq $failure)
        launchFailure = $failure
        evidenceDirectory = $StepDirectory
    }
    Write-ViiperJsonAtomic -Path (Join-Path $StepDirectory 'result.json') -Value $result
    return [pscustomobject]$result
}

function Assert-CapturedSuccess {
    param([Parameter(Mandatory = $true)]$Result, [Parameter(Mandatory = $true)][string]$Label)
    if (-not [bool]$Result.success) {
        throw "$Label failed with exit code '$($Result.exitCode)'. Evidence: '$($Result.evidenceDirectory)'."
    }
}

function Save-State {
    param(
        [Parameter(Mandatory = $true)][string]$Lifecycle,
        [string]$PendingTransaction,
        [string]$RequiredBootChangeFrom,
        [string]$Note
    )

    $script:state.lifecycle = $Lifecycle
    $script:state.pendingTransaction = $PendingTransaction
    $script:state.requiredBootChangeFrom = $RequiredBootChangeFrom
    $script:state.lastUpdatedUtc = [DateTime]::UtcNow.ToString('o')
    $entry = [ordered]@{
        utc = $script:state.lastUpdatedUtc
        phase = $Phase
        lifecycle = $Lifecycle
        bootIdentity = Get-ViiperBootIdentity
        note = $Note
    }
    $script:state.history = @($script:state.history) + @($entry)
    Write-ViiperJsonAtomic -Path $script:statePath -Value $script:state
}

function Assert-Lifecycle {
    param([Parameter(Mandatory = $true)][string[]]$Allowed)
    if ($Allowed -cnotcontains [string]$script:state.lifecycle) {
        throw "Phase '$Phase' is not valid from lifecycle '$($script:state.lifecycle)'; expected: $($Allowed -join ', ')."
    }
}

function Get-ExactLocalTestTrustCount {
    param([string]$StoreName, [string]$CertificatePath)

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName, [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $matches = $null
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint, $false)
        $expected = [Convert]::ToBase64String($certificate.RawData)
        $exact = @($matches | Where-Object {
            [Convert]::ToBase64String($_.RawData) -ceq $expected
        })
        if ($matches.Count -ne $exact.Count -or $exact.Count -gt 1) {
            throw "Certificate collision in LocalMachine\$StoreName."
        }
        return [int]$exact.Count
    }
    finally {
        if ($null -ne $matches) { foreach ($match in $matches) { $match.Dispose() } }
        $store.Close()
        $certificate.Dispose()
    }
}

function Remove-NewLocalTestTrust {
    param([string]$StoreName, [string]$CertificatePath, [int]$PreflightCount)

    if ($PreflightCount -ne 0) { return 'preserved-preexisting' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName, [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $matches = $null
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint, $false)
        $expected = [Convert]::ToBase64String($certificate.RawData)
        $exact = @($matches | Where-Object {
            [Convert]::ToBase64String($_.RawData) -ceq $expected
        })
        if ($matches.Count -ne $exact.Count -or $exact.Count -gt 1) {
            throw "Certificate collision in LocalMachine\$StoreName during uninstall cleanup."
        }
        if ($exact.Count -eq 1) { $store.Remove($exact[0]) }
    }
    finally {
        if ($null -ne $matches) { foreach ($match in $matches) { $match.Dispose() } }
        $store.Close()
        $certificate.Dispose()
    }
    if ((Get-ExactLocalTestTrustCount -StoreName $StoreName -CertificatePath $CertificatePath) -ne 0) {
        throw "Exact local-test certificate remained in LocalMachine\$StoreName."
    }
    return 'removed-or-absent'
}

function Invoke-InstallTransaction {
    param([string]$OperationName)

    $installer = Join-Path $script:viiperRoot 'native\udecx\tools\Install-ViiperUdeLocalTest.ps1'
    return Invoke-CapturedPowerShell -Name $OperationName -ScriptPath $installer -Arguments @(
        '-PackageRoot', $script:packageRoot,
        '-ExpectedSourceRevision', [string]$manifest.viiper.sourceRevision,
        '-ExpectedPackageLockSHA256', [string]$manifest.package.lockSha256,
        '-TargetUserSID', $TargetUserSID,
        '-AcknowledgeDisposableTestMachine'
    )
}

function Invoke-UninstallTransaction {
    $manager = Join-Path $bundleRoot ([string]$manifest.ds4Windows.packageManagerRelativePath).Replace('/', '\')
    $priorOptIn = [Environment]::GetEnvironmentVariable('DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST', 'Process')
    try {
        $env:DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST = '1'
        return Invoke-CapturedPowerShell -Name 'uninstall' -ScriptPath $manager -Arguments @(
            '-Operation', 'Uninstall', '-TargetUserSID', $TargetUserSID,
            '-AllowLocalTest', '-AcknowledgeDisposableTestMachine'
        )
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST', $priorOptIn, 'Process')
    }
}

function Complete-UninstallCleanup {
    $crashState = Join-Path $script:evidence 'state\crash-policy-backup.json'
    if (Test-Path -LiteralPath $crashState -PathType Leaf) {
        $restore = Invoke-CapturedPowerShell -Name 'restore-crash-diagnostics' `
            -ScriptPath (Join-Path $script:viiperRoot 'native\udecx\tools\Set-ViiperCrashDiagnostics.ps1') `
            -Arguments @('-Mode', 'Restore', '-StatePath', $crashState)
        Assert-CapturedSuccess -Result $restore -Label 'Crash-diagnostic policy restore'
    }
    $cleanup = [ordered]@{}
    $cleanup.Root = Remove-NewLocalTestTrust -StoreName 'Root' `
        -CertificatePath $script:certificatePath -PreflightCount ([int]$script:state.trustBeforeInstall.Root)
    $cleanup.TrustedPublisher = Remove-NewLocalTestTrust -StoreName 'TrustedPublisher' `
        -CertificatePath $script:certificatePath `
        -PreflightCount ([int]$script:state.trustBeforeInstall.TrustedPublisher)
    Write-ViiperJsonAtomic -Path (Join-Path $script:evidence 'state\uninstall-cleanup.json') -Value $cleanup
}

# Verify every critical file before importing or invoking any bound tool.
$boundPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in @($manifest.files)) {
    $relative = [string]$entry.path
    if ($relative -cnotmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or
        -not $boundPaths.Add($relative) -or [long]$entry.length -le 0 -or
        [string]$entry.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Bundle manifest has an unsafe or duplicate file entry '$relative'."
    }
    $path = Join-Path $bundleRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -ne [long]$entry.length -or
        (Get-ViiperSha256 -Path $item.FullName) -cne [string]$entry.sha256) {
        throw "Bundle-bound file '$relative' does not match the manifest."
    }
}
$gitPath = Resolve-ViiperRegularFile -Path $GitExecutable -Label 'Git executable'
$goPath = Resolve-ViiperRegularFile -Path $GoExecutable -Label 'Go executable'
if ((Split-Path -Leaf $gitPath) -ine 'git.exe' -or
    (Split-Path -Leaf $goPath) -ine 'go.exe') {
    throw 'Explicit source-bound tools must retain the canonical names git.exe and go.exe.'
}
if ((Get-ViiperSha256 -Path $gitPath) -cne [string]$manifest.tools.git.sha256 -or
    (Get-ViiperSha256 -Path $goPath) -cne [string]$manifest.tools.go.sha256) {
    throw 'The explicit Git or Go executable differs from the bundle-bound tool identity.'
}
$gitVersionOutput = @(& $gitPath --version 2>&1)
if ($LASTEXITCODE -ne 0 -or $gitVersionOutput.Count -ne 1 -or
    ([string]$gitVersionOutput[0]).Trim() -cne [string]$manifest.tools.git.version) {
    throw 'The explicit Git executable version output differs from the bundle identity.'
}
$oldIdentityGoToolchain = [Environment]::GetEnvironmentVariable('GOTOOLCHAIN', 'Process')
$goVersionExitCode = -1
$goVersionOutput = @()
try {
    $env:GOTOOLCHAIN = 'local'
    $goVersionOutput = @(& $goPath version 2>&1)
    $goVersionExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable('GOTOOLCHAIN', $oldIdentityGoToolchain, 'Process')
}
if ($goVersionExitCode -ne 0 -or $goVersionOutput.Count -ne 1 -or
    ([string]$goVersionOutput[0]).Trim() -cne [string]$manifest.tools.go.version) {
    throw 'The explicit Go executable version output differs from the bundle identity.'
}
$viiperRelative = [string]$manifest.viiper.repositoryRelativePath
$packageRelative = [string]$manifest.package.relativePath
$managerRelative = [string]$manifest.ds4Windows.packageManagerRelativePath
$runtimeMetadataRelative = [string]$manifest.ds4Windows.runtimeMetadataRelativePath
$ds4ArtifactRelative = [string]$manifest.ds4Windows.artifactRelativePath
$ds4ExecutableRelative = [string]$manifest.ds4Windows.executableRelativePath
$ds4LiveRunnerRelative = [string]$manifest.ds4Windows.liveRunnerRelativePath
$ds4LiveHarnessRelative = [string]$manifest.ds4Windows.liveHarnessRelativePath
$sdlBinaryRelative = [string]$manifest.latency.sdlBinaryRelativePath
foreach ($relativeInput in @($viiperRelative, $packageRelative, $managerRelative,
        $runtimeMetadataRelative, $ds4ArtifactRelative, $ds4ExecutableRelative, $ds4LiveRunnerRelative,
        $ds4LiveHarnessRelative, $sdlBinaryRelative)) {
    if ($relativeInput -cnotmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$') {
        throw "Bundle manifest has unsafe relative input '$relativeInput'."
    }
}
$viiperRoot = Join-Path $bundleRoot $viiperRelative.Replace('/', '\')
[void](Test-ViiperGitIdentity -RepositoryRoot $viiperRoot `
    -ExpectedRevision ([string]$manifest.viiper.sourceRevision) `
    -GitExecutable $gitPath -Label 'Bundled VIIPER source checkout')
$packageRoot = Join-Path $bundleRoot $packageRelative.Replace('/', '\')
[void](Test-ViiperLocalTestPackage -PackageRoot $packageRoot `
    -ExpectedSourceRevision ([string]$manifest.viiper.sourceRevision) `
    -ExpectedPackageLockSHA256 ([string]$manifest.package.lockSha256))
$certificatePath = Join-Path $packageRoot 'ViiperUdeTest.cer'
$runtimeMetadataPath = Resolve-ViiperRegularFile `
    -Path (Join-Path $bundleRoot $runtimeMetadataRelative.Replace('/', '\')) `
    -Label 'Bound DS4Windows runtime metadata'
$runnerArtifactRoot = Split-Path -Parent $runtimeMetadataPath
$ds4ArtifactRoot = Join-Path $bundleRoot $ds4ArtifactRelative.Replace('/', '\')
$ds4ArtifactRoot = Resolve-ViiperSafeDirectory -Path $ds4ArtifactRoot `
    -Label 'Bound DS4Windows published artifact'
$unsafeDs4Directories = @(Get-ChildItem -LiteralPath $ds4ArtifactRoot -Directory -Recurse -Force |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
if ($unsafeDs4Directories.Count -ne 0) {
    throw "DS4Windows artifact contains reparse directory '$($unsafeDs4Directories[0].FullName)'."
}
$actualDs4Files = @(Get-ChildItem -LiteralPath $ds4ArtifactRoot -File -Recurse -Force)
$expectedDs4Files = @($manifest.files | Where-Object {
    ([string]$_.path).StartsWith($ds4ArtifactRelative.TrimEnd('/') + '/',
        [StringComparison]::Ordinal)
})
if ($actualDs4Files.Count -ne [int]$manifest.ds4Windows.artifactFileCount -or
    $expectedDs4Files.Count -ne $actualDs4Files.Count) {
    throw 'DS4Windows artifact has missing or extra files relative to its exact bundle inventory.'
}
foreach ($file in $actualDs4Files) {
    $relative = $file.FullName.Substring($bundleRoot.TrimEnd('\').Length + 1).Replace('\', '/')
    if (-not $boundPaths.Contains($relative)) {
        throw "DS4Windows artifact contains unbound file '$relative'."
    }
}
$ds4ExecutablePath = Resolve-ViiperRegularFile `
    -Path (Join-Path $ds4ArtifactRoot $ds4ExecutableRelative.Replace('/', '\')) `
    -Label 'Bound DS4Windows executable'
if ((Get-ViiperSha256 -Path $ds4ExecutablePath) -cne
    [string]$manifest.ds4Windows.executableSha256) {
    throw 'DS4Windows executable differs from its exact bundle identity.'
}
$ds4LiveRunnerPath = Resolve-ViiperRegularFile `
    -Path (Join-Path $ds4ArtifactRoot $ds4LiveRunnerRelative.Replace('/', '\')) `
    -Label 'DS4Windows live-validation runner'
$ds4LiveHarnessPath = Resolve-ViiperRegularFile `
    -Path (Join-Path $ds4ArtifactRoot $ds4LiveHarnessRelative.Replace('/', '\')) `
    -Label 'DS4Windows live-validation harness'
$sdlBinaryPath = Resolve-ViiperRegularFile `
    -Path (Join-Path $viiperRoot $sdlBinaryRelative.Replace('/', '\')) `
    -Label 'source-built SDL3 latency binary'
if ((Get-ViiperSha256 -Path $ds4LiveRunnerPath) -cne
        [string]$manifest.ds4Windows.liveRunnerSha256 -or
    (Get-ViiperSha256 -Path $ds4LiveHarnessPath) -cne
        [string]$manifest.ds4Windows.liveHarnessSha256 -or
    (Get-ViiperSha256 -Path $sdlBinaryPath) -cne
        [string]$manifest.latency.sdlBinarySha256) {
    throw 'DS4Windows live-runner or SDL latency input differs from its exact bundle identity.'
}

$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$bundlePrefix = $bundleRoot.TrimEnd('\') + '\'
if ($evidence -ieq $bundleRoot -or
    $evidence.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceRoot must be outside the immutable source-bound validation bundle.'
}
if (-not (Test-Path -LiteralPath $evidence)) {
    [void][IO.Directory]::CreateDirectory($evidence)
}
$evidence = Resolve-ViiperSafeDirectory -Path $evidence -Label 'Evidence root'
[void][IO.Directory]::CreateDirectory((Join-Path $evidence 'steps'))
[void][IO.Directory]::CreateDirectory((Join-Path $evidence 'state'))
$statePath = Join-Path $evidence 'state\validation-state.json'
$state = $null
if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ([string]$state.schema -cne 'viiper.windows11.validation-state/v1' -or
        [string]$state.bundleManifestSha256 -cne $actualManifestHash -or
        [string]$state.targetUserSid -cne $TargetUserSID -or
        [string]$state.machine -cne $env:COMPUTERNAME -or
        [string]$state.viiperSourceRevision -cne [string]$manifest.viiper.sourceRevision -or
        [string]$state.ds4WindowsSourceRevision -cne [string]$manifest.ds4Windows.sourceRevision -or
        [string]$state.packageLockSha256 -cne [string]$manifest.package.lockSha256 -or
        [string]$state.ds4WindowsExecutableSha256 -cne
            [string]$manifest.ds4Windows.executableSha256) {
        throw 'Existing evidence state belongs to a different bundle, user, machine, or schema.'
    }
}
elseif ($Phase -notin @('Preflight', 'Status')) {
    throw "Phase '$Phase' requires a successful Preflight state in '$statePath'."
}

try {
if ($Phase -ceq 'Status') {
    [ordered]@{
        result = 'status'
        bundleManifestSha256 = $actualManifestHash
        viiperSourceRevision = [string]$manifest.viiper.sourceRevision
        ds4WindowsSourceRevision = [string]$manifest.ds4Windows.sourceRevision
        ds4WindowsExecutableSha256 = [string]$manifest.ds4Windows.executableSha256
        packageLockSha256 = [string]$manifest.package.lockSha256
        ds4WindowsExecutable = [ordered]@{
            path = $ds4ExecutablePath
            sha256 = [string]$manifest.ds4Windows.executableSha256
        }
        tools = $manifest.tools
        claims = $manifest.claims
        state = $state
        phaseModel = Get-ViiperValidationPhaseModel
    } | ConvertTo-Json -Depth 20
    return
}

Assert-Administrator
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($currentIdentity -cne $TargetUserSID) {
    throw "Elevated session user SID '$currentIdentity' differs from TargetUserSID '$TargetUserSID'. Use same-account elevation."
}

if ($Phase -ceq 'Preflight') {
    if ($null -ne $state) { throw 'Preflight refuses to overwrite existing validation state.' }
    $snapshot = Get-ViiperMachineEvidenceSnapshot
    $snapshotPath = Join-Path (New-StepDirectory -Name 'machine-preflight') 'machine-snapshot.json'
    Write-ViiperJsonAtomic -Path $snapshotPath -Value $snapshot
    if ($null -eq $snapshot.operatingSystem -or
        [uint32]$snapshot.operatingSystem.productType -ne 1 -or
        [int]$snapshot.operatingSystem.buildNumber -lt 22000 -or
        -not [Environment]::Is64BitOperatingSystem) {
        throw "Preflight requires a 64-bit Windows 11 client. Snapshot: '$snapshotPath'."
    }
    if ($null -eq $snapshot.bootConfiguration -or
        $snapshot.bootConfiguration.testSigning -ne $true) {
        Write-Warning 'MANUAL REBOOT PROMPT: enable TESTSIGNING from an elevated prompt, reboot this disposable laptop, then rerun Preflight with the identical bundle digest and paths.'
        throw "Local-test TESTSIGNING is not active. Snapshot: '$snapshotPath'."
    }
    if ($null -ne $snapshot.pendingReboot -and
        ($snapshot.pendingReboot.componentBasedServicing -or
         $snapshot.pendingReboot.windowsUpdate -or
         $snapshot.pendingReboot.pendingFileRenameOperations -or
         $snapshot.pendingReboot.pendingComputerRename)) {
        Write-Warning 'MANUAL REBOOT PROMPT: Windows reports a pending reboot. Restart, rerun Preflight, and preserve the same inputs.'
        throw "Preflight refuses a pending-reboot baseline. Snapshot: '$snapshotPath'."
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($evidence))
    if ([uint64]$drive.AvailableFreeSpace -lt 10GB) {
        throw 'Evidence volume needs at least 10 GB free before lifecycle, ETL, and dump collection.'
    }
    $preflight = Invoke-CapturedPowerShell -Name 'package-preflight' `
        -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Install-ViiperUdeLocalTest.ps1') `
        -Arguments @(
            '-PackageRoot', $packageRoot,
            '-ExpectedSourceRevision', [string]$manifest.viiper.sourceRevision,
            '-ExpectedPackageLockSHA256', [string]$manifest.package.lockSha256,
            '-TargetUserSID', $TargetUserSID,
            '-AcknowledgeDisposableTestMachine', '-PreflightOnly'
        )
    Assert-CapturedSuccess -Result $preflight -Label 'Exact local-test package preflight'
    $state = [pscustomobject][ordered]@{
        schema = 'viiper.windows11.validation-state/v1'
        machine = $env:COMPUTERNAME
        targetUserSid = $TargetUserSID
        bundleManifestSha256 = $actualManifestHash
        viiperSourceRevision = [string]$manifest.viiper.sourceRevision
        ds4WindowsSourceRevision = [string]$manifest.ds4Windows.sourceRevision
        packageLockSha256 = [string]$manifest.package.lockSha256
        ds4WindowsExecutableSha256 = [string]$manifest.ds4Windows.executableSha256
        createdUtc = [DateTime]::UtcNow.ToString('o')
        lastUpdatedUtc = [DateTime]::UtcNow.ToString('o')
        lifecycle = 'new'
        pendingTransaction = $null
        requiredBootChangeFrom = $null
        ds4WindowsLiveEvidence = $null
        latencyMatrixEvidence = $null
        trustBeforeInstall = [ordered]@{
            Root = Get-ExactLocalTestTrustCount -StoreName 'Root' -CertificatePath $certificatePath
            TrustedPublisher = Get-ExactLocalTestTrustCount -StoreName 'TrustedPublisher' -CertificatePath $certificatePath
        }
        history = @()
    }
    Save-State -Lifecycle 'preflight-complete' -Note 'Exact identities and Windows 11 machine snapshot passed.'
    Write-Host "Preflight passed. Evidence: '$evidence'. Next phase: Install."
    return
}

if ($Phase -in @('Install', 'Repair')) {
    if ($Phase -ceq 'Install') {
        Assert-Lifecycle -Allowed @('preflight-complete', 'transaction-running',
            'transaction-failed')
    }
    else {
        Assert-Lifecycle -Allowed @('installed', 'manual-complete', 'verifier-ready',
            'live-complete', 'performance-complete', 'latency-complete', 'transaction-running',
            'transaction-failed')
    }
    if ([string]$state.lifecycle -in @('transaction-running', 'transaction-failed') -and
        [string]$state.pendingTransaction -cne $Phase) {
        throw "A '$($state.pendingTransaction)' transaction is pending; '$Phase' cannot replace it."
    }
    Save-State -Lifecycle 'transaction-running' -PendingTransaction $Phase `
        -RequiredBootChangeFrom (Get-ViiperBootIdentity) `
        -Note "$Phase transaction child is starting."
    $transaction = Invoke-InstallTransaction -OperationName $Phase.ToLowerInvariant()
    if ([int]$transaction.exitCode -eq 3010) {
        Save-State -Lifecycle 'awaiting-transaction-reboot' -PendingTransaction $Phase `
            -RequiredBootChangeFrom (Get-ViiperBootIdentity) `
            -Note 'Transaction stopped at the VIIPER safe reboot boundary.'
        Write-Warning "MANUAL REBOOT PROMPT: restart Windows, then run Phase=RebootResume with identical inputs. Evidence: '$($transaction.evidenceDirectory)'."
        return
    }
    if (-not [bool]$transaction.success) {
        Save-State -Lifecycle 'transaction-failed' -PendingTransaction $Phase `
            -Note "$Phase child failed; inspect its retained evidence before retrying."
    }
    Assert-CapturedSuccess -Result $transaction -Label "$Phase transaction"
    Save-State -Lifecycle 'installed' -Note "$Phase transaction completed with exit code 0."
    Write-Host "$Phase completed. Next phase: ManualChecks."
    return
}

if ($Phase -ceq 'RebootResume') {
    Assert-Lifecycle -Allowed @('awaiting-transaction-reboot', 'transaction-running')
    $boot = Get-ViiperBootIdentity
    if ([string]$state.requiredBootChangeFrom -ceq $boot) {
        throw 'Required reboot has not occurred; boot identity is unchanged.'
    }
    $pending = [string]$state.pendingTransaction
    if ($pending -in @('Install', 'Repair')) {
        $transaction = Invoke-InstallTransaction -OperationName ('resume-' + $pending.ToLowerInvariant())
    }
    elseif ($pending -ceq 'Uninstall') {
        $transaction = Invoke-UninstallTransaction
    }
    else { throw "Unknown pending transaction '$pending'." }
    if ([int]$transaction.exitCode -eq 3010) {
        Save-State -Lifecycle 'awaiting-transaction-reboot' -PendingTransaction $pending `
            -RequiredBootChangeFrom $boot -Note 'Transaction requires another safe reboot boundary.'
        Write-Warning 'MANUAL REBOOT PROMPT: restart Windows again, then rerun RebootResume with identical inputs.'
        return
    }
    if (-not [bool]$transaction.success) {
        Save-State -Lifecycle 'transaction-failed' -PendingTransaction $pending `
            -Note "Resumed $pending child failed; inspect its retained evidence before retrying."
    }
    Assert-CapturedSuccess -Result $transaction -Label "Resumed $pending transaction"
    if ($pending -ceq 'Uninstall') {
        Save-State -Lifecycle 'uninstall-cleanup-pending' `
            -Note 'Resumed uninstall completed; crash policy and test-trust cleanup remain.'
        Complete-UninstallCleanup
        Save-State -Lifecycle 'uninstalled' -Note 'Uninstall and local-test cleanup completed.'
        Write-Warning 'MANUAL FINAL REBOOT PROMPT: restart Windows to apply restored diagnostics/pagefile policy, then archive the immutable bundle, state JSON, logs, ETL, and dumps.'
    }
    else {
        Save-State -Lifecycle 'installed' -Note "Resumed $pending transaction completed."
    }
    return
}

if ($Phase -ceq 'ManualChecks') {
    if ([string]$state.lifecycle -ceq 'awaiting-manual-reboot') {
        if (-not $AcknowledgeManualReboot) {
            Write-Warning 'MANUAL REBOOT PROMPT: perform a full Windows restart, then rerun ManualChecks with -AcknowledgeManualReboot.'
            return
        }
        if ([string]$state.requiredBootChangeFrom -ceq (Get-ViiperBootIdentity)) {
            throw 'Manual reboot acknowledgment was supplied but the boot identity is unchanged.'
        }
        Save-State -Lifecycle 'manual-complete' -Note 'Operator acknowledged full reboot and boot identity changed.'
        Write-Host 'Manual lifecycle checks are complete. Next phase: EnableVerifier.'
        return
    }
    Assert-Lifecycle -Allowed @('installed')
    if (-not $AcknowledgePhysicalHotplug) {
        Write-Warning "MANUAL HOTPLUG PROMPT: run only the bound DS4Windows executable '$ds4ExecutablePath'; disconnect and reconnect each physical DS4/DS5 over every intended USB/Bluetooth path; confirm reacquisition without duplicate virtual devices."
    }
    if (-not $AcknowledgeSleepWake) {
        Write-Warning 'MANUAL SLEEP PROMPT: enter Windows sleep, wake the laptop, then confirm physical input, virtual HID, feedback, and audio endpoints recover.'
    }
    if (-not $AcknowledgeHibernateWake) {
        Write-Warning 'MANUAL HIBERNATE PROMPT: hibernate and resume the laptop, then confirm physical input, virtual HID, feedback, and audio endpoints recover.'
    }
    if (-not ($AcknowledgePhysicalHotplug -and $AcknowledgeSleepWake -and
        $AcknowledgeHibernateWake)) {
        Write-Host 'Rerun ManualChecks with the three acknowledgments only after completing each physical check.'
        return
    }
    $boot = Get-ViiperBootIdentity
    Save-State -Lifecycle 'awaiting-manual-reboot' -RequiredBootChangeFrom $boot `
        -Note 'Hotplug, sleep, and hibernate checks acknowledged; full reboot remains.'
    Write-Warning 'MANUAL REBOOT PROMPT: perform a full Windows restart, then rerun ManualChecks with -AcknowledgeManualReboot.'
    return
}

if ($Phase -ceq 'EnableVerifier') {
    Assert-Lifecycle -Allowed @('manual-complete', 'enabling-verifier')
    if ([string]$state.lifecycle -ceq 'manual-complete') {
        Save-State -Lifecycle 'enabling-verifier' `
            -Note 'Crash diagnostics and one-boot Driver Verifier setup started.'
    }
    $crashState = Join-Path $evidence 'state\crash-policy-backup.json'
    $crashReady = $false
    if (Test-Path -LiteralPath $crashState -PathType Leaf) {
        $savedCrashPolicy = Get-Content -LiteralPath $crashState -Raw -Encoding UTF8 |
            ConvertFrom-Json -ErrorAction Stop
        if ([int]$savedCrashPolicy.schema -ne 1 -or
            [string]$savedCrashPolicy.machine -cne $env:COMPUTERNAME) {
            throw 'Existing crash-policy backup belongs to a different machine or schema.'
        }
        $currentCrashPolicy = Get-ItemProperty `
            -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' `
            -ErrorAction Stop
        $dumpEnabledProperty = $currentCrashPolicy.PSObject.Properties['CrashDumpEnabled']
        $keepProperty = $currentCrashPolicy.PSObject.Properties['AlwaysKeepMemoryDump']
        $overwriteProperty = $currentCrashPolicy.PSObject.Properties['Overwrite']
        $crashReady = $null -ne $dumpEnabledProperty -and
            $null -ne $keepProperty -and $null -ne $overwriteProperty -and
            [int]$dumpEnabledProperty.Value -eq 7 -and
            [int]$keepProperty.Value -eq 1 -and [int]$overwriteProperty.Value -eq 1
        if (-not $crashReady) {
            $restoreAttempt = Invoke-CapturedPowerShell `
                -Name 'restore-incomplete-crash-diagnostics-attempt' `
                -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Set-ViiperCrashDiagnostics.ps1') `
                -Arguments @('-Mode', 'Restore', '-StatePath', $crashState)
            Assert-CapturedSuccess -Result $restoreAttempt `
                -Label 'Incomplete crash-diagnostic attempt restore'
            $archivedCrashState = Join-Path $evidence ('state\crash-policy-failed-attempt-' +
                [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json')
            Move-Item -LiteralPath $crashState -Destination $archivedCrashState -ErrorAction Stop
        }
    }
    if (-not $crashReady) {
        $crash = Invoke-CapturedPowerShell -Name 'enable-crash-diagnostics' `
            -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Set-ViiperCrashDiagnostics.ps1') `
            -Arguments @('-Mode', 'Enable', '-DumpType', 'Automatic', '-StatePath', $crashState,
                '-AcknowledgeDiskUse')
        Assert-CapturedSuccess -Result $crash -Label 'Crash diagnostics enablement'
    }
    $verifier = Invoke-CapturedPowerShell -Name 'enable-driver-verifier' `
        -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Enable-ViiperUdeVerifierForNextBoot.ps1') `
        -Arguments @(
            '-SignedPackageDirectory', (Join-Path $packageRoot 'signed-package'),
            '-SubmissionManifestPath', (Join-Path $packageRoot 'submission-manifest.json'),
            '-ExpectedSourceRevision', [string]$manifest.viiper.sourceRevision,
            '-SignatureValidationMode', 'LocalTest',
            '-LocalTestCertificatePath', $certificatePath,
            '-DisposableTestMachine')
    Assert-CapturedSuccess -Result $verifier -Label 'Driver Verifier one-boot setup'
    Save-State -Lifecycle 'awaiting-verifier-reboot' `
        -RequiredBootChangeFrom (Get-ViiperBootIdentity) `
        -Note 'Automatic crash diagnostics and one-boot Driver Verifier are configured.'
    Write-Warning 'MANUAL VERIFIER REBOOT PROMPT: restart the disposable laptop. If it boot-loops, enter Safe Mode, run verifier.exe /reset, reboot, and collect dumps. Otherwise run VerifierResume.'
    return
}

if ($Phase -ceq 'VerifierResume') {
    Assert-Lifecycle -Allowed @('awaiting-verifier-reboot')
    if ([string]$state.requiredBootChangeFrom -ceq (Get-ViiperBootIdentity)) {
        throw 'Driver Verifier reboot has not occurred; boot identity is unchanged.'
    }
    $step = New-StepDirectory -Name 'verifier-resume-query'
    $query = & (Join-Path $env:SystemRoot 'System32\verifier.exe') /query 2>&1 | Out-String
    $queryExit = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $step 'verifier-query.txt'), $query,
        [Text.UTF8Encoding]::new($false))
    if ($queryExit -ne 0 -or $query -notmatch '(?im)\bViiperUde\.sys\b') {
        throw "Driver Verifier is not active for ViiperUde.sys. Evidence: '$step'."
    }
    Save-State -Lifecycle 'verifier-ready' -Note 'Boot changed and Driver Verifier query names ViiperUde.sys.'
    Write-Host 'Verifier is active. Next phase: Live.'
    return
}

if ($Phase -in @('Live', 'Performance')) {
    if ($Phase -ceq 'Live') { Assert-Lifecycle -Allowed @('verifier-ready') }
    else { Assert-Lifecycle -Allowed @('live-complete') }
    $oldPath = $env:Path
    try {
        $env:Path = (Split-Path -Parent $goPath) + ';' +
            (Split-Path -Parent $gitPath) + ';' + $oldPath
        $commonArguments = @(
            '-SignedPackageDirectory', (Join-Path $packageRoot 'signed-package'),
            '-SubmissionManifestPath', (Join-Path $packageRoot 'submission-manifest.json'),
            '-ExpectedSourceRevision', [string]$manifest.viiper.sourceRevision,
            '-SignatureValidationMode', 'LocalTest',
            '-LocalTestCertificatePath', $certificatePath,
            '-Iterations', [string]$Iterations,
            '-MediaProbePath', (Join-Path $packageRoot 'ViiperUdeMediaProbe.exe'),
            '-InputProbePath', (Join-Path $packageRoot 'ViiperUdeInputProbe.exe'),
            '-ProbeManifestPath', (Join-Path $packageRoot 'ViiperUdeLiveProbes.manifest.json'),
            '-MediaDurationSeconds', [string]$MediaDurationSeconds,
            '-RequireDriverVerifier', '-RestartRootDevice', '-DisposableTestMachine',
            '-ManageInstalledBrokerService')
        if ($Phase -ceq 'Live') {
            $arguments = $commonArguments + @('-RepositoryRoot', $viiperRoot)
            $result = Invoke-CapturedPowerShell -Name 'live-validation' `
                -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Invoke-ViiperUdeLiveValidation.ps1') `
                -Arguments $arguments
        }
        else {
            $step = New-StepDirectory -Name 'performance-validation'
            $trace = Join-Path $step 'viiper-localtest-performance.etl'
            $arguments = $commonArguments + @('-OutputPath', $trace)
            $result = Invoke-CapturedPowerShell -Name 'performance-validation' `
                -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Invoke-ViiperUdePerformanceValidation.ps1') `
                -Arguments $arguments -StepDirectory $step
        }
    }
    finally { $env:Path = $oldPath }
    Assert-CapturedSuccess -Result $result -Label "$Phase validation"
    if ($Phase -ceq 'Live') {
        $ds4Step = New-StepDirectory -Name 'ds4windows-live-validation'
        $ds4EvidencePath = Join-Path $ds4Step 'ds4windows-live-validation.json'
        $ds4Receipts = @(& $ds4LiveHarnessPath `
            -RunnerPath $ds4LiveRunnerPath `
            -MetadataPath $runtimeMetadataPath `
            -ArtifactRoot $runnerArtifactRoot `
            -OutputPath $ds4EvidencePath `
            -Samples 256 `
            -MediaSeconds 10 `
            -AllowLocalTestPackage `
            -IUnderstandThisExercisesLiveControllers)
        if ($ds4Receipts.Count -ne 1 -or
            [string]$ds4Receipts[0].status -cne 'pass' -or
            [string]$ds4Receipts[0].evidencePath -cne $ds4EvidencePath) {
            throw "DS4Windows live validation did not return one exact pass receipt. Evidence: '$ds4Step'."
        }
        $ds4Evidence = Resolve-ViiperRegularFile -Path $ds4EvidencePath `
            -Label 'DS4Windows live-validation evidence'
        $ds4EvidenceHash = Get-ViiperSha256 -Path $ds4Evidence
        if ($ds4EvidenceHash -cne [string]$ds4Receipts[0].evidenceSha256) {
            throw 'DS4Windows live-validation evidence changed after its exact receipt.'
        }
        $script:state.ds4WindowsLiveEvidence = [ordered]@{
            path = $ds4Evidence
            sha256 = $ds4EvidenceHash
            consentNonceSha256 = [string]$ds4Receipts[0].consentNonceSha256
            runnerSha256 = [string]$ds4Receipts[0].runnerSha256
            metadataSha256 = [string]$ds4Receipts[0].metadataSha256
            installedBrokerSha256 = [string]$ds4Receipts[0].installedBrokerSha256
            driverStoreInfSha256 = [string]$ds4Receipts[0].driverStoreInfSha256
            driverStoreCatSha256 = [string]$ds4Receipts[0].driverStoreCatSha256
            driverStoreSysSha256 = [string]$ds4Receipts[0].driverStoreSysSha256
            loadedDriverSha256 = [string]$ds4Receipts[0].loadedDriverSha256
        }
        Save-State -Lifecycle 'live-complete' `
            -Note 'Reference and DS4Windows source-bound lifecycle/HID/media/reconnect validation passed.'
        Write-Host 'Live validation passed. Next phase: Performance.'
    }
    else {
        Save-State -Lifecycle 'performance-complete' `
            -Note 'Local-test WPR performance capture passed; no native-versus-USB/IP claim is made.'
        Write-Host "Performance capture completed. This is not ABBA evidence. Next phase: LatencyMatrix. Evidence: '$($result.evidenceDirectory)'."
    }
    return
}

if ($Phase -ceq 'LatencyMatrix') {
    Assert-Lifecycle -Allowed @('performance-complete')
    if ([string]$manifest.latency.packageValidationMode -cne 'LocalTest' -or
        [int]$manifest.latency.cyclesPerPriority -lt 6 -or
        ([int]$manifest.latency.cyclesPerPriority % 2) -ne 0 -or
        [int]$manifest.latency.samplePairsPerTransition -lt 256) {
        throw 'Bundle latency policy is not the exact LocalTest balanced-cycle policy.'
    }
    $step = New-StepDirectory -Name 'latency-matrix'
    $matrixEvidenceRoot = Join-Path $step 'matrix'
    [void][IO.Directory]::CreateDirectory($matrixEvidenceRoot)
    $matrixResult = Invoke-CapturedPowerShell -Name 'latency-matrix' `
        -ScriptPath (Join-Path $viiperRoot '_testing\e2e\scripts\Invoke-ViiperE2ELatencyMatrix.ps1') `
        -Arguments @(
            '-SignedPackageDirectory', (Join-Path $packageRoot 'signed-package'),
            '-SubmissionManifestPath', (Join-Path $packageRoot 'submission-manifest.json'),
            '-PackageValidationMode', 'LocalTest',
            '-LocalTestCertificatePath', $certificatePath,
            '-ExpectedSourceRevision', [string]$manifest.viiper.sourceRevision,
            '-SDLBinarySHA256', [string]$manifest.latency.sdlBinarySha256,
            '-EvidenceDirectory', $matrixEvidenceRoot,
            '-Samples', [string]$manifest.latency.samplePairsPerTransition,
            '-CyclesPerPriority', [string]$manifest.latency.cyclesPerPriority,
            '-RepositoryRoot', $viiperRoot,
            '-GitExecutable', $gitPath,
            '-GoExecutable', $goPath
        ) -StepDirectory $step
    Assert-CapturedSuccess -Result $matrixResult -Label 'Source-bound latency matrix'
    $matrixPath = Resolve-ViiperRegularFile `
        -Path (Join-Path $matrixEvidenceRoot 'viiper-latency-priority-matrix.json') `
        -Label 'latency priority matrix'
    $superiorityPath = Resolve-ViiperRegularFile `
        -Path (Join-Path $matrixEvidenceRoot 'viiper-latency-superiority.json') `
        -Label 'latency superiority evidence'
    $superiority = Get-Content -LiteralPath $superiorityPath -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    $certificateHash = Get-ViiperSha256 -Path $certificatePath
    if ([string]$superiority.verdict -cne 'pass' -or
        [string]$superiority.analysis.verdict -cne 'pass' -or
        [string]$superiority.analysis.source_revision -cne [string]$manifest.viiper.sourceRevision -or
        [string]$superiority.analysis.native_package_validation_mode -cne 'local-test' -or
        [string]$superiority.analysis.native_local_test_certificate_sha256 -cne $certificateHash -or
        [string]$superiority.analysis.native_package_manifest_sha256 -cne
            (Get-ViiperSha256 -Path (Join-Path $packageRoot 'submission-manifest.json')) -or
        [string]$superiority.analysis.native_driver_build_identity -cne
            [string]$manifest.package.driverBuildIdentity) {
        throw 'Latency analyzer returned a contradictory source/package/signer-bound result.'
    }
    $script:state.latencyMatrixEvidence = [ordered]@{
        matrixPath = $matrixPath
        matrixSha256 = Get-ViiperSha256 -Path $matrixPath
        superiorityPath = $superiorityPath
        superioritySha256 = Get-ViiperSha256 -Path $superiorityPath
        cycleId = [string]$superiority.analysis.cycle_id
        cycleCount = [int]$superiority.analysis.cycle_count
        inferenceScope = [string]$superiority.analysis.inference_scope
        verdict = 'pass'
    }
    Save-State -Lifecycle 'latency-complete' `
        -Note 'Native latency was lower in every observed balanced cycle for this exact machine session.'
    Write-Host "Latency matrix passed for this exact machine session. Evidence: '$superiorityPath'."
    return
}

if ($Phase -ceq 'CollectDumps') {
    Assert-Lifecycle -Allowed @('installed', 'manual-complete', 'awaiting-verifier-reboot',
        'enabling-verifier', 'verifier-ready', 'live-complete', 'performance-complete',
        'latency-complete', 'uninstall-cleanup-pending')
    $destination = Join-Path (Join-Path $evidence 'dumps') (
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    $result = Invoke-CapturedPowerShell -Name 'collect-crash-dumps' `
        -ScriptPath (Join-Path $viiperRoot 'native\udecx\tools\Copy-ViiperCrashDumps.ps1') `
        -Arguments @('-Destination', $destination, '-MaxMiniDumps', '10',
            '-GrantReadToSID', $TargetUserSID)
    Assert-CapturedSuccess -Result $result -Label 'Crash-dump collection'
    Write-Host "Crash dumps and their hash manifest were retained at '$destination'."
    return
}

if ($Phase -ceq 'Uninstall') {
    Assert-Lifecycle -Allowed @('installed', 'manual-complete', 'awaiting-verifier-reboot',
        'enabling-verifier', 'verifier-ready', 'live-complete', 'performance-complete',
        'latency-complete', 'uninstall-cleanup-pending', 'transaction-running', 'transaction-failed')
    if ([string]$state.lifecycle -in @('transaction-running', 'transaction-failed') -and
        [string]$state.pendingTransaction -cne 'Uninstall') {
        throw "A '$($state.pendingTransaction)' transaction is pending; Uninstall cannot replace it."
    }
    if ([string]$state.lifecycle -cne 'uninstall-cleanup-pending') {
        Save-State -Lifecycle 'transaction-running' -PendingTransaction 'Uninstall' `
            -RequiredBootChangeFrom (Get-ViiperBootIdentity) `
            -Note 'Uninstall transaction child is starting.'
        $transaction = Invoke-UninstallTransaction
        if ([int]$transaction.exitCode -eq 3010) {
            Save-State -Lifecycle 'awaiting-transaction-reboot' -PendingTransaction 'Uninstall' `
                -RequiredBootChangeFrom (Get-ViiperBootIdentity) `
                -Note 'Uninstall stopped at the VIIPER safe reboot boundary.'
            Write-Warning 'MANUAL REBOOT PROMPT: restart Windows, then run RebootResume to finish uninstall and local-test cleanup.'
            return
        }
        if (-not [bool]$transaction.success) {
            Save-State -Lifecycle 'transaction-failed' -PendingTransaction 'Uninstall' `
                -Note 'Uninstall child failed; inspect its retained evidence before retrying.'
        }
        Assert-CapturedSuccess -Result $transaction -Label 'Uninstall transaction'
        Save-State -Lifecycle 'uninstall-cleanup-pending' `
            -Note 'VIIPER uninstall completed; crash policy and test-trust cleanup remain.'
    }
    Complete-UninstallCleanup
    Save-State -Lifecycle 'uninstalled' `
        -Note 'VIIPER uninstall, crash-policy restore, and non-preexisting test trust cleanup completed.'
    Write-Warning 'MANUAL FINAL REBOOT PROMPT: restart Windows to apply restored diagnostics/pagefile policy, then archive the immutable bundle, state JSON, logs, ETL, and dumps.'
    return
}

throw "Unhandled validation phase '$Phase'."
}
catch {
    $phaseFailure = $_
    try {
        $failureStep = New-StepDirectory -Name ('orchestrator-' + $Phase + '-failure')
        [IO.File]::WriteAllText((Join-Path $failureStep 'stdout.log'), '',
            [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $failureStep 'stderr.log'),
            ($phaseFailure | Out-String), [Text.UTF8Encoding]::new($false))
        Write-ViiperJsonAtomic -Path (Join-Path $failureStep 'result.json') -Value ([ordered]@{
            schema = 'viiper.windows11.phase-result/v1'
            phase = $Phase
            result = 'error'
            completedUtc = [DateTime]::UtcNow.ToString('o')
            message = $phaseFailure.Exception.Message
            bundleManifestSha256 = $actualManifestHash
            evidenceDirectory = $failureStep
        })
        Write-Warning "Phase failure evidence retained at '$failureStep'."
    }
    catch {
        Write-Warning "Could not write secondary phase-failure receipt: $($_.Exception.Message)"
    }
    throw $phaseFailure
}
