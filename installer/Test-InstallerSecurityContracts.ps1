[CmdletBinding()]
param([string]$RepositoryRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..'))
}

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-SharingViolation {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $blocked = $false
    try {
        & $Action
    }
    catch [IO.IOException] {
        $blocked = $true
    }
    Assert-Contract -Condition $blocked -Message $Message
}

function Get-SourceSlice {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Start,
        [Parameter(Mandatory = $true)][string]$End
    )
    $startIndex = $Source.IndexOf($Start, [StringComparison]::Ordinal)
    $endIndex = $Source.IndexOf(
        $End, $startIndex + $Start.Length, [StringComparison]::Ordinal)
    Assert-Contract -Condition ($startIndex -ge 0 -and $endIndex -gt $startIndex) `
        -Message "Could not isolate security-contract source between '$Start' and '$End'."
    return $Source.Substring($startIndex, $endIndex - $startIndex)
}

function Test-ExactTestDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OwnerSid,
        [Parameter(Mandatory = $true)][string]$SecondSid
    )
    $security = Get-Acl -LiteralPath $Path
    if (-not $security.AreAccessRulesProtected -or
        $security.GetOwner(
            [Security.Principal.SecurityIdentifier]).Value -cne $OwnerSid) {
        return $false
    }
    $rules = @($security.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) {
        return $false
    }
    $expected = @{$OwnerSid = $true; $SecondSid = $true}
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if (-not $expected.ContainsKey($sid) -or
            $rule.IsInherited -or
            $rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne
                [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne $inheritance -or
            $rule.PropagationFlags -ne
                [Security.AccessControl.PropagationFlags]::None) {
            return $false
        }
        [void]$expected.Remove($sid)
    }
    return $expected.Count -eq 0
}

if ($env:OS -cne 'Windows_NT') {
    throw 'Installer security contracts require Windows ACL and share-mode semantics.'
}

if (-not ('Ds4WindowsInstallerSecurityContractNative' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class Ds4WindowsInstallerSecurityContractNative
{
    private const uint OpenExisting = 3;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    public static SafeFileHandle OpenDirectoryNoDelete(string path)
    {
        SafeFileHandle handle = CreateFileW(path, FileListDirectory,
            FileShare.Read | FileShare.Write, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }
}
'@
}

$repo = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar)
$setupActionsPath = Join-Path $repo `
    'installer\DS4Windows.SetupActions\Program.cs'
$importPath = Join-Path $repo 'installer\Import-ProductionNativePackage.ps1'
$probePath = Join-Path $repo `
    'installer\DS4Windows.Bootstrapper\InfrastructureProbe.cs'
$managerPath = Join-Path $repo 'extras\manage-viiper-native-package.ps1'
foreach ($path in @($setupActionsPath, $importPath, $probePath, $managerPath)) {
    Assert-Contract -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
        -Message "Security-contract source is missing: '$path'."
}

$atomicMutationMask = [long]0
$atomicRights = @(
    [Security.AccessControl.FileSystemRights]::WriteData,
    [Security.AccessControl.FileSystemRights]::AppendData,
    [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes,
    [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles,
    [Security.AccessControl.FileSystemRights]::WriteAttributes,
    [Security.AccessControl.FileSystemRights]::Delete,
    [Security.AccessControl.FileSystemRights]::ChangePermissions,
    [Security.AccessControl.FileSystemRights]::TakeOwnership
)
foreach ($right in $atomicRights) {
    $atomicMutationMask = $atomicMutationMask -bor [long]$right
}
Assert-Contract -Condition ($atomicMutationMask -eq [long]0x000D0156) `
    -Message 'The atomic filesystem mutation mask changed unexpectedly.'
$mutationOrAclControlMask = $atomicMutationMask -bor
    [long]0x10000000 -bor [long]0x40000000
$readAndExecute =
    [long][Security.AccessControl.FileSystemRights]::ReadAndExecute
Assert-Contract -Condition (
        ($readAndExecute -band $mutationOrAclControlMask) -eq 0) `
    -Message 'ReadAndExecute incorrectly overlaps the protected-path mutation mask.'
foreach ($right in $atomicRights) {
    Assert-Contract -Condition (
            ([long]$right -band $mutationOrAclControlMask) -ne 0) `
        -Message "Atomic mutation right '$right' escaped the protected-path mask."
}

