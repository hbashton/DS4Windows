<#
.SYNOPSIS
Downloads DS4Windows and VIIPER CI artifacts, runs a timed mic diagnostic
session, and collects the DS4Windows plus Windows raw-input logs.

.DESCRIPTION
This helper is intentionally self-contained so mic corruption tests can be
repeated without hand-copying artifacts. It can use local artifact zips,
download the latest successful artifacts from GitHub Actions, install a VIIPER
artifact over the local backend, start VIIPER and DS4Windows, run the raw-input
debugger, wait for the diagnostic auto-shutdown, and summarize the signatures
that previously leaked mic transport data into Windows input.

.EXAMPLE
.\run-viiper-artifact-mic-diagnostics.ps1 -DurationSeconds 60

.EXAMPLE
.\run-viiper-artifact-mic-diagnostics.ps1 -SkipDownload -ArtifactZip C:\Temp\DS4.zip -SkipViiperInstall
#>
[CmdletBinding()]
param(
    [string]$Repo = "hbashton/DS4Windows",
    [string]$Branch = "viiper-full-backend-debug",
    [string]$Workflow = "ci-build.yml",
    [string]$ArtifactNamePattern = "_x64$",
    [string]$ViiperRepo = "hbashton/VIIPER",
    [string]$ViiperBranch = "main",
    [string]$ViiperWorkflow = "snapshots.yml",
    [string]$ViiperArtifactNamePattern = "^VIIPER-windows-amd64",
    [string]$ViiperArtifactZip = "",
    [string]$ViiperExePath = "",
    [string]$ViiperInstallPath = (Join-Path $env:LOCALAPPDATA "VIIPER\viiper.exe"),
    [int]$DurationSeconds = 75,
    [string]$WorkRoot = (Join-Path $env:TEMP "ds4windows-viiper-artifact-diagnostics"),
    [string]$ArtifactZip = "",
    [switch]$SkipDownload,
    [switch]$SkipViiperDownload,
    [switch]$SkipViiperInstall,
    [switch]$IncludeUnchangedHid,
    [switch]$NoViiperStart,
    [switch]$KeepProcesses
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Join-Arguments {
    param([string[]]$Parts)
    return ($Parts | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join " "
}

if (-not (Test-Administrator)) {
    $script = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($script)) {
        throw "This script must be saved to disk before it can self-elevate."
    }

    $argsList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $script,
        "-Repo", $Repo,
        "-Branch", $Branch,
        "-Workflow", $Workflow,
        "-ArtifactNamePattern", $ArtifactNamePattern,
        "-ViiperRepo", $ViiperRepo,
        "-ViiperBranch", $ViiperBranch,
        "-ViiperWorkflow", $ViiperWorkflow,
        "-ViiperArtifactNamePattern", $ViiperArtifactNamePattern,
        "-ViiperInstallPath", $ViiperInstallPath,
        "-DurationSeconds", "$DurationSeconds",
        "-WorkRoot", $WorkRoot
    )

    if ($ArtifactZip) { $argsList += @("-ArtifactZip", $ArtifactZip) }
    if ($ViiperArtifactZip) { $argsList += @("-ViiperArtifactZip", $ViiperArtifactZip) }
    if ($ViiperExePath) { $argsList += @("-ViiperExePath", $ViiperExePath) }
    if ($SkipDownload) { $argsList += "-SkipDownload" }
    if ($SkipViiperDownload) { $argsList += "-SkipViiperDownload" }
    if ($SkipViiperInstall) { $argsList += "-SkipViiperInstall" }
    if ($IncludeUnchangedHid) { $argsList += "-IncludeUnchangedHid" }
    if ($NoViiperStart) { $argsList += "-NoViiperStart" }
    if ($KeepProcesses) { $argsList += "-KeepProcesses" }

    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -Verb RunAs `
        -ArgumentList (Join-Arguments $argsList)
    return
}

function Get-GitHubHeaders {
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "DS4Windows-VIIPER-MicDiagnostics"
        "X-GitHub-Api-Version" = "2022-11-28"
    }

    $token = $env:GH_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = $env:GITHUB_TOKEN
    }

    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $headers["Authorization"] = "Bearer $token"
    }

    return $headers
}

function Invoke-GitHubApi {
    param([string]$Uri)
    return Invoke-RestMethod -Uri $Uri -Headers (Get-GitHubHeaders)
}

function Save-LatestArtifact {
    param(
        [string]$Label,
        [string]$Repository,
        [string]$BranchName,
        [string]$WorkflowName,
        [string]$NamePattern,
        [string]$DestinationZip
    )

    Write-Step "Finding latest successful $Label artifact"
    $encodedBranch = [Uri]::EscapeDataString($BranchName)
    $encodedWorkflow = [Uri]::EscapeDataString($WorkflowName)
    $runsUri = "https://api.github.com/repos/$Repository/actions/workflows/$encodedWorkflow/runs?branch=$encodedBranch&status=success&per_page=10"
    $runs = Invoke-GitHubApi $runsUri
    $run = @($runs.workflow_runs | Where-Object { $_.conclusion -eq "success" } | Select-Object -First 1)
    if (-not $run) {
        throw "No successful workflow run found for $Repository branch $BranchName workflow $WorkflowName."
    }

    Write-Host ("Run: id={0} head={1} created={2}" -f $run.id, $run.head_sha, $run.created_at)
    $artifacts = Invoke-GitHubApi "https://api.github.com/repos/$Repository/actions/runs/$($run.id)/artifacts?per_page=100"
    $artifact = @($artifacts.artifacts |
        Where-Object { -not $_.expired -and $_.name -match $NamePattern } |
        Sort-Object name |
        Select-Object -First 1)

    if (-not $artifact) {
        $names = (@($artifacts.artifacts | ForEach-Object { $_.name }) -join ", ")
        throw "No non-expired artifact matched '$NamePattern'. Available artifacts: $names"
    }

    Write-Host ("{0} artifact: {1} size={2} bytes" -f $Label, $artifact.name, $artifact.size_in_bytes)
    Invoke-WebRequest -Uri $artifact.archive_download_url -Headers (Get-GitHubHeaders) -OutFile $DestinationZip -UseBasicParsing
    return @{
        RunId = $run.id
        HeadSha = $run.head_sha
        CreatedAt = $run.created_at
        ArtifactName = $artifact.name
        Zip = $DestinationZip
    }
}

function Stop-NamedProcesses {
    param([string[]]$Names)

    foreach ($name in $Names) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Write-Host "Stopping $($process.ProcessName).exe pid=$($process.Id)"
            try {
                if ($process.MainWindowHandle -ne 0) {
                    [void]$process.CloseMainWindow()
                    if ($process.WaitForExit(2500)) {
                        continue
                    }
                }
            }
            catch { }

            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            try { [void]$process.WaitForExit(5000) } catch { }
        }
    }
}

function Expand-ZipToDirectory {
    param(
        [string]$ZipPath,
        [string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $Destination -Force
}

function Find-ExecutableInExtractedArtifact {
    param(
        [string]$Root,
        [string]$ExecutableName
    )

    $direct = Get-ChildItem -LiteralPath $Root -Filter $ExecutableName -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($direct) {
        return $direct.FullName
    }

    $nestedZips = @(Get-ChildItem -LiteralPath $Root -Filter "*.zip" -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName)
    $nestedIndex = 0
    foreach ($nestedZip in $nestedZips) {
        $nestedIndex++
        $nestedDestination = Join-Path $Root ("nested-{0}-{1}" -f $nestedIndex, [IO.Path]::GetFileNameWithoutExtension($nestedZip.Name))
        Expand-ZipToDirectory -ZipPath $nestedZip.FullName -Destination $nestedDestination

        $nestedExe = Get-ChildItem -LiteralPath $nestedDestination -Filter $ExecutableName -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object FullName |
            Select-Object -First 1
        if ($nestedExe) {
            return $nestedExe.FullName
        }
    }

    return $null
}

function Install-ViiperExecutable {
    param(
        [string]$SourceExe,
        [string]$InstallPath
    )

    if ([string]::IsNullOrWhiteSpace($SourceExe) -or -not (Test-Path -LiteralPath $SourceExe)) {
        throw "VIIPER source executable was not found: $SourceExe"
    }

    $resolvedInstallPath = [IO.Path]::GetFullPath($InstallPath)
    $installDir = Split-Path -Parent $resolvedInstallPath
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null

    Stop-NamedProcesses @("viiper")
    Write-Host "Installing VIIPER: $SourceExe -> $resolvedInstallPath"
    Copy-Item -LiteralPath $SourceExe -Destination $resolvedInstallPath -Force
    return $resolvedInstallPath
}

function Start-InstalledViiper {
    param([string]$InstallPath)

    $candidates = @(
        $InstallPath,
        (Join-Path $env:LOCALAPPDATA "VIIPER\viiper.exe"),
        (Join-Path $env:ProgramFiles "VIIPER\viiper.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "VIIPER\viiper.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        Select-Object -Unique

    $viiperExe = @($candidates | Select-Object -First 1)
    if (-not $viiperExe) {
        Write-Host "VIIPER executable was not found; assuming it is already running or not needed." -ForegroundColor Yellow
        return $null
    }

    if (Get-Process -Name "viiper" -ErrorAction SilentlyContinue) {
        Write-Host "VIIPER is already running."
        return $null
    }

    Write-Host "Starting VIIPER: $viiperExe"
    return Start-Process -FilePath $viiperExe -WorkingDirectory (Split-Path -Parent $viiperExe) -PassThru -WindowStyle Hidden
}

function Get-RecentFile {
    param(
        [string]$Path,
        [string]$Filter
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $Path -Filter $Filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Count-RegexMatches {
    param(
        [string]$Path,
        [string]$Pattern
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return 0
    }

    $count = 0
    Select-String -LiteralPath $Path -Pattern $Pattern -AllMatches -ErrorAction SilentlyContinue |
        ForEach-Object { $count += $_.Matches.Count }
    return $count
}

function Copy-IfPresent {
    param(
        [string]$Path,
        [string]$DestinationDirectory
    )

    if ($Path -and (Test-Path -LiteralPath $Path)) {
        $destination = Join-Path $DestinationDirectory (Split-Path -Leaf $Path)
        Copy-Item -LiteralPath $Path -Destination $destination -Force
        return $destination
    }

    return $null
}

function Write-DiagnosticsSummary {
    param(
        [string]$SessionDirectory,
        [string]$Ds4LogCopy,
        [string]$InputLogCopy,
        [hashtable]$Ds4ArtifactInfo,
        [hashtable]$ViiperArtifactInfo,
        [string]$InstalledViiperPath
    )

    $summaryPath = Join-Path $SessionDirectory "diagnostic-summary.txt"
    $patterns = @(
        @{ Name = "full_cm_signature"; Regex = "43 4D 01 01 21" },
        @{ Name = "shifted_cm_signature"; Regex = "4D 01 01 21" },
        @{ Name = "short_cm_signature"; Regex = "01 01 21" },
        @{ Name = "full_cp_signature"; Regex = "43 50 80 87 43" },
        @{ Name = "shifted_cp_signature"; Regex = "50 80 87 43" },
        @{ Name = "short_cp_signature"; Regex = "80 87 43" },
        @{ Name = "virtual_dualsense_hid"; Regex = "VID_054C&PID_0CE6&MI_03" },
        @{ Name = "virtual_dualsense_audio"; Regex = "VID_054C&PID_0CE6&MI_01" },
        @{ Name = "mic_diverted"; Regex = "MIC_FRAME_CAPTURED_DIVERTED_FROM_HID_PARSER" },
        @{ Name = "mic_queue"; Regex = "VIIPER_MIC_QUEUE_DIAG" },
        @{ Name = "mic_health"; Regex = "VIIPER_MIC_HEALTH" },
        @{ Name = "control_report"; Regex = "DualSenseMicDiag CONTROL_REPORT" },
        @{ Name = "ds4windows_corrupt"; Regex = "corrupt|micLeak=true|transport leak|MIC_TRANSPORT" },
        @{ Name = "viiper_usb_corrupt"; Regex = "DualSense USB input report was corrupt|DualSense framed input was corrupt" }
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("DS4Windows VIIPER artifact mic diagnostic summary")
    $lines.Add("UTC: $([DateTime]::UtcNow.ToString("O"))")
    $lines.Add("Session: $SessionDirectory")
    if ($Ds4ArtifactInfo) {
        $lines.Add(("DS4 artifact: run={0} head={1} name={2}" -f $Ds4ArtifactInfo.RunId, $Ds4ArtifactInfo.HeadSha, $Ds4ArtifactInfo.ArtifactName))
    }
    if ($ViiperArtifactInfo) {
        $lines.Add(("VIIPER artifact: run={0} head={1} name={2}" -f $ViiperArtifactInfo.RunId, $ViiperArtifactInfo.HeadSha, $ViiperArtifactInfo.ArtifactName))
    }
    if ($InstalledViiperPath) {
        $lines.Add("Installed VIIPER path: $InstalledViiperPath")
    }
    $lines.Add("DS4 log: $Ds4LogCopy")
    $lines.Add("Windows input log: $InputLogCopy")
    $lines.Add("")

    foreach ($entry in $patterns) {
        $ds4Count = Count-RegexMatches -Path $Ds4LogCopy -Pattern $entry.Regex
        $inputCount = Count-RegexMatches -Path $InputLogCopy -Pattern $entry.Regex
        $lines.Add(("{0}: ds4={1} windowsInput={2}" -f $entry.Name, $ds4Count, $inputCount))
    }

    $lines.Add("")
    $lines.Add("Recent DS4 mic/control lines:")
    if ($Ds4LogCopy -and (Test-Path -LiteralPath $Ds4LogCopy)) {
        Select-String -LiteralPath $Ds4LogCopy -Pattern "DualSenseMicDiag|VIIPER_MIC_|micLeak|corrupt|CONTROL_REPORT" -ErrorAction SilentlyContinue |
            Select-Object -Last 80 |
            ForEach-Object { $lines.Add($_.Line) }
    }

    $lines.Add("")
    $lines.Add("Recent Windows raw input signature lines:")
    if ($InputLogCopy -and (Test-Path -LiteralPath $InputLogCopy)) {
        Select-String -LiteralPath $InputLogCopy -Pattern "VID_054C&PID_0CE6&MI_03|43 4D 01 01 21|4D 01 01 21|01 01 21|43 50 80 87 43|50 80 87 43|80 87 43" -ErrorAction SilentlyContinue |
            Select-Object -Last 80 |
            ForEach-Object { $lines.Add($_.Line) }
    }

    Set-Content -LiteralPath $summaryPath -Value $lines -Encoding UTF8
    return $summaryPath
}

if ($DurationSeconds -lt 20) {
    $DurationSeconds = 20
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$inputDebuggerScript = Join-Path $PSScriptRoot "windows-input-debugger.ps1"
if (-not (Test-Path -LiteralPath $inputDebuggerScript)) {
    throw "Could not find windows-input-debugger.ps1 next to this script."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$sessionRoot = Join-Path $WorkRoot $stamp
$downloadRoot = Join-Path $sessionRoot "download"
$extractRoot = Join-Path $sessionRoot "extract"
$logRoot = Join-Path $sessionRoot "logs"
$viiperExtractRoot = Join-Path $sessionRoot "viiper-extract"
New-Item -ItemType Directory -Path $downloadRoot,$extractRoot,$logRoot -Force | Out-Null

try {
    $ds4ArtifactInfo = $null
    $viiperArtifactInfo = $null
    $installedViiperPath = $null

    if (-not $SkipDownload) {
        if ([string]::IsNullOrWhiteSpace($ArtifactZip)) {
            $ArtifactZip = Join-Path $downloadRoot "ds4windows-artifact.zip"
        }

        $ds4ArtifactInfo = Save-LatestArtifact -Label "DS4Windows" -Repository $Repo -BranchName $Branch -WorkflowName $Workflow -NamePattern $ArtifactNamePattern -DestinationZip $ArtifactZip
    }
    elseif ([string]::IsNullOrWhiteSpace($ArtifactZip)) {
        throw "-SkipDownload requires -ArtifactZip."
    }

    if (-not $SkipViiperInstall) {
        if ([string]::IsNullOrWhiteSpace($ViiperExePath)) {
            if (-not $SkipViiperDownload) {
                if ([string]::IsNullOrWhiteSpace($ViiperArtifactZip)) {
                    $ViiperArtifactZip = Join-Path $downloadRoot "viiper-artifact.zip"
                }

                $viiperArtifactInfo = Save-LatestArtifact -Label "VIIPER" -Repository $ViiperRepo -BranchName $ViiperBranch -WorkflowName $ViiperWorkflow -NamePattern $ViiperArtifactNamePattern -DestinationZip $ViiperArtifactZip
            }
            elseif ([string]::IsNullOrWhiteSpace($ViiperArtifactZip)) {
                throw "-SkipViiperDownload requires either -ViiperArtifactZip or -ViiperExePath."
            }

            Write-Step "Extracting VIIPER artifact"
            Expand-ZipToDirectory -ZipPath $ViiperArtifactZip -Destination $viiperExtractRoot
            $ViiperExePath = Find-ExecutableInExtractedArtifact -Root $viiperExtractRoot -ExecutableName "viiper.exe"
            if ([string]::IsNullOrWhiteSpace($ViiperExePath)) {
                throw "VIIPER artifact did not contain viiper.exe. Extracted to $viiperExtractRoot"
            }
        }

        Write-Step "Installing VIIPER backend for this run"
        $installedViiperPath = Install-ViiperExecutable -SourceExe $ViiperExePath -InstallPath $ViiperInstallPath
    }

    Write-Step "Extracting DS4Windows artifact"
    Expand-Archive -LiteralPath $ArtifactZip -DestinationPath $extractRoot -Force
    $ds4Exe = Get-ChildItem -LiteralPath $extractRoot -Filter "DS4Windows.exe" -Recurse -File |
        Sort-Object FullName |
        Select-Object -First 1
    if (-not $ds4Exe) {
        throw "Artifact did not contain DS4Windows.exe."
    }

    Write-Host "DS4Windows artifact exe: $($ds4Exe.FullName)"

    if (-not $KeepProcesses) {
        Write-Step "Stopping stale DS4Windows processes"
        Stop-NamedProcesses @("DS4Windows")
    }

    if (-not $NoViiperStart) {
        Write-Step "Ensuring VIIPER server is running"
        [void](Start-InstalledViiper -InstallPath $ViiperInstallPath)
        Start-Sleep -Seconds 2
    }

    $inputLogPath = Join-Path $logRoot "windows-input-debugger.log"
    $inputArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $inputDebuggerScript,
        "-DurationSeconds", "$DurationSeconds",
        "-LogPath", $inputLogPath
    )
    if ($IncludeUnchangedHid) {
        $inputArgs += "-IncludeUnchangedHid"
    }

    Write-Step "Starting Windows raw-input debugger"
    $inputDebugger = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList (Join-Arguments $inputArgs) `
        -PassThru `
        -WindowStyle Minimized

    Start-Sleep -Seconds 2

    Write-Step "Launching DS4Windows artifact"
    $ds4Process = Start-Process -FilePath $ds4Exe.FullName `
        -WorkingDirectory (Split-Path -Parent $ds4Exe.FullName) `
        -PassThru

    $waitSeconds = $DurationSeconds + 25
    Write-Host "Waiting up to $waitSeconds seconds for DS4Windows diagnostic shutdown."
    if (-not $ds4Process.WaitForExit($waitSeconds * 1000)) {
        Write-Host "DS4Windows did not exit by itself; closing it for log collection." -ForegroundColor Yellow
        try {
            if ($ds4Process.MainWindowHandle -ne 0) {
                [void]$ds4Process.CloseMainWindow()
                [void]$ds4Process.WaitForExit(5000)
            }
        }
        catch { }

        if (-not $ds4Process.HasExited) {
            Stop-Process -Id $ds4Process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    try {
        [void]$inputDebugger.WaitForExit(15000)
    }
    catch { }

    Write-Step "Collecting logs"
    $ds4Log = Join-Path $env:APPDATA "DS4Windows\Logs\ds4windows_log.txt"
    $copiedDs4Log = Copy-IfPresent -Path $ds4Log -DestinationDirectory $logRoot
    $copiedInputLog = Copy-IfPresent -Path $inputLogPath -DestinationDirectory $logRoot

    $desktopInputLog = Get-RecentFile -Path ([Environment]::GetFolderPath("Desktop")) -Filter "ds4windows-windows-input-debugger-*.log"
    if ($desktopInputLog -and $desktopInputLog.FullName -ne $inputLogPath) {
        Copy-IfPresent -Path $desktopInputLog.FullName -DestinationDirectory $logRoot | Out-Null
    }

    $summary = Write-DiagnosticsSummary -SessionDirectory $sessionRoot -Ds4LogCopy $copiedDs4Log -InputLogCopy $copiedInputLog -Ds4ArtifactInfo $ds4ArtifactInfo -ViiperArtifactInfo $viiperArtifactInfo -InstalledViiperPath $installedViiperPath

    Write-Step "Summary"
    Get-Content -LiteralPath $summary | Select-Object -First 80 | ForEach-Object { Write-Host $_ }
    Write-Host ""
    Write-Host "Full session folder: $sessionRoot"
    Write-Host "Summary: $summary"
}
finally {
    if (-not $KeepProcesses) {
        try { Stop-NamedProcesses @("DS4Windows") } catch { }
    }
}
