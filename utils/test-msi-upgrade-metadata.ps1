[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$ExistingMsi,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Fail without prompting before any filesystem/COM work. The expected version
# must come from the caller's build contract, never from the MSI being checked.
if ($PSBoundParameters.ContainsKey('ExistingMsi') -ne
    $PSBoundParameters.ContainsKey('ExpectedVersion')) {
    throw 'ExistingMsi and ExpectedVersion must be supplied together.'
}

# Parse, then load only read-only metadata/path functions. Never execute the
# hosted fixture's body, download/layout a bundle, or invoke Windows Installer.
$tokens = $null
$parseErrors = $null
$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'test-previous-msi-upgrade.ps1')).Path
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The hosted upgrade fixture has parser errors.' }
foreach ($name in @('Assert-LocalPath', 'Get-PreviousMsiPayload', 'Get-MsiProperty', 'Inspect-Msi')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $function) { throw "Missing metadata function: $name" }
    Invoke-Expression $function.Extent.Text
}
$upgradeCode = '{65E808E3-D35A-4825-AE11-8D9415F16446}'
if ($PSBoundParameters.ContainsKey('ExistingMsi')) {
    $resolvedMsi = (Resolve-Path -LiteralPath $ExistingMsi).Path
    $result = @(Inspect-Msi $resolvedMsi $ExpectedVersion)
    if ($result.Count -ne 1 -or $result[0] -isnot [hashtable]) { throw 'Actual MSI identity has pipeline contamination.' }
    Write-Output "PASS existing MSI readonly inspection for expected version $ExpectedVersion (no installation)"
    return
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ds4w-msi-metadata-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
$createdFiles = [Collections.Generic.List[string]]::new()
$createdDirectories = [Collections.Generic.List[string]]::new()
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
            ProductName = 'DS4Windows'; ProductVersion = '5.0.5.7'
            ProductCode = '{8C3839ED-2834-4927-90DE-D058EB1E0495}'; UpgradeCode = $upgradeCode
        }
        if ($mutation -notin @('none', 'CustomAction')) { $properties[$mutation] = 'unexpected-fixture-value' }
        $path = Join-Path $testRoot ($mutation + '.msi')
        $createdFiles.Add($path)
        New-MetadataFixture $path $properties -CustomAction:($mutation -eq 'CustomAction')
        if ($mutation -eq 'none') {
            $result = @(Inspect-Msi $path '5.0.5.7')
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
            try { [void](Inspect-Msi $path '5.0.5.7') }
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
    foreach ($shape in @('exact', 'missing', 'wrong-name', 'wrong-location', 'duplicate')) {
        $payloadRoot = Join-Path $testRoot ('payloads-' + $shape)
        $attached = Join-Path $payloadRoot 'WixAttachedContainer'
        [void](New-Item -ItemType Directory -Path $attached -Force)
        $createdDirectories.Add($payloadRoot)
        $createdDirectories.Add($attached)
        $expected = Join-Path $attached 'DS4Windows_5.0.5.7_x64.msi'
        $copies = switch ($shape) {
            'exact' { @($expected) }
            'missing' { @() }
            'wrong-name' { @(Join-Path $attached 'DS4Windows_other_x64.msi') }
            'wrong-location' { @(Join-Path $payloadRoot 'DS4Windows_5.0.5.7_x64.msi') }
            'duplicate' { @($expected, (Join-Path $payloadRoot 'DS4Windows_5.0.5.7_x64.msi')) }
        }
        foreach ($copy in $copies) {
            Copy-Item -LiteralPath (Join-Path $testRoot 'none.msi') -Destination $copy
            $createdFiles.Add($copy)
        }
        if ($shape -eq 'exact') {
            $selected = @(Get-PreviousMsiPayload $payloadRoot '5.0.5.7')
            if ($selected.Count -ne 1 -or $selected[0] -cne $expected) { throw 'Expected one exact attached MSI path.' }
            [void](Inspect-Msi $selected[0] '5.0.5.7')
        }
        else {
            $failure = $null
            try { [void](Get-PreviousMsiPayload $payloadRoot '5.0.5.7') }
            catch { $failure = $_.Exception.Message }
            if ($failure -notlike '*one expected MSI*Observed MSI paths*') {
                throw "Payload selection did not safely reject $shape with diagnostics: $failure"
            }
        }
        $caseCount++
        Write-Output "PASS attached MSI payload $shape"
    }
    # Exercise the same script entry point used by CI against a newer,
    # disposable metadata-only database. Historical fixtures above stay fixed.
    $newerMsi = Join-Path $testRoot 'newer-caller-version.msi'
    $createdFiles.Add($newerMsi)
    New-MetadataFixture $newerMsi @{
        ProductName = 'DS4Windows'; ProductVersion = '5.0.5.8'
        ProductCode = '{AFCE9079-ED1B-485E-ABFD-AFA60858A752}'; UpgradeCode = $upgradeCode
    }
    $callerResult = @(& $PSCommandPath -ExistingMsi $newerMsi -ExpectedVersion '5.0.5.8')
    if ($callerResult.Count -ne 1 -or $callerResult[0] -notlike 'PASS existing MSI readonly inspection for expected version 5.0.5.8*') {
        throw 'The actual metadata caller did not accept the explicitly expected newer version.'
    }
    $caseCount++
    Write-Output 'PASS existing MSI caller accepts explicit newer expected version'
    foreach ($callerFailure in @('wrong-expected', 'missing-expected')) {
        $failure = $null
        try {
            if ($callerFailure -eq 'wrong-expected') {
                [void](& $PSCommandPath -ExistingMsi $newerMsi -ExpectedVersion '5.0.5.7')
            }
            else {
                [void](& $PSCommandPath -ExistingMsi $newerMsi)
            }
        }
        catch { $failure = $_.Exception.Message }
        if ($callerFailure -eq 'wrong-expected') {
            if ($failure -notlike '*5.0.5.8*' -or $failure -notlike '*ProductVersion*') {
                throw "Wrong expected version was not rejected with actual MSI metadata: $failure"
            }
        }
        elseif ($failure -notlike '*ExistingMsi and ExpectedVersion must be supplied together*') {
            throw "Missing expected version was not rejected before inspection: $failure"
        }
        $caseCount++
        Write-Output "PASS existing MSI caller rejects $callerFailure"
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
        if (-not [IO.Path]::GetFullPath($path).StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected metadata cleanup target.' }
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    for ($index = $createdDirectories.Count - 1; $index -ge 0; $index--) {
        [IO.Directory]::Delete($createdDirectories[$index], $false)
    }
    [IO.Directory]::Delete($resolvedRoot, $false)
}
