[CmdletBinding()]
param(
    [ValidateSet('Install', 'Recover', 'Uninstall')]
    [string]$Operation = 'Install',

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^S-[0-9]+(?:-[0-9]+)+$')]
    [string]$TargetUserSID,

    [switch]$AllowLocalTest,
    [switch]$AcknowledgeDisposableTestMachine,

    [string]$RecoveryAuthorizationPath,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedRecoveryAuthorizationSHA256,
    [switch]$RecoveryResume
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSchema = 1
$localTestOptInEnvironment = 'DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST'
$structuredOutcomeWritten = $false
$transactionStarted = $false
$localTestCertificate = $null
$recoveryAuthorizationLease = $null
$recoveryPredecessorLeases = $null

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TrustManagerFileSystemSecurity {
    param(
        [Parameter(Mandatory = $true)][IO.FileSystemInfo]$Item,
        [Parameter(Mandatory = $true)]
        [Security.AccessControl.AccessControlSections]$Sections
    )

    if ($null -ne $Item.PSObject.Methods['GetAccessControl']) {
        return $Item.GetAccessControl($Sections)
    }
    if ($Item -is [IO.DirectoryInfo]) {
        return [IO.FileSystemAclExtensions]::GetAccessControl(
            [IO.DirectoryInfo]$Item, $Sections)
    }
    return [IO.FileSystemAclExtensions]::GetAccessControl(
        [IO.FileInfo]$Item, $Sections)
}

if (-not ('ViiperLocalTestTrustLeaseNative' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class ViiperLocalTestTrustLeaseNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    public static uint LinkCount(SafeFileHandle handle)
    {
        ByHandleFileInformation information;
        if (!GetFileInformationByHandle(handle, out information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return information.NumberOfLinks;
    }
}
'@
}

function Assert-ExactProtectedTrustObjectSecurity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][bool]$Directory
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -ne $Directory -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected trust-manager object has the wrong type or is a reparse point: '$Path'."
    }
    $security = Get-TrustManagerFileSystemSecurity -Item $item -Sections (
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access)
    $owner = $security.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    $group = $security.GetGroup(
        [Security.Principal.SecurityIdentifier]).Value
    if (-not $security.AreAccessRulesProtected -or
        $owner -cne 'S-1-5-32-544' -or $group -cne 'S-1-5-32-544') {
        throw "Protected trust-manager object has an unsafe owner, group, or inherited DACL: '$Path'."
    }
    $rules = @($security.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) {
        throw "Protected trust-manager object has an unexpected access-rule count: '$Path'."
    }
    $expectedInheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
    } else {
        [Security.AccessControl.InheritanceFlags]::None
    }
    foreach ($expectedSid in @('S-1-5-18', 'S-1-5-32-544')) {
        $matches = @($rules | Where-Object {
            $_.IdentityReference.Value -ceq $expectedSid
        })
        if ($matches.Count -ne 1) {
            throw "Protected trust-manager object is missing an exact trusted principal: '$Path'."
        }
        $rule = $matches[0]
        if ($rule.IsInherited -or
            $rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne
                [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne
                [Security.AccessControl.PropagationFlags]::None) {
            throw "Protected trust-manager object has an unexpected access rule: '$Path'."
        }
    }
}

function Assert-LocalTestBootAdmission {
    $bcdeditPath = Join-Path ([Environment]::SystemDirectory) 'bcdedit.exe'
    $bcdOutput = (& $bcdeditPath /enum '{current}' 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        $bcdOutput -notmatch '(?im)^\s*testsigning\s+Yes\s*$') {
        throw "The current boot entry does not report 'testsigning Yes'. Enable TESTSIGNING and reboot before local-test installation.`n$bcdOutput"
    }
}

function ConvertTo-WindowsProcessArgument {
    param([AllowEmptyString()][Parameter(Mandatory = $true)][string]$Value)

    if ($Value.IndexOf([char]0) -ge 0) {
        throw 'Native process argument contains NUL.'
    }
    if ($Value.Length -ne 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append([char]34)
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            ++$slashes
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $slashes) + 1)
            [void]$builder.Append([char]34)
            $slashes = 0
            continue
        }
        if ($slashes -ne 0) {
            [void]$builder.Append([char]92, $slashes)
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -ne 0) {
        [void]$builder.Append([char]92, 2 * $slashes)
    }
    [void]$builder.Append([char]34)
    return $builder.ToString()
}

function Set-ExactProcessArguments {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.ProcessStartInfo]$StartInfo,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    if ($null -ne $StartInfo.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $Arguments) {
            $StartInfo.ArgumentList.Add($argument)
        }
        return
    }
    $StartInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-WindowsProcessArgument -Value $_
    }) -join ' ')
}

function Invoke-JoinedNativeProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][ref]$Started
    )

    $Started.Value = $false
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    Set-ExactProcessArguments -StartInfo $startInfo -Arguments $Arguments
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $joined = $false
    try {
        if (-not $process.Start()) {
            throw 'The protected native broker process was not created.'
        }
        $Started.Value = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $joined) {
            try {
                $process.WaitForExit()
                $joined = $true
            }
            catch {
                # Never unwind while the exact mutating child may remain alive.
                Start-Sleep -Milliseconds 250
            }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $combined = @($stdout, $stderr) -join [Environment]::NewLine
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = @($combined -split '\r?\n' | Where-Object {
                $_.Length -ne 0
            })
        }
    }
    finally {
        if ($Started.Value -and -not $joined) {
            while (-not $joined) {
                try {
                    $process.WaitForExit()
                    $joined = $true
                }
                catch {
                    Start-Sleep -Milliseconds 250
                }
            }
        }
        $process.Dispose()
    }
}

function Resolve-SingleMetadataPath {
    $outputRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $outputRoot 'ViiperNativeRuntimeMetadata.json'),
        (Join-Path $PSScriptRoot 'ViiperNativeRuntimeMetadata.json')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

    $resolved = @($candidates | ForEach-Object {
        (Resolve-Path -LiteralPath $_ -ErrorAction Stop).Path
    } | Select-Object -Unique)
    if ($resolved.Count -ne 1) {
        throw "Expected exactly one bundled ViiperNativeRuntimeMetadata.json; found $($resolved.Count)."
    }
    $item = Get-Item -LiteralPath $resolved[0] -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Runtime metadata must not be a reparse point: '$($item.FullName)'."
    }
    return $item.FullName
}

function Get-UniqueArtifact {
    param(
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $matches = @($Metadata.artifacts | Where-Object { [string]$_.role -ceq $Role })
    if ($matches.Count -ne 1) {
        throw "Runtime metadata must contain exactly one '$Role' artifact; found $($matches.Count)."
    }
    return $matches[0]
}

function Assert-NoReparseDirectoryChain {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$FilePath
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $cursor = Split-Path -Parent ([IO.Path]::GetFullPath($FilePath))
    while ($cursor.Length -ge $rootPath.Length) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Native package directory chain is not an ordinary directory: '$cursor'."
        }
        if ($cursor -ceq $rootPath) {
            return
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -ceq $cursor) {
            break
        }
        $cursor = $parent
    }
    throw "Artifact escaped the native package root: '$FilePath'."
}

function Resolve-VerifiedArtifact {
    param(
        [Parameter(Mandatory = $true)]$Artifact,
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    $relativePath = [string]$Artifact.relativePath
    $sha256 = ([string]$Artifact.sha256).ToLowerInvariant()
    $expectedLength = [long]$Artifact.length
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.IndexOf([char]0) -ge 0 -or
        $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $expectedLength -le 0) {
        throw "Artifact '$([string]$Artifact.role)' has invalid path, length, or SHA-256 metadata."
    }

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
    $candidate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relativePath))
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact '$relativePath' escapes '$root'."
    }
    Assert-NoReparseDirectoryChain -Root $root -FilePath $candidate
    $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Artifact must be an ordinary file: '$candidate'."
    }
    if ($item.Length -ne $expectedLength) {
        throw "Artifact '$relativePath' length is $($item.Length); expected $expectedLength."
    }
    $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $sha256) {
        throw "Artifact '$relativePath' SHA-256 is $actualHash; expected $sha256."
    }
    return $item.FullName
}

