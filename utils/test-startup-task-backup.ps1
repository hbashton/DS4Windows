[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendScript
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$backendPath = (Resolve-Path -LiteralPath $BackendScript).Path
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $backendPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Backend installer has PowerShell parse errors."
}
foreach ($functionName in @("Assert-ManagedStartupTaskName",
        "Assert-SafeManagedDirectory", "Write-StartupTaskBackup")) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true)
    if (-not $definition) { throw "Missing backend function: $functionName" }
    Invoke-Expression $definition.Extent.Text
}

$script:SetupLogs = @()
function Write-SetupLog([string]$message, $color) {
    $script:SetupLogs += $message
}

# The actual directory and file APIs remain in use. Only the one leaf's
# attributes can be substituted, avoiding symlink privileges in this fixture.
$script:MockReparseLeaf = $null
function Get-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force)
    if ($script:MockReparseLeaf -and
            [string]::Equals($LiteralPath, $script:MockReparseLeaf,
                [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{
            PSIsContainer = $false
            Attributes = [IO.FileAttributes]::ReparsePoint
            FullName = $LiteralPath
        }
    }
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}

function Assert-Equal($actual, $expected, [string]$message) {
    if (-not [object]::Equals($actual, $expected)) {
        throw "$message Expected '$expected', observed '$actual'."
    }
}

function Get-OnlyBackup([string]$directory) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $directory "task-backups") -File)
    Assert-Equal $files.Count 1 "Expected one original task backup."
    return $files[0].FullName
}

function Assert-BackupRejected([string]$xml, [string]$reason) {
    $rejected = $false
    try { Write-StartupTaskBackup "RunVIIPER" $xml }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Backup unexpectedly accepted $reason." }
}

$fixtureParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$fixtureName = "DS4Windows-StartupBackupTests-" + [Guid]::NewGuid().ToString("N")
$fixtureRoot = Join-Path $fixtureParent $fixtureName
New-Item -ItemType Directory -Path $fixtureRoot -ErrorAction Stop | Out-Null
try {
    # Use code points so Windows PowerShell 5.1 exercises the same Unicode
    # text even when this source file is decoded with the system code page.
    $unicodeText = "Controller " + [char]0x00E9 + [char]0x0644 +
        [char]::ConvertFromUtf32(0x1F3AE)
    $xml = '<?xml version="1.0" encoding="UTF-16"?><Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Description>' +
        $unicodeText + '</Description></RegistrationInfo></Task>'

    # No parent Installer directory exists: production must create only the
    # configured fixture subtree and persist a complete Unicode definition.
    $script:InstallerLogRoot = Join-Path $fixtureRoot "missing\Installer"
    Write-StartupTaskBackup "RunVIIPER" $xml
    $backup = Get-OnlyBackup $script:InstallerLogRoot
    Assert-Equal ([IO.File]::ReadAllText($backup)) $xml "Unicode task XML did not round-trip."
    $bytes = [IO.File]::ReadAllBytes($backup)
    Assert-Equal $bytes[0] ([byte]0xFF) "Backup lacks the UTF-16 BOM."
    Assert-Equal $bytes[1] ([byte]0xFE) "Backup lacks the UTF-16 BOM."
    $parsed = [Xml.XmlDocument]::new()
    $parsed.Load($backup)
    Assert-Equal $parsed.DocumentElement.InnerText $unicodeText "Saved task XML did not parse."

    # A duplicate definition must reuse the existing durable file without
    # rewriting its bytes, timestamp, or emitting a second preservation log.
    $sentinelTime = [DateTime]::new(2020, 1, 2, 3, 4, 6, [DateTimeKind]::Utc)
    [IO.File]::SetLastWriteTimeUtc($backup, $sentinelTime)
    $logCount = $script:SetupLogs.Count
    Write-StartupTaskBackup "RunVIIPER" $xml
    Assert-Equal (Get-OnlyBackup $script:InstallerLogRoot) $backup "Duplicate backup used another path."
    Assert-Equal ([IO.File]::GetLastWriteTimeUtc($backup)) $sentinelTime "Duplicate backup rewrote the file."
    Assert-Equal ([IO.File]::ReadAllText($backup)) $xml "Duplicate backup changed XML."
    Assert-Equal $script:SetupLogs.Count $logCount "Duplicate backup produced another preservation log."

    # The same hash-named file containing different bytes must never be
    # overwritten or mistaken for a successful archive.
    $corrupt = "partial previous backup"
    [IO.File]::WriteAllText($backup, $corrupt, [Text.Encoding]::Unicode)
    Assert-BackupRejected $xml "a corrupt existing definition"
    Assert-Equal ([IO.File]::ReadAllText($backup)) $corrupt "Corrupt backup was overwritten."

    # A leaf can change after directory traversal, so the final reuse branch
    # must inspect that exact file too. No actual symlink is created here.
    [IO.File]::WriteAllText($backup, $xml, [Text.Encoding]::Unicode)
    $script:MockReparseLeaf = $backup
    Assert-BackupRejected $xml "a reparse-point backup leaf"
    $script:MockReparseLeaf = $null
    Assert-Equal ([IO.File]::ReadAllText($backup)) $xml "Rejected leaf backup was changed."

    # A directory at the deterministic filename is a collision, not a file
    # which setup can replace. Only a file created by this test is removed.
    Remove-Item -LiteralPath $backup -Force -ErrorAction Stop
    New-Item -ItemType Directory -Path $backup -ErrorAction Stop | Out-Null
    Assert-BackupRejected $xml "a directory at the backup filename"
    if (-not [IO.Directory]::Exists($backup)) {
        throw "The directory collision was removed."
    }
}
finally {
    $script:MockReparseLeaf = $null
    # Resolve and bound the sole cleanup target before recursive deletion.
    $cleanupRoot = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\', '/')
    $expectedRoot = [IO.Path]::GetFullPath((Join-Path $fixtureParent $fixtureName)).TrimEnd('\', '/')
    if (-not [string]::Equals($cleanupRoot, $expectedRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
            -not $cleanupRoot.StartsWith($fixtureParent + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            $fixtureName -notmatch '^DS4Windows-StartupBackupTests-[0-9a-f]{32}$') {
        throw "Refusing cleanup outside the isolated startup-backup fixture."
    }
    Remove-Item -LiteralPath $cleanupRoot -Recurse -Force -ErrorAction Stop
}

Write-Host "Real startup-task backup Unicode, idempotence, corruption, leaf-reparse, and directory-collision tests passed; no Task Scheduler or ProgramData state was changed."
