[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [string]$ProductVersion = "5.0.3.0",
    [string]$DisplayVersion = "5.0.3.0",
    [string]$BundleVersion,
    [string]$OutputDirectory,
    [switch]$SkipApplicationPublish,
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishPath = [IO.Path]::GetFullPath($PublishRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "bin\x64\Release\installer"
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$signingCertificatePath = $env:DS4W_SIGN_CERT_PATH
$signingCertificateThumbprint = $env:DS4W_SIGN_CERT_THUMBPRINT
$approvedSignerThumbprint = $env:DS4W_SIGN_EXPECTED_THUMBPRINT
if ([string]::IsNullOrWhiteSpace($signingCertificatePath)) {
    $signingCertificatePath = $null
}
else {
    $signingCertificatePath = $signingCertificatePath.Trim()
}
if ([string]::IsNullOrWhiteSpace($signingCertificateThumbprint)) {
    $signingCertificateThumbprint = $null
}
else {
    $signingCertificateThumbprint =
        $signingCertificateThumbprint.Trim().ToUpperInvariant()
}
if ([string]::IsNullOrWhiteSpace($approvedSignerThumbprint)) {
    $approvedSignerThumbprint = $null
}
else {
    $approvedSignerThumbprint =
        $approvedSignerThumbprint.Trim().ToUpperInvariant()
}
$signingEnabled =
    $null -ne $signingCertificatePath -or
    $null -ne $signingCertificateThumbprint
if ($null -ne $signingCertificatePath -and
        $null -ne $signingCertificateThumbprint) {
    throw "Configure either a signing PFX or a certificate-store thumbprint, not both."
}
if ($RequireSigning -and -not $signingEnabled) {
    throw (
        "Release signing is required, but DS4W_SIGN_CERT_PATH is not set. " +
        "Unsigned public installers are intentionally blocked."
    )
}
if ($RequireSigning -and
        $approvedSignerThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    throw (
        "Release signing requires DS4W_SIGN_EXPECTED_THUMBPRINT to pin " +
        "the approved 40-character signer identity."
    )
}
if ($RequireSigning -and $signingCertificateThumbprint -and
        $signingCertificateThumbprint -ne $approvedSignerThumbprint) {
    throw "Certificate-store signing identity does not match the approved signer."
}
$signtool = $null
if ($signingEnabled) {
    $signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
}

function Invoke-SignAndVerify([string]$path) {
    if (-not $signingEnabled) { return }
    $timestampUrl = if ($env:DS4W_SIGN_TIMESTAMP_URL) {
        $env:DS4W_SIGN_TIMESTAMP_URL
    }
    else { "http://timestamp.digicert.com" }
    if ($signingCertificateThumbprint) {
        if ($signingCertificateThumbprint -notmatch '^[0-9A-F]{40}$') {
            throw "Signing certificate thumbprint must contain exactly 40 hexadecimal characters."
        }
        & $signtool sign /fd SHA256 /s My /sha1 `
            $signingCertificateThumbprint /tr $timestampUrl /td SHA256 $path
    }
    else {
        & $signtool sign /fd SHA256 /f $signingCertificatePath `
            /p $env:DS4W_SIGN_CERT_PASSWORD /tr $timestampUrl /td SHA256 $path
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed: $path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed for '$path': $($signature.StatusMessage)"
    }
    if ($approvedSignerThumbprint -and
            $signature.SignerCertificate.Thumbprint -ne
            $approvedSignerThumbprint) {
        throw "Authenticode signer is not the approved release identity: $path"
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "Authenticode timestamp is missing: $path"
    }
}

function Assert-ReleaseSignature([string]$path) {
    if (-not $RequireSigning) { return }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid) {
        throw "Required first-party signature is invalid for '$path': $($signature.StatusMessage)"
    }
    if ($signature.SignerCertificate.Thumbprint -ne
            $approvedSignerThumbprint) {
        throw "Required signature does not use the approved release identity: $path"
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "Required release timestamp is missing: $path"
    }
}

function Invoke-SignOrVerify([string]$path) {
    if (-not $signingEnabled) { return }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($RequireSigning -and
            $signature.Status -eq
                [Management.Automation.SignatureStatus]::Valid -and
            $signature.SignerCertificate.Thumbprint -eq
                $approvedSignerThumbprint -and
            $signature.TimeStamperCertificate) {
        return
    }
    Invoke-SignAndVerify $path
}

function Resolve-WixExecutable([string]$projectPath) {
    [xml]$project = Get-Content -Raw -LiteralPath $projectPath
    $sdk = [string]$project.Project.Sdk
    if ($sdk -notmatch '^WixToolset\.Sdk/([0-9]+(?:\.[0-9]+){2})$') {
        throw "Could not determine the exact WiX SDK version from $projectPath"
    }

    $packageRoot = if (-not [string]::IsNullOrWhiteSpace(
            $env:NUGET_PACKAGES)) {
        $env:NUGET_PACKAGES.Trim()
    }
    else {
        Join-Path ([Environment]::GetFolderPath('UserProfile')) `
            '.nuget\packages'
    }
    $candidate = Join-Path $packageRoot (
        "wixtoolset.sdk\$($Matches[1])\tools\net472\x64\wix.exe")
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The WiX SDK executable was not found: $candidate"
    }
    return [IO.Path]::GetFullPath($candidate)
}

function Invoke-WixBurn(
        [string]$wixExecutable,
        [string[]]$wixArguments,
        [string]$failureMessage) {
    & $wixExecutable @wixArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$failureMessage (WiX exit code $LASTEXITCODE)."
    }
}

function Assert-BurnBundleIntegrity(
        [string]$wixExecutable,
        [string]$bundlePath,
        [string]$workRoot,
        [string]$expectedMsiPath,
        [string]$expectedSetupActionsPath,
        [string]$expectedBootstrapperPath) {
    $verifyRoot = Join-Path $workRoot 'verify'
    $verifyIntermediate = Join-Path $verifyRoot 'intermediate'
    $extractRoot = Join-Path $verifyRoot 'payloads'
    $bootstrapperRoot = Join-Path $verifyRoot 'bootstrapper'
    New-Item -ItemType Directory -Path $verifyIntermediate -Force |
        Out-Null
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $bootstrapperRoot -Force |
        Out-Null

    Invoke-WixBurn $wixExecutable @(
        'burn', 'extract', $bundlePath,
        '-o', $extractRoot,
        '-outba', $bootstrapperRoot,
        '-intermediateFolder', $verifyIntermediate
    ) 'Could not extract the final Burn attached container'

    $attachedRoot = Join-Path $extractRoot 'WixAttachedContainer'
    $extractedMsi = Join-Path $attachedRoot (
        Split-Path -Leaf $expectedMsiPath)
    $extractedSetupActions = Join-Path $attachedRoot `
        'DS4Windows.SetupActions.Preflight.exe'
    $extractedBootstrapper = Join-Path $bootstrapperRoot `
        'DS4Windows.Bootstrapper.exe'
    $requiredPairs = @(
        @($extractedMsi, $expectedMsiPath),
        @($extractedSetupActions, $expectedSetupActionsPath),
        @($extractedBootstrapper, $expectedBootstrapperPath)
    )
    foreach ($pair in $requiredPairs) {
        if (-not (Test-Path -LiteralPath $pair[0] -PathType Leaf)) {
            throw "The final Burn bundle did not extract '$($pair[0])'."
        }
        $actualHash = (Get-FileHash -LiteralPath $pair[0] `
            -Algorithm SHA256).Hash
        $expectedHash = (Get-FileHash -LiteralPath $pair[1] `
            -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Extracted Burn payload does not match its signed source: $($pair[0])"
        }
    }
}

$buildMutex = [Threading.Mutex]::new($false,
    "Global\DS4Windows-Installer-Build")
$buildMutexOwned = $false
try {
    try {
        $buildMutexOwned = $buildMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $buildMutexOwned = $true
    }
    if (-not $buildMutexOwned) {
        throw (
            "Another DS4Windows installer composition is already running. " +
            "Wait for that build to finish instead of overlapping WiX/MSI validation."
        )
    }

    # A forcibly terminated shell releases its mutex immediately, while a
    # dotnet/WiX child can survive long enough to keep MSI consistency
    # validation active. Converge only exact build children whose command
    # line points into this repository before starting another composition.
    $repoPattern = [regex]::Escape($repoRoot.TrimEnd('\', '/'))
    $installerProjectPattern =
        '(?i)(DS4Windows\.Package|DS4Windows\.Bundle|build-installer\.ps1)'
    $orphanedBuilds = @()
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $orphanedBuilds = @(Get-CimInstance Win32_Process `
            -ErrorAction SilentlyContinue | Where-Object {
                $_.ProcessId -ne $PID -and $_.CommandLine -and
                $_.CommandLine -match $repoPattern -and
                $_.CommandLine -match $installerProjectPattern
            })
        if ($orphanedBuilds.Count -eq 0) { break }
        if ($attempt -eq 0) {
            Write-Host (
                "Waiting for a previous orphaned WiX/MSI build from this " +
                "repository to exit safely...") -ForegroundColor Yellow
        }
        Start-Sleep -Seconds 1
    }
    if ($orphanedBuilds.Count -gt 0) {
        $owners = ($orphanedBuilds | ForEach-Object {
            "PID=$($_.ProcessId) $($_.Name)"
        }) -join ", "
        throw (
            "A previous installer build from this repository is still " +
            "active after 120 seconds ($owners). No overlapping WiX/MSI " +
            "composition was started."
        )
    }

if ($ProductVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "ProductVersion must be a numeric Windows Installer version: $ProductVersion"
}
if (-not $BundleVersion) {
    # Human CI labels (date + commit) belong in filenames, not in the Burn
    # upgrade ordering contract. Release workflows can explicitly pass their
    # monotonic numeric bundle version.
    $BundleVersion = $ProductVersion
}
if ($BundleVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$') {
    throw "BundleVersion must be a numeric or semantic version: $BundleVersion"
}
if ([string]::IsNullOrWhiteSpace($DisplayVersion) -or
    $DisplayVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,79}$') {
    throw (
        "DisplayVersion must be a filename-safe release identifier using " +
        "only letters, numbers, periods, underscores, and hyphens: " +
        $DisplayVersion
    )
}

