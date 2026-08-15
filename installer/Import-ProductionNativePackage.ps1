[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,
    [Parameter(Mandatory = $true)]
    [string]$ProvenanceManifest,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedProvenanceSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
        throw "Expected a rooted directory: '$Path'."
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
            throw "Protected package path is unsafe: '$cursor'."
        }
    }
}

function Assert-ProtectedSourceAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    $trustedWriteSids = @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
    )
    # Composite Write/Modify/FullControl values contain ordinary
    # read/synchronize bits. Build the mask only from atomic mutation and
    # ACL-control rights so a default Program Files ReadAndExecute ACE does
    # not become a false positive.
    $mutationOrAclControlMask = [long]0
    foreach ($right in @(
        [Security.AccessControl.FileSystemRights]::WriteData,
        [Security.AccessControl.FileSystemRights]::AppendData,
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes,
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles,
        [Security.AccessControl.FileSystemRights]::WriteAttributes,
        [Security.AccessControl.FileSystemRights]::Delete,
        [Security.AccessControl.FileSystemRights]::ChangePermissions,
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    )) {
        $mutationOrAclControlMask =
            $mutationOrAclControlMask -bor [long]$right
    }
    $mutationOrAclControlMask = $mutationOrAclControlMask -bor
        [long]0x10000000 -bor [long]0x40000000
    $acl = Get-Acl -LiteralPath $Path
    $ownerSid = $acl.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -notin $trustedWriteSids) {
        throw "Production native package source has an untrusted owner: '$Path'."
    }
    $rules = @($acl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        $creatorOwnerInheritOnly = $sid -ceq 'S-1-3-0' -and
            ($rule.PropagationFlags -band
             [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
        if ($rule.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            ([long]$rule.FileSystemRights -band
             $mutationOrAclControlMask) -ne 0 -and
            $sid -notin $trustedWriteSids -and
            -not $creatorOwnerInheritOnly) {
            throw "Production native package source grants write access outside trusted installer principals: '$Path'."
        }
    }
}

function Get-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or
        [IO.Path]::IsPathRooted($Value) -or
        $Value.Contains('\') -or $Value.Contains(':') -or
        $Value.StartsWith('/') -or $Value.EndsWith('/')) {
        throw "Unsafe provenance path '$Value'."
    }
    $parts = $Value.Split('/')
    if (@($parts | Where-Object {
        [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..')
    }).Count -ne 0) {
        throw "Unsafe provenance path '$Value'."
    }
    return $Value
}

function Remove-VerifiedStage {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$ExtrasRoot
    )
    $stagePath = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
    $extrasPath = [IO.Path]::GetFullPath($ExtrasRoot).TrimEnd('\')
    $prefix = $extrasPath + [IO.Path]::DirectorySeparatorChar +
        'viiper-native-package.stage-'
    if (-not $stagePath.StartsWith(
            $prefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $stagePath) -ine $extrasPath) {
        throw "Refusing unsafe production package stage cleanup: '$stagePath'."
    }
    if (Test-Path -LiteralPath $stagePath -PathType Container) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}

$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$provenancePath = [IO.Path]::GetFullPath($ProvenanceManifest)
if ($source -ieq [IO.Path]::GetPathRoot($source).TrimEnd('\')) {
    throw 'SourceRoot must be a dedicated protected directory, not a drive root.'
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Production native package source is missing: '$source'."
}
Assert-NoReparseDirectoryChain -Path $source
Assert-ProtectedSourceAcl -Path $source
Assert-OrdinaryFile -Path $provenancePath
if ((Split-Path -Parent $provenancePath) -ine $source) {
    throw 'The production provenance manifest must be an ordinary file directly under SourceRoot.'
}
Assert-ProtectedSourceAcl -Path $provenancePath
$sourceEntries = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
foreach ($entry in $sourceEntries) {
    if (($entry.Attributes -band
         [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Production package source contains a reparse point: '$($entry.FullName)'."
    }
    Assert-ProtectedSourceAcl -Path $entry.FullName
}
$expectedHash = $ExpectedProvenanceSha256.ToUpperInvariant()
if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
    throw 'ExpectedProvenanceSha256 must be 64 hexadecimal characters.'
}
$actualProvenanceHash =
    (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash
if ($actualProvenanceHash -cne $expectedHash) {
    throw "Production provenance SHA-256 is $actualProvenanceHash; expected $expectedHash."
}

$provenance = Get-Content -LiteralPath $provenancePath -Raw |
    ConvertFrom-Json
if ([int]$provenance.schemaVersion -ne 1 -or
    [string]$provenance.packageKind -cne
        'viiper-native-udecx-production' -or
    [string]$provenance.sourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
    [string]$provenance.releaseEligibility -cne 'production') {
    throw 'Production native package provenance header is invalid.'
}
$records = @($provenance.files)
if ($records.Count -eq 0) {
    throw 'Production native package provenance has no files.'
}
$bound = @{}
foreach ($record in $records) {
    $relative = Get-SafeRelativePath -Value ([string]$record.relativePath)
    $folded = $relative.ToLowerInvariant()
    if ($bound.ContainsKey($folded) -or
        [long]$record.length -lt 0 -or
        [string]$record.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Production provenance record is invalid or duplicated: '$relative'."
    }
    $path = [IO.Path]::GetFullPath(
        (Join-Path $source $relative.Replace('/', '\')))
    $prefix = $source + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith(
            $prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Production provenance path escapes its root: '$relative'."
    }
    Assert-OrdinaryFile -Path $path
    Assert-NoReparseDirectoryChain -Path (Split-Path -Parent $path)
    $item = Get-Item -LiteralPath $path -Force
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($item.Length -ne [long]$record.length -or
        $hash.ToLowerInvariant() -cne [string]$record.sha256) {
        throw "Production package file differs from provenance: '$relative'."
    }
    $bound.Add($folded, [pscustomobject]@{
        RelativePath = $relative
        SourcePath = $path
        Length = $item.Length
        Sha256 = $hash
    })
}

$metadataRelative = 'ViiperNativeRuntimeMetadata.json'
if (-not $bound.ContainsKey($metadataRelative.ToLowerInvariant())) {
    throw 'Production provenance does not bind ViiperNativeRuntimeMetadata.json.'
}
$packageRecords = @($bound.Values | Where-Object {
    $_.RelativePath.StartsWith(
        'viiper-native-package/', [StringComparison]::Ordinal)
})
if ($packageRecords.Count -eq 0 -or
    $packageRecords.Count -ne $bound.Count - 1) {
    throw 'Production provenance may bind only metadata and the native package tree.'
}

$actualFiles = @($sourceEntries | Where-Object {
    -not $_.PSIsContainer -and
    [IO.Path]::GetFullPath($_.FullName) -ine $provenancePath
})
if ($actualFiles.Count -ne $bound.Count) {
    throw 'Production package source contains missing or unbound files.'
}
foreach ($file in $actualFiles) {
    Assert-OrdinaryFile -Path $file.FullName
    $relative = $file.FullName.Substring(
        $source.Length + 1).Replace('\', '/').ToLowerInvariant()
    if (-not $bound.ContainsKey($relative)) {
        throw "Production package source contains an unbound file: '$relative'."
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$extrasRoot = Join-Path $repoRoot 'extras'
$destinationPackage = Join-Path $extrasRoot 'viiper-native-package'
$destinationMetadata = Join-Path $extrasRoot 'ViiperNativeRuntimeMetadata.json'
if (Test-Path -LiteralPath $destinationPackage) {
    throw "Fresh release checkout unexpectedly contains '$destinationPackage'."
}
Assert-NoReparseDirectoryChain -Path $extrasRoot
Assert-OrdinaryFile -Path $destinationMetadata

$stage = Join-Path $extrasRoot (
    'viiper-native-package.stage-' +
    [Guid]::NewGuid().ToString('N'))
$metadataPending = $destinationMetadata + '.production-' +
    [Guid]::NewGuid().ToString('N')
$metadataBackup = $destinationMetadata + '.local-backup-' +
    [Guid]::NewGuid().ToString('N')
$packageCommitted = $false
$metadataCommitted = $false
try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    Assert-NoReparseDirectoryChain -Path $stage
    foreach ($record in $packageRecords) {
        $packageRelative = $record.RelativePath.Substring(
            'viiper-native-package/'.Length)
        $destination = Join-Path $stage $packageRelative.Replace('/', '\')
        $parent = Split-Path -Parent $destination
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        Assert-NoReparseDirectoryChain -Path $parent
        Copy-Item -LiteralPath $record.SourcePath -Destination $destination
        Assert-OrdinaryFile -Path $destination
        if ((Get-Item -LiteralPath $destination).Length -ne
                $record.Length -or
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne
                $record.Sha256) {
            throw "Copied production package file differs: '$packageRelative'."
        }
    }
    $metadataRecord = $bound[$metadataRelative.ToLowerInvariant()]
    Copy-Item -LiteralPath $metadataRecord.SourcePath -Destination $metadataPending
    Assert-OrdinaryFile -Path $metadataPending
    if ((Get-FileHash -LiteralPath $metadataPending -Algorithm SHA256).Hash -cne
        $metadataRecord.Sha256) {
        throw 'Copied production metadata differs from provenance.'
    }

    # Commit the package tree immediately before the atomic metadata swap.
    # If the swap fails, the exact just-created destination is removed below;
    # the checked-in local-test metadata therefore remains authoritative.
    [IO.Directory]::Move($stage, $destinationPackage)
    $packageCommitted = $true
    [IO.File]::Replace(
        $metadataPending, $destinationMetadata, $metadataBackup, $true)
    $metadataCommitted = $true
} catch {
    if (-not $metadataCommitted -and $packageCommitted -and
        (Test-Path -LiteralPath $destinationPackage -PathType Container)) {
        $resolvedDestination = [IO.Path]::GetFullPath(
            $destinationPackage).TrimEnd('\')
        if ((Split-Path -Parent $resolvedDestination) -ine $extrasRoot -or
            (Split-Path -Leaf $resolvedDestination) -cne
                'viiper-native-package') {
            throw "Refusing unsafe failed-import cleanup: '$resolvedDestination'."
        }
        Remove-Item -LiteralPath $resolvedDestination -Recurse -Force
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stage -PathType Container) {
        Remove-VerifiedStage -Stage $stage -ExtrasRoot $extrasRoot
    }
    foreach ($pending in @($metadataPending, $metadataBackup)) {
        if (Test-Path -LiteralPath $pending -PathType Leaf) {
            Remove-Item -LiteralPath $pending -Force
        }
    }
}

Write-Host (
    'Imported provenance-bound production native package ' +
    "for source $([string]$provenance.sourceRevision).")
