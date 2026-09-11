[CmdletBinding()]
param([Parameter(Mandatory)][string]$NewMsi)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This fixture installs application-only MSIs in an ephemeral GitHub-hosted VM.
# There is deliberately no local override. Never run it on a developer PC.
if ($env:GITHUB_ACTIONS -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or
    $env:GITHUB_REPOSITORY -cne 'hbashton/DS4Windows' -or
    $env:GITHUB_RUN_ID -notmatch '^\d+$' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    [string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    throw 'Previous-release MSI upgrade validation is restricted to an ephemeral GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The hosted MSI fixture requires the runner administrator token.'
    }
}
finally { $identity.Dispose() }

function Assert-LocalPath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\')) {
        throw "Expected a local absolute path: $Path"
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    for ($cursor = $resolved; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a linked fixture path: $cursor"
            }
        }
    }
    return $resolved
}
function Test-AtOrBelow([string]$Path, [string]$Root) {
    $normalized = [IO.Path]::TrimEndingDirectorySeparator($Root)
    return $Path.Equals($normalized, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($normalized + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
}

$runnerRoot = Assert-LocalPath $env:RUNNER_TEMP
$workspaceRoot = Assert-LocalPath $env:GITHUB_WORKSPACE
$NewMsi = Assert-LocalPath (Resolve-Path -LiteralPath $NewMsi).Path
if (-not (Test-AtOrBelow $NewMsi $workspaceRoot) -and
    -not (Test-AtOrBelow $NewMsi $runnerRoot)) {
    throw 'The new MSI must be in this hosted runner workspace or temporary directory.'
}
$installRoot = Assert-LocalPath (Join-Path $env:ProgramFiles 'DS4Windows')
$installedApp = Join-Path $installRoot 'DS4Windows.exe'
$upgradeCode = '{65E808E3-D35A-4825-AE11-8D9415F16446}'
$oldVersion = '5.0.5.5'
$newVersion = '5.0.5.6'
$oldInstallerSha256 = '5DCA513CE521DD660BADAC0DD6A23FDCE8A3192930397C4A6C45D44A69B8ED74'
$oldInstallerLength = 200120243L
$oldInstallerUrl = 'https://github.com/hbashton/DS4Windows/releases/download/VIIPERRC4.5.5/DS4Windows_5.0.5.5_Setup_x64.exe'

# Inventory only. Avoid Win32_Product, which can trigger unrelated MSI repairs.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Ds4HostedMsiInventory {
    [DllImport("msi.dll", CharSet=CharSet.Unicode, EntryPoint="MsiEnumRelatedProductsW")]
    private static extern uint Enumerate(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    public static string[] Related(string upgradeCode) {
        var products = new List<string>();
        for (uint index = 0; ; index++) {
            var code = new StringBuilder(39);
            uint result = Enumerate(upgradeCode, 0, index, code);
            if (result == 259) return products.ToArray();
            if (result != 0) throw new InvalidOperationException("MSI inventory failed: " + result);
            products.Add(code.ToString());
        }
    }
}
'@
function Assert-NoRuntimeProcesses {
    if (@(Get-Process -Name DS4Windows, viiper -ErrorAction SilentlyContinue).Count) {
        throw 'A DS4Windows or VIIPER process exists; the application-only fixture will not stop or adopt it.'
    }
}
Assert-NoRuntimeProcesses
if (@([Ds4HostedMsiInventory]::Related($upgradeCode)).Count -ne 0 -or
    ((Test-Path -LiteralPath $installRoot) -and
        @(Get-ChildItem -LiteralPath $installRoot -Force).Count -ne 0)) {
    throw 'A pre-existing DS4Windows installation is present; refusing the upgrade fixture.'
}
$registeredPath = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\DS4Windows' -Name InstallPath -ErrorAction SilentlyContinue
if ($registeredPath) { throw 'A pre-existing managed DS4Windows registration is present.' }

$runRoot = Join-Path $runnerRoot ('ds4windows-previous-msi-upgrade-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $runRoot)
$script:MutationTimedOut = $false
$script:InstalledCodes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:Sentinels = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'

function Invoke-BoundedProcess([string]$Executable, [string]$Arguments, [string]$Phase) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(180000)) {
            $script:MutationTimedOut = $true
            throw "$Phase exceeded three minutes. No concurrent cleanup will be started; discard this hosted runner."
        }
        if ($process.ExitCode -ne 0) {
            throw "$Phase failed with exit code $($process.ExitCode). Evidence: $runRoot"
        }
    }
    finally { $process.Dispose() }
}
function Invoke-Msi([string]$Arguments, [string]$Phase) {
    $log = Join-Path $runRoot ($Phase + '.log')
    Invoke-BoundedProcess $msiexec "$Arguments /qn /norestart /L*v `"$log`"" $Phase
}
function Get-MsiProperty($Database, [string]$Name) {
    $view = $null
    $record = $null
    try {
        $view = $Database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'")
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "MSI property is absent: $Name" }
        return [string]$record.StringData(1)
    }
    finally {
        if ($record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        if ($view) { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    }
}
function Inspect-Msi([string]$Path, [string]$ExpectedVersion) {
    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $properties = @{}
        foreach ($name in @('ProductCode', 'ProductName', 'ProductVersion', 'UpgradeCode')) {
            $properties[$name] = Get-MsiProperty $database $name
        }
        if ($properties.ProductName -cne 'DS4Windows' -or
            $properties.ProductVersion -cne $ExpectedVersion -or
            $properties.UpgradeCode -ine $upgradeCode -or
            $properties.ProductCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') {
            throw "Unexpected application MSI identity in $Path"
        }
        # A payload-only MSI must not execute infrastructure or launch the app.
        $view = $database.OpenView('SELECT `Name` FROM `_Tables` WHERE `Name`=''CustomAction''')
        $view.Execute()
        $record = $view.Fetch()
        if ($record) { throw 'The MSI contains custom actions; driver/app side effects are not authorized by this fixture.' }
        return $properties
    }
    finally {
        if ($record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        if ($view) { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        if ($database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
}
function Assert-Installed([string]$ProductCode, [string]$Version) {
    $products = @([Ds4HostedMsiInventory]::Related($upgradeCode))
    if ($products.Count -ne 1 -or $products[0] -ine $ProductCode) {
        throw 'Upgrade did not leave exactly the expected MSI registration.'
    }
    if (-not (Test-Path -LiteralPath $installedApp -PathType Leaf) -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo($installedApp).FileVersion -cne $Version) {
        throw "Installed application file version is not $Version."
    }
    Assert-NoRuntimeProcesses
}
function Assert-Sentinels {
    foreach ($entry in $script:Sentinels.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Key -PathType Leaf) -or
            (Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash -cne $entry.Value) {
            throw "The MSI lifecycle changed an unowned test profile: $($entry.Key)"
        }
    }
}

try {
    $newIdentity = Inspect-Msi $NewMsi $newVersion
    $oldInstaller = Join-Path $runRoot 'previous-installer.exe'
    Invoke-WebRequest -Uri $oldInstallerUrl -OutFile $oldInstaller -TimeoutSec 180
    if ((Get-Item -LiteralPath $oldInstaller).Length -ne $oldInstallerLength -or
        (Get-FileHash -LiteralPath $oldInstaller -Algorithm SHA256).Hash -cne $oldInstallerSha256) {
        throw 'Published RC4.5.5 installer bytes do not match the immutable fixture pin.'
    }
    $layout = Join-Path $runRoot 'previous-layout'
    Invoke-BoundedProcess $oldInstaller "/layout `"$layout`" /quiet /log `"$runRoot\layout.log`"" 'Previous bundle layout only'
    Assert-NoRuntimeProcesses
    if (@([Ds4HostedMsiInventory]::Related($upgradeCode)).Count -ne 0) {
        throw 'Layout unexpectedly changed MSI registration; refusing further work.'
    }
    $oldPackages = @(Get-ChildItem -LiteralPath $layout -Filter 'DS4Windows_5.0.5.5_x64.msi' -File -Recurse)
    if ($oldPackages.Count -ne 1) { throw 'Layout did not produce exactly one expected previous MSI.' }
    $oldMsi = Assert-LocalPath $oldPackages[0].FullName
    $oldIdentity = Inspect-Msi $oldMsi $oldVersion
    if ($oldIdentity.ProductCode -ieq $newIdentity.ProductCode) { throw 'The upgrade MSI must have a new ProductCode.' }

    [void]$script:InstalledCodes.Add($oldIdentity.ProductCode)
    Invoke-Msi "/i `"$oldMsi`"" '01-install-previous'
    Assert-Installed $oldIdentity.ProductCode $oldVersion
    foreach ($profileRoot in @((Join-Path $installRoot 'Profiles'),
        (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'DS4Windows\Profiles'))) {
        $profileRoot = Assert-LocalPath $profileRoot
        [void](New-Item -ItemType Directory -Path $profileRoot -Force)
        $sentinel = Join-Path $profileRoot ('rc456-upgrade-fixture-' + [Guid]::NewGuid().ToString('N') + '.xml')
        [IO.File]::WriteAllText($sentinel, '<DS4Windows><Name>CI upgrade preservation sentinel</Name></DS4Windows>')
        $script:Sentinels.Add($sentinel, (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash)
    }
    [void]$script:InstalledCodes.Add($newIdentity.ProductCode)
    Invoke-Msi "/i `"$NewMsi`"" '02-upgrade-current'
    Assert-Installed $newIdentity.ProductCode $newVersion
    Assert-Sentinels
    Invoke-Msi "/fa `"$NewMsi`"" '03-repair-current'
    Assert-Installed $newIdentity.ProductCode $newVersion
    Assert-Sentinels
    Invoke-Msi "/x $($newIdentity.ProductCode)" '04-uninstall-current'
    if (@([Ds4HostedMsiInventory]::Related($upgradeCode)).Count -ne 0 -or
        (Test-Path -LiteralPath $installedApp)) {
        throw 'Uninstall retained a package registration or application keypath.'
    }
    Assert-Sentinels
    Assert-NoRuntimeProcesses
    [ordered]@{
        PreviousTag = 'VIIPERRC4.5.5'; PreviousInstallerSha256 = $oldInstallerSha256
        PreviousProductCode = $oldIdentity.ProductCode; NewProductCode = $newIdentity.ProductCode
        PreviousFileVersion = $oldVersion; NewFileVersion = $newVersion
        NewMsiSha256 = (Get-FileHash -LiteralPath $NewMsi -Algorithm SHA256).Hash
        PreservedProfiles = $script:Sentinels.Count; InstalledRegistrationCountAfterUninstall = 0
        ApplicationOrDriverLaunched = $false; CompletedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'VERIFICATION.json') -Encoding utf8
    Write-Output "Previous MSI upgrade, single-registration, profile preservation, repair and uninstall passed. Evidence: $runRoot"
}
finally {
    if (-not $script:MutationTimedOut) {
        foreach ($code in @([Ds4HostedMsiInventory]::Related($upgradeCode))) {
            if (-not $script:InstalledCodes.Contains($code)) {
                throw 'An unowned MSI appeared during validation; refusing cleanup.'
            }
            Invoke-Msi "/x $code" 'failure-cleanup'
        }
        # Remove only exact files created by this invocation, never a user tree.
        foreach ($sentinel in $script:Sentinels.Keys) {
            $verified = Assert-LocalPath $sentinel
            if ([IO.Path]::GetFileName($verified) -notmatch '^rc456-upgrade-fixture-[0-9a-f]{32}\.xml$') {
                throw 'Unexpected sentinel cleanup target.'
            }
            Remove-Item -LiteralPath $verified -Force -ErrorAction SilentlyContinue
        }
    }
}
