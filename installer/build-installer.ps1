[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [string]$ProductVersion = "4.0.2.1",
    [string]$DisplayVersion = "4.0.2.1",
    [string]$BundleVersion,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishPath = [IO.Path]::GetFullPath($PublishRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "bin\x64\Release\installer"
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)

function Assert-OrdinaryFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Expected an ordinary file: '$Path'."
    }
}

function Assert-NoReparseDirectoryChain {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($resolved)
    if ([string]::IsNullOrWhiteSpace($root)) {
        throw "Expected a rooted directory path: '$Path'."
    }
    $cursor = $root
    foreach ($component in $resolved.Substring($root.Length).Split(
        [char[]]@('\', '/'),
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $component
        if (-not (Test-Path -LiteralPath $cursor)) { continue }
        $item = Get-Item -LiteralPath $cursor -Force
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band
             [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Installer build path is unsafe: '$cursor'."
        }
    }
}

function Get-CertificateSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($Certificate.RawData))).Replace('-', '')
    } finally {
        $algorithm.Dispose()
    }
}

function Find-SignTool {
    $programFilesX86 = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)
    $kitsRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object {
                Join-Path $_.FullName 'x64\signtool.exe'
            } |
            Where-Object {
                Test-Path -LiteralPath $_ -PathType Leaf
            } |
            Select-Object -First 1
        if ($candidate) {
            Assert-OrdinaryFile -Path $candidate
            Assert-NoReparseDirectoryChain -Path (
                Split-Path -Parent $candidate)
            $signature = Get-AuthenticodeSignature -LiteralPath $candidate
            if ($signature.Status -ne
                    [Management.Automation.SignatureStatus]::Valid -or
                $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.Subject -notmatch
                    '(?:^|, )O=Microsoft Corporation(?:,|$)') {
                throw 'The Windows SDK signtool.exe is not validly Microsoft-signed.'
            }
            return $candidate
        }
    }
    throw 'A Windows SDK x64 signtool.exe is required.'
}

function Assert-AuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedCertificateSha256
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne
        [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed for '$Path': $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate -or
        (Get-CertificateSha256 -Certificate $signature.SignerCertificate) -cne
            $ExpectedCertificateSha256) {
        throw "Authenticode signer identity did not match for '$Path'."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode timestamp is missing for '$Path'."
    }
    & $script:signTool verify /pa /all $Path
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verification failed for '$Path'."
    }
}