# Exercise the ACL predicate against the default Program Files DACL. This is
# the regression for ordinary Users/application-package ReadAndExecute grants:
# they must be accepted while every untrusted mutation grant remains rejected.
$trustedWriteSids = @(
    'S-1-5-18',
    'S-1-5-32-544',
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
)
$programFiles = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFiles)
$programFilesAcl = Get-Acl -LiteralPath $programFiles
$ownerSid = $programFilesAcl.GetOwner(
    [Security.Principal.SecurityIdentifier]).Value
Assert-Contract -Condition ($ownerSid -in $trustedWriteSids) `
    -Message 'The default Program Files owner is outside the installer trust boundary.'
$ordinaryReadGrantObserved = $false
$programFilesRules = @($programFilesAcl.GetAccessRules(
    $true, $true, [Security.Principal.SecurityIdentifier]))
foreach ($rule in $programFilesRules) {
    if ($rule.AccessControlType -ne
        [Security.AccessControl.AccessControlType]::Allow) {
        continue
    }
    $sid = $rule.IdentityReference.Value
    $rights = [long]$rule.FileSystemRights
    $creatorOwnerInheritOnly = $sid -ceq 'S-1-3-0' -and
        ($rule.PropagationFlags -band
         [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
    if ($sid -notin $trustedWriteSids -and
        -not $creatorOwnerInheritOnly) {
        Assert-Contract -Condition (
                ($rights -band $mutationOrAclControlMask) -eq 0) `
            -Message "Default Program Files grants an untrusted mutation right to '$sid'."
        if (($rights -band $readAndExecute) -ne 0) {
            $ordinaryReadGrantObserved = $true
        }
    }
}
Assert-Contract -Condition $ordinaryReadGrantObserved `
    -Message 'Default Program Files exposed no ordinary untrusted ReadAndExecute regression case.'

$setupActions = Get-Content -LiteralPath $setupActionsPath -Raw
$setupAcl = Get-SourceSlice -Source $setupActions `
    -Start 'private static void RequireProtectedAcl(' `
    -End 'private static VerifiedMedia VerifyProtectedNativeMedia('
foreach ($token in @(
    'FileSystemRights.WriteData',
    'FileSystemRights.AppendData',
    'FileSystemRights.WriteExtendedAttributes',
    'FileSystemRights.DeleteSubdirectoriesAndFiles',
    'FileSystemRights.WriteAttributes',
    'FileSystemRights.Delete',
    'FileSystemRights.ChangePermissions',
    'FileSystemRights.TakeOwnership',
    'genericWrite',
    'genericAll'
)) {
    Assert-Contract -Condition $setupAcl.Contains($token) `
        -Message "SetupActions lost atomic ACL token '$token'."
}
Assert-Contract -Condition (
        $setupActions.Contains('AccessControlSections.Owner |') -and
        $setupActions.Contains('AccessControlSections.Access)')) `
    -Message 'SetupActions ACL reads do not request both owner and access sections.'
foreach ($token in @(
    'FileSystemRights.Write |',
    'FileSystemRights.Modify |',
    'FileSystemRights.FullControl |'
)) {
    Assert-Contract -Condition (-not $setupAcl.Contains($token)) `
        -Message "SetupActions restored unsafe composite ACL mask '$token'."
}

$setupLog = Get-SourceSlice -Source $setupActions `
    -Start 'private static void InitializeProtectedLog()' `
    -End 'private sealed class PackageManifest'