if (-not $SkipApplicationPublish) {
    & dotnet publish (Join-Path $repoRoot "DS4Windows\DS4WinWPF.csproj") `
        -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        -p:AssemblyVersion=$ProductVersion -p:FileVersion=$ProductVersion `
        -p:Version=$ProductVersion -p:InformationalVersion=$DisplayVersion `
        -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw "DS4Windows publish failed." }
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath "DS4Windows.exe") -PathType Leaf)) {
    throw "DS4Windows publish output is incomplete: $publishPath"
}
Invoke-SignOrVerify (Join-Path $publishPath "DS4Windows.exe")
# VIIPER is an immutable upstream release payload. Its compiled-in SHA-256
# and generated package sidecar are validated below; signing it here would
# mutate the executable after DS4Windows has pinned that identity.
$bundledViiper = Join-Path $publishPath "extras\VIIPER-0.1.2-x64.exe"
if (-not (Test-Path -LiteralPath $bundledViiper -PathType Leaf)) {
    throw "Published VIIPER payload is missing: $bundledViiper"
}
$releaseMarker = Join-Path $publishPath "DS4Windows.release"
if (-not (Test-Path -LiteralPath $releaseMarker -PathType Leaf)) {
    throw "DS4Windows publish output has no release identity: $releaseMarker"
}
$publishedRelease = (Get-Content -LiteralPath $releaseMarker -Raw).Trim()
if (-not [string]::Equals($publishedRelease, $DisplayVersion,
        [StringComparison]::Ordinal)) {
    throw (
        "DisplayVersion '$DisplayVersion' does not match the completed " +
        "publish identity '$publishedRelease'. Refusing to compose a mixed " +
        "or stale installer."
    )
}

$generatedWix = Join-Path $repoRoot "installer\DS4Windows.Package\GeneratedFiles.wxs"
$manifestPath = Join-Path $publishPath "package-manifest.json"
& python (Join-Path $repoRoot "utils\generate-installer-files.py") `
    $publishPath $generatedWix $manifestPath --version $DisplayVersion
if ($LASTEXITCODE -ne 0) { throw "Installer manifest generation failed." }

& dotnet publish (Join-Path $repoRoot "installer\DS4Windows.SetupActions\DS4Windows.SetupActions.csproj") `
    -c Release -p:Platform=x64 -p:Version=$ProductVersion `
    -r win-x64 --self-contained true `
    -o (Join-Path $repoRoot "installer\DS4Windows.SetupActions\bin\x64\Release\publish")
if ($LASTEXITCODE -ne 0) { throw "Setup action host build failed." }
$setupActions = Join-Path $repoRoot "installer\DS4Windows.SetupActions\bin\x64\Release\publish\DS4Windows.SetupActions.exe"
Invoke-SignAndVerify $setupActions

& dotnet publish (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj") `
    -c Release -p:Platform=x64 -p:Version=$ProductVersion `
    -r win-x64 --self-contained true `
    -o (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\bin\x64\Release\publish")
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper UI build failed." }
$baRoot = Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\bin\x64\Release\publish"
Invoke-SignAndVerify (Join-Path $baRoot "DS4Windows.Bootstrapper.exe")

$packageProject = Join-Path $repoRoot "installer\DS4Windows.Package\DS4Windows.Package.wixproj"
& dotnet build $packageProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:ProductVersion=$ProductVersion `
    -p:PublishRoot=$publishPath
if ($LASTEXITCODE -ne 0) { throw "DS4Windows MSI build failed." }

$msiPath = Join-Path $repoRoot "installer\DS4Windows.Package\bin\x64\Release\DS4Windows_${ProductVersion}_x64.msi"
Invoke-SignAndVerify $msiPath
$setupActionsHash = (Get-FileHash -LiteralPath $setupActions -Algorithm SHA256).Hash
if ($setupActionsHash -notmatch '^[0-9A-F]{64}$') {
    throw "Could not derive a content-addressed setup-helper cache identity."
}
$extrasRoot = Join-Path $repoRoot "extras"
$bundleProject = Join-Path $repoRoot "installer\DS4Windows.Bundle\DS4Windows.Bundle.wixproj"
$wixExecutable = Resolve-WixExecutable $bundleProject
& dotnet build $bundleProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:BundleVersion=$BundleVersion `
    -p:DisplayVersion=$DisplayVersion `
    -p:MsiPath=$msiPath -p:BootstrapperRoot=$baRoot `
    -p:SetupActionsPath=$setupActions -p:SetupActionsHash=$setupActionsHash `
    -p:ExtrasRoot=$extrasRoot
if ($LASTEXITCODE -ne 0) { throw "DS4Windows Burn bundle build failed." }

$builtInstaller = Join-Path $repoRoot "installer\DS4Windows.Bundle\bin\x64\Release\DS4Windows_${DisplayVersion}_Setup_x64.exe"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$finalInstaller = Join-Path $outputPath "DS4Windows_${DisplayVersion}_Setup_x64.exe"
$finalManifest = Join-Path $outputPath "package-manifest.json"
$publishId = [Guid]::NewGuid().ToString("N")
$pendingInstaller = $finalInstaller + ".pending-" + $publishId
$pendingManifest = $finalManifest + ".pending-" + $publishId
$burnWorkRoot = [IO.Path]::GetFullPath((Join-Path $outputPath `
    (".burn-sign-" + $publishId)))
