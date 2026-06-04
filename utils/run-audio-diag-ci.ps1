param(
    [string]$Repo = "hbashton/DS4Windows",
    [string]$Branch = "triggertest",
    [string]$HeadSha = "",
    [string]$ArtifactName = "DS4Windows-x64",
    [int]$DurationSeconds = 45,
    [string]$CaptureEndpointId = "",
    [string]$SpeakerEndpointId = "",
    [string]$DownloadRoot = "$PSScriptRoot\..\ci-audio-diag",
    [switch]$NoShutdown,
    [switch]$NoForceSpeaker
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "TEST BUILD AUDIO DIAG SCRIPT: $Message"
}

function Require-Command($Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Get-GitHubHeaders {
    $headers = @{
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }

    if ($env:GITHUB_TOKEN) {
        $headers.Authorization = "Bearer $env:GITHUB_TOKEN"
    }

    return $headers
}

function Invoke-GitHubJson($Uri) {
    Invoke-RestMethod -Uri $Uri -Headers (Get-GitHubHeaders)
}

function Get-LatestSuccessfulRun($Repo, $Branch) {
    $encodedBranch = [System.Uri]::EscapeDataString($Branch)
    $uri = "https://api.github.com/repos/$Repo/actions/workflows/ci-build.yml/runs?branch=$encodedBranch&per_page=10"
    $runs = (Invoke-GitHubJson $uri).workflow_runs
    $completed = $runs | Where-Object { $_.status -eq "completed" } | Select-Object -First 1
    return $completed
}

function Download-Artifact($Repo, $RunId, $ArtifactName, $DownloadRoot) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        gh run download $RunId --repo $Repo --name $ArtifactName --dir $DownloadRoot
        return
    }

    $artifacts = (Invoke-GitHubJson "https://api.github.com/repos/$Repo/actions/runs/$RunId/artifacts").artifacts
    $artifact = $artifacts | Where-Object { $_.name -eq $ArtifactName } | Select-Object -First 1
    if ($artifact -eq $null) {
        throw "Artifact '$ArtifactName' was not found in run $RunId."
    }

    $containerZip = Join-Path $DownloadRoot "$ArtifactName-container.zip"
    Invoke-WebRequest -Uri $artifact.archive_download_url -Headers (Get-GitHubHeaders) -OutFile $containerZip

    $artifactDir = Join-Path $DownloadRoot "$ArtifactName-container"
    New-Item -ItemType Directory -Path $artifactDir | Out-Null
    Expand-Archive -LiteralPath $containerZip -DestinationPath $artifactDir -Force
}

$repoRoot = Resolve-Path "$PSScriptRoot\.."
Set-Location $repoRoot
if ($HeadSha.Length -eq 0) {
    $HeadSha = (git rev-parse HEAD).Trim()
}

Write-Step "waiting for successful CI Build on repo '$Repo' branch '$Branch' sha '$HeadSha'"
$run = $null
for ($attempt = 0; $attempt -lt 60 -and $run -eq $null; $attempt++) {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $runsJson = gh run list --repo $Repo --workflow "CI Build" --branch $Branch --limit 10 --json databaseId,status,conclusion,headSha,createdAt
        $runs = $runsJson | ConvertFrom-Json
        $completed = $runs | Where-Object { $_.status -eq "completed" } | Select-Object -First 1
    }
    else {
        $completed = Get-LatestSuccessfulRun $Repo $Branch
    }

    $completedSha = if ($completed.headSha) { $completed.headSha } else { $completed.head_sha }
    if ($completed -and $completedSha -ne $HeadSha) {
        Write-Step "latest completed run is for sha '$completedSha'; waiting for '$HeadSha'"
        Start-Sleep -Seconds 15
        continue
    }

    if ($completed -and $completed.conclusion -eq "success") {
        $run = $completed
        break
    }

    if ($completed -and $completed.conclusion -ne "success") {
        Write-Step "latest completed run $($completed.databaseId) ended with conclusion '$($completed.conclusion)'; waiting for a newer successful run"
    }
    else {
        Write-Step "no completed run yet; waiting"
    }

    Start-Sleep -Seconds 15
}

if ($run -eq $null) {
    throw "Timed out waiting for a successful CI Build run on branch '$Branch'."
}

$runId = if ($run.databaseId) { $run.databaseId } else { $run.id }
$runSha = if ($run.headSha) { $run.headSha } else { $run.head_sha }
Write-Step "using run $runId sha=$runSha"

if (Test-Path $DownloadRoot) {
    Remove-Item -LiteralPath $DownloadRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $DownloadRoot | Out-Null
Download-Artifact $Repo $runId $ArtifactName $DownloadRoot

$artifactZip = Get-ChildItem -Path $DownloadRoot -Filter *.zip -Recurse |
    Where-Object { $_.Name -like "DS4Windows_*.zip" } |
    Select-Object -First 1
if ($artifactZip -eq $null) {
    throw "Downloaded artifact '$ArtifactName' did not contain a zip file."
}

$extractDir = Join-Path $DownloadRoot "expanded"
New-Item -ItemType Directory -Path $extractDir | Out-Null
Expand-Archive -LiteralPath $artifactZip.FullName -DestinationPath $extractDir -Force

$exe = Get-ChildItem -Path $extractDir -Filter DS4Windows.exe -Recurse | Select-Object -First 1
if ($exe -eq $null) {
    throw "Could not find DS4Windows.exe in downloaded artifact."
}

Write-Step "stopping any running DS4Windows processes"
Get-Process DS4Windows -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$args = @("-audiodiag", "$DurationSeconds", "-m")
if ($NoShutdown) {
    $args += "-audiodiag-noshutdown"
}
if ($NoForceSpeaker) {
    $args += "-audiodiag-noforce"
}
if ($CaptureEndpointId.Length -gt 0) {
    $args += @("-audiodiag-capture", $CaptureEndpointId)
}
if ($SpeakerEndpointId.Length -gt 0) {
    $args += @("-audiodiag-speaker", $SpeakerEndpointId)
}

Write-Step "starting '$($exe.FullName)' $($args -join ' ')"
$process = Start-Process -FilePath $exe.FullName -ArgumentList $args -WorkingDirectory $exe.DirectoryName -PassThru

$waitSeconds = $DurationSeconds + 20
if (-not $process.WaitForExit($waitSeconds * 1000)) {
    Write-Step "process did not exit after ${waitSeconds}s; stopping it"
    $process | Stop-Process -Force
}

$logRoots = @(
    (Join-Path $exe.DirectoryName "Logs"),
    (Join-Path $env:APPDATA "DS4Windows\Logs")
) | Where-Object { Test-Path $_ }

if ($logRoots.Count -eq 0) {
    Write-Step "no log folders found near artifact or in AppData"
    return
}

$latestLog = Get-ChildItem -Path $logRoots -Filter *.txt -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($latestLog -eq $null) {
    Write-Step "no log files found"
    return
}

Write-Step "latest log: $($latestLog.FullName)"
Select-String -Path $latestLog.FullName -Pattern "TEST BUILD AUDIO DIAG" | ForEach-Object {
    $_.Line
}
