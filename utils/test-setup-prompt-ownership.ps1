[CmdletBinding()]
param([string]$BackendScript)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $BackendScript) {
    $BackendScript = Join-Path $PSScriptRoot '../extras/install-viiper-backend.ps1'
}
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $BackendScript).Path, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer script has parse errors.' }
foreach ($name in @('Test-SetupUnattended', 'Remove-ForeignViiperInstallations',
        'Test-ManagedViiperPath', 'Stop-ViiperProcesses', 'Disable-ConflictingCitrixUsbMonitor')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
    }, $true)
    if ($definition) { Invoke-Expression $definition.Extent.Text }
}
$policy = Get-Item Function:/Test-SetupUnattended -ErrorAction SilentlyContinue
if ($policy) {
    $script:ActualPolicy = $policy.ScriptBlock
    function Test-SetupUnattended { & $script:ActualPolicy -HostArguments $script:HostArguments }
}
$transaction = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.TryStatementAst] -and $_.Finally
})[-1]
$finallyText = $transaction.Finally.Extent.Text
$script:ActualFinally = [ScriptBlock]::Create($finallyText.Substring(1, $finallyText.Length - 2))
$catchText = $transaction.CatchClauses[0].Body.Extent.Text
$script:ActualCatch = [ScriptBlock]::Create($catchText.Substring(1, $catchText.Length - 2))
$initialConfirmation = $transaction.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
        $_.Extent.Text.Contains('Install bundled VIIPER first and continue?')
} | Select-Object -First 1
if (-not $initialConfirmation) { throw 'Initial installation confirmation is missing.' }
$script:ActualInitialConfirmation = [ScriptBlock]::Create($initialConfirmation.Extent.Text)

