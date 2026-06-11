param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $PSScriptRoot "HBashtonVirtualDualSense.vcxproj"
$packagesConfig = Join-Path $repoRoot "packages.config"
$packagesDir = Join-Path $repoRoot "packages"

function Get-NuGetExe {
    $nuget = Get-Command nuget.exe -ErrorAction SilentlyContinue
    if ($nuget) {
        return $nuget.Source
    }

    $toolsDir = Join-Path $repoRoot ".tools"
    $nugetExe = Join-Path $toolsDir "nuget.exe"
    if (-not (Test-Path $nugetExe)) {
        New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nugetExe
    }

    return $nugetExe
}

$nugetExe = Get-NuGetExe
& $nugetExe restore $packagesConfig -PackagesDirectory $packagesDir -Source "https://api.nuget.org/v3/index.json" -NonInteractive

function Get-MSBuildExe {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($installPath) {
            $amd64Candidate = Join-Path $installPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
            if (Test-Path $amd64Candidate) {
                return $amd64Candidate
            }

            $candidate = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    $msbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($msbuild) {
        return $msbuild.Source
    }

    return $null
}

$msbuild = Get-MSBuildExe
if (-not $msbuild) {
    throw "MSBuild was not found. Install Visual Studio with the Windows Driver Kit and KMDF tools."
}

& $msbuild $project /m /p:Configuration=$Configuration /p:Platform=$Platform