$outputPrefix = $outputPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $burnWorkRoot.StartsWith(
        $outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Burn signing workspace escaped the installer output directory."
}

function Publish-InstallerFileAtomically(
        [string]$source, [string]$destination) {
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $backup = $destination + ".backup-" + [Guid]::NewGuid().ToString("N")
        try {
            [IO.File]::Replace($source, $destination, $backup, $true)
        }
        finally {
            if (Test-Path -LiteralPath $backup) {
                Remove-Item -LiteralPath $backup -Force
            }
        }
    }
    else {
        [IO.File]::Move($source, $destination)
    }
}

try {
    Copy-Item -LiteralPath $manifestPath -Destination $pendingManifest
    New-Item -ItemType Directory -Path $burnWorkRoot -Force | Out-Null

    if ($signingEnabled) {
        # A Burn bundle must be signed in two pieces. Signing the completed
        # bundle directly leaves its cached engine unable to locate the
        # attached payload container during elevated apply.
        $signRoot = Join-Path $burnWorkRoot 'sign'
        $signIntermediate = Join-Path $signRoot 'intermediate'
        New-Item -ItemType Directory -Path $signIntermediate -Force |
            Out-Null
        $detachedBurnEngine = Join-Path $signRoot 'burn-engine.exe'
        Invoke-WixBurn $wixExecutable @(
            'burn', 'detach', $builtInstaller,
            '-engine', $detachedBurnEngine,
            '-intermediateFolder', $signIntermediate
        ) 'Could not detach the Burn engine for signing'
        Invoke-SignAndVerify $detachedBurnEngine

        Invoke-WixBurn $wixExecutable @(
            'burn', 'reattach', $builtInstaller,
            '-engine', $detachedBurnEngine,
            '-o', $pendingInstaller,
            '-intermediateFolder', $signIntermediate
        ) 'Could not reattach the signed Burn engine'
        Invoke-SignAndVerify $pendingInstaller
    }
    else {
        Copy-Item -LiteralPath $builtInstaller `
            -Destination $pendingInstaller
    }

    Assert-BurnBundleIntegrity $wixExecutable $pendingInstaller `
        $burnWorkRoot $msiPath $setupActions `
        (Join-Path $baRoot 'DS4Windows.Bootstrapper.exe')

& (Join-Path $env:SystemRoot `
    "System32\WindowsPowerShell\v1.0\powershell.exe") `
    -NoLogo -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $repoRoot "utils\test-viiper-reboot-boundary.ps1") `
    -BackendScript (Join-Path $repoRoot "extras\install-viiper-backend.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "USB-IP reboot-boundary simulation failed."
}

& python (Join-Path $repoRoot "utils\test-installer-state-machine.py")
if ($LASTEXITCODE -ne 0) {
    throw "Installer state-machine simulation failed."
}

& python (Join-Path $repoRoot "utils\validate-installer.py") `
    --publish-root $publishPath --manifest $manifestPath `
    --installer $pendingInstaller --bundle-source (Join-Path $repoRoot "installer\DS4Windows.Bundle\Bundle.wxs")
if ($LASTEXITCODE -ne 0) { throw "Installer validation failed." }

Assert-ReleaseSignature $pendingInstaller

    # The installer EXE is the externally visible commit point. Publish its
    # verified sidecar first so a terminated composition can never expose a
    # new installer beside an older manifest.
    Publish-InstallerFileAtomically $pendingManifest $finalManifest
    Publish-InstallerFileAtomically $pendingInstaller $finalInstaller
}
finally {
    foreach ($pending in @($pendingInstaller, $pendingManifest)) {
        if (Test-Path -LiteralPath $pending) {
            Remove-Item -LiteralPath $pending -Force
        }
    }
    if (Test-Path -LiteralPath $burnWorkRoot) {
        Remove-Item -LiteralPath $burnWorkRoot -Recurse -Force
    }
}

Write-Host "Standard installer ready: $finalInstaller" -ForegroundColor Green
}
finally {
    if ($buildMutexOwned) {
        try { $buildMutex.ReleaseMutex() } catch { }
    }
    $buildMutex.Dispose()
}