foreach ($token in @(
    'OpenAndValidateOrdinaryDirectory(programData)',
    'CreateOrLockProtectedDirectory(productDirectory,',
    'CreateOrLockProtectedDirectory(installerDirectory,',
    'Directory.CreateDirectory(path, expectedSecurity)',
    'OpenDirectoryWithoutDeleteSharing(',
    'FileFlagOpenReparsePoint',
    'FileFlagBackupSemantics',
    'FileShare.Read | FileShare.Write',
    'FileMode.CreateNew',
    'FileShare.Read, 4096, FileOptions.WriteThrough,',
    'stream.GetAccessControl()',
    'RequireExactLogSecurity(',
    'stream.SetLength(0)',
    'logStream.Write(bytes, 0, bytes.Length)',
    'DisposeProtectedLog()',
    'CreateFileW('
)) {
    Assert-Contract -Condition $setupLog.Contains($token) `
        -Message "SetupActions lost protected log token '$token'."
}
foreach ($token in @(
    'Directory.SetAccessControl',
    'File.Delete(logPath)',
    'File.Create(logPath)',
    'File.AppendAllText',
    'FileShare.Delete',
    'private static string logPath'
)) {
    Assert-Contract -Condition (-not $setupLog.Contains($token)) `
        -Message "SetupActions restored path-racy log behavior '$token'."
}
$logInitializationOrder = @(
    $setupLog.IndexOf('OpenAndValidateOrdinaryDirectory(programData)'),
    $setupLog.IndexOf('CreateOrLockProtectedDirectory(productDirectory,'),
    $setupLog.IndexOf('CreateOrLockProtectedDirectory(installerDirectory,'),
    $setupLog.IndexOf('OpenOrCreateProtectedLogFile(')
)
Assert-Contract -Condition (
        $logInitializationOrder[0] -ge 0 -and
        (@($logInitializationOrder | Sort-Object) -join ',') -ceq
            ($logInitializationOrder -join ',')) `
    -Message 'SetupActions does not lock the protected log hierarchy from root to file.'
$existingLogOrder = @(
    $setupLog.IndexOf('existingHandle = CreateFileW(path,'),
    $setupLog.IndexOf('stream = new FileStream(existingHandle,'),
    $setupLog.IndexOf('var attributes = File.GetAttributes(path);'),
    $setupLog.IndexOf('var actualSecurity = stream.GetAccessControl();'),
    $setupLog.IndexOf('stream.SetLength(0);')
)
Assert-Contract -Condition (
        $existingLogOrder[0] -ge 0 -and
        (@($existingLogOrder | Sort-Object) -join ',') -ceq
            ($existingLogOrder -join ',')) `
    -Message 'SetupActions existing-log lock/validate/truncate order is unsafe.'

$productionImport = Get-Content -LiteralPath $importPath -Raw
$importAcl = Get-SourceSlice -Source $productionImport `
    -Start 'function Assert-ProtectedSourceAcl {' `
    -End 'function Get-SafeRelativePath {'
foreach ($token in @(
    '::WriteData',
    '::AppendData',
    '::WriteExtendedAttributes',
    '::DeleteSubdirectoriesAndFiles',
    '::WriteAttributes',
    '::Delete',
    '::ChangePermissions',
    '::TakeOwnership',
    '[long]0x10000000',
    '[long]0x40000000',
    '$acl.GetAccessRules(',
    '[Security.Principal.SecurityIdentifier]'
)) {
    Assert-Contract -Condition $importAcl.Contains($token) `
        -Message "Production import lost atomic ACL token '$token'."
}
foreach ($token in @('::Write -bor', '::Modify -bor', '::FullControl -bor')) {
    Assert-Contract -Condition (-not $importAcl.Contains($token)) `
        -Message "Production import restored unsafe composite ACL mask '$token'."
}

$probe = Get-Content -LiteralPath $probePath -Raw
$brokerBlock = Get-SourceSlice -Source $probe `
    -Start 'var brokerPath = Path.Combine(' `
    -End 'var credentialPath = Path.Combine('
$credentialBlock = Get-SourceSlice -Source $probe `
    -Start 'var credentialPath = Path.Combine(' `
    -End 'var logPath = Path.Combine('
$logBlock = Get-SourceSlice -Source $probe `
    -Start 'var logPath = Path.Combine(' `
    -End 'if (!IsOrdinaryFile(brokerPath)'
Assert-Contract -Condition (
        $brokerBlock.Contains('Environment.SpecialFolder.ProgramFiles') -and
        -not $brokerBlock.Contains('CommonApplicationData') -and
        $brokerBlock.Contains('"VIIPER", "viiper.exe"')) `
    -Message 'Infrastructure probe broker path is not exact Program Files\VIIPER\viiper.exe.'
foreach ($entry in @(
    @($credentialBlock, '"VIIPER", "viiper.key.txt"'),
    @($logBlock, '"VIIPER", "viiper-native-broker.log"')
)) {
    Assert-Contract -Condition (
            $entry[0].Contains('Environment.SpecialFolder.CommonApplicationData') -and
            -not $entry[0].Contains('Environment.SpecialFolder.ProgramFiles') -and
            $entry[0].Contains($entry[1])) `
        -Message 'Infrastructure probe credential/log path escaped exact ProgramData ownership.'
}

