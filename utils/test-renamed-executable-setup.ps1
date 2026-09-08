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
foreach ($functionName in @('Get-Ds4WindowsProcessesForSetup',
        'Stop-Ds4WindowsProcesses', 'Stop-InstallerHostForStandardMigration')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true)
    if ($definition) { Invoke-Expression $definition.Extent.Text }
}

# Only the three extracted functions run. Every process/termination/log/wait
# boundary is replaced; no installer entry point or real process is invoked.
function Get-CimInstance {
    param($ClassName, $Filter, $ErrorAction)
    if ($ClassName -ne 'Win32_Process') { throw 'Unexpected CIM class.' }
    if ($Filter) {
        $requestedId = [int]($Filter -replace '^ProcessId=', '')
        return @($script:FakeProcesses | Where-Object ProcessId -eq $requestedId)
    }
    return $script:FakeProcesses
}
function Get-Process {
    param($Id, $Name, $ErrorAction)
    $matches = @($script:FakeProcesses | Where-Object {
        if ($null -ne $Id) { $_.ProcessId -eq $Id }
        else { [IO.Path]::GetFileNameWithoutExtension($_.Name) -eq $Name }
    })
    foreach ($entry in $matches) {
        $process = [pscustomobject]@{ Id = $entry.ProcessId }
        $process | Add-Member ScriptMethod CloseMainWindow { $true }
        $process
    }
}
function Stop-Process {
    param($Id, [switch]$Force, $ErrorAction)
    $script:Stopped.Add([int]$Id)
    $script:FakeProcesses = @($script:FakeProcesses | Where-Object ProcessId -ne $Id)
}
function Start-Sleep { param($Milliseconds) }
function Write-SetupLog { param($Message, $Color) }
function Test-RecognizedProductExecutable {
    param($Path, $ExpectedProduct)
    return $ExpectedProduct -eq 'DS4Windows' -and $Path -and
        $Path -ne 'C:\Unrelated\DS4Windows.exe'
}
function New-FakeProcess($Id, $Path, $Name) {
    if (-not $Name) { $Name = [IO.Path]::GetFileName($Path) }
    return [pscustomobject]@{ ProcessId = $Id; Name = $Name; ExecutablePath = $Path }
}
function Assert-True($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Reset-Fixture {
    $script:Stopped = [Collections.Generic.List[int]]::new()
    $script:FakeProcesses = @()
    $script:InstallerHostPid = 12
    $script:TargetDs4WindowsPath = 'C:\Portable\Controller Companion.exe'
    $script:Ds4WindowsRestartPath = $script:TargetDs4WindowsPath
    $script:Ds4WindowsInstallDir = 'C:\Program Files\DS4Windows'
    $script:KeepDs4WindowsPortable = $false
}
$failures = [Collections.Generic.List[string]]::new()
function Test-Case([string]$Name, [scriptblock]$Test) {
    Reset-Fixture
    try { & $Test; Write-Output "PASS $Name" }
    catch { $failures.Add("${Name}: $($_.Exception.Message)"); Write-Output "FAIL $Name" }
}

Test-Case 'renamed parent stopped; host and unrelated aliases preserved' {
    $script:FakeProcesses = @(
        (New-FakeProcess 11 $script:TargetDs4WindowsPath),
        (New-FakeProcess 12 $script:TargetDs4WindowsPath),
        (New-FakeProcess 13 'C:\Other\Controller Companion.exe'))
    Assert-True (Stop-Ds4WindowsProcesses 'fixture') 'Quiesce failed.'
    Assert-True ($script:Stopped.Count -eq 1 -and $script:Stopped[0] -eq 11) 'Wrong or missing process termination.'
}
Test-Case 'canonical parent still stopped' {
    $script:FakeProcesses = @((New-FakeProcess 11 'C:\Portable\DS4Windows.exe'))
    Assert-True (Stop-Ds4WindowsProcesses 'fixture') 'Canonical quiesce failed.'
    Assert-True ($script:Stopped.Count -eq 1 -and $script:Stopped[0] -eq 11) 'Canonical parent was not stopped.'
}
Test-Case 'unverified same-name process blocks cleanup' {
    $script:FakeProcesses = @((New-FakeProcess 11 'C:\Unrelated\DS4Windows.exe'))
    Assert-True (-not (Stop-Ds4WindowsProcesses 'fixture')) 'Unverified process accepted.'
    Assert-True ($script:Stopped.Count -eq 0) 'Unverified process terminated.'
}
Test-Case 'unreadable requested alias blocks cleanup without termination' {
    $script:FakeProcesses = @((New-FakeProcess 11 $null 'Controller Companion.exe'))
    Assert-True (-not (Stop-Ds4WindowsProcesses 'fixture')) 'Unreadable alias was treated as quiesced.'
    Assert-True ($script:Stopped.Count -eq 0) 'Unreadable alias terminated.'
}
Test-Case 'renamed exact host can close for standard migration' {
    $script:FakeProcesses = @((New-FakeProcess 12 $script:TargetDs4WindowsPath))
    Stop-InstallerHostForStandardMigration
    Assert-True ($script:Stopped.Count -eq 1 -and $script:Stopped[0] -eq 12) 'Exact host was not closed.'
}
Test-Case 'wrong-path host refused' {
    $script:FakeProcesses = @((New-FakeProcess 12 'C:\Other\Controller Companion.exe'))
    $rejected = $false
    try { Stop-InstallerHostForStandardMigration } catch { $rejected = $true }
    Assert-True $rejected 'Wrong-path host accepted.'
    Assert-True ($script:Stopped.Count -eq 0) 'Wrong-path host terminated.'
}
Test-Case 'unrecognized exact host refused' {
    $script:TargetDs4WindowsPath = 'C:\Unrelated\DS4Windows.exe'
    $script:FakeProcesses = @((New-FakeProcess 12 $script:TargetDs4WindowsPath))
    $rejected = $false
    try { Stop-InstallerHostForStandardMigration } catch { $rejected = $true }
    Assert-True $rejected 'Unrecognized product accepted.'
    Assert-True ($script:Stopped.Count -eq 0) 'Unrecognized product terminated.'
}
Test-Case 'portable host retained' {
    $script:KeepDs4WindowsPortable = $true
    $script:FakeProcesses = @((New-FakeProcess 12 $script:TargetDs4WindowsPath))
    Stop-InstallerHostForStandardMigration
    Assert-True ($script:Stopped.Count -eq 0) 'Portable host terminated.'
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output 'Renamed executable setup process tests passed without process or installer mutations.'