function Set-RequiredAuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)][string]$TimestampUrl,
        [Parameter(Mandatory = $true)][string]$ExpectedCertificateSha256
    )
    Assert-OrdinaryFile -Path $Path
    $result = Set-AuthenticodeSignature -FilePath $Path `
        -Certificate $Certificate -HashAlgorithm SHA256 `
        -TimestampServer $TimestampUrl -IncludeChain All
    if ($result.Status -ne
        [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed for '$Path': $($result.StatusMessage)"
    }
    Assert-AuthenticodeSignature -Path $Path `
        -ExpectedCertificateSha256 $ExpectedCertificateSha256
}

$certificate = $null
$expectedCertificateSha256 = $null
$timestampUrl = $null
$script:signTool = $null
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
if ($BundleVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "BundleVersion must be numeric so Burn and the custom BA use identical upgrade ordering: $BundleVersion"
}
if ([string]::IsNullOrWhiteSpace($DisplayVersion) -or
    $DisplayVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,79}$') {
    throw (
        "DisplayVersion must be a filename-safe release identifier using " +
        "only letters, numbers, periods, underscores, and hyphens: " +
        $DisplayVersion
    )
}

# There is deliberately no unsigned production mode.
foreach ($name in @(
    'DS4W_SIGN_CERT_PATH',
    'DS4W_SIGN_CERT_PASSWORD',
    'DS4W_SIGN_CERTIFICATE_SHA256',
    'DS4W_SIGN_TIMESTAMP_URL')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Production installer signing requires environment variable $name."
    }
}
$expectedCertificateSha256 =
    $env:DS4W_SIGN_CERTIFICATE_SHA256.ToUpperInvariant()
if ($expectedCertificateSha256 -notmatch '^[0-9A-F]{64}$') {
    throw 'DS4W_SIGN_CERTIFICATE_SHA256 must be 64 hexadecimal characters.'
}
$timestampUri = $null
if (-not [Uri]::TryCreate($env:DS4W_SIGN_TIMESTAMP_URL,
        [UriKind]::Absolute, [ref]$timestampUri) -or
    $timestampUri.Scheme -cne 'https') {
    throw 'DS4W_SIGN_TIMESTAMP_URL must be an absolute HTTPS URL.'
}
$timestampUrl = $timestampUri.AbsoluteUri
$certificatePath = [IO.Path]::GetFullPath(
    $env:DS4W_SIGN_CERT_PATH)
Assert-OrdinaryFile -Path $certificatePath
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath, $env:DS4W_SIGN_CERT_PASSWORD,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
if (-not $certificate.HasPrivateKey) {
    throw 'The Authenticode certificate has no private key.'
}
$now = [DateTime]::UtcNow
if ($certificate.NotBefore.ToUniversalTime() -gt $now -or
    $certificate.NotAfter.ToUniversalTime() -le $now) {
    throw 'The Authenticode certificate is not currently valid.'
}
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$eku = $certificate.Extensions |
    Where-Object {
        $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
    } |
    Select-Object -First 1
if ($null -eq $eku -or
    -not ($eku.EnhancedKeyUsages | Where-Object {
        $_.Value -ceq $codeSigningOid
    })) {
    throw 'The Authenticode certificate lacks the Code Signing EKU.'
}
if ((Get-CertificateSha256 -Certificate $certificate) -cne
    $expectedCertificateSha256) {
    throw 'The Authenticode certificate did not match its pinned SHA-256.'
}
$script:signTool = Find-SignTool

$nativeContract = Join-Path $repoRoot 'extras\Test-ViiperNativePackageContract.ps1'
$installerSecurityContract = Join-Path $repoRoot `
    'installer\Test-InstallerSecurityContracts.ps1'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $windowsPowerShell -NoLogo -NoProfile -NonInteractive `
    -ExecutionPolicy Bypass -File $installerSecurityContract
if ($LASTEXITCODE -ne 0) {
    throw 'The Windows PowerShell installer security contract failed.'
}
$powerShellCore = (Get-Command pwsh.exe -CommandType Application `
    -ErrorAction Stop).Source
& $powerShellCore -NoLogo -NoProfile -NonInteractive `
    -File $installerSecurityContract
if ($LASTEXITCODE -ne 0) {
    throw 'The PowerShell 7 installer security contract failed.'
}
& $windowsPowerShell -NoLogo -NoProfile -NonInteractive `
    -ExecutionPolicy Bypass -File $nativeContract `
    -RequireProduction -RequirePackage
if ($LASTEXITCODE -ne 0) {
    throw 'The production VIIPER native package contract failed.'
}

Assert-NoReparseDirectoryChain -Path $repoRoot
Assert-NoReparseDirectoryChain -Path (Split-Path -Parent $publishPath)
if (Test-Path -LiteralPath $publishPath) {
    throw "PublishRoot must not already exist; production composition requires a fresh publish tree: '$publishPath'."
}
& dotnet publish (Join-Path $repoRoot "DS4Windows\DS4WinWPF.csproj") `
    -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:AssemblyVersion=$ProductVersion -p:FileVersion=$ProductVersion `
    -p:Version=$ProductVersion -p:InformationalVersion=$DisplayVersion `
    -o $publishPath
