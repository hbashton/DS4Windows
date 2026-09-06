param(
    [Parameter(Mandatory)][string]$LabRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedViiperSha256
)
$ErrorActionPreference = 'Stop'
$labFullPath = [IO.Path]::GetFullPath($LabRoot).TrimEnd('\')
$desktopPath = [Environment]::GetFolderPath('DesktopDirectory').TrimEnd('\')
if (!$labFullPath.StartsWith($desktopPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This test requires a dedicated Desktop lab directory.'
}
$labExe = Join-Path $labFullPath 'DS4Windows.exe'
$labViiper = Join-Path $labFullPath 'viiper.exe'
$labData = Join-Path $labFullPath 'lab-data'
if (!(Test-Path -LiteralPath $labExe) -or (Test-Path -LiteralPath $labData)) {
    throw 'Use a fresh complete publish with no lab-data directory for the refusal test.'
}
if ((Get-FileHash -LiteralPath $labViiper -Algorithm SHA256).Hash -ne $ExpectedViiperSha256) {
    throw 'The staged backend does not match the independently recorded digest.'
}
if (@(Get-Process -Name DS4Windows -ErrorAction SilentlyContinue).Count -ne 1) {
    throw 'This negative test requires exactly one already-running mapper; it will not stop it.'
}

function Get-RefusalSnapshot {
    $snapshot = [ordered]@{}
    foreach ($file in @(
        'C:\Program Files\DS4Windows\DS4Windows.exe',
        'C:\Program Files\DS4Windows\VIIPER\viiper.exe',
        'C:\Program Files\USBip\usbip.exe')) {
        $snapshot[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    foreach ($taskName in @('RunDS4Windows', 'RunVIIPER')) {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        $snapshot["task:$taskName"] = if ($task) {
            Export-ScheduledTask -TaskName $taskName
        } else { '<absent>' }
    }
    $startup = [Environment]::GetFolderPath('Startup')
    foreach ($file in @(Get-ChildItem -LiteralPath $startup -File | Sort-Object FullName)) {
        $snapshot[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $roaming = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'DS4Windows'
    foreach ($file in @(Get-ChildItem -LiteralPath $roaming -File -Recurse | Where-Object {
        $_.Extension -in @('.xml', '.json') -and $_.FullName -notlike '*\Logs\*'
    } | Sort-Object FullName)) {
        $snapshot[$file.FullName] = [ordered]@{
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            lastWriteUtc = $file.LastWriteTimeUtc.ToString('O')
        }
    }
    $snapshot['existingProcesses'] = @(Get-Process -Name DS4Windows,viiper -ErrorAction SilentlyContinue |
        Sort-Object Id | ForEach-Object { [ordered]@{ id=$_.Id; name=$_.ProcessName; started=$_.StartTime.ToUniversalTime().ToString('O') } })
    return ($snapshot | ConvertTo-Json -Depth 5)
}

$before = Get-RefusalSnapshot
$evidencePath = Join-Path $labFullPath 'refusal-evidence'
if (Test-Path -LiteralPath $evidencePath) { throw 'Refusing to overwrite prior smoke evidence.' }
New-Item -ItemType Directory -Path $evidencePath | Out-Null
$before | Set-Content -LiteralPath (Join-Path $evidencePath 'before.json') -Encoding utf8
$labProcess = Start-Process -FilePath $labExe -ArgumentList @('--portable-lab', $ExpectedViiperSha256, '-stop') -WorkingDirectory $labFullPath -WindowStyle Hidden -PassThru
Write-Output "Refusal test launched PID $($labProcess.Id). Observe and dismiss only its expected already-running-instance message."
# Each bounded wait allows the orchestrator to inspect/dismiss the actual dialog.
while (!$labProcess.WaitForExit(1000)) { }
$after = Get-RefusalSnapshot
$after | Set-Content -LiteralPath (Join-Path $evidencePath 'after.json') -Encoding utf8
$result = [ordered]@{
    exited = $true
    exitCode = $labProcess.ExitCode
    comparedStateUnchanged = $before -ceq $after
    labDataAbsent = !(Test-Path -LiteralPath $labData)
    hidHidePolicy = 'Not measured: registry query denied. No all-machine-state claim.'
    controllerInputAndFeedback = 'Not exercised by this startup refusal test.'
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath 'result.json') -Encoding utf8
$result | ConvertTo-Json -Compress | Write-Output
if ($result.exitCode -ne 1 -or !$result.comparedStateUnchanged -or !$result.labDataAbsent) {
    throw 'Portable lab startup refusal gate failed; inspect retained evidence.'
}
