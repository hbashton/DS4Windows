[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [string]$ProductVersion = '4.0.2.1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishPath = [IO.Path]::GetFullPath($PublishRoot)

if ($ProductVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw 'ProductVersion must be numeric.'
}
$applicationPath = Join-Path $publishPath 'DS4Windows.exe'
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Composition-test publish root is incomplete: '$publishPath'."
}
$publishItem = Get-Item -LiteralPath $publishPath -Force
if (($publishItem.Attributes -band
     [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Composition-test publish root cannot be a reparse point.'
}

# This script proves that all WiX sources bind into an MSI/Burn PE. It never
# signs, publishes, launches, or installs the disposable output. Production
# callers must use build-installer.ps1, which has no unsigned mode.
$generatedWix = Join-Path $repoRoot `
    'installer\DS4Windows.Package\GeneratedFiles.wxs'
$manifestPath = Join-Path $publishPath 'package-manifest.json'
& python (Join-Path $repoRoot `
    'utils\generate-installer-files.py') $publishPath `
    $generatedWix $manifestPath --version 'composition-test'
if ($LASTEXITCODE -ne 0) {
    throw 'Composition-test manifest generation failed.'
}

$setupProject = Join-Path $repoRoot `
    'installer\DS4Windows.SetupActions\DS4Windows.SetupActions.csproj'
$bootstrapperProject = Join-Path $repoRoot `
    'installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj'
& dotnet build $setupProject -t:Rebuild -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'SetupActions build failed.' }
& dotnet build $bootstrapperProject -t:Rebuild -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper build failed.' }

$packageProject = Join-Path $repoRoot `
    'installer\DS4Windows.Package\DS4Windows.Package.wixproj'
& dotnet build $packageProject -t:Rebuild -c Release `
    -p:Platform=x64 -p:ProductVersion=$ProductVersion `
    "-p:PublishRoot=$publishPath"
if ($LASTEXITCODE -ne 0) { throw 'Composition-test MSI build failed.' }
$msi = Join-Path $repoRoot (
    'installer\DS4Windows.Package\bin\x64\Release\DS4Windows_' +
    $ProductVersion + '_x64.msi')

$setupActions = Join-Path $repoRoot `
    'installer\DS4Windows.SetupActions\bin\x64\Release\net48\DS4Windows.SetupActions.exe'
$bootstrapperRoot = Join-Path $repoRoot `
    'installer\DS4Windows.Bootstrapper\bin\x64\Release\net48\win-x64'
$setupHash = (Get-FileHash -LiteralPath $setupActions `
    -Algorithm SHA256).Hash
$bundleProject = Join-Path $repoRoot `
    'installer\DS4Windows.Bundle\DS4Windows.Bundle.wixproj'
& dotnet build $bundleProject -t:Rebuild -c Release `
    -p:Platform=x64 -p:BundleVersion=$ProductVersion `
    -p:DisplayVersion=composition-test `
    "-p:MsiPath=$msi" `
    "-p:BootstrapperRoot=$bootstrapperRoot" `
    "-p:SetupActionsPath=$setupActions" `
    "-p:SetupActionsHash=$setupHash" `
    "-p:RepositoryRoot=$repoRoot"
if ($LASTEXITCODE -ne 0) { throw 'Composition-test Burn build failed.' }

$bundle = Join-Path $repoRoot `
    'installer\DS4Windows.Bundle\bin\x64\Release\DS4Windows_composition-test_Setup_x64.exe'
$item = Get-Item -LiteralPath $bundle -Force -ErrorAction Stop
if ($item.Length -le 262144) {
    throw 'Composition-test Burn output is unexpectedly small.'
}
$stream = [IO.File]::OpenRead($bundle)
try {
    if ($stream.ReadByte() -ne [byte][char]'M' -or
        $stream.ReadByte() -ne [byte][char]'Z') {
        throw 'Composition-test Burn output is not a Windows PE file.'
    }
} finally {
    $stream.Dispose()
}

Write-Host "Disposable WiX composition passed: $bundle"
