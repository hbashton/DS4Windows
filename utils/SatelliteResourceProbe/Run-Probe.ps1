param(
    [Parameter(Mandatory = $true)][string]$SourceBuild,
    [switch]$StandardLayout
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $SourceBuild).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$probeBuild = Join-Path $PSScriptRoot 'bin/Release/net8.0-windows'
$fixtureRoot = Join-Path $repositoryRoot ('artifacts/issue60-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $fixtureRoot 'package'
$outsideRoot = Join-Path $fixtureRoot 'outside'
New-Item -ItemType Directory -Path $packageRoot, $outsideRoot | Out-Null

# Preserve the real application's resource/dependency identities, but omit
# unrelated libraries: this child only requests resources, never runs the app.
$sourceDeps = Get-Content -LiteralPath (Join-Path $sourceRoot 'DS4Windows.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$sourceTarget = $sourceDeps.targets[$sourceDeps.runtimeTarget.name]
$probeDeps = Get-Content -LiteralPath (Join-Path $probeBuild 'SatelliteResourceProbe.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$probeTarget = $probeDeps.targets[$probeDeps.runtimeTarget.name]
foreach ($libraryName in $sourceTarget.Keys) {
    if ($libraryName -notlike 'DS4Windows/*' -and $libraryName -notlike 'TaskScheduler/*') { continue }
    $library = $sourceTarget[$libraryName]
    $probeTarget[$libraryName] = @{ runtime = $library.runtime; resources = $library.resources }
    $probeDeps.libraries[$libraryName] = $sourceDeps.libraries[$libraryName]
    if ($libraryName -like 'DS4Windows/*') {
        # This is the exact application-path adjustment made by post-build.py.
        $probeDeps.libraries[$libraryName].path = './'
    }

    foreach ($asset in $library.runtime.Keys) {
        $leaf = Split-Path -Leaf $asset
        Copy-Item -LiteralPath (Join-Path $sourceRoot $leaf) -Destination (Join-Path $packageRoot $leaf)
    }
    foreach ($asset in $library.resources.Keys) {
        $culture = $library.resources[$asset].locale
        $leaf = Split-Path -Leaf $asset
        $sourceSatellite = Join-Path $sourceRoot (Join-Path $culture $leaf)
        if (-not (Test-Path -LiteralPath $sourceSatellite -PathType Leaf)) {
            $sourceSatellite = Join-Path $sourceRoot (Join-Path 'Lang' (Join-Path $culture $leaf))
        }
        $destination = if ($StandardLayout) { Join-Path $packageRoot $culture } else { Join-Path $packageRoot (Join-Path 'Lang' $culture) }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $sourceSatellite -Destination (Join-Path $destination $leaf)
    }
}

Copy-Item -LiteralPath (Join-Path $probeBuild 'SatelliteResourceProbe.dll') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $probeBuild 'SatelliteResourceProbe.runtimeconfig.json') -Destination $packageRoot
$runtimeConfigPath = Join-Path $packageRoot 'SatelliteResourceProbe.runtimeconfig.json'
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $StandardLayout) {
    $runtimeConfig.runtimeOptions.additionalProbingPaths = @('./Lang/')
}
$runtimeConfig | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $runtimeConfigPath -Encoding utf8
$probeDeps | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $packageRoot 'SatelliteResourceProbe.deps.json') -Encoding utf8

$resultCount = 0
foreach ($startDirectory in @($packageRoot, $outsideRoot)) {
    Push-Location -LiteralPath $startDirectory
    try {
        $probeOutput = & dotnet (Join-Path $packageRoot 'SatelliteResourceProbe.dll')
        $probeExit = $LASTEXITCODE
        $probeOutput
        $result = $probeOutput | ConvertFrom-Json
        $shouldLocalize = $StandardLayout -or $startDirectory -eq $packageRoot
        if ($result.Localized -ne $shouldLocalize -or
            ($null -ne $result.SchedulerSatellite) -ne $shouldLocalize -or
            -not $result.UnrelatedRejected -or
            $result.RegionalGerman -ne $result.German -or
            $result.UnavailableCulture -ne $result.Neutral -or
            $probeExit -ne $(if ($shouldLocalize) { 0 } else { 1 })) {
            throw 'Resource loading did not match the expected isolated layout behavior.'
        }
        $resultCount++
    }
    finally { Pop-Location }
}
Write-Output "Verified $resultCount isolated resource-loading cases. Fixture: $fixtureRoot"