$manager = Get-Content -LiteralPath $managerPath -Raw
foreach ($token in @(
    'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)',
    '[IO.Directory]::CreateDirectory(',
    '[IO.FileSystemAclExtensions]::Create(',
    'function Assert-ProtectedStage {',
    'function Open-VerifiedStagedBroker {',
    '$sourceAlgorithm.ComputeHash($sourceStream)',
    '[IO.FileShare]::None',
    '$stagedAlgorithm.ComputeHash($launchLock)',
    '[IO.FileShare]::Read)',
    '$stagedBrokerLease.LaunchLock.Dispose()',
    'Refusing protected staging cleanup with unexpected entries'
)) {
    Assert-Contract -Condition $manager.Contains($token) `
        -Message "Native manager lost protected-stage/launch token '$token'."
}
foreach ($token in @(
    'Copy-Item -LiteralPath $brokerPath',
    'Get-FileHash -LiteralPath $stagedBroker',
    'Remove-Item -LiteralPath $stage -Recurse'
)) {
    Assert-Contract -Condition (-not $manager.Contains($token)) `
        -Message "Native manager restored pathname-racy stage behavior '$token'."
}
$openBroker = Get-SourceSlice -Source $manager `
    -Start 'function Open-VerifiedStagedBroker {' `
    -End 'function Remove-ProtectedStage {'
$openOrder = @(
    $openBroker.IndexOf('$sourceAlgorithm.ComputeHash($sourceStream)'),
    $openBroker.IndexOf('$sourceStream.CopyTo($destinationStream)'),
    $openBroker.IndexOf('$launchLock = [IO.FileStream]::new('),
    $openBroker.IndexOf('$stagedAlgorithm.ComputeHash($launchLock)')
)
Assert-Contract -Condition (
        $openOrder[0] -ge 0 -and
        (@($openOrder | Sort-Object) -join ',') -ceq ($openOrder -join ',')) `
    -Message 'Native manager source-lock/copy/launch-lock/hash order is unsafe.'
$mainStageStart = $manager.LastIndexOf(
    '$programDataRoot = ', [StringComparison]::Ordinal)
Assert-Contract -Condition ($mainStageStart -ge 0) `
    -Message 'Native manager staging transaction was not found.'
