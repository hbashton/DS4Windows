[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [string]$ProductVersion = "5.0.3.0",
    [string]$DisplayVersion = "5.0.3.0",
    [string]$BundleVersion,
    [string]$OutputDirectory,
    [switch]$SkipApplicationPublish,
    [switch]$RequireSigning,
    [switch]$RequireNativeBundle
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishPath = [IO.Path]::GetFullPath($PublishRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "bin\x64\Release\installer"
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$signingEnabled = -not [string]::IsNullOrWhiteSpace(
    $env:DS4W_SIGN_CERT_PATH)
if ($RequireSigning -and -not $signingEnabled) {
    throw (
        "Release signing is required, but DS4W_SIGN_CERT_PATH is not set. " +
        "Unsigned public installers are intentionally blocked."
    )
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
    else { "https://timestamp.digicert.com" }
    & $signtool sign /fd SHA256 /f $env:DS4W_SIGN_CERT_PATH `
        /p $env:DS4W_SIGN_CERT_PASSWORD /tr $timestampUrl /td SHA256 $path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed: $path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed for '$path': $($signature.StatusMessage)"
    }
}

function Assert-ReleaseSignature([string]$path) {
    if (-not $RequireSigning) { return }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid) {
        throw "Required first-party signature is invalid for '$path': $($signature.StatusMessage)"
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
    throw (
        "Native installer composition requires a completed source-bound " +
        "DS4Windows publish. Build the application with the immutable native " +
        "pins, run post-build.py --require-native-bundle, then pass " +
        "-SkipApplicationPublish."
    )
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath "DS4Windows.exe") -PathType Leaf)) {
    throw "DS4Windows publish output is incomplete: $publishPath"
}
Invoke-SignAndVerify (Join-Path $publishPath "DS4Windows.exe")
# VIIPER is an immutable upstream release payload. Its compiled-in SHA-256
# and generated package sidecar are validated below; signing it here would
# mutate the executable after DS4Windows has pinned that identity.
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

if (-not $RequireNativeBundle) {
    throw (
        "The standard installer is native-VIIPER-only. Supply " +
        "-RequireNativeBundle with the reviewed signed package."
    )
}
$nativeBundle = Join-Path $publishPath `
    "extras\viiper-native-udecx"
$nativeLock = Join-Path $publishPath `
    "extras\viiper-native-udecx.lock.json"
$nativeContract = Join-Path $repoRoot `
    "DS4Windows\DS4Control\Viiper\ViiperNativePackageContract.cs"
& (Join-Path $env:SystemRoot `
    "System32\WindowsPowerShell\v1.0\powershell.exe") `
    -NoLogo -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $repoRoot `
        "utils\validate-viiper-native-bundle-signatures.ps1") `
    -BundleRoot $nativeBundle -LockPath $nativeLock `
    -ContractPath $nativeContract
if ($LASTEXITCODE -ne 0) {
    throw "The production VIIPER native signed-bundle gate failed."
}

$nativeLockObject = Get-Content -LiteralPath $nativeLock -Raw |
    ConvertFrom-Json
$nativeFilesByPath = @{}
foreach ($entry in @($nativeLockObject.files)) {
    if ($nativeFilesByPath.ContainsKey([string]$entry.path)) {
        throw "The validated native lock contains a duplicate file path."
    }
    $nativeFilesByPath[[string]$entry.path] = [string]$entry.sha256
}
function Get-NativeFilePin([string]$relativePath) {
    if (-not $nativeFilesByPath.ContainsKey($relativePath)) {
        throw "The validated native lock has no '$relativePath' entry."
    }
    $value = $nativeFilesByPath[$relativePath]
    if ($value -notmatch '^[0-9a-f]{64}$') {
        throw "The native lock hash for '$relativePath' is not canonical."
    }
    return $value
}
$nativeLockHash = (Get-FileHash -LiteralPath $nativeLock `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$nativeMsbuildArguments = @(
    "-p:ViiperNativeBundleEnabled=true",
    "-p:ViiperNativeSourceRevision=$($nativeLockObject.provenance.sourceRevision)",
    "-p:ViiperNativeDriverPackageVersion=$($nativeLockObject.driverPackageVersion)",
    "-p:ViiperNativeDriverBuildIdentity=$($nativeLockObject.driverBuildIdentity)",
    "-p:ViiperNativeBrokerSha256=$(Get-NativeFilePin 'viiper.exe')",
    "-p:ViiperNativeHelperSha256=$(Get-NativeFilePin 'ViiperUdeCtl.exe')",
    "-p:ViiperNativeManifestSha256=$(Get-NativeFilePin 'submission-manifest.json')",
    "-p:ViiperNativeInfSha256=$(Get-NativeFilePin 'driver/ViiperUde.inf')",
    "-p:ViiperNativeSysSha256=$(Get-NativeFilePin 'driver/ViiperUde.sys')",
    "-p:ViiperNativeCatSha256=$(Get-NativeFilePin 'driver/ViiperUde.cat')",
    "-p:ViiperNativeLockSha256=$nativeLockHash"
)

$generatedWix = Join-Path $repoRoot "installer\DS4Windows.Package\GeneratedFiles.wxs"
$manifestPath = Join-Path $publishPath "package-manifest.json"
& python (Join-Path $repoRoot "utils\generate-installer-files.py") `
    $publishPath $generatedWix $manifestPath --version $DisplayVersion
if ($LASTEXITCODE -ne 0) { throw "Installer manifest generation failed." }

& dotnet publish (Join-Path $repoRoot "installer\DS4Windows.SetupActions\DS4Windows.SetupActions.csproj") `
    -c Release -p:Platform=x64 -p:Version=$ProductVersion `
    -r win-x64 --self-contained true `
    -o (Join-Path $repoRoot "installer\DS4Windows.SetupActions\bin\x64\Release\publish") `
    @nativeMsbuildArguments
if ($LASTEXITCODE -ne 0) { throw "Setup action host build failed." }
$setupActions = Join-Path $repoRoot "installer\DS4Windows.SetupActions\bin\x64\Release\publish\DS4Windows.SetupActions.exe"
Invoke-SignAndVerify $setupActions

& dotnet publish (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj") `
    -c Release -p:Platform=x64 -p:Version=$ProductVersion `
    -r win-x64 --self-contained true `
    -o (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\bin\x64\Release\publish") `
    @nativeMsbuildArguments
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
$extrasRoot = Join-Path $publishPath "extras"
$nativeCacheHasher = [Security.Cryptography.SHA256]::Create()
try {
    $nativeCacheBytes = [Text.Encoding]::UTF8.GetBytes(
        $setupActionsHash.ToLowerInvariant() + "`n" + $nativeLockHash)
    $nativeActionCacheId = [BitConverter]::ToString(
        $nativeCacheHasher.ComputeHash($nativeCacheBytes)).Replace("-", "")
}
finally {
    $nativeCacheHasher.Dispose()
}
$bundleProject = Join-Path $repoRoot "installer\DS4Windows.Bundle\DS4Windows.Bundle.wixproj"
& dotnet build $bundleProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:BundleVersion=$BundleVersion `
    -p:DisplayVersion=$DisplayVersion `
    -p:MsiPath=$msiPath -p:BootstrapperRoot=$baRoot `
    -p:SetupActionsPath=$setupActions -p:SetupActionsHash=$setupActionsHash `
    -p:ExtrasRoot=$extrasRoot -p:ViiperNativeBundleEnabled=true `
    -p:NativeActionCacheId=$nativeActionCacheId
if ($LASTEXITCODE -ne 0) { throw "DS4Windows Burn bundle build failed." }

$builtInstaller = Join-Path $repoRoot "installer\DS4Windows.Bundle\bin\x64\Release\DS4Windows_${DisplayVersion}_Setup_x64.exe"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$finalInstaller = Join-Path $outputPath "DS4Windows_${DisplayVersion}_Setup_x64.exe"
$finalManifest = Join-Path $outputPath "package-manifest.json"
$publishId = [Guid]::NewGuid().ToString("N")
$pendingInstaller = $finalInstaller + ".pending-" + $publishId
$pendingManifest = $finalManifest + ".pending-" + $publishId

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
    Copy-Item -LiteralPath $builtInstaller -Destination $pendingInstaller
    Copy-Item -LiteralPath $manifestPath -Destination $pendingManifest

& python (Join-Path $repoRoot "utils\test-installer-state-machine.py")
if ($LASTEXITCODE -ne 0) {
    throw "Installer state-machine simulation failed."
}

$installerValidationArguments = @(
    (Join-Path $repoRoot "utils\validate-installer.py"),
    "--publish-root", $publishPath,
    "--manifest", $manifestPath,
    "--installer", $pendingInstaller,
    "--bundle-source", (Join-Path $repoRoot `
        "installer\DS4Windows.Bundle\Bundle.wxs"))
$installerValidationArguments += "--require-native-bundle"
& python @installerValidationArguments
if ($LASTEXITCODE -ne 0) { throw "Installer validation failed." }

Invoke-SignAndVerify $pendingInstaller
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
}

Write-Host "Standard installer ready: $finalInstaller" -ForegroundColor Green
}
finally {
    if ($buildMutexOwned) {
        try { $buildMutex.ReleaseMutex() } catch { }
    }
    $buildMutex.Dispose()
}