# Only extracted functions and the final cleanup block run. All filesystem,
# process, startup, prompt and logging boundaries are inert mocks. The mutex
# probe uses a unique Local test name, never the production setup mutex.
Add-Type -TypeDefinition @'
using System;
using System.Threading;
public static class SetupPromptMutexProbe {
    public static bool CanAcquire(string name) {
        bool acquired = false;
        var thread = new Thread(() => {
            using (var mutex = new Mutex(false, name)) {
                acquired = mutex.WaitOne(0);
                if (acquired) mutex.ReleaseMutex();
            }
        });
        thread.Start();
        if (!thread.Join(5000)) throw new Exception("Isolated mutex probe did not finish.");
        return acquired;
    }
}
'@
function Get-ForeignViiperProcesses { return $script:Foreign }
function Write-SetupLog { param($Message, $Color) }
function Write-Host { param($Object, $ForegroundColor) }
function Disable-ViiperStartup {
    $script:Mutations++
    throw 'FIXTURE: approved interactive mutation boundary'
}
function Test-Path { param($LiteralPath, $PathType) return $false }
function Remove-Item { throw 'Unexpected filesystem mutation.' }
function Get-RunningViiperProcesses { return $script:Running }
function Stop-Process {
    param($Id, [switch]$Force, $ErrorAction)
    $script:Stopped.Add([int]$Id)
    $script:Running = @($script:Running | Where-Object ProcessId -ne $Id)
}
function Start-Sleep { param($Milliseconds) }
function Save-StartupSetupIntent { param($Reason) }
function Set-InfrastructureState { param($State) }
function Set-InfrastructureStartupFailClosed { param($ViiperPath, $Ds4WindowsPath) }
function Get-CitrixUsbMonitorState { return [pscustomobject]@{ State = 'Running'; Start = 2 } }
function Set-ItemProperty {
    param($LiteralPath, $Name, $Type, $Value)
    $script:Mutations++
    throw 'FIXTURE: approved Citrix mutation boundary'
}
function Read-Host {
    param($Prompt)
    $script:Prompts++
    if ($script:ProbeAtPrompt) {
        if (-not [SetupPromptMutexProbe]::CanAcquire($script:TestMutexName)) {
            throw 'Setup still owns its mutex while waiting at the final prompt.'
        }
        if ($script:ThrowAtPrompt) { throw 'FIXTURE: prompt failed' }
    }
    return $script:Answer
}
function Assert-True($Value, $Message) { if (-not $Value) { throw $Message } }
function Reset-Fixture {
    $script:Yes = $false
    $script:NoPause = $false
    $script:InstallerMode = $false
    $script:HostArguments = @('powershell.exe', '-File', 'fixture.ps1')
    $script:InstallDir = 'C:\Fixture\Managed\VIIPER'
    $script:Foreign = @([pscustomobject]@{ ProcessId = 17; ExecutablePath = 'C:\Fixture\Other\viiper.exe' })
    $script:Prompts = 0
    $script:Mutations = 0
    $script:Answer = 'N'
    $script:UserCanceled = $false
    $script:ProbeAtPrompt = $false
    $script:ThrowAtPrompt = $false
    $script:TempDir = 'C:\Fixture\NeverCreated'
    $script:SetupMutexOwned = $false
    $script:SetupMutex = $null
    $script:SetupTransactionStarted = $true
    $script:RebootBoundaryPending = $false
    $script:SafetyRestartPending = $false
    $script:Ds4WindowsRestartPath = 'C:\Fixture\Managed\DS4Windows.exe'
    $script:LogPath = 'C:\Fixture\NeverCreated\setup.log'
    $script:Running = @(
        [pscustomobject]@{ ProcessId = 16; ExecutablePath = 'C:\Fixture\Managed\VIIPER\viiper.exe' },
        $script:Foreign[0],
        [pscustomobject]@{ ProcessId = 18; ExecutablePath = $null })
    $script:Stopped = [Collections.Generic.List[int]]::new()
}
$failures = [Collections.Generic.List[string]]::new()
$caseCount = 0
function Test-Case($Name, [scriptblock]$Body) {
    Reset-Fixture
    try { & $Body; Write-Output "PASS $Name" }
    catch { $failures.Add("${Name}: $($_.Exception.Message)"); Write-Output "FAIL $Name" }
    $script:caseCount++
}
foreach ($mode in @('Yes', 'NoPause', 'InstallerMode', 'NonInteractive', 'NonI')) {
    Test-Case "initial confirmation is never implicit or blocking: $mode" {
        if ($mode -in @('NonInteractive', 'NonI')) {
            $script:HostArguments = @('powershell.exe', "-$mode")
        } else { Set-Variable -Name $mode -Value $true -Scope Script }
        $failure = $null
        try { & $script:ActualInitialConfirmation } catch { $failure = $_.Exception.Message }
        if ($mode -eq 'Yes') {
            Assert-True ($null -eq $failure) 'Explicit installation consent was rejected.'
        } else {
            Assert-True ($failure -like '*Installation requires explicit confirmation*') 'Missing unattended installation refusal.'
        }
        Assert-True ($script:Prompts -eq 0 -and $script:Mutations -eq 0) 'Unattended initial confirmation prompted/changed state.'
    }
    Test-Case "Citrix confirmation is never implicit or blocking: $mode" {
        if ($mode -in @('NonInteractive', 'NonI')) {
            $script:HostArguments = @('powershell.exe', "-$mode")
        } else { Set-Variable -Name $mode -Value $true -Scope Script }
        $failure = $null
        try { Disable-ConflictingCitrixUsbMonitor } catch { $failure = $_.Exception.Message }
        if ($mode -eq 'Yes') {
            Assert-True ($failure -eq 'FIXTURE: approved Citrix mutation boundary' -and $script:Mutations -eq 1) 'Explicit Citrix consent changed.'
        } else {
            Assert-True ($failure -like '*Citrix USB monitor requires explicit confirmation*' -and $script:Mutations -eq 0) 'Missing unattended Citrix refusal.'
        }
        Assert-True ($script:Prompts -eq 0) 'Unattended Citrix confirmation prompted.'
    }
    Test-Case "foreign VIIPER is preserved without a prompt: $mode" {
        if ($mode -in @('NonInteractive', 'NonI')) {
            $script:HostArguments = @('powershell.exe', "-$mode", '-File', 'fixture.ps1')
        } else { Set-Variable -Name $mode -Value $true -Scope Script }
        $failure = $null
        try { Remove-ForeignViiperInstallations } catch { $failure = $_.Exception.Message }
        Assert-True ($failure -like '*Close the listed VIIPER process*') 'Missing actionable unattended failure.'
        Assert-True ($script:Prompts -eq 0) 'Unattended setup attempted to prompt.'
        Assert-True ($script:Mutations -eq 0) 'Unattended setup changed startup/process state.'
        Assert-True (-not $script:UserCanceled) 'A blocked unattended repair is not user cancellation.'
    }
    Test-Case "no foreign VIIPER remains admissible: $mode" {
        if ($mode -in @('NonInteractive', 'NonI')) {
            $script:HostArguments = @('powershell.exe', "-$mode")
        } else { Set-Variable -Name $mode -Value $true -Scope Script }
        $script:Foreign = @()
        Remove-ForeignViiperInstallations
        Assert-True ($script:Prompts -eq 0 -and $script:Mutations -eq 0) 'Empty inventory performed work.'
    }
    Test-Case "final unattended cleanup never prompts: $mode" {
        if ($mode -in @('NonInteractive', 'NonI')) {
            $script:HostArguments = @('powershell.exe', "-$mode")
        } else { Set-Variable -Name $mode -Value $true -Scope Script }
        & $script:ActualFinally
        Assert-True ($script:Prompts -eq 0) 'Unattended final cleanup prompted.'
    }
}
Test-Case 'unknown foreign image is not killed to discover its path' {
    $script:Yes = $true
    $script:Foreign[0].ExecutablePath = $null
    try { Remove-ForeignViiperInstallations } catch { }
    Assert-True ($script:Prompts -eq 0 -and $script:Mutations -eq 0) 'Unknown process was prompted/changed.'
}
Test-Case 'manual decline preserves foreign processes' {
    $failure = $null
    try { Remove-ForeignViiperInstallations } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -like '*Setup canceled*' -and $script:UserCanceled) 'Manual decline was not preserved.'
    Assert-True ($script:Prompts -eq 1 -and $script:Mutations -eq 0) 'Manual decline changed processes.'
}
Test-Case 'manual approval reaches existing mutation boundary only after consent' {
    $script:Answer = 'Y'
    $failure = $null
    try { Remove-ForeignViiperInstallations } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -eq 'FIXTURE: approved interactive mutation boundary') 'Manual approval path changed.'
    Assert-True ($script:Prompts -eq 1 -and $script:Mutations -eq 1) 'Explicit consent was not required.'
}
foreach ($manualConsent in @('N', 'Y')) {
    Test-Case "manual initial confirmation remains explicit: $manualConsent" {
        $script:Answer = $manualConsent
        $failure = $null
        try { & $script:ActualInitialConfirmation } catch { $failure = $_.Exception.Message }
        Assert-True ($script:Prompts -eq 1) 'Manual initial confirmation disappeared.'
        if ($manualConsent -eq 'N') {
            Assert-True ($failure -like '*Setup canceled*' -and $script:UserCanceled) 'Manual initial decline changed.'
        } else { Assert-True ($null -eq $failure) 'Manual initial approval changed.' }
    }
    Test-Case "manual Citrix confirmation remains explicit: $manualConsent" {
        $script:Answer = $manualConsent
        $failure = $null
        try { Disable-ConflictingCitrixUsbMonitor } catch { $failure = $_.Exception.Message }
        Assert-True ($script:Prompts -eq 1) 'Manual Citrix confirmation disappeared.'
        if ($manualConsent -eq 'N') {
            Assert-True ($failure -like '*Setup canceled*' -and $script:UserCanceled -and $script:Mutations -eq 0) 'Manual Citrix decline changed.'
        } else {
            Assert-True ($failure -eq 'FIXTURE: approved Citrix mutation boundary' -and $script:Mutations -eq 1) 'Manual Citrix approval changed.'
        }
    }
}
Test-Case 'failure containment preserves foreign and unknown backends' {
    $script:Yes = $true
    try { throw 'Synthetic refused foreign backend' }
    catch { & $script:ActualCatch }
    Assert-True ($script:Stopped.Count -eq 1 -and $script:Stopped[0] -eq 16) 'Failure containment terminated a foreign or unknown backend.'
    Assert-True ($script:Running.Count -eq 2) 'Failure containment lost unowned processes.'
    Assert-True ($script:ExitCode -eq 1) 'Failed transaction became successful.'
    Assert-True ($script:Prompts -eq 0) 'Failure containment prompted.'
}
Test-Case 'normal explicitly authorized process cleanup remains unchanged' {
    Assert-True (Stop-ViiperProcesses 'fixture approved cleanup') 'Existing stop helper failed.'
    Assert-True ($script:Stopped.Count -eq 3 -and $script:Running.Count -eq 0) 'Default cleanup contract changed.'
}
foreach ($promptThrows in @($false, $true)) {
    Test-Case "final prompt cannot retain setup ownership (prompt throws=$promptThrows)" {
        $script:TestMutexName = 'Local\DS4Windows-Prompt-Test-' + [Guid]::NewGuid().ToString('N')
        $ownedMutex = [Threading.Mutex]::new($false, $script:TestMutexName)
        try {
            Assert-True ($ownedMutex.WaitOne(0)) 'Could not acquire isolated fixture mutex.'
            $script:SetupMutex = $ownedMutex
            $script:SetupMutexOwned = $true
            $script:ProbeAtPrompt = $true
            $script:ThrowAtPrompt = $promptThrows
            $failure = $null
            try { & $script:ActualFinally } catch { $failure = $_.Exception.Message }
            if ($promptThrows) {
                Assert-True ($failure -eq 'FIXTURE: prompt failed') 'Wrong final prompt failure.'
            } else { Assert-True ($null -eq $failure) $failure }
            Assert-True ($script:Prompts -eq 1) 'Manual final pause disappeared.'
            Assert-True (-not $script:SetupMutexOwned -and $null -eq $script:SetupMutex) 'Owned state survived cleanup.'
        } finally {
            try { $ownedMutex.ReleaseMutex() } catch { }
            $ownedMutex.Dispose()
        }
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output "$caseCount setup prompt/ownership cases passed without installer, process, registry or device mutations."