$mainStage = $manager.Substring($mainStageStart)
$mainOrder = @(
    $mainStage.IndexOf('Open-VerifiedStagedBroker'),
    $mainStage.IndexOf('Invoke-JoinedNativeProcess'),
    $mainStage.IndexOf('$stagedBrokerLease.LaunchLock.Dispose()'),
    $mainStage.IndexOf('Remove-ProtectedStage'),
    $mainStage.IndexOf('Write-StructuredOutcome -RequestedOperation')
)
Assert-Contract -Condition (
        $mainOrder[0] -ge 0 -and
        (@($mainOrder | Sort-Object) -join ',') -ceq ($mainOrder -join ',')) `
    -Message 'Native manager did not hold the verified broker lock through join and cleanup before its receipt.'

# Deterministically exercise both cross-runtime atomic ACL creation and the
# exact share modes used to bind source bytes and staged launch bytes. This
# mutates only a unique temporary directory and never starts the broker.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DS4Windows.InstallerSecurity.' + [Guid]::NewGuid().ToString('N'))
$atomicDirectory = Join-Path $testRoot 'atomic-acl'
$sourcePath = Join-Path $atomicDirectory 'source.bin'
$destinationPath = Join-Path $atomicDirectory 'staged.bin'
$sourceMovedPath = $sourcePath + '.moved'
$destinationMovedPath = $destinationPath + '.moved'
$preownedDirectory = Join-Path $testRoot 'preowned-parent'
$junctionTarget = Join-Path $testRoot 'junction-target'
$junctionPath = Join-Path $testRoot 'junction-parent'
$lockedDirectory = Join-Path $testRoot 'locked-directory'
$lockedDirectoryMoved = $lockedDirectory + '.moved'
$heldLogPath = Join-Path $atomicDirectory 'held-log.bin'
$heldLogMovedPath = $heldLogPath + '.moved'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $systemSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $testSecurity = [Security.AccessControl.DirectorySecurity]::new()
    $testSecurity.SetOwner($currentSid)
    $testSecurity.SetGroup($currentSid)
    $testSecurity.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($currentSid, $systemSid)) {
        [void]$testSecurity.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    if ($PSVersionTable.PSEdition -ceq 'Desktop') {
        $testDirectory = [IO.Directory]::CreateDirectory(
            $atomicDirectory, $testSecurity)
    } else {
        $testDirectory = [IO.DirectoryInfo]::new($atomicDirectory)
        [IO.FileSystemAclExtensions]::Create(
            $testDirectory, $testSecurity)
    }
    $actualTestAcl = Get-Acl -LiteralPath $atomicDirectory
    Assert-Contract -Condition $actualTestAcl.AreAccessRulesProtected `
        -Message 'Atomic ACL directory creation inherited a DACL.'
    Assert-Contract -Condition (
            $actualTestAcl.GetOwner(
                [Security.Principal.SecurityIdentifier]).Value -ceq
            $currentSid.Value) `
        -Message 'Atomic ACL directory creation changed its exact owner.'
    Assert-Contract -Condition (Test-ExactTestDirectoryAcl `
            -Path $atomicDirectory -OwnerSid $currentSid.Value `
            -SecondSid $systemSid.Value) `
        -Message 'Atomic ACL directory creation lost its exact two-principal DACL.'

    # An inherited/preowned parent must be rejected, never repaired by an ACL
    # rewrite. A junction must likewise be identified before use.
    [IO.Directory]::CreateDirectory($preownedDirectory) | Out-Null
    Assert-Contract -Condition (-not (Test-ExactTestDirectoryAcl `
            -Path $preownedDirectory -OwnerSid $currentSid.Value `
            -SecondSid $systemSid.Value)) `
        -Message 'The exact-ACL predicate accepted an inherited preowned parent.'
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    New-Item -ItemType Junction -Path $junctionPath `
        -Target $junctionTarget -ErrorAction Stop | Out-Null
    $junctionAttributes = (Get-Item -LiteralPath $junctionPath `
        -Force).Attributes
    Assert-Contract -Condition (
            ($junctionAttributes -band
             [IO.FileAttributes]::ReparsePoint) -ne 0) `
        -Message 'The deterministic junction adversary was not a reparse point.'

    # Holding a directory handle without FILE_SHARE_DELETE prevents its path
    # from being renamed or deleted between validation and child creation.
    [IO.Directory]::CreateDirectory($lockedDirectory) | Out-Null
    $directoryLock =
        [Ds4WindowsInstallerSecurityContractNative]::OpenDirectoryNoDelete(
            $lockedDirectory)
    try {
        Assert-SharingViolation -Message `
            'No-share-delete directory handle allowed a path swap.' -Action {
                [IO.Directory]::Move(
                    $lockedDirectory, $lockedDirectoryMoved)
            }
        Assert-SharingViolation -Message `
            'No-share-delete directory handle allowed deletion.' -Action {
                [IO.Directory]::Delete($lockedDirectory, $false)
            }
    }
    finally {
        $directoryLock.Dispose()
    }

    # Create a protected log analogue atomically and hold the returned stream
    # with read sharing only. Writers, rename, and deletion must all fail until
    # cleanup disposes the exact handle.
    $testFileSecurity = [Security.AccessControl.FileSecurity]::new()
    $testFileSecurity.SetOwner($currentSid)
    $testFileSecurity.SetGroup($currentSid)
    $testFileSecurity.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($currentSid, $systemSid)) {
        [void]$testFileSecurity.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]::None,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    $requestedFileRights =
        [Security.AccessControl.FileSystemRights]::Read -bor
        [Security.AccessControl.FileSystemRights]::Write
    if ($PSVersionTable.PSEdition -ceq 'Desktop') {
        $heldLog = [IO.FileStream]::new(
            $heldLogPath, [IO.FileMode]::CreateNew,
            $requestedFileRights, [IO.FileShare]::Read,
            4096, [IO.FileOptions]::WriteThrough, $testFileSecurity)
    }
    else {
        $heldLog = [IO.FileSystemAclExtensions]::Create(
            [IO.FileInfo]::new($heldLogPath),
            [IO.FileMode]::CreateNew, $requestedFileRights,
            [IO.FileShare]::Read, 4096,
            [IO.FileOptions]::WriteThrough, $testFileSecurity)
    }
    try {
        $heldAcl = Get-Acl -LiteralPath $heldLogPath
        Assert-Contract -Condition (
                $heldAcl.AreAccessRulesProtected -and
                $heldAcl.GetOwner(
                    [Security.Principal.SecurityIdentifier]).Value -ceq
                    $currentSid.Value -and
                @($heldAcl.GetAccessRules(
                    $true, $true,
                    [Security.Principal.SecurityIdentifier])).Count -eq 2) `
            -Message 'Atomic protected log creation lost its exact ACL.'
        Assert-SharingViolation -Message `
            'Held protected log allowed a concurrent writer.' -Action {
                $writer = [IO.FileStream]::new(
                    $heldLogPath, [IO.FileMode]::Open,
                    [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
                $writer.Dispose()
            }
        Assert-SharingViolation -Message `
            'Held protected log allowed a path swap.' -Action {
                [IO.File]::Move($heldLogPath, $heldLogMovedPath)
            }
        Assert-SharingViolation -Message `
            'Held protected log allowed deletion.' -Action {
                [IO.File]::Delete($heldLogPath)
            }
    }
    finally {
        $heldLog.Dispose()
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        'deterministic VIIPER broker share-mode race contract')
    [IO.File]::WriteAllBytes($sourcePath, $bytes)
    $expectedAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedDigest = ([BitConverter]::ToString(
            $expectedAlgorithm.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $expectedAlgorithm.Dispose()
    }

    $sourceLock = [IO.FileStream]::new(
        $sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $sourceAlgorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $sourceDigest = ([BitConverter]::ToString(
                $sourceAlgorithm.ComputeHash($sourceLock))).Replace('-', '')
        }
        finally {
            $sourceAlgorithm.Dispose()
        }
        Assert-Contract -Condition ($sourceDigest -ceq $expectedDigest) `
            -Message 'Source-lock digest was not bound to the expected bytes.'
        Assert-SharingViolation -Message `
            'Source lock allowed a concurrent writer.' -Action {
                $writer = [IO.FileStream]::new(
                    $sourcePath, [IO.FileMode]::Open,
                    [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
                $writer.Dispose()
            }
        Assert-SharingViolation -Message `
            'Source lock allowed a concurrent rename/delete open.' -Action {
                [IO.File]::Move($sourcePath, $sourceMovedPath)
            }
        $sourceLock.Position = 0
        $writer = [IO.FileStream]::new(
            $destinationPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None,
            4096, [IO.FileOptions]::WriteThrough)
        try {
            $sourceLock.CopyTo($writer)
            $writer.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $sourceLock.Dispose()
    }

    $launchLock = [IO.FileStream]::new(
        $destinationPath, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $launchAlgorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $launchDigest = ([BitConverter]::ToString(
                $launchAlgorithm.ComputeHash($launchLock))).Replace('-', '')
        }
        finally {
            $launchAlgorithm.Dispose()
        }
        Assert-Contract -Condition ($launchDigest -ceq $expectedDigest) `
            -Message 'Launch lock was not bound to the verified staged bytes.'
        Assert-SharingViolation -Message `
            'Launch lock allowed a post-hash writer.' -Action {
                $writer = [IO.FileStream]::new(
                    $destinationPath, [IO.FileMode]::Open,
                    [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
                $writer.Dispose()
            }
        Assert-SharingViolation -Message `
            'Launch lock allowed post-hash rename/delete.' -Action {
                [IO.File]::Move($destinationPath, $destinationMovedPath)
            }
    }
    finally {
        $launchLock.Dispose()
    }
}
finally {
    foreach ($path in @(
        $sourcePath, $sourceMovedPath,
        $destinationPath, $destinationMovedPath,
        $heldLogPath, $heldLogMovedPath
    )) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [IO.File]::Delete($path)
        }
    }
    if (Test-Path -LiteralPath $atomicDirectory -PathType Container) {
        [IO.Directory]::Delete($atomicDirectory, $false)
    }
    if (Test-Path -LiteralPath $junctionPath) {
        [IO.Directory]::Delete($junctionPath, $false)
    }
    foreach ($path in @(
        $junctionTarget, $preownedDirectory,
        $lockedDirectory, $lockedDirectoryMoved
    )) {
        if (Test-Path -LiteralPath $path -PathType Container) {
            [IO.Directory]::Delete($path, $false)
        }
    }
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        [IO.Directory]::Delete($testRoot, $false)
    }
}

Write-Host (
    'Installer ACL, Program Files broker-path, protected-stage, and ' +
    'hash-to-launch race contracts passed under PowerShell ' +
    $PSVersionTable.PSVersion + '.')
