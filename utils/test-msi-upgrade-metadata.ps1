[CmdletBinding()]
param([string]$ExistingMsi)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Parse, then load only two read-only metadata functions. Never execute the
# hosted fixture's body, download/layout a bundle, or invoke Windows Installer.
$tokens = $null
$parseErrors = $null
$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'test-previous-msi-upgrade.ps1')).Path
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The hosted upgrade fixture has parser errors.' }
foreach ($name in @('Get-MsiProperty', 'Inspect-Msi')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $function) { throw "Missing metadata function: $name" }
    Invoke-Expression $function.Extent.Text
}
$upgradeCode = '{65E808E3-D35A-4825-AE11-8D9415F16446}'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ds4w-msi-metadata-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
$createdFiles = [Collections.Generic.List[string]]::new()
$caseCount = 0

function New-MetadataFixture([string]$Path, [hashtable]$Properties, [switch]$CustomAction) {
    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        # Create a disposable database file only. It has no cabinet, payload,
        # execute sequence or package registration, and is never installed.
        $database = $installer.OpenDatabase($Path, 3)
        $commands = [Collections.Generic.List[string]]::new()
        $commands.Add('CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)')
        foreach ($property in $Properties.GetEnumerator()) {
            if ($property.Key.Contains("'") -or $property.Value.Contains("'")) { throw 'Unexpected fixture SQL value.' }
            $commands.Add("INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('$($property.Key)', '$($property.Value)')")
        }
        if ($CustomAction) {
            $commands.Add('CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(0) PRIMARY KEY `Action`)')
        }
        foreach ($command in $commands) {
            $view = $null
            try {
                $view = $database.OpenView($command)
                [void]$view.Execute()
            }
            finally {
                if ($view) { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
        }
        [void]$database.Commit()
    }
    finally {
        if ($database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
}

try {
    foreach ($mutation in @('none', 'ProductName', 'ProductVersion', 'ProductCode', 'UpgradeCode', 'CustomAction')) {
        $properties = @{
            ProductName = 'DS4Windows'; ProductVersion = '5.0.5.6'
            ProductCode = '{8C3839ED-2834-4927-90DE-D058EB1E0495}'; UpgradeCode = $upgradeCode
        }
        if ($mutation -notin @('none', 'CustomAction')) { $properties[$mutation] = 'unexpected-fixture-value' }
        $path = Join-Path $testRoot ($mutation + '.msi')
        $createdFiles.Add($path)
        New-MetadataFixture $path $properties -CustomAction:($mutation -eq 'CustomAction')
        if ($mutation -eq 'none') {
            $result = @(Inspect-Msi $path '5.0.5.6')
            if ($result.Count -ne 1 -or $result[0] -isnot [hashtable]) {
                throw 'Inspect-Msi must return one identity object without COM pipeline nulls.'
            }
            foreach ($name in $properties.Keys) {
                if ($result[0][$name] -isnot [string] -or $result[0][$name] -cne $properties[$name]) {
                    throw "MSI property $name must be exactly one scalar string."
                }
            }
        }
        else {
            $failure = $null
            try { [void](Inspect-Msi $path '5.0.5.6') }
            catch { $failure = $_.Exception.Message }
            if (-not $failure) { throw "Unsafe MSI metadata was accepted: $mutation" }
            if ($mutation -eq 'CustomAction') {
                if ($failure -notlike '*contains custom actions*') { throw "Wrong action rejection: $failure" }
            }
            elseif ($failure -notlike '*unexpected-fixture-value*' -or $failure -notlike "*$mutation*") {
                throw "Identity rejection omitted the observed metadata: $failure"
            }
        }
        $caseCount++
        Write-Output "PASS MSI metadata $mutation"
    }
    if ($ExistingMsi) {
        $resolvedMsi = (Resolve-Path -LiteralPath $ExistingMsi).Path
        $result = @(Inspect-Msi $resolvedMsi '5.0.5.6')
        if ($result.Count -ne 1 -or $result[0] -isnot [hashtable]) { throw 'Actual MSI identity has pipeline contamination.' }
        Write-Output 'PASS existing MSI readonly inspection (no installation)'
        $caseCount++
    }
    Write-Output "$caseCount MSI metadata regression cases passed without installation, application launch or driver mutation."
}
finally {
    # Only files explicitly created above can be removed. No recursive delete
    # or deletion of the optional real MSI is allowed.
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
        [IO.Path]::GetFileName($resolvedRoot) -notmatch '^ds4w-msi-metadata-[0-9a-f]{32}$') {
        throw 'Unexpected fixture cleanup root.'
    }
    foreach ($path in $createdFiles) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $resolvedRoot) { throw 'Unexpected metadata cleanup target.' }
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    [IO.Directory]::Delete($resolvedRoot, $false)
}
