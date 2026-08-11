[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleRoot,

    [Parameter(Mandatory = $true)]
    [string]$LockPath,

    [string]$ContractPath = (Join-Path $PSScriptRoot `
        '..\DS4Windows\DS4Control\Viiper\ViiperNativePackageContract.cs'),

    [string]$StaticValidatorPath = (Join-Path $PSScriptRoot `
        'viiper_native_bundle.py')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$python = (Get-Command python -ErrorAction Stop).Source
& $python $StaticValidatorPath validate `
    --bundle-root $BundleRoot --lock $LockPath --contract $ContractPath
if ($LASTEXITCODE -ne 0) {
    throw 'The VIIPER native bundle failed its immutable static contract.'
}

$root = (Resolve-Path -LiteralPath $BundleRoot -ErrorAction Stop).Path
$lockFile = (Resolve-Path -LiteralPath $LockPath -ErrorAction Stop).Path
$lock = Get-Content -LiteralPath $lockFile -Raw | ConvertFrom-Json
$expectedUserSigner = [string]$lock.signing.userModeSignerCertificateSha256
$signTool = (Get-Command signtool.exe -ErrorAction Stop).Source

# Re-resolve the immutable GitHub release and asset object, then independently
# hash a fresh download. The checked-in archive hash alone is content-safe but
# cannot prove which upstream object was reviewed.
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'DS4Windows-native-bundle-validator'
    'X-GitHub-Api-Version' = '2022-11-28'
}
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
    $headers.Authorization = "Bearer $($env:GITHUB_TOKEN)"
}
$repository = [string]$lock.provenance.repository
$releaseId = [long]$lock.provenance.releaseId
$assetId = [long]$lock.provenance.releaseAssetId
$releaseUri = "https://api.github.com/repos/$repository/releases/$releaseId"
$release = Invoke-RestMethod -Uri $releaseUri -Headers $headers -Method Get
if ([long]$release.id -ne $releaseId -or
    [string]$release.tag_name -cne [string]$lock.provenance.releaseTag) {
    throw 'The immutable GitHub release ID/tag no longer resolves to the reviewed object.'
}
$tagName = [string]$lock.provenance.releaseTag
$encodedTag = [Uri]::EscapeDataString($tagName)
$tagRef = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/$repository/git/ref/tags/$encodedTag" `
    -Headers $headers -Method Get
$tagObject = $tagRef.object
for ($depth = 0; $depth -lt 8 -and
        [string]$tagObject.type -ceq 'tag'; $depth++) {
    $annotatedTag = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repository/git/tags/$([string]$tagObject.sha)" `
        -Headers $headers -Method Get
    $tagObject = $annotatedTag.object
}
if ([string]$tagObject.type -cne 'commit' -or
    [string]$tagObject.sha -cne [string]$lock.provenance.sourceRevision) {
    throw 'The immutable GitHub release tag does not resolve to the reviewed VIIPER source revision.'
}
$assets = @($release.assets | Where-Object { [long]$_.id -eq $assetId })
if ($assets.Count -ne 1 -or
    [string]$assets[0].name -cne [string]$lock.provenance.releaseAssetName -or
    [string]$assets[0].digest -cne [string]$lock.provenance.releaseAssetApiDigest) {
    throw 'The immutable GitHub asset ID/name/API digest does not match the reviewed object.'
}
$download = Join-Path ([IO.Path]::GetTempPath()) `
    ("viiper-native-release-" + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    # VIIPER is public. Do not forward the repository-scoped Actions token
    # through the release-download redirect to the object-storage host.
    $downloadHeaders = @{
        'User-Agent' = 'DS4Windows-native-bundle-validator'
    }
    Invoke-WebRequest -Uri ([string]$assets[0].browser_download_url) `
        -Headers $downloadHeaders -OutFile $download
    $downloadHash = (Get-FileHash -LiteralPath $download `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadHash -cne [string]$lock.provenance.releaseAssetSha256) {
        throw 'The independently downloaded GitHub release archive does not match the immutable lock.'
    }
    & $python $StaticValidatorPath validate-archive `
        --archive $download --bundle-root $BundleRoot `
        --lock $LockPath --contract $ContractPath
    if ($LASTEXITCODE -ne 0) {
        throw 'The GitHub release archive is not exactly the local locked six-file runtime.'
    }
}
finally {
    if (Test-Path -LiteralPath $download) {
        Remove-Item -LiteralPath $download -Force
    }
}

function Get-CertificateSHA256(
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($Certificate.RawData))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-CertificateEkuOids(
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $result = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -cne '2.5.29.37') { continue }
        $eku = if ($extension -is
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            $extension
        }
        else {
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $extension, $false)
        }
        foreach ($oid in $eku.EnhancedKeyUsages) {
            [void]$result.Add($oid.Value)
        }
    }
    return ,$result
}

foreach ($name in @('viiper.exe', 'ViiperUdeCtl.exe')) {
    $path = Join-Path $root $name
    & $signTool verify /pa /all /v $path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode policy validation failed for '$name' (exit $LASTEXITCODE)."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Native user-mode artifact '$name' lacks a valid timestamped Authenticode signature."
    }
    $actualSigner = Get-CertificateSHA256 $signature.SignerCertificate
    if ($actualSigner -cne $expectedUserSigner) {
        throw "Native user-mode artifact '$name' does not match the immutable signer allowlist."
    }
    $ekus = Get-CertificateEkuOids $signature.SignerCertificate
    if (-not $ekus.Contains('1.3.6.1.5.5.7.3.3')) {
        throw "Native user-mode artifact '$name' signer lacks the Code Signing EKU."
    }
}

$hardwareVerificationOid = '1.3.6.1.4.1.311.10.3.5'
$attestedVerificationOid = '1.3.6.1.4.1.311.10.3.5.1'
foreach ($name in @('ViiperUde.sys', 'ViiperUde.cat')) {
    $path = Join-Path (Join-Path $root 'driver') $name
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch
            '(?i)(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw "Native driver artifact '$name' is not validly signed by Microsoft Corporation."
    }
    $ekus = Get-CertificateEkuOids $signature.SignerCertificate
    if (-not $ekus.Contains($hardwareVerificationOid) -or
        $ekus.Contains($attestedVerificationOid)) {
        throw "Native driver artifact '$name' is not a production HLK/WHCP signature."
    }
}

$driverRoot = Join-Path $root 'driver'
$catalog = Join-Path $driverRoot 'ViiperUde.cat'
$inf = Join-Path $driverRoot 'ViiperUde.inf'
$sys = Join-Path $driverRoot 'ViiperUde.sys'
foreach ($path in @($catalog, $sys)) {
    & $signTool verify /kp /v $path
    if ($LASTEXITCODE -ne 0) {
        throw "Kernel-policy signature validation failed for '$path' (exit $LASTEXITCODE)."
    }
}
foreach ($path in @($inf, $sys)) {
    & $signTool verify /kp /v /c $catalog $path
    if ($LASTEXITCODE -ne 0) {
        throw "'$path' is not a verified member of the immutable VIIPER catalog."
    }
}

Write-Host (
    "Validated the exact hash-locked, timestamped user-mode and Microsoft " +
    "HLK/WHCP VIIPER native bundle at '$root'.")
