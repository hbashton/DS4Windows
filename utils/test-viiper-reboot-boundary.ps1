[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendScript
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$backendPath = (Resolve-Path -LiteralPath $BackendScript).Path
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $backendPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Backend installer has PowerShell parse errors: " +
        (($parseErrors | ForEach-Object Message) -join "; ")
}

function Get-BackendFunctionDefinition([string]$name) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
    }, $true)
    if (-not $definition) {
        throw "Backend installer function is missing: $name"
    }
    return $definition.Extent.Text
}

# Import the literal needed by the extracted startup functions without running
# the installer's top-level code (which would touch real machine state).
$argumentAssignments = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'script:ViiperServerArguments'
}, $true))
if ($argumentAssignments.Count -ne 1 -or
        $argumentAssignments[0].Right -isnot [Management.Automation.Language.CommandExpressionAst] -or
        $argumentAssignments[0].Right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]) {
    throw 'Backend startup arguments must have one literal definition.'
}
$script:ViiperServerArguments = $argumentAssignments[0].Right.Expression.Value
if ($script:ViiperServerArguments -ne
        'server --usb.retained-import-authority-id=4923336367393615921') {
    throw 'Backend startup arguments lost the required retained-import authority.'
}

foreach ($functionName in @(
        "ConvertTo-VersionFromObject",
        "Set-UsbipReplacementBoundary",
        "Remove-MismatchedUsbipPackage",
        "Resolve-UsbipReplacementBoundary",
        "Suspend-StartupTasksUntilInfrastructureReady",
        "Set-InfrastructureStartupFailClosed")) {
    Invoke-Expression (Get-BackendFunctionDefinition $functionName)
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "DS4Windows-Usbip-Reboot-Test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $usbipRoot = Join-Path $testRoot "USBip"
    New-Item -ItemType Directory -Path $usbipRoot | Out-Null
    $script:CanonicalUsbipPath = Join-Path $usbipRoot "usbip.exe"
    $uninstaller = Join-Path $usbipRoot "unins000.exe"
    [IO.File]::WriteAllBytes($script:CanonicalUsbipPath, [byte[]](0))
    [IO.File]::WriteAllBytes($uninstaller, [byte[]](0))
    $script:UsbipReplacementStatePath = Join-Path $testRoot `
        "usbip-replacement-pending.json"
    $script:UsbipReplacementPhaseOne = $false
    $script:RebootRecommended = $false
    $script:UsbipRuntimeReady = $true
    $script:RebootBoundaryPending = $false
    $script:CurrentTestBoot = "boot-a"
    $script:PostRebootChecks = 0
    $script:FakeUninstallExitCode = 0

    function Get-WindowsBootSessionId {
        return $script:CurrentTestBoot
    }
    function Assert-UsbipPostRebootState {
        $script:PostRebootChecks++
    }
    function Write-SetupLog {
        param([string]$message, $color)
    }
    $script:FakeStartupTasks = @{
        RunVIIPER = $true
        RunDS4Windows = $true
    }
    function Test-HighestLogonTask {
        param(
            [string]$taskName,
            [string]$executablePath,
            [string]$arguments,
            [string]$workingDirectory,
            [bool]$requireEnabled = $true
        )
        return $script:FakeStartupTasks.ContainsKey($taskName) -and
            (-not $requireEnabled -or
            $script:FakeStartupTasks[$taskName])
    }
    function Test-ManagedStartupTaskMarker {
        param($registered)
        return $null -ne $registered
    }
    function Test-ManagedStartupTaskOwnership {
        param($registered, [string]$taskName)
        return $null -ne $registered
    }
    function Test-HighestLogonTaskDefinition {
        return $true
    }
    function Get-RootScheduledTask {
        param([string]$taskName)
        if (-not $script:FakeStartupTasks.ContainsKey($taskName)) {
            return $null
        }
        return [pscustomobject]@{
            TaskPath = "\"
            TaskName = $taskName
            Description = "DS4Windows managed startup task v1"
            Settings = [pscustomobject]@{
                Enabled = $script:FakeStartupTasks[$taskName]
            }
        }
    }
    function Disable-ScheduledTask {
        param(
            [string]$TaskPath,
            [string]$TaskName,
            $ErrorAction
        )
        $script:FakeStartupTasks[$TaskName] = $false
    }
    function Get-ScheduledTask {
        param(
            [string]$TaskPath,
            [string]$TaskName,
            $ErrorAction
        )
        return [pscustomobject]@{
            Settings = [pscustomobject]@{
                Enabled = $script:FakeStartupTasks[$TaskName]
            }
        }
    }
    function Start-Process {
        param(
            [string]$FilePath,
            [object]$ArgumentList,
            [switch]$PassThru
        )
        if (-not [string]::Equals((Resolve-Path -LiteralPath $FilePath).Path,
                (Resolve-Path -LiteralPath $uninstaller).Path,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Replacement test attempted an unexpected executable."
        }
        $process = [pscustomobject]@{
            ExitCode = $script:FakeUninstallExitCode
        }
        $process | Add-Member ScriptMethod WaitForExit {
            param([int]$milliseconds)
            return $true
        }
        $process | Add-Member ScriptMethod Refresh { }
        return $process
    }

    $entry = [pscustomobject]@{
        DisplayVersion = "0.9.7.8"
        QuietUninstallString = '"' + $uninstaller + '"'
        UninstallString = $null
    }
    $removed = Remove-MismatchedUsbipPackage $entry `
        ([Version]"0.9.7.8") ([Version]"0.9.7.7")
    if (-not $removed -or -not $script:UsbipReplacementPhaseOne -or
            -not $script:RebootRecommended -or
            $script:UsbipRuntimeReady) {
        throw "0.9.7.8 phase-one replacement contract did not converge."
    }

    $state = Get-Content -LiteralPath $script:UsbipReplacementStatePath `
        -Raw | ConvertFrom-Json
    if ($state.BootSessionId -ne "boot-a" -or
            $state.RemovedVersion -ne "0.9.7.8" -or
            $state.RequiredVersion -ne "0.9.7.7") {
        throw "USB-IP replacement marker did not preserve exact versions."
    }

    $sameBootRejected = $false
    try {
        Resolve-UsbipReplacementBoundary
    }
    catch {
        $sameBootRejected = $_.Exception.Message -match
            "Restart Windows before"
    }
    if (-not $sameBootRejected -or
            -not $script:RebootBoundaryPending -or
            -not (Test-Path -LiteralPath $script:UsbipReplacementStatePath)) {
        throw "USB-IP replacement crossed its same-boot safety boundary."
    }

    $script:CurrentTestBoot = "boot-b"
    $script:RebootBoundaryPending = $false
    Resolve-UsbipReplacementBoundary
    if ($script:PostRebootChecks -ne 1 -or
            (Test-Path -LiteralPath $script:UsbipReplacementStatePath)) {
        throw "USB-IP replacement did not validate and clear after reboot."
    }

    Suspend-StartupTasksUntilInfrastructureReady `
        (Join-Path $testRoot "viiper.exe") `
        (Join-Path $testRoot "DS4Windows.exe")
    if ($script:FakeStartupTasks.RunVIIPER -or
            $script:FakeStartupTasks.RunDS4Windows) {
        throw "Startup tasks could race phase-two USB-IP setup after reboot."
    }

    $script:FakeStartupTasks.RunVIIPER = $true
    $script:FakeStartupTasks.RunDS4Windows = $true
    Set-InfrastructureStartupFailClosed `
        (Join-Path $testRoot "viiper.exe") `
        (Join-Path $testRoot "DS4Windows.exe")
    if ($script:FakeStartupTasks.RunVIIPER -or
            $script:FakeStartupTasks.RunDS4Windows) {
        throw "Failed setup could leave an owned startup task enabled."
    }

    $backendText = Get-Content -LiteralPath $backendPath -Raw
    if ([regex]::Matches($backendText,
            'Suspend-StartupTasksUntilInfrastructureReady').Count -lt 3) {
        throw "Both reboot-pending branches must suspend startup tasks."
    }

    # A failed uninstaller must never leave a marker that authorizes phase two.
    $script:CurrentTestBoot = "boot-c"
    $script:FakeUninstallExitCode = 5
    $script:UsbipReplacementPhaseOne = $false
    $failedUninstallRejected = $false
    try {
        [void](Remove-MismatchedUsbipPackage $entry `
            ([Version]"0.9.7.8") ([Version]"0.9.7.7"))
    }
    catch {
        $failedUninstallRejected = $_.Exception.Message -match "exit code 5"
    }
    if (-not $failedUninstallRejected -or
            (Test-Path -LiteralPath $script:UsbipReplacementStatePath)) {
        throw "A failed USB-IP uninstall could authorize a later phase."
    }

    Write-Host "USB-IP 0.9.7.8 -> reboot -> 0.9.7.7 state simulation passed."
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force `
        -ErrorAction SilentlyContinue
}
