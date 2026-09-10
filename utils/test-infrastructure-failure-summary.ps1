[CmdletBinding()]
param(
    [string]$BootstrapperSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BootstrapperSource)) {
    $BootstrapperSource = Join-Path $PSScriptRoot `
        '../installer/DS4Windows.Bootstrapper/InstallerApplication.cs'
}

# Compile the production parser alone. No installer, WPF application, Windows
# service, scheduled task, or real setup log is loaded or changed by this test.
$source = Get-Content -LiteralPath $BootstrapperSource -Raw
$signature = 'internal static string InfrastructureFailureSummary(string helperTail,'
$start = $source.IndexOf($signature, [StringComparison]::Ordinal)
if ($start -lt 0) {
    throw 'Could not locate the production infrastructure failure parser.'
}
$end = $source.IndexOf('        private static string ReadLogTail(', `
    $start, [StringComparison]::Ordinal)
if ($end -le $start) {
    throw 'Could not locate the production infrastructure failure parser.'
}
$method = $source.Substring($start, $end - $start).Replace( `
    'internal static string InfrastructureFailureSummary(', `
    'public static string InfrastructureFailureSummary(')
Add-Type -TypeDefinition ("using System; public static class InfrastructureSummaryTestHarness {`n" + `
    $method + "`n}")

$script:CaseCount = 0
$runId = '22df81db9aad4c9d97b78808babdfeaf'
$otherId = 'c26770c1fc4545e9a94f14bd6ae7e59a'
$generic = 'VIIPER/USB-IP setup failed. Open Log includes the detailed child-process diagnostics.'

function Invoke-Summary([string]$text, [string]$correlation = $runId) {
    return [InfrastructureSummaryTestHarness]::InfrastructureFailureSummary( `
        $text, $correlation)
}

function Assert-Summary([string]$actual, [string]$expected, [string]$message) {
    if ($actual -cne $expected) {
        throw "$message`nExpected: $expected`nActual: $actual"
    }
    $script:CaseCount++
}

function Invocation([string]$id, [string]$body, [int]$exitCode = 1) {
    return "2026-09-09T17:15:42.7399572-05:00 === DS4Windows setup invocation $id started ===`r`n" + `
        "$body`r`n" + `
        "2026-09-09T17:15:46.9663551-05:00 === DS4Windows setup invocation $id completed with exit code $exitCode ===`r`n"
}

function Conflict([string]$taskName) {
    return "Setup could not finish: Refusing to overwrite, disable, or remove foreign root task '$taskName'. Rename or remove that task manually, then rerun Install / Repair."
}

function Expected-Conflict([string]$taskName) {
    return "Setup could not verify the existing Windows startup task '$taskName' as belonging to DS4Windows. The task was preserved.`r`nOpen Log includes the diagnostic details."
}

$viiperConflict = Conflict 'RunVIIPER'
$ds4Conflict = Conflict 'RunDS4Windows'
$viiperExpected = Expected-Conflict 'RunVIIPER'
$ds4Expected = Expected-Conflict 'RunDS4Windows'
$failedRun = Invocation $runId $viiperConflict

Assert-Summary (Invoke-Summary $failedRun) $viiperExpected `
    'The completed marker must not hide the preceding child failure.'
Assert-Summary (Invoke-Summary (Invocation $runId $ds4Conflict)) $ds4Expected `
    'The other managed startup task must be identified accurately.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    "Setup could not finish: Startup task 'RunVIIPER' became a foreign same-name collision during registration.")) `
    $viiperExpected 'A task ownership race must report the preserved conflicting task.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    'Setup could not finish: Refusing to suspend an unverified startup task: RunDS4Windows')) `
    $ds4Expected 'Suspension must describe the same verified task ownership boundary.'
Assert-Summary (Invoke-Summary ($failedRun + (Invocation $runId $ds4Conflict))) `
    $ds4Expected 'A retry must use its own most recent invocation.'
Assert-Summary (Invoke-Summary ($failedRun + (Invocation $runId `
    'System.InvalidOperationException: failure before child startup'))) `
    $generic 'An early retry failure must not resurrect the previous task conflict.'
Assert-Summary (Invoke-Summary ($failedRun + (Invocation $runId `
    'Setup complete. VIIPER is ready for DS4Windows.' 0))) `
    $generic 'A successful retry must not reuse its earlier failure.'
Assert-Summary (Invoke-Summary ((Invocation $otherId $ds4Conflict) + $failedRun)) `
    $viiperExpected 'An outgoing bundle must not override the current bundle diagnostic.'
Assert-Summary (Invoke-Summary ((Invocation $runId 'No captured child failure.') + `
    (Invocation $otherId $ds4Conflict))) $generic `
    'An unrelated later invocation must not leak into the current invocation.'
Assert-Summary (Invoke-Summary (Invocation $otherId $viiperConflict)) $generic `
    'A log from a different setup transaction must not become the current diagnostic.'
Assert-Summary (Invoke-Summary $failedRun '') $generic `
    'A missing correlation ID must not select arbitrary log history.'
Assert-Summary (Invoke-Summary $failedRun 'invalid') $generic `
    'An invalid correlation ID must fail closed to the generic diagnostic.'
Assert-Summary (Invoke-Summary '') $generic `
    'A missing or unreadable helper log must retain a usable generic diagnostic.'
Assert-Summary (Invoke-Summary "$viiperConflict`r`n=== DS4Windows setup invocation $runId completed with exit code 1 ===") `
    $generic 'A truncated tail without the invocation start must not infer ownership.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    'Setup could not finish: private token=not-for-setup-page; C:\Users\private\secret.txt')) `
    $generic 'Unknown exception text and user paths must remain out of the setup page.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    ($viiperConflict + "`r`nSetup could not finish: a different final failure"))) `
    $generic 'Only the final failure within one invocation may explain its result.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    ($viiperConflict + ' Sensitive command arguments must stay in the log.'))) `
    $viiperExpected 'Known diagnostics must not copy raw remainder text into the setup page.'
Assert-Summary (Invoke-Summary (Invocation $runId (Conflict 'UnrelatedTask'))) `
    $generic 'Only the two managed task names may enter the task-conflict diagnostic.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    'Setup could not finish: Refusing to suspend an unverified startup task: RunVIIPERExtra')) `
    $generic 'A task name beginning with RunVIIPER is not the managed task.'
Assert-Summary (Invoke-Summary (Invocation $runId `
    'Setup could not finish: Refusing to suspend an unverified startup task: RunDS4Windows-copy')) `
    $generic 'A task name beginning with RunDS4Windows is not the managed task.'
Assert-Summary (Invoke-Summary $failedRun $runId.ToUpperInvariant()) `
    $viiperExpected 'Canonical GUID casing must not change correlation.'
Assert-Summary (Invoke-Summary $failedRun.Replace("`r`n", "`n")) `
    $viiperExpected 'Log line endings must not change the parsed result.'

Write-Output "Infrastructure failure summary regression checks passed ($script:CaseCount cases)."