function Write-StructuredOutcome {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedOperation,
        [Parameter(Mandatory = $true)][int]$ExitCode
    )

    $outcome = [ordered]@{
        schemaVersion = 1
        operation = $RequestedOperation.ToLowerInvariant()
        exitCode = $ExitCode
        succeeded = ($ExitCode -eq 0)
        rebootRequired = ($ExitCode -eq 3010)
        rollbackStatus = if ($ExitCode -eq 0) {
            'not-required'
        } elseif ($ExitCode -eq 3010) {
            'safely-settled'
        } elseif (-not $script:transactionStarted) {
            'not-started'
        } else {
            'unverified-see-transaction-log'
        }
        manualRecoveryRequired = $script:transactionStarted -and
            $ExitCode -notin @(0, 3010)
    }
    Write-Host ('DS4WINDOWS_VIIPER_NATIVE_RESULT ' +
        ($outcome | ConvertTo-Json -Compress))
    $script:structuredOutcomeWritten = $true
}

function Initialize-ProtectedStage {
    param([Parameter(Mandatory = $true)][string]$ProgramDataRoot)

    $programData = [IO.Path]::GetFullPath($ProgramDataRoot).TrimEnd('\')
    $programDataItem = Get-Item -LiteralPath $programData -Force
    if (-not $programDataItem.PSIsContainer -or
        ($programDataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ProgramData is not a safe staging parent: '$programData'."
    }
    $stage = Join-Path $programData (
        'VIIPER.DS4WindowsStage.' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $stage) {
        throw "Refusing to reuse protected staging directory '$stage'."
    }

    # Apply the protected DACL as part of directory creation. Creating with
    # inherited ProgramData permissions and tightening them afterward leaves
    # a write/reparse race before Set-Acl. Windows PowerShell exposes the
    # Directory.CreateDirectory ACL overload; modern PowerShell exposes the
    # equivalent FileSystemAclExtensions API.
    $expectedSecurity = [Security.AccessControl.DirectorySecurity]::new()
    $expectedSecurity.SetSecurityDescriptorSddlForm(
        'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)',
        [Security.AccessControl.AccessControlSections]::All)
    if ($PSVersionTable.PSEdition -ceq 'Desktop') {
        $directory = [IO.Directory]::CreateDirectory(
            $stage, $expectedSecurity)
        $directory.SetAccessControl($expectedSecurity)
    } else {
        $directory = [IO.DirectoryInfo]::new($stage)
        [IO.FileSystemAclExtensions]::Create(
            $directory, $expectedSecurity)
        [IO.FileSystemAclExtensions]::SetAccessControl(
            $directory, $expectedSecurity)
    }
    Assert-ProtectedStage -StagePath $stage
    return $stage
}

function Assert-ProtectedStage {
    param([Parameter(Mandatory = $true)][string]$StagePath)

    $directory = Get-Item -LiteralPath $StagePath -Force -ErrorAction Stop
    if (-not $directory.PSIsContainer -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected staging directory is missing, not a directory, or a reparse point: '$StagePath'."
    }
    $security = Get-Acl -LiteralPath $directory.FullName
    if (-not $security.AreAccessRulesProtected) {
        throw "Protected staging directory inherited an unsafe DACL: '$StagePath'."
    }
    $owner = $security.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    if ($owner -cne 'S-1-5-32-544') {
        throw "Protected staging directory has an unexpected owner: '$StagePath'."
    }
    $rules = @($security.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) {
        throw "Protected staging directory has an unexpected access-rule count: '$StagePath'."
    }
    $expectedInheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($expectedSid in @('S-1-5-18', 'S-1-5-32-544')) {
        $matches = @($rules | Where-Object {
            $_.IdentityReference.Value -ceq $expectedSid
        })
        if ($matches.Count -ne 1) {
            throw "Protected staging directory is missing an exact trusted principal: '$StagePath'."
        }
        $rule = $matches[0]
        if ($rule.IsInherited -or
            $rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne
                [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne
                [Security.AccessControl.PropagationFlags]::None) {
            throw "Protected staging directory has an unexpected access rule: '$StagePath'."
        }
    }
}

function Open-VerifiedStagedBroker {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][long]$ExpectedLength,
        [Parameter(Mandatory = $true)][string]$ExpectedSHA256
    )

    $destinationPath = Join-Path $DestinationDirectory 'viiper.exe'
    $sourceStream = [IO.FileStream]::new(
        $SourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        if ($sourceStream.Length -ne $ExpectedLength) {
            throw 'The manifest-bound broker changed before protected staging.'
        }
        $sourceAlgorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $sourceDigest = ([BitConverter]::ToString(
                $sourceAlgorithm.ComputeHash($sourceStream))).Replace(
                    '-', '').ToLowerInvariant()
        }
        finally {
            $sourceAlgorithm.Dispose()
        }
        if ($sourceDigest -cne $ExpectedSHA256) {
            throw 'The manifest-bound broker changed before protected staging.'
        }
        $sourceStream.Position = 0
        $destinationStream = [IO.FileStream]::new(
            $destinationPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None,
            1MB, [IO.FileOptions]::WriteThrough)
        try {
            $sourceStream.CopyTo($destinationStream)
            $destinationStream.Flush($true)
        }
        finally {
            $destinationStream.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }

    $launchLock = $null
    try {
        # Hash the same open file object that remains locked through process
        # creation and join. FileShare.Read lets the image loader read it but
        # denies write and delete/rename opens, closing the hash-to-launch
        # pathname race.
        $launchLock = [IO.FileStream]::new(
            $destinationPath, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $staged = Get-Item -LiteralPath $destinationPath -Force -ErrorAction Stop
        if ($staged.PSIsContainer -or
            ($staged.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $launchLock.Length -ne $ExpectedLength) {
            throw 'The protected staged broker is not the expected ordinary file.'
        }
        $stagedAlgorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $stagedDigest = ([BitConverter]::ToString(
                $stagedAlgorithm.ComputeHash($launchLock))).Replace(
                    '-', '').ToLowerInvariant()
        }
        finally {
            $stagedAlgorithm.Dispose()
        }
        if ($stagedDigest -cne $ExpectedSHA256) {
            throw 'The protected staged broker failed exact verification.'
        }
        $launchLock.Position = 0
        return [pscustomobject]@{
            Path = $destinationPath
            LaunchLock = $launchLock
        }
    }
    catch {
        if ($null -ne $launchLock) {
            $launchLock.Dispose()
        }
        throw
    }
}

function New-ProtectedLocalTestTrustCapability {
    param(
        [Parameter(Mandatory = $true)][string]$StagePath,
        [Parameter(Mandatory = $true)][string]$SourceRevision,
        [Parameter(Mandatory = $true)][string]$CertificatePath,
        [Parameter(Mandatory = $true)][string]$CertificateSHA256,
        [Parameter(Mandatory = $true)][string]$PackageLockSHA256,
        [Parameter(Mandatory = $true)][string]$TrustJournalDirectory
    )

    Assert-ProtectedStage -StagePath $StagePath
    $path = Join-Path $StagePath 'local-test-trust-capability.json'
    if ((Split-Path -Parent $path) -ine $StagePath -or
        (Test-Path -LiteralPath $path)) {
        throw 'Refusing to reuse or redirect a local-test trust capability.'
    }
    $parentProcess = Get-Process -Id $PID -ErrorAction Stop
    $parentCreationFileTime = [uint64]$parentProcess.StartTime.ToUniversalTime().ToFileTimeUtc()
    if ($parentCreationFileTime -eq 0) {
        throw 'The local-test trust capability parent creation time is invalid.'
    }
    $value = [ordered]@{
        schema = 'viiper.native.local-test-trust-capability/v1'
        nonce = [Guid]::NewGuid().ToString('N')
        parentPid = [uint32]$PID
        parentCreationFileTime = $parentCreationFileTime
        sourceRevision = $SourceRevision.ToLowerInvariant()
        certificatePath = [IO.Path]::GetFullPath($CertificatePath)
        certificateSha256 = $CertificateSHA256.ToLowerInvariant()
        packageLockSha256 = $PackageLockSHA256.ToLowerInvariant()
        trustJournalSchema = 'viiper.native.local-test-trust-ownership/v1'
        trustJournalDirectory = [IO.Path]::GetFullPath($TrustJournalDirectory)
    }
    $json = $value | ConvertTo-Json -Compress
    if ($json.IndexOf("`r") -ge 0 -or $json.IndexOf("`n") -ge 0) {
        throw 'The local-test trust capability is not single-line canonical JSON.'
    }
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString(
            $algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetSecurityDescriptorSddlForm(
        'O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)',
        [Security.AccessControl.AccessControlSections]::All)
    $stream = $null
    try {
        if ($PSVersionTable.PSEdition -ceq 'Desktop') {
            $stream = [IO.FileStream]::new(
                $path, [IO.FileMode]::CreateNew,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [IO.FileShare]::Read, 4096,
                [IO.FileOptions]::WriteThrough, $security)
        } else {
            $stream = [IO.FileSystemAclExtensions]::Create(
                [IO.FileInfo]::new($path), [IO.FileMode]::CreateNew,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [IO.FileShare]::Read, 4096,
                [IO.FileOptions]::WriteThrough, $security)
        }
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        Assert-ExactProtectedTrustObjectSecurity -Path $path -Directory $false
        if ($stream.Length -ne $bytes.Length -or
            [ViiperLocalTestTrustLeaseNative]::LinkCount(
                $stream.SafeFileHandle) -ne 1) {
            throw 'The protected local-test trust capability changed after creation.'
        }
        return [pscustomobject]@{ Path = $path; SHA256 = $hash; Stream = $stream }
    }
    catch {
        if ($null -ne $stream) { $stream.Dispose() }
        throw
    }
}

function Assert-ExactRecoveryJsonObjectProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$ExpectedNames,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($null -eq $Value -or
        $Value -isnot [Management.Automation.PSCustomObject]) {
        throw "Recovery authorization $Label is not one exact JSON object."
    }
    $actualNames = @($Value.PSObject.Properties |
        ForEach-Object { [string]$_.Name })
    $expected = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($name in $ExpectedNames) {
        if (-not $expected.Add($name)) {
            throw "Recovery authorization $Label contract repeats '$name'."
        }
    }
    if ($actualNames.Count -ne $ExpectedNames.Count) {
        throw "Recovery authorization $Label has missing or unknown fields."
    }
    foreach ($name in $actualNames) {
        if (-not $expected.Contains($name)) {
            throw "Recovery authorization $Label has unknown field '$name'."
        }
    }
}

function Assert-ExactR4FailedInstallRecoveryAuthorization {
    param(
        [Parameter(Mandatory = $true)][string]$AuthorizationText,
        [Parameter(Mandatory = $true)]$Authorization,
        [Parameter(Mandatory = $true)][string]$CurrentViiperSourceRevision,
        [Parameter(Mandatory = $true)][string]$CurrentPackageLockSHA256,
        [Parameter(Mandatory = $true)][string]$CurrentBundleManifestSHA256,
        [Parameter(Mandatory = $true)][string]$CurrentCertificateSHA256,
        [Parameter(Mandatory = $true)][string]$ExpectedMachine,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetUserSID,
        [Parameter(Mandatory = $true)][bool]$Resume
    )

    $topNames = @(
        'schema', 'status', 'retryPermitted', 'firstAuthorizedUtc',
        'currentBundleManifestSha256', 'currentViiperSourceRevision',
        'currentPackageLockSha256', 'predecessor',
        'predecessorCertificateSha256', 'machine', 'targetUserSid',
        'trustBeforeNativeAttempt', 'resume', 'updatedUtc'
    )
    if ($Resume) { $topNames += 'recoveryRootAuthorizationSha256' }
    $predecessorNames = @(
        'predecessorEvidenceRoot', 'installEvidenceDirectory', 'statePath',
        'stateSha256', 'commandSha256', 'resultSha256', 'stdoutSha256',
        'stderrSha256', 'bundleManifestSha256', 'viiperSourceRevision',
        'ds4WindowsSourceRevision', 'packageLockSha256'
    )
    $trustNames = @('Root', 'TrustedPublisher')

    # ConvertFrom-Json can collapse duplicate properties. Scan the same locked
    # UTF-8 text before trusting its object model and require every simple,
    # canonical property name exactly once across this fixed schema.
    $allNames = @($topNames + $predecessorNames + $trustNames)
    $allowedNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($name in $allNames) { [void]$allowedNames.Add($name) }
    $seenNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $keyPattern = '"(?<name>(?:\\["\\/bfnrt]|\\u[0-9a-fA-F]{4}|[^"\\\x00-\x1f])*)"\s*:'
    $keyMatches = [regex]::Matches(
        $AuthorizationText, $keyPattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    foreach ($match in $keyMatches) {
        $name = [string]$match.Groups['name'].Value
        if ($name.IndexOf('\') -ge 0 -or
            -not $allowedNames.Contains($name) -or
            -not $seenNames.Add($name)) {
            throw "Recovery authorization has an escaped, unknown, or duplicate JSON field '$name'."
        }
    }
    if ($keyMatches.Count -ne $allNames.Count -or
        $seenNames.Count -ne $allNames.Count) {
        throw 'Recovery authorization has missing, unknown, or duplicate JSON fields.'
    }

    Assert-ExactRecoveryJsonObjectProperties -Value $Authorization `
        -ExpectedNames $topNames -Label 'root'
    Assert-ExactRecoveryJsonObjectProperties -Value $Authorization.predecessor `
        -ExpectedNames $predecessorNames -Label 'predecessor'
    Assert-ExactRecoveryJsonObjectProperties `
        -Value $Authorization.trustBeforeNativeAttempt `
        -ExpectedNames $trustNames -Label 'trust admission'

    $r4 = [ordered]@{
        predecessorEvidenceRoot = 'C:\Users\hbash\Documents\Codex\2026-08-15\the\outputs\VIIPER-Win11-9481f9d-272f6a0-r4'
        installEvidenceDirectory = 'C:\Users\hbash\Documents\Codex\2026-08-15\the\outputs\VIIPER-Win11-9481f9d-272f6a0-r4\steps\20260816T034608909Z-install-27fffa05b7e544feb3c5a415ebd1f6c4'
        statePath = 'C:\Users\hbash\Documents\Codex\2026-08-15\the\outputs\VIIPER-Win11-9481f9d-272f6a0-r4\state\validation-state.json'
        stateSha256 = 'e13c686a0cddcf66620940005568b3a7a9a41abb277f61977dd88994863d8cda'
        commandSha256 = 'c38579b1504c8851dd72317d49f4439d14b7878b4e19907ebe864c8ad986e3f7'
        resultSha256 = '1095194f448455f746b5af92b89ae4f08f8f69a7ba9fac1d17a90d73e8a971b0'
        # This exact stdout digest binds changed=0, rebootRequired=0,
        # rollback=not-needed, exitCode=4, phase=install-journal-broker-image-hash,
        # win32Error=23, and the immutable-broker-digest failure message.
        stdoutSha256 = 'ca95fac3b8bd6fe7871a7f42400031f01ea946dc88786e9e9a746084144c205b'
        stderrSha256 = '2610d56f76be3c1aea4f6b3dd4e4b38d134a1d311133ac46f389a28f8faeb520'
        bundleManifestSha256 = '765de4fe822004e97940fa66ba73602dafd68194d14fd64e20b388444cd4c247'
        viiperSourceRevision = '9481f9dbfde64af99905fa325546e50b5ea03d6e'
        ds4WindowsSourceRevision = '272f6a05f1476d5aa9c055a234e61c292d3c1556'
        packageLockSha256 = '16e08c31bb1c240a3612a6c4ddc8219b040d0e2dec5773e39f363d045113ab8c'
        certificateSha256 = '09ca0c2d4d3da29268eff59cf85b6c1347d4a28ddc098b8640381694ad74c517'
    }
    $predecessor = $Authorization.predecessor
    if ([string]$Authorization.schema -cne
            'viiper.windows11.failed-install-recovery-progress/v1' -or
        [string]$Authorization.status -cne 'native-attempt' -or
        $Authorization.retryPermitted -isnot [bool] -or
        $Authorization.retryPermitted -ne $true -or
        $Authorization.resume -isnot [bool] -or
        [bool]$Authorization.resume -ne $Resume -or
        [string]$Authorization.currentViiperSourceRevision -cne
            $CurrentViiperSourceRevision.ToLowerInvariant() -or
        [string]$Authorization.currentPackageLockSha256 -cne
            $CurrentPackageLockSHA256.ToLowerInvariant() -or
        [string]$Authorization.currentBundleManifestSha256 -cne
            $CurrentBundleManifestSHA256.ToLowerInvariant() -or
        [string]$Authorization.predecessorCertificateSha256 -cne
            $r4.certificateSha256 -or
        [string]$Authorization.predecessorCertificateSha256 -cne
            $CurrentCertificateSHA256.ToLowerInvariant() -or
        [string]$Authorization.machine -cne $ExpectedMachine -or
        [string]$Authorization.targetUserSid -cne $ExpectedTargetUserSID -or
        [string]$predecessor.predecessorEvidenceRoot -ine
            $r4.predecessorEvidenceRoot -or
        [string]$predecessor.installEvidenceDirectory -ine
            $r4.installEvidenceDirectory -or
        [string]$predecessor.statePath -ine $r4.statePath -or
        [string]$predecessor.stateSha256 -cne $r4.stateSha256 -or
        [string]$predecessor.commandSha256 -cne $r4.commandSha256 -or
        [string]$predecessor.resultSha256 -cne $r4.resultSha256 -or
        [string]$predecessor.stdoutSha256 -cne $r4.stdoutSha256 -or
        [string]$predecessor.stderrSha256 -cne $r4.stderrSha256 -or
        [string]$predecessor.bundleManifestSha256 -cne
            $r4.bundleManifestSha256 -or
        [string]$predecessor.viiperSourceRevision -cne
            $r4.viiperSourceRevision -or
        [string]$predecessor.ds4WindowsSourceRevision -cne
            $r4.ds4WindowsSourceRevision -or
        [string]$predecessor.packageLockSha256 -cne
            $r4.packageLockSha256) {
        throw 'Recovery authorization does not bind the exact manifest-known R4 failed-install predecessor and failure proof.'
    }

    foreach ($timestampName in @('firstAuthorizedUtc', 'updatedUtc')) {
        try {
            $timestampValue = $Authorization.$timestampName
            if ($timestampValue -is [DateTime]) {
                [void]([DateTime]$timestampValue).ToUniversalTime()
            } elseif ($timestampValue -is [DateTimeOffset]) {
                [void]([DateTimeOffset]$timestampValue).ToUniversalTime()
            } else {
                [void][DateTimeOffset]::ParseExact(
                    [string]$timestampValue, 'o',
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind)
            }
        }
        catch {
            throw "Recovery authorization has invalid '$timestampName'."
        }
    }
    $trust = $Authorization.trustBeforeNativeAttempt
    if (($trust.Root -isnot [int] -and $trust.Root -isnot [long]) -or
        ($trust.TrustedPublisher -isnot [int] -and
         $trust.TrustedPublisher -isnot [long])) {
        throw 'Recovery authorization trust admission is not integral.'
    }
    if (-not $Resume -and
        ([int]$trust.Root -ne 1 -or [int]$trust.TrustedPublisher -ne 1)) {
        throw 'Initial recovery authorization does not bind exact trust 1/1.'
    }
    if ($Resume -and
        ([int]$trust.Root -notin @(0, 1) -or
         [int]$trust.TrustedPublisher -notin @(0, 1) -or
         [string]$Authorization.recoveryRootAuthorizationSha256 -cnotmatch
            '^[0-9a-f]{64}$')) {
        throw 'Recovery retry authorization has invalid trust or root authority.'
    }
}

function Open-ExactR4FailedInstallEvidenceLeases {
    param(
        [Parameter(Mandatory = $true)]$Authorization,
        [Parameter(Mandatory = $true)][string]$ExpectedMachine,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetUserSID
    )

    $predecessor = $Authorization.predecessor
    $evidence = @(
        [pscustomobject]@{
            Label = 'state'
            Path = [string]$predecessor.statePath
            SHA256 = [string]$predecessor.stateSha256
            State = $true
        },
        [pscustomobject]@{
            Label = 'command'
            Path = Join-Path ([string]$predecessor.installEvidenceDirectory) `
                'command.json'
            SHA256 = [string]$predecessor.commandSha256
            State = $false
        },
        [pscustomobject]@{
            Label = 'result'
            Path = Join-Path ([string]$predecessor.installEvidenceDirectory) `
                'result.json'
            SHA256 = [string]$predecessor.resultSha256
            State = $false
        },
        [pscustomobject]@{
            Label = 'stdout'
            Path = Join-Path ([string]$predecessor.installEvidenceDirectory) `
                'stdout.log'
            SHA256 = [string]$predecessor.stdoutSha256
            State = $false
        },
        [pscustomobject]@{
            Label = 'stderr'
            Path = Join-Path ([string]$predecessor.installEvidenceDirectory) `
                'stderr.log'
            SHA256 = [string]$predecessor.stderrSha256
            State = $false
        }
    )
    $streams = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach ($entry in $evidence) {
            $item = Get-Item -LiteralPath $entry.Path -Force -ErrorAction Stop
            if ($item.PSIsContainer -or
                ($item.Attributes -band
                 [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $item.Length -le 0 -or $item.Length -gt 16777216) {
                throw "Exact R4 predecessor $($entry.Label) evidence is not one bounded ordinary file."
            }
            $stream = [IO.FileStream]::new(
                $item.FullName, [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $streams.Add($stream)
            if ([ViiperLocalTestTrustLeaseNative]::LinkCount(
                    $stream.SafeFileHandle) -ne 1) {
                throw "Exact R4 predecessor $($entry.Label) evidence has multiple hard links."
            }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $digest = ([BitConverter]::ToString(
                    $algorithm.ComputeHash($stream))).Replace(
                        '-', '').ToLowerInvariant()
            }
            finally {
                $algorithm.Dispose()
            }
            $stream.Position = 0
            if ($digest -cne ([string]$entry.SHA256).ToLowerInvariant()) {
                throw "Exact R4 predecessor $($entry.Label) evidence differs from its compiled digest."
            }
            if (-not [bool]$entry.State) { continue }
            $stateBytes = [byte[]]::new([int]$stream.Length)
            $offset = 0
            while ($offset -lt $stateBytes.Length) {
                $read = $stream.Read(
                    $stateBytes, $offset, $stateBytes.Length - $offset)
                if ($read -le 0) {
                    throw 'Exact R4 predecessor state ended before its locked length.'
                }
                $offset += $read
            }
            $stream.Position = 0
            $stateText = [Text.UTF8Encoding]::new(
                $false, $true).GetString($stateBytes)
            $state = $stateText | ConvertFrom-Json -ErrorAction Stop
            if ([string]$state.schema -cne
                    'viiper.windows11.validation-state/v1' -or
                [string]$state.machine -cne $ExpectedMachine -or
                [string]$state.targetUserSid -cne $ExpectedTargetUserSID) {
                throw 'Exact R4 predecessor state does not bind this machine and target user.'
            }
        }
        return [IO.FileStream[]]$streams.ToArray()
    }
    catch {
        for ($index = $streams.Count - 1; $index -ge 0; --$index) {
            $streams[$index].Dispose()
        }
        throw
    }
}

function Close-ExactR4FailedInstallEvidenceLeases {
    param([IO.FileStream[]]$Streams)

    if ($null -eq $Streams) { return }
    for ($index = $Streams.Count - 1; $index -ge 0; --$index) {
        if ($null -ne $Streams[$index]) { $Streams[$index].Dispose() }
    }
}

function New-ProtectedFailedInstallRecoveryCapability {
    param(
        [Parameter(Mandatory = $true)][string]$StagePath,
        [Parameter(Mandatory = $true)][string]$LeasePath,
        [Parameter(Mandatory = $true)][string]$SourceRevision,
        [Parameter(Mandatory = $true)][string]$HelperSHA256,
        [Parameter(Mandatory = $true)][string]$CertificateSHA256,
        [Parameter(Mandatory = $true)][string]$AuthorizationSHA256,
        [Parameter(Mandatory = $true)][string]$RootAuthorizationSHA256,
        [Parameter(Mandatory = $true)][string]$PackageLockSHA256,
        [Parameter(Mandatory = $true)][string]$BundleManifestSHA256,
        [Parameter(Mandatory = $true)][bool]$AllowPartialCertificateState
    )

    Assert-ProtectedStage -StagePath $StagePath
    $path = Join-Path $StagePath 'failed-install-recovery-capability.json'
    if ((Split-Path -Parent $path) -ine $StagePath -or
        (Test-Path -LiteralPath $path)) {
        throw 'Refusing to reuse or redirect a failed-install recovery capability.'
    }
    $parentProcess = Get-Process -Id $PID -ErrorAction Stop
    $parentCreationFileTime =
        [uint64]$parentProcess.StartTime.ToUniversalTime().ToFileTimeUtc()
    if ($parentCreationFileTime -eq 0) {
        throw 'The failed-install recovery capability parent creation time is invalid.'
    }
    $value = [ordered]@{
        schema = 'viiper.native.failed-install-recovery-capability/v1'
        nonce = [Guid]::NewGuid().ToString('N')
        parentPid = [uint32]$PID
        parentCreationFileTime = $parentCreationFileTime
        leasePath = [IO.Path]::GetFullPath($LeasePath)
        sourceRevision = $SourceRevision.ToLowerInvariant()
        helperSha256 = $HelperSHA256.ToLowerInvariant()
        certificateSha256 = $CertificateSHA256.ToLowerInvariant()
        recoveryAuthorizationSha256 = $AuthorizationSHA256.ToLowerInvariant()
        recoveryRootAuthorizationSha256 =
            $RootAuthorizationSHA256.ToLowerInvariant()
        packageLockSha256 = $PackageLockSHA256.ToLowerInvariant()
        bundleManifestSha256 = $BundleManifestSHA256.ToLowerInvariant()
        allowPartialCertificateState = $AllowPartialCertificateState
    }
    $json = $value | ConvertTo-Json -Compress
    if ($json.IndexOf("`r") -ge 0 -or $json.IndexOf("`n") -ge 0) {
        throw 'The failed-install recovery capability is not single-line canonical JSON.'
    }
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString(
            $algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetSecurityDescriptorSddlForm(
        'O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)',
        [Security.AccessControl.AccessControlSections]::All)
    $stream = $null
    try {
        if ($PSVersionTable.PSEdition -ceq 'Desktop') {
            $stream = [IO.FileStream]::new(
                $path, [IO.FileMode]::CreateNew,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [IO.FileShare]::Read, 4096,
                [IO.FileOptions]::WriteThrough, $security)
        } else {
            $stream = [IO.FileSystemAclExtensions]::Create(
                [IO.FileInfo]::new($path), [IO.FileMode]::CreateNew,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [IO.FileShare]::Read, 4096,
                [IO.FileOptions]::WriteThrough, $security)
        }
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        Assert-ExactProtectedTrustObjectSecurity -Path $path -Directory $false
        if ($stream.Length -ne $bytes.Length -or
            [ViiperLocalTestTrustLeaseNative]::LinkCount(
                $stream.SafeFileHandle) -ne 1) {
            throw 'The protected failed-install recovery capability changed after creation.'
        }
        return [pscustomobject]@{ Path = $path; SHA256 = $hash; Stream = $stream }
    }
    catch {
        if ($null -ne $stream) { $stream.Dispose() }
        throw
    }
}

function Remove-ProtectedStage {
    param(
        [Parameter(Mandatory = $true)][string]$StagePath,
        [Parameter(Mandatory = $true)][string]$ProgramDataRoot
    )

    $stage = [IO.Path]::GetFullPath($StagePath).TrimEnd('\')
    $programData = [IO.Path]::GetFullPath($ProgramDataRoot).TrimEnd('\')
    $prefix = $programData + [IO.Path]::DirectorySeparatorChar +
        'VIIPER.DS4WindowsStage.'
    if (-not $stage.StartsWith(
            $prefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $stage) -ine $programData -or
        (Split-Path -Leaf $stage) -cnotmatch
            '^VIIPER\.DS4WindowsStage\.[0-9a-f]{32}$') {
        throw "Refusing to remove an unverified staging directory: '$stage'."
    }
    if (Test-Path -LiteralPath $stage) {
        Assert-ProtectedStage -StagePath $stage
        $children = @(Get-ChildItem -LiteralPath $stage -Force)
        $allowed = @(
            'local-test-trust-capability.json',
            'failed-install-recovery-capability.json',
            'viiper.exe')
        if ($children.Count -gt 2 -or @($children | Where-Object {
                $_.PSIsContainer -or
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $_.Name -cnotin $allowed
            }).Count -ne 0) {
            throw "Refusing protected staging cleanup with unexpected entries: '$stage'."
        }
        foreach ($child in $children) {
            [IO.File]::Delete($child.FullName)
        }
        [IO.Directory]::Delete($stage, $false)
    }
}

trap {
    $primaryFailure = $_.Exception
    if ($null -ne $script:recoveryPredecessorLeases) {
        Close-ExactR4FailedInstallEvidenceLeases `
            -Streams $script:recoveryPredecessorLeases
        $script:recoveryPredecessorLeases = $null
    }
    if ($null -ne $script:recoveryAuthorizationLease) {
        $script:recoveryAuthorizationLease.Dispose()
        $script:recoveryAuthorizationLease = $null
    }
    if (-not $script:structuredOutcomeWritten) {
        Write-StructuredOutcome -RequestedOperation $Operation -ExitCode 1
    }
    Write-Host ("VIIPER native UDE setup stopped: " +
        $primaryFailure.Message) -ForegroundColor Red
    if ($null -ne $script:localTestCertificate) {
        $script:localTestCertificate.Dispose()
        $script:localTestCertificate = $null
    }
    exit 1
}

if (-not [Environment]::Is64BitOperatingSystem -or
    -not [Environment]::Is64BitProcess) {
    throw 'VIIPER native UDE setup requires a 64-bit DS4Windows process on 64-bit Windows.'
}
if (-not (Test-IsAdministrator)) {
    throw 'VIIPER native UDE setup must run from the administrator prompt started by DS4Windows.'
}

$sid = [Security.Principal.SecurityIdentifier]::new($TargetUserSID)
if ($sid.IsWellKnown([Security.Principal.WellKnownSidType]::LocalSystemSid) -or
    $sid.IsWellKnown([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)) {
    throw 'TargetUserSID must name the interactive DS4Windows user, not a system principal.'
}

$metadataPath = Resolve-SingleMetadataPath
$metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$sourceRevision = [string]$metadata.sourceRevision
$driverPackageVersion = [string]$metadata.driverPackageVersion
$driverABIMajor = [int]$metadata.driverAbi.major
$driverABIMinor = [int]$metadata.driverAbi.minor
$driverCapabilities = [uint32]$metadata.requiredCapabilities
$driverCapabilitiesHex = '0x{0:x8}' -f $driverCapabilities
$driverBuildIdentity = [string]$metadata.loadedDriverBuildIdentity
if ([int]$metadata.schemaVersion -ne $expectedSchema -or
    [string]$metadata.localTestOptInEnvironment -cne
        $localTestOptInEnvironment -or
    $sourceRevision -cnotmatch '^[0-9a-f]{40}$|^[0-9a-f]{64}$' -or
    $driverPackageVersion -cnotmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
    $driverABIMajor -le 0 -or $driverABIMajor -gt 65535 -or
    $driverABIMinor -lt 0 -or $driverABIMinor -gt 65535 -or
    $driverCapabilities -eq 0 -or
    [string]$metadata.requiredCapabilitiesHex -cne $driverCapabilitiesHex -or
    $driverBuildIdentity -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Bundled VIIPER metadata has an invalid source/package/ABI/capability/build-identity contract.'
}
if ([string]$metadata.managedBroker.serviceName -cne 'VIIPERNativeBroker' -or
    [string]$metadata.managedBroker.serviceAccount -cne 'LocalSystem' -or
    [string]$metadata.managedBroker.startMode -cne 'automatic' -or
    [string]$metadata.managedBroker.transport -cne 'native-ude' -or
    [string]$metadata.managedBroker.apiHost -cne '127.0.0.1' -or
    [int]$metadata.managedBroker.apiPort -ne 3242 -or
    [string]$metadata.managedBroker.credentialPath -cne
        '%ProgramData%/VIIPER/viiper.key.txt') {
    throw 'Bundled VIIPER metadata has an invalid managed LocalSystem broker contract.'
}

$controllerContract = $metadata.controllerApiContract
$expectedControllerRegistrations = [ordered]@{
    xbox360 = 'xbox360|0x045e|0x028e|0x028e|xusb-composite|fixed'
    dualshock4 = 'dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|fixed'
    dualshock4audioduplexv3 = 'dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|framed-v3'
    dualshock4audioonlyduplexv3 = 'dualshock4|0x054c|0x09cc|0x05c4|audio-duplex-only|framed-v3'
    dualsensecombinedaudioduplexv5 = 'dualsense|0x054c|0x0ce6|0x0ce6|hid-audio-duplex|framed-v5'
    dualsenseaudioonlyduplexv5 = 'dualsense|0x054c|0x0ce6|0x0ce6|audio-duplex-only|framed-v5'
    dualsensegamepadv5 = 'dualsense|0x054c|0x0ce6|0x0ce6|hid-gamepad-only|framed-v5'
    dualsenseedgecombinedaudioduplexv5 = 'dualsense-edge|0x054c|0x0df2|0x0df2|hid-audio-duplex|framed-v5'
    dualsenseedgegamepadv5 = 'dualsense-edge|0x054c|0x0df2|0x0df2|hid-gamepad-only|framed-v5'
    ns2pro = 'switch2-pro|0x057e|0x2069|0x2069|hid-vendor-bulk|fixed'
}
$actualControllerTypes = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
if ([int]$controllerContract.schemaVersion -ne 1 -or
    [string]$controllerContract.sourceRevision -cne $sourceRevision) {
    throw 'Bundled VIIPER metadata has an invalid source-bound controller API contract.'
}
foreach ($registration in @($controllerContract.registrations)) {
    $type = [string]$registration.type
    if (-not $actualControllerTypes.Add($type) -or
        -not $expectedControllerRegistrations.Contains($type)) {
        throw "Bundled controller API contract has unexpected or duplicate type '$type'."
    }
    $signature = @(
        [string]$registration.persona,
        [string]$registration.defaultVid,
        [string]$registration.defaultPid,
        [string]$registration.ds4WindowsPid,
        [string]$registration.interfaceProfile,
        [string]$registration.streamProtocol
    ) -join '|'
    if ($signature -cne [string]$expectedControllerRegistrations[$type]) {
        throw "Bundled controller API type '$type' diverges from its VIIPER HID/interface implementation."
    }
}
if ($actualControllerTypes.Count -ne $expectedControllerRegistrations.Count) {
    throw 'Bundled controller API contract omits a DS4Windows controller persona.'
}

$eligibility = [string]$metadata.releaseEligibility
$driverValidationMode = 'production'
if ($eligibility -ceq 'production') {
    if ($AllowLocalTest -or $AcknowledgeDisposableTestMachine) {
        throw 'Local-test switches cannot be combined with production VIIPER metadata.'
    }
} elseif ($eligibility -ceq 'local-test-evidence-only') {
    if (-not $AllowLocalTest -or -not $AcknowledgeDisposableTestMachine -or
        [Environment]::GetEnvironmentVariable($localTestOptInEnvironment) -cne '1') {
        throw "This bundle is local-test evidence only. A developer must set $localTestOptInEnvironment=1 and pass both -AllowLocalTest and -AcknowledgeDisposableTestMachine on a disposable VM."
    }
    $driverValidationMode = 'local-test'
} else {
    throw "Unsupported VIIPER release eligibility '$eligibility'."
}
if ($Operation -cne 'Recover' -and
    (-not [string]::IsNullOrWhiteSpace($RecoveryAuthorizationPath) -or
     -not [string]::IsNullOrWhiteSpace($ExpectedRecoveryAuthorizationSHA256) -or
     $RecoveryResume)) {
    throw 'Recovery authorization parameters are valid only for Operation Recover.'
}

$packageRoot = Join-Path $PSScriptRoot 'viiper-native-package'
$packageRootItem = Get-Item -LiteralPath $packageRoot -Force -ErrorAction Stop
if (-not $packageRootItem.PSIsContainer -or
    ($packageRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The bundled VIIPER native package root is missing or unsafe: '$packageRoot'."
}

if (@('Install', 'Recover') -ccontains $Operation) {
    $boundPackageFiles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $boundRoles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($artifact in @($metadata.artifacts)) {
        $role = [string]$artifact.role
        if ($role -cnotmatch '^[a-z0-9-]+$' -or -not $boundRoles.Add($role)) {
            throw "Native metadata has invalid or duplicate artifact role '$role'."
        }
        $verifiedPath = Resolve-VerifiedArtifact -Artifact $artifact -PackageRoot $packageRoot
        if (-not $boundPackageFiles.Add($verifiedPath)) {
            throw "Native metadata binds duplicate package path '$verifiedPath'."
        }
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $packageRoot -Directory -Recurse -Force)) {
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Native package contains unsafe directory '$($directory.FullName)'."
        }
    }
    $actualPackageFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force)
    if ($actualPackageFiles.Count -ne $boundPackageFiles.Count) {
        throw "Native package inventory has $($actualPackageFiles.Count) files but metadata binds $($boundPackageFiles.Count)."
    }
    foreach ($file in $actualPackageFiles) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $boundPackageFiles.Contains($file.FullName)) {
            throw "Native package contains unbound or unsafe file '$($file.FullName)'."
        }
    }
}

$brokerArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'broker'
$helperArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-helper'
$brokerPath = Resolve-VerifiedArtifact -Artifact $brokerArtifact -PackageRoot $packageRoot
$helperPath = Resolve-VerifiedArtifact -Artifact $helperArtifact -PackageRoot $packageRoot
$helperHash = ([string]$helperArtifact.sha256).ToLowerInvariant()
if ((Split-Path -Leaf $brokerPath) -cne 'viiper.exe' -or
    (Split-Path -Leaf $helperPath) -cne 'ViiperUdeCtl.exe') {
    throw 'Native metadata must bind viiper.exe and ViiperUdeCtl.exe by their canonical names.'
}

$programDataRoot = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonApplicationData)
$trustJournalDirectory = Join-Path $programDataRoot 'VIIPER-TrustManager'
$trustManagerLeasePath = Join-Path $trustJournalDirectory 'lease-v1.lock'
# PowerShell only binds these fixed paths into parent capabilities. The native
# child initializes and lifetime-owns Trust -> Package -> Service, the durable
# ownership journal, and all LocalMachine certificate-store mutation.

$arguments = @()
if (@('Install', 'Recover') -ccontains $Operation) {
    $manifestArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'submission-manifest'
    $infArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-inf'
    $sysArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-sys'
    $catArtifact = Get-UniqueArtifact -Metadata $metadata -Role 'driver-cat'
    $manifestPath = Resolve-VerifiedArtifact -Artifact $manifestArtifact -PackageRoot $packageRoot
    $infPath = Resolve-VerifiedArtifact -Artifact $infArtifact -PackageRoot $packageRoot
    $sysPath = Resolve-VerifiedArtifact -Artifact $sysArtifact -PackageRoot $packageRoot
    $catPath = Resolve-VerifiedArtifact -Artifact $catArtifact -PackageRoot $packageRoot
    $driverDirectory = Split-Path -Parent $infPath
    if ((Split-Path -Leaf $manifestPath) -cne 'submission-manifest.json' -or
        (Split-Path -Leaf $infPath) -cne 'ViiperUde.inf' -or
        (Split-Path -Leaf $sysPath) -cne 'ViiperUde.sys' -or
        (Split-Path -Leaf $catPath) -cne 'ViiperUde.cat' -or
        (Split-Path -Parent $sysPath) -ine $driverDirectory -or
        (Split-Path -Parent $catPath) -ine $driverDirectory) {
        throw 'Native metadata must bind the canonical submission manifest and one co-located INF/SYS/CAT driver package.'
    }

    $submission = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json -ErrorAction Stop
    if ([string]$submission.sourceRevision -cne $sourceRevision -or
        [string]$submission.driverPackageVersion -cne $driverPackageVersion -or
        [int]$submission.driverABIMajor -ne $driverABIMajor -or
        [int]$submission.driverABIMinor -ne $driverABIMinor -or
        [string]$submission.driverCapabilities -cne $driverCapabilitiesHex -or
        [string]$submission.driverBuildIdentity -cne $driverBuildIdentity) {
        throw 'The source-bound submission manifest disagrees with bundled runtime metadata.'
    }
    if ($driverValidationMode -ceq 'production') {
        if ($submission.releaseEligible -ne $true -or
            [string]$submission.signingRoute -notmatch 'HLK|WHCP|Microsoft') {
            throw 'Production installation requires a release-eligible HLK/WHCP submission manifest.'
        }
        if (@($metadata.artifacts | Where-Object {
            [string]$_.role -eq 'local-test-certificate-evidence'
        }).Count -ne 0) {
            throw 'Production runtime metadata must not reference a local-test certificate.'
        }
        $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catPath
        if ($catalogSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $catalogSignature.SignerCertificate -or
            $catalogSignature.SignerCertificate.Subject -notmatch
                'Microsoft Windows Hardware Compatibility Publisher') {
            throw 'Production ViiperUde.cat is not signed by Microsoft Windows Hardware Compatibility Publisher.'
        }
    } elseif ($submission.releaseEligible -ne $false -or
        [string]$submission.signingRoute -cne 'LocalTest') {
        throw 'Local-test installation requires the non-release-eligible LocalTest submission manifest.'
    }

    if ($driverValidationMode -ceq 'local-test') {
        $certificateArtifact = Get-UniqueArtifact -Metadata $metadata `
            -Role 'local-test-certificate-evidence'
        $localTestPackageLockArtifact = Get-UniqueArtifact -Metadata $metadata `
            -Role 'local-test-package-lock'
        $localTestPackageLockPath = Resolve-VerifiedArtifact `
            -Artifact $localTestPackageLockArtifact -PackageRoot $packageRoot
        $certificatePath = Resolve-VerifiedArtifact `
            -Artifact $certificateArtifact -PackageRoot $packageRoot
        if ((Split-Path -Leaf $localTestPackageLockPath) -cne
                'local-test-package.lock.json' -or
            (Split-Path -Leaf $certificatePath) -cne 'ViiperUdeTest.cer' -or
            [string]$submission.testSignerCertificateSha256 -cne
                ([string]$certificateArtifact.sha256).ToLowerInvariant()) {
            throw 'The local-test signer certificate disagrees with the source-bound package evidence.'
        }
        $script:localTestCertificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $certificatePath)
        if ($script:localTestCertificate.HasPrivateKey) {
            throw 'The local-test package must contain only the public signer certificate.'
        }
        $certificateAlgorithm =
            [Security.Cryptography.SHA256]::Create()
        try {
            $certificateSha256 = ([BitConverter]::ToString(
                $certificateAlgorithm.ComputeHash(
                    $script:localTestCertificate.RawData))).Replace(
                        '-', '').ToLowerInvariant()
        }
        finally {
            $certificateAlgorithm.Dispose()
        }
        if ($certificateSha256 -cne
            ([string]$certificateArtifact.sha256).ToLowerInvariant()) {
            throw 'The parsed local-test signer certificate bytes differ from their package hash.'
        }
        $localTestPackageLockSha256 =
            ([string]$localTestPackageLockArtifact.sha256).ToLowerInvariant()
        if ($Operation -ceq 'Install') {
            Assert-LocalTestBootAdmission
        }
    }

    if ($Operation -ceq 'Install') {
        $arguments = @(
            'native-package-install',
            '--package-directory', $driverDirectory,
            '--submission-manifest', $manifestPath,
            '--source-revision', $sourceRevision,
            '--driver-helper', $helperPath,
            '--expected-broker-sha-256', ([string]$brokerArtifact.sha256).ToLowerInvariant(),
            '--expected-helper-sha-256', $helperHash,
            '--expected-manifest-sha-256', ([string]$manifestArtifact.sha256).ToLowerInvariant(),
            '--expected-inf-sha-256', ([string]$infArtifact.sha256).ToLowerInvariant(),
            '--expected-sys-sha-256', ([string]$sysArtifact.sha256).ToLowerInvariant(),
            '--expected-cat-sha-256', ([string]$catArtifact.sha256).ToLowerInvariant(),
            '--target-user-sid', $TargetUserSID,
            '--driver-validation-mode', $driverValidationMode
        )
    }
    else {
        # Recovery is deliberately journal-only. The source-bound package is
        # still validated in full above, but the broker may invoke only the
        # exact verified helper's recover path.
        if ($driverValidationMode -cne 'local-test') {
            throw 'Operation Recover is restricted to an exact local-test failed-install certificate rollback.'
        }
        if ([string]::IsNullOrWhiteSpace($RecoveryAuthorizationPath) -or
            [string]::IsNullOrWhiteSpace(
                $ExpectedRecoveryAuthorizationSHA256)) {
            throw 'Operation Recover requires one exact recovery authorization path and SHA-256.'
        }
        $authorizationItem = Get-Item -LiteralPath $RecoveryAuthorizationPath `
            -Force -ErrorAction Stop
        if ($authorizationItem.PSIsContainer -or
            ($authorizationItem.Attributes -band
             [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Recovery authorization must be an ordinary local file.'
        }
        $authorizationPath = $authorizationItem.FullName
        if ((Split-Path -Leaf $authorizationPath) -cne
                'failed-install-recovery-progress.json') {
            throw 'Recovery authorization has the wrong canonical file name.'
        }
        $script:recoveryAuthorizationLease = [IO.FileStream]::new(
            $authorizationPath, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($script:recoveryAuthorizationLease.Length -le 0 -or
            $script:recoveryAuthorizationLease.Length -gt 262144) {
            throw 'Recovery authorization length is outside its exact bounded contract.'
        }
        $authorizationBytes = [byte[]]::new(
            [int]$script:recoveryAuthorizationLease.Length)
        $authorizationOffset = 0
        while ($authorizationOffset -lt $authorizationBytes.Length) {
            $read = $script:recoveryAuthorizationLease.Read(
                $authorizationBytes, $authorizationOffset,
                $authorizationBytes.Length - $authorizationOffset)
            if ($read -le 0) {
                throw 'Recovery authorization ended before its locked length.'
            }
            $authorizationOffset += $read
        }
        $authorizationAlgorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $authorizationHash = ([BitConverter]::ToString(
                $authorizationAlgorithm.ComputeHash(
                    $authorizationBytes))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $authorizationAlgorithm.Dispose()
        }
        if ($authorizationHash -cne
            $ExpectedRecoveryAuthorizationSHA256.ToLowerInvariant()) {
            throw 'Recovery authorization bytes differ from their caller-bound SHA-256.'
        }
        $authorizationText = [Text.UTF8Encoding]::new(
            $false, $true).GetString($authorizationBytes)
        $authorization = $authorizationText |
            ConvertFrom-Json -ErrorAction Stop
        $packageLockArtifact = Get-UniqueArtifact -Metadata $metadata `
            -Role 'local-test-package-lock'
        $bundleManifestCandidate = Join-Path (Split-Path -Parent $PSScriptRoot) `
            'bundle-manifest.json'
        $bundleManifestItem = Get-Item -LiteralPath $bundleManifestCandidate `
            -Force -ErrorAction Stop
        if ($bundleManifestItem.PSIsContainer -or
            ($bundleManifestItem.Attributes -band
             [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Current recovery bundle manifest must be an ordinary file.'
        }
        $bundleManifestHash = (Get-FileHash `
            -LiteralPath $bundleManifestItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-ExactR4FailedInstallRecoveryAuthorization `
            -AuthorizationText $authorizationText `
            -Authorization $authorization `
            -CurrentViiperSourceRevision $sourceRevision `
            -CurrentPackageLockSHA256 (
                ([string]$packageLockArtifact.sha256).ToLowerInvariant()) `
            -CurrentBundleManifestSHA256 $bundleManifestHash `
            -CurrentCertificateSHA256 (
                ([string]$certificateArtifact.sha256).ToLowerInvariant()) `
            -ExpectedMachine $env:COMPUTERNAME `
            -ExpectedTargetUserSID $TargetUserSID `
            -Resume ([bool]$RecoveryResume)
        $script:recoveryPredecessorLeases = @(
            Open-ExactR4FailedInstallEvidenceLeases `
                -Authorization $authorization `
                -ExpectedMachine $env:COMPUTERNAME `
                -ExpectedTargetUserSID $TargetUserSID)
        if ($script:recoveryPredecessorLeases.Count -ne 5) {
            throw 'Recovery did not retain all five exact R4 predecessor evidence leases.'
        }
        $rootAuthorizationHash = if ($RecoveryResume) {
            ([string]$authorization.recoveryRootAuthorizationSha256).ToLowerInvariant()
        } else {
            $authorizationHash
        }
        if ($rootAuthorizationHash -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Recovery authorization has an invalid stable root-authorization binding.'
        }
        $arguments = @(
            'native-package-recover',
            '--driver-helper', $helperPath,
            '--expected-helper-sha-256', $helperHash,
            '--certificate-path', $certificatePath,
            '--expected-certificate-sha-256',
                ([string]$certificateArtifact.sha256).ToLowerInvariant(),
            '--recovery-authorization', $authorizationPath,
            '--expected-recovery-authorization-sha-256', $authorizationHash,
            '--recovery-root-authorization-sha-256', $rootAuthorizationHash,
            '--source-revision', $sourceRevision,
            '--current-package-lock-sha-256',
                ([string]$packageLockArtifact.sha256).ToLowerInvariant(),
            '--current-bundle-manifest-sha-256', $bundleManifestHash
        )
        if ($RecoveryResume) {
            $arguments += '--allow-partial-certificate-state'
        }
    }
} else {
    $arguments = @(
        'uninstall', '--yes',
        '--target-user-sid', $TargetUserSID,
        '--driver-helper', $helperPath,
        '--expected-helper-sha-256', $helperHash
    )
}

if ($Operation -ceq 'Uninstall' -and
    $driverValidationMode -ceq 'local-test') {
    $certificateArtifact = Get-UniqueArtifact -Metadata $metadata `
        -Role 'local-test-certificate-evidence'
    $localTestPackageLockArtifact = Get-UniqueArtifact -Metadata $metadata `
        -Role 'local-test-package-lock'
    $certificatePath = Resolve-VerifiedArtifact `
        -Artifact $certificateArtifact -PackageRoot $packageRoot
    $localTestPackageLockPath = Resolve-VerifiedArtifact `
        -Artifact $localTestPackageLockArtifact -PackageRoot $packageRoot
    if ((Split-Path -Leaf $certificatePath) -cne 'ViiperUdeTest.cer' -or
        (Split-Path -Leaf $localTestPackageLockPath) -cne
            'local-test-package.lock.json') {
        throw 'Local-test Uninstall requires the canonical certificate and package-lock artifacts.'
    }
    $script:localTestCertificate =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certificatePath)
    if ($script:localTestCertificate.HasPrivateKey) {
        throw 'The local-test package must contain only the public signer certificate.'
    }
    $certificateAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $certificateSha256 = ([BitConverter]::ToString(
            $certificateAlgorithm.ComputeHash(
                $script:localTestCertificate.RawData))).Replace(
                    '-', '').ToLowerInvariant()
    }
    finally {
        $certificateAlgorithm.Dispose()
    }
    if ($certificateSha256 -cne
        ([string]$certificateArtifact.sha256).ToLowerInvariant()) {
        throw 'The parsed Uninstall certificate differs from its source-bound artifact hash.'
    }
    $localTestPackageLockSha256 =
        ([string]$localTestPackageLockArtifact.sha256).ToLowerInvariant()
    $arguments += @(
        '--source-revision', $sourceRevision,
        '--local-test-certificate-path', $certificatePath,
        '--expected-local-test-certificate-sha-256', $certificateSha256,
        '--expected-local-test-package-lock-sha-256',
            $localTestPackageLockSha256
    )
}

$stagePath = $null
$stagedBrokerLease = $null
$trustCapability = $null
$recoveryCapability = $null
$exitCode = 1
try {
    $stagePath = Initialize-ProtectedStage -ProgramDataRoot $programDataRoot
    $stagedBrokerLease = Open-VerifiedStagedBroker `
        -SourcePath $brokerPath -DestinationDirectory $stagePath `
        -ExpectedLength ([long]$brokerArtifact.length) `
        -ExpectedSHA256 ([string]$brokerArtifact.sha256).ToLowerInvariant()
    if ($Operation -ceq 'Install' -and
        $driverValidationMode -ceq 'local-test') {
        $trustCapability = New-ProtectedLocalTestTrustCapability `
            -StagePath $stagePath `
            -SourceRevision $sourceRevision `
            -CertificatePath $certificatePath `
            -CertificateSHA256 $certificateSha256 `
            -PackageLockSHA256 $localTestPackageLockSha256 `
            -TrustJournalDirectory $trustJournalDirectory
        $arguments += @(
            '--local-test-trust-capability', $trustCapability.Path,
            '--expected-trust-capability-sha-256', $trustCapability.SHA256,
            '--local-test-certificate-path', $certificatePath,
            '--expected-local-test-certificate-sha-256', $certificateSha256,
            '--expected-local-test-package-lock-sha-256',
                $localTestPackageLockSha256
        )
    } elseif ($Operation -ceq 'Recover') {
        $recoveryCapability = New-ProtectedFailedInstallRecoveryCapability `
            -StagePath $stagePath -LeasePath $trustManagerLeasePath `
            -SourceRevision $sourceRevision -HelperSHA256 $helperHash `
            -CertificateSHA256 $certificateSha256 `
            -AuthorizationSHA256 $authorizationHash `
            -RootAuthorizationSHA256 $rootAuthorizationHash `
            -PackageLockSHA256 $localTestPackageLockSha256 `
            -BundleManifestSHA256 $bundleManifestHash `
            -AllowPartialCertificateState ([bool]$RecoveryResume)
        $arguments += @(
            '--recovery-capability', $recoveryCapability.Path,
            '--expected-recovery-capability-sha-256',
                $recoveryCapability.SHA256
        )
    }

    Write-Host "Running VIIPER native UDE $($Operation.ToLowerInvariant()) transaction..."
    $processStarted = $false
    try {
        $processResult = Invoke-JoinedNativeProcess `
            -FileName $stagedBrokerLease.Path -Arguments $arguments `
            -WorkingDirectory $stagePath -Started ([ref]$processStarted)
    }
    finally {
        $script:transactionStarted = $processStarted
    }
    $output = @($processResult.Output)
    $exitCode = [int]$processResult.ExitCode
    $output | ForEach-Object { Write-Host ([string]$_) }
} finally {
    $stageCleanupErrors = [Collections.Generic.List[Exception]]::new()
    if ($null -ne $recoveryCapability) {
        try {
            $recoveryCapability.Stream.Dispose()
        }
        catch {
            $stageCleanupErrors.Add([InvalidOperationException]::new(
                'Failed to release the parent-bound recovery capability.',
                $_.Exception))
        }
        $recoveryCapability = $null
    }
    if ($null -ne $trustCapability) {
        try {
            $trustCapability.Stream.Dispose()
        }
        catch {
            $stageCleanupErrors.Add([InvalidOperationException]::new(
                'Failed to release the parent-bound trust capability.',
                $_.Exception))
        }
        $trustCapability = $null
    }
    if ($null -ne $stagedBrokerLease) {
        try {
            $stagedBrokerLease.LaunchLock.Dispose()
        }
        catch {
            $stageCleanupErrors.Add([InvalidOperationException]::new(
                'Failed to release the verified staged-broker launch lock.',
                $_.Exception))
        }
        $stagedBrokerLease = $null
    }
    if ($null -ne $stagePath) {
        try {
            Remove-ProtectedStage -StagePath $stagePath `
                -ProgramDataRoot $programDataRoot
        }
        catch {
            $stageCleanupErrors.Add([InvalidOperationException]::new(
                'Failed to remove the exact protected broker stage.',
                $_.Exception))
        }
    }
    if ($stageCleanupErrors.Count -eq 1) {
        throw $stageCleanupErrors[0]
    }
    if ($stageCleanupErrors.Count -gt 1) {
        throw [AggregateException]::new(
            'Protected broker stage cleanup had multiple failures.',
            [Exception[]]$stageCleanupErrors.ToArray())
    }
}

if ($null -ne $script:recoveryAuthorizationLease) {
    $script:recoveryAuthorizationLease.Dispose()
    $script:recoveryAuthorizationLease = $null
}
if ($null -ne $script:recoveryPredecessorLeases) {
    Close-ExactR4FailedInstallEvidenceLeases `
        -Streams $script:recoveryPredecessorLeases
    $script:recoveryPredecessorLeases = $null
}

Write-StructuredOutcome -RequestedOperation $Operation -ExitCode $exitCode
if ($null -ne $script:localTestCertificate) {
    $script:localTestCertificate.Dispose()
    $script:localTestCertificate = $null
}

if ($exitCode -eq 0) {
    if ($Operation -ceq 'Recover') {
        Write-Host 'VIIPER retained native journals were reconciled and the recovery admission proved no current or successor VIIPER topology.'
    }
    else {
        Write-Host 'VIIPER native UDE transaction completed and authenticated service readiness was verified by the package transaction.'
    }
} elseif ($exitCode -eq 3010) {
    Write-Warning 'VIIPER stopped at a safe reboot boundary before mutation or after successful rollback. Restart Windows, then rerun this identical transaction.'
} else {
    Write-Warning "VIIPER native UDE transaction failed with exit code $exitCode. Review the protected transaction/recovery logs before retrying."
}
exit $exitCode
