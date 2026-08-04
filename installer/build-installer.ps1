[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [string]$ProductVersion = "5.0.0.0",
    [string]$DisplayVersion = "5.0.0.0",
    [string]$BundleVersion,
    [string]$OutputDirectory,
    [switch]$SkipApplicationPublish
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishPath = [IO.Path]::GetFullPath($PublishRoot)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "bin\x64\Release\installer"
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)

if ($ProductVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "ProductVersion must be a numeric Windows Installer version: $ProductVersion"
}
if (-not $BundleVersion) {
    # Human CI labels (date + commit) belong in filenames, not in the Burn
    # upgrade ordering contract. Release workflows can explicitly pass their
    # monotonic numeric bundle version.
    $BundleVersion = $ProductVersion
}
if ($BundleVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$') {
    throw "BundleVersion must be a numeric or semantic version: $BundleVersion"
}

if (-not $SkipApplicationPublish) {
    & dotnet publish (Join-Path $repoRoot "DS4Windows\DS4WinWPF.csproj") `
        -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        -p:AssemblyVersion=$ProductVersion -p:FileVersion=$ProductVersion `
        -p:Version=$DisplayVersion -p:InformationalVersion=$DisplayVersion `
        -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw "DS4Windows publish failed." }
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath "DS4Windows.exe") -PathType Leaf)) {
    throw "DS4Windows publish output is incomplete: $publishPath"
}

$generatedWix = Join-Path $repoRoot "installer\DS4Windows.Package\GeneratedFiles.wxs"
$manifestPath = Join-Path $publishPath "package-manifest.json"
& python (Join-Path $repoRoot "utils\generate-installer-files.py") `
    $publishPath $generatedWix $manifestPath --version $DisplayVersion
if ($LASTEXITCODE -ne 0) { throw "Installer manifest generation failed." }

& dotnet build (Join-Path $repoRoot "installer\DS4Windows.SetupActions\DS4Windows.SetupActions.csproj") `
    -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Setup action host build failed." }

& dotnet build (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj") `
    -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper UI build failed." }

$packageProject = Join-Path $repoRoot "installer\DS4Windows.Package\DS4Windows.Package.wixproj"
& dotnet build $packageProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:ProductVersion=$ProductVersion -p:PublishRoot=$publishPath
if ($LASTEXITCODE -ne 0) { throw "DS4Windows MSI build failed." }

$msiPath = Join-Path $repoRoot "installer\DS4Windows.Package\bin\x64\Release\DS4Windows_${ProductVersion}_x64.msi"
$baRoot = Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\bin\x64\Release\net48\win-x64"
$setupActions = Join-Path $repoRoot "installer\DS4Windows.SetupActions\bin\x64\Release\net48\DS4Windows.SetupActions.exe"
$extrasRoot = Join-Path $repoRoot "extras"
$bundleProject = Join-Path $repoRoot "installer\DS4Windows.Bundle\DS4Windows.Bundle.wixproj"
& dotnet build $bundleProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:BundleVersion=$BundleVersion -p:DisplayVersion=$DisplayVersion `
    -p:MsiPath=$msiPath -p:BootstrapperRoot=$baRoot `
    -p:SetupActionsPath=$setupActions -p:ExtrasRoot=$extrasRoot
if ($LASTEXITCODE -ne 0) { throw "DS4Windows Burn bundle build failed." }

$builtInstaller = Join-Path $repoRoot "installer\DS4Windows.Bundle\bin\x64\Release\DS4Windows_${DisplayVersion}_Setup_x64.exe"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$finalInstaller = Join-Path $outputPath "DS4Windows_${DisplayVersion}_Setup_x64.exe"
Copy-Item -LiteralPath $builtInstaller -Destination $finalInstaller -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $outputPath "package-manifest.json") -Force

& python (Join-Path $repoRoot "utils\validate-installer.py") `
    --publish-root $publishPath --manifest $manifestPath `
    --installer $finalInstaller --bundle-source (Join-Path $repoRoot "installer\DS4Windows.Bundle\Bundle.wxs")
if ($LASTEXITCODE -ne 0) { throw "Installer validation failed." }

if ($env:DS4W_SIGN_CERT_PATH) {
    $signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
    $timestampUrl = if ($env:DS4W_SIGN_TIMESTAMP_URL) { $env:DS4W_SIGN_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
    & $signtool sign /fd SHA256 /f $env:DS4W_SIGN_CERT_PATH `
        /p $env:DS4W_SIGN_CERT_PASSWORD /tr $timestampUrl /td SHA256 $finalInstaller
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed." }
}

Write-Host "Standard installer ready: $finalInstaller" -ForegroundColor Green