if ($LASTEXITCODE -ne 0) { throw "DS4Windows publish failed." }
[IO.File]::WriteAllText(
    (Join-Path $publishPath 'DS4Windows.release'),
    $DisplayVersion + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
if (-not (Test-Path -LiteralPath (Join-Path $publishPath "DS4Windows.exe") -PathType Leaf)) {
    throw "DS4Windows publish output is incomplete: $publishPath"
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

$applicationPath = Join-Path $publishPath 'DS4Windows.exe'
$managerPath = Join-Path $publishPath 'extras\manage-viiper-native-package.ps1'
$metadataPath = Join-Path $publishPath 'ViiperNativeRuntimeMetadata.json'
$packageRoot = Join-Path $publishPath 'extras\viiper-native-package'
foreach ($requiredPath in @($applicationPath, $managerPath,
    $metadataPath)) {
    Assert-OrdinaryFile -Path $requiredPath
}
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw 'The publish output has no production native package tree.'
}
Set-RequiredAuthenticodeSignature -Path $applicationPath `
    -Certificate $certificate -TimestampUrl $timestampUrl `
    -ExpectedCertificateSha256 $expectedCertificateSha256
Set-RequiredAuthenticodeSignature -Path $managerPath `
    -Certificate $certificate -TimestampUrl $timestampUrl `
    -ExpectedCertificateSha256 $expectedCertificateSha256

$generatedWix = Join-Path $repoRoot "installer\DS4Windows.Package\GeneratedFiles.wxs"
$manifestPath = Join-Path $publishPath "package-manifest.json"
& python (Join-Path $repoRoot "utils\generate-installer-files.py") `
    $publishPath $generatedWix $manifestPath --version $DisplayVersion
if ($LASTEXITCODE -ne 0) { throw "Installer manifest generation failed." }

& dotnet build (Join-Path $repoRoot "installer\DS4Windows.SetupActions\DS4Windows.SetupActions.csproj") `
    -t:Rebuild -c Release -p:Platform=x64 -p:Version=$ProductVersion
if ($LASTEXITCODE -ne 0) { throw "Setup action host build failed." }

& dotnet build (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj") `
    -t:Rebuild -c Release -p:Platform=x64 -p:Version=$ProductVersion
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper UI build failed." }

$setupActions = Join-Path $repoRoot 'installer\DS4Windows.SetupActions\bin\x64\Release\net48\DS4Windows.SetupActions.exe'
$baRoot = Join-Path $repoRoot 'installer\DS4Windows.Bootstrapper\bin\x64\Release\net48\win-x64'
$bootstrapperExe = Join-Path $baRoot 'DS4Windows.Bootstrapper.exe'
Set-RequiredAuthenticodeSignature -Path $setupActions `
    -Certificate $certificate -TimestampUrl $timestampUrl `
    -ExpectedCertificateSha256 $expectedCertificateSha256
Set-RequiredAuthenticodeSignature -Path $bootstrapperExe `
    -Certificate $certificate -TimestampUrl $timestampUrl `
    -ExpectedCertificateSha256 $expectedCertificateSha256

$packageProject = Join-Path $repoRoot "installer\DS4Windows.Package\DS4Windows.Package.wixproj"
& dotnet build $packageProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:ProductVersion=$ProductVersion `
    -p:PublishRoot=$publishPath
if ($LASTEXITCODE -ne 0) { throw "DS4Windows MSI build failed." }

$msiPath = Join-Path $repoRoot (
    'installer\DS4Windows.Package\bin\x64\Release\DS4Windows_' +
    $ProductVersion + '_x64.msi')
Set-RequiredAuthenticodeSignature -Path $msiPath `
    -Certificate $certificate -TimestampUrl $timestampUrl `
    -ExpectedCertificateSha256 $expectedCertificateSha256
$setupActionsHash = (Get-FileHash -LiteralPath $setupActions -Algorithm SHA256).Hash
if ($setupActionsHash -notmatch '^[0-9A-F]{64}$') {
    throw "Could not derive a content-addressed setup-helper cache identity."
}
$bundleProject = Join-Path $repoRoot "installer\DS4Windows.Bundle\DS4Windows.Bundle.wixproj"
& dotnet build $bundleProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:BundleVersion=$BundleVersion `
    -p:DisplayVersion=$DisplayVersion `
    -p:MsiPath=$msiPath -p:BootstrapperRoot=$baRoot `
    -p:SetupActionsPath=$setupActions -p:SetupActionsHash=$setupActionsHash `
    -p:RepositoryRoot=$repoRoot
if ($LASTEXITCODE -ne 0) { throw "DS4Windows Burn bundle build failed." }

$builtInstaller = Join-Path $repoRoot (
    'installer\DS4Windows.Bundle\bin\x64\Release\DS4Windows_' +
    $DisplayVersion + '_Setup_x64.exe')
Set-RequiredAuthenticodeSignature -Path $builtInstaller `
    -Certificate $certificate -TimestampUrl $timestampUrl `
    -ExpectedCertificateSha256 $expectedCertificateSha256

& python (Join-Path $repoRoot "utils\test-installer-state-machine.py")
if ($LASTEXITCODE -ne 0) {
    throw "Installer state-machine simulation failed."
}
& python (Join-Path $repoRoot "utils\validate-installer.py") `
    --publish-root $publishPath --manifest $manifestPath `
    --installer $builtInstaller `
    --bundle-source (Join-Path $repoRoot "installer\DS4Windows.Bundle\Bundle.wxs") `
    --setup-actions-source (Join-Path $repoRoot "installer\DS4Windows.SetupActions\Program.cs") `
    --bootstrapper-source (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\InstallerApplication.cs")
if ($LASTEXITCODE -ne 0) { throw "Installer validation failed." }

$sourceRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not bind installer provenance to an exact source commit.'
}
$nativeMetadata = Get-Content -LiteralPath $metadataPath -Raw |
    ConvertFrom-Json
$installerHash = (Get-FileHash -LiteralPath $builtInstaller -Algorithm SHA256).Hash
$installerManifest = [ordered]@{
    schemaVersion = 1
    product = 'DS4Windows'
    displayVersion = $DisplayVersion
    productVersion = $ProductVersion
    bundleVersion = $BundleVersion
    architecture = 'x64'
    sourceRevision = $sourceRevision
    nativeSourceRevision = [string]$nativeMetadata.sourceRevision
    nativeDriverPackageVersion = [string]$nativeMetadata.driverPackageVersion
    nativeDriverBuildIdentity =
        [string]$nativeMetadata.loadedDriverBuildIdentity
    nativeMetadataSha256 =
        (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
    packageManifestSha256 =
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    setupActionsSha256 = $setupActionsHash
    msiSha256 = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
    authenticodeCertificateSha256 = $expectedCertificateSha256
    installer = [ordered]@{
        fileName = [IO.Path]::GetFileName($builtInstaller)
        length = (Get-Item -LiteralPath $builtInstaller).Length
        sha256 = $installerHash
    }
}

Assert-NoReparseDirectoryChain -Path (Split-Path -Parent $outputPath)
if (Test-Path -LiteralPath $outputPath) {
    Assert-NoReparseDirectoryChain -Path $outputPath
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$finalInstaller = Join-Path $outputPath (
    'DS4Windows_' + $DisplayVersion + '_Setup_x64.exe')
$finalManifest = Join-Path $outputPath (
    'DS4Windows_' + $DisplayVersion + '_Setup_x64.manifest.json')
$publishId = [Guid]::NewGuid().ToString("N")
$pendingInstaller = Join-Path $outputPath (
    'DS4Windows_' + $DisplayVersion + '_Setup_x64.pending-' +
    $publishId + '.exe')
$pendingManifest = Join-Path $outputPath (
    'DS4Windows_' + $DisplayVersion + '_Setup_x64.manifest.pending-' +
    $publishId + '.json')

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
    [IO.File]::WriteAllText(
        $pendingManifest,
        ($installerManifest | ConvertTo-Json -Depth 6) +
            [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Assert-AuthenticodeSignature -Path $pendingInstaller `
        -ExpectedCertificateSha256 $expectedCertificateSha256
    if ((Get-FileHash -LiteralPath $pendingInstaller -Algorithm SHA256).Hash -cne
        $installerHash) {
        throw 'The pending installer differs from the verified signed bundle.'
    }

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

Write-Host "Signed production installer ready: $finalInstaller" -ForegroundColor Green
}
finally {
    if ($buildMutexOwned) {
        try { $buildMutex.ReleaseMutex() } catch { }
    }
    $buildMutex.Dispose()
    if ($null -ne $certificate) {
        $certificate.Dispose()
    }
}
