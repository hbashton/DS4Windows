[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [string]$ProductVersion = "5.0.1.0",
    [string]$DisplayVersion = "5.0.1.0",
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

$buildMutex = [Threading.Mutex]::new($false,
    "Global\DS4Windows-Installer-Build")
$buildMutexOwned = $false
try {
    try {
        $buildMutexOwned = $buildMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $buildMutexOwned = $true
    }
    if (-not $buildMutexOwned) {
        throw (
            "Another DS4Windows installer composition is already running. " +
            "Wait for that build to finish instead of overlapping WiX/MSI validation."
        )
    }

    # A forcibly terminated shell releases its mutex immediately, while a
    # dotnet/WiX child can survive long enough to keep MSI consistency
    # validation active. Converge only exact build children whose command
    # line points into this repository before starting another composition.
    $repoPattern = [regex]::Escape($repoRoot.TrimEnd('\', '/'))
    $installerProjectPattern =
        '(?i)(DS4Windows\.Package|DS4Windows\.Bundle|build-installer\.ps1)'
    $orphanedBuilds = @()
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $orphanedBuilds = @(Get-CimInstance Win32_Process `
            -ErrorAction SilentlyContinue | Where-Object {
                $_.ProcessId -ne $PID -and $_.CommandLine -and
                $_.CommandLine -match $repoPattern -and
                $_.CommandLine -match $installerProjectPattern
            })
        if ($orphanedBuilds.Count -eq 0) { break }
        if ($attempt -eq 0) {
            Write-Host (
                "Waiting for a previous orphaned WiX/MSI build from this " +
                "repository to exit safely...") -ForegroundColor Yellow
        }
        Start-Sleep -Seconds 1
    }
    if ($orphanedBuilds.Count -gt 0) {
        $owners = ($orphanedBuilds | ForEach-Object {
            "PID=$($_.ProcessId) $($_.Name)"
        }) -join ", "
        throw (
            "A previous installer build from this repository is still " +
            "active after 120 seconds ($owners). No overlapping WiX/MSI " +
            "composition was started."
        )
    }

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
if ([string]::IsNullOrWhiteSpace($DisplayVersion) -or
    $DisplayVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,79}$') {
    throw (
        "DisplayVersion must be a filename-safe release identifier using " +
        "only letters, numbers, periods, underscores, and hyphens: " +
        $DisplayVersion
    )
}

if (-not $SkipApplicationPublish) {
    & dotnet publish (Join-Path $repoRoot "DS4Windows\DS4WinWPF.csproj") `
        -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        -p:AssemblyVersion=$ProductVersion -p:FileVersion=$ProductVersion `
        -p:Version=$ProductVersion -p:InformationalVersion=$DisplayVersion `
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
    -c Release -p:Platform=x64 -p:Version=$ProductVersion
if ($LASTEXITCODE -ne 0) { throw "Setup action host build failed." }

& dotnet build (Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj") `
    -c Release -p:Platform=x64 -p:Version=$ProductVersion
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper UI build failed." }

$packageProject = Join-Path $repoRoot "installer\DS4Windows.Package\DS4Windows.Package.wixproj"
& dotnet build $packageProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:ProductVersion=$ProductVersion `
    -p:PublishRoot=$publishPath
if ($LASTEXITCODE -ne 0) { throw "DS4Windows MSI build failed." }

$msiPath = Join-Path $repoRoot "installer\DS4Windows.Package\bin\x64\Release\DS4Windows_${ProductVersion}_x64.msi"
$baRoot = Join-Path $repoRoot "installer\DS4Windows.Bootstrapper\bin\x64\Release\net48\win-x64"
$setupActions = Join-Path $repoRoot "installer\DS4Windows.SetupActions\bin\x64\Release\net48\DS4Windows.SetupActions.exe"
$setupActionsHash = (Get-FileHash -LiteralPath $setupActions -Algorithm SHA256).Hash
if ($setupActionsHash -notmatch '^[0-9A-F]{64}$') {
    throw "Could not derive a content-addressed setup-helper cache identity."
}
$extrasRoot = Join-Path $repoRoot "extras"
$bundleProject = Join-Path $repoRoot "installer\DS4Windows.Bundle\DS4Windows.Bundle.wixproj"
& dotnet build $bundleProject -t:Rebuild -c Release -p:Platform=x64 `
    -p:Version=$ProductVersion -p:BundleVersion=$BundleVersion `
    -p:DisplayVersion=$DisplayVersion `
    -p:MsiPath=$msiPath -p:BootstrapperRoot=$baRoot `
    -p:SetupActionsPath=$setupActions -p:SetupActionsHash=$setupActionsHash `
    -p:ExtrasRoot=$extrasRoot
if ($LASTEXITCODE -ne 0) { throw "DS4Windows Burn bundle build failed." }

$builtInstaller = Join-Path $repoRoot "installer\DS4Windows.Bundle\bin\x64\Release\DS4Windows_${DisplayVersion}_Setup_x64.exe"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$finalInstaller = Join-Path $outputPath "DS4Windows_${DisplayVersion}_Setup_x64.exe"
$finalManifest = Join-Path $outputPath "package-manifest.json"
$publishId = [Guid]::NewGuid().ToString("N")
$pendingInstaller = $finalInstaller + ".pending-" + $publishId
$pendingManifest = $finalManifest + ".pending-" + $publishId

function Publish-InstallerFileAtomically(
        [string]$source, [string]$destination) {
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $backup = $destination + ".backup-" + [Guid]::NewGuid().ToString("N")
        try {
            [IO.File]::Replace($source, $destination, $backup, $true)
        }
        finally {
            if (Test-Path -LiteralPath $backup) {
                Remove-Item -LiteralPath $backup -Force
            }
        }
    }
    else {
        [IO.File]::Move($source, $destination)
    }
}

try {
    Copy-Item -LiteralPath $builtInstaller -Destination $pendingInstaller
    Copy-Item -LiteralPath $manifestPath -Destination $pendingManifest

& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $repoRoot "utils\test-viiper-reboot-boundary.ps1") `
    -BackendScript (Join-Path $repoRoot "extras\install-viiper-backend.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "USB-IP reboot-boundary simulation failed."
}

& python (Join-Path $repoRoot "utils\test-installer-state-machine.py")
if ($LASTEXITCODE -ne 0) {
    throw "Installer state-machine simulation failed."
}

& python (Join-Path $repoRoot "utils\validate-installer.py") `
    --publish-root $publishPath --manifest $manifestPath `
    --installer $pendingInstaller --bundle-source (Join-Path $repoRoot "installer\DS4Windows.Bundle\Bundle.wxs")
if ($LASTEXITCODE -ne 0) { throw "Installer validation failed." }

if ($env:DS4W_SIGN_CERT_PATH) {
    $signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
    $timestampUrl = if ($env:DS4W_SIGN_TIMESTAMP_URL) { $env:DS4W_SIGN_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
    & $signtool sign /fd SHA256 /f $env:DS4W_SIGN_CERT_PATH `
        /p $env:DS4W_SIGN_CERT_PASSWORD /tr $timestampUrl /td SHA256 $pendingInstaller
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed." }
    $signature = Get-AuthenticodeSignature -LiteralPath $pendingInstaller
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed after signing: $($signature.StatusMessage)"
    }
}

    Publish-InstallerFileAtomically $pendingInstaller $finalInstaller
    Publish-InstallerFileAtomically $pendingManifest $finalManifest
}
finally {
    foreach ($pending in @($pendingInstaller, $pendingManifest)) {
        if (Test-Path -LiteralPath $pending) {
            Remove-Item -LiteralPath $pending -Force
        }
    }
}

Write-Host "Standard installer ready: $finalInstaller" -ForegroundColor Green
}
finally {
    if ($buildMutexOwned) {
        try { $buildMutex.ReleaseMutex() } catch { }
    }
    $buildMutex.Dispose()
}
