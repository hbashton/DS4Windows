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
$legacyPin = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'script:LegacyPortableViiperSha256'
}, $true))
if ($legacyPin.Count -ne 1 -or
        $legacyPin[0].Right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]) {
    throw 'Portable task recovery must use one fixed historical package hash.'
}
$script:LegacyPortableViiperSha256 = $legacyPin[0].Right.Expression.Value
if ($script:LegacyPortableViiperSha256 -ne
        'F1ECEF158F02D0BDCD1296C8D5097A281169081D0D59C1A8971592FAC78155EF') {
    throw 'The historical RC4.5 portable recovery identity changed.'
}

foreach ($functionName in @(
        "Assert-ManagedStartupTaskName",
        "Get-RootScheduledTask",
        "Convert-AccountToSid",
        "Test-HighestLogonTaskDefinition",
        "Test-HighestLogonTask",
        "Test-ManagedStartupTaskMarker",
        "Test-KnownPackagedViiperExecutable",
        "Test-LegacyManagedStartupTask",
        "Test-ManagedStartupTaskOwnership",
        "Save-ManagedStartupTaskBackup",
        "Assert-StartupTaskMutationAllowed",
        "Remove-ManagedStartupTask",
        "Remove-ManagedStartupTaskPair",
        "Add-ScheduledTaskXmlElement",
        "New-HighestLogonTaskXml",
        "Register-HighestLogonTask",
        "Register-ViiperRunTask",
        "Register-Ds4WindowsRunTask",
        "Register-ManagedStartupTaskPair",
        "Set-InfrastructureStartupFailClosed")) {
    Invoke-Expression (Get-BackendFunctionDefinition $functionName)
}

function Assert-Equal($actual, $expected, [string]$message) {
    if (-not [object]::Equals($actual, $expected)) {
        throw "$message Expected '$expected', observed '$actual'."
    }
}

function Select-TaskXmlNode([Xml.XmlDocument]$document, [string]$xpath) {
    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace("t",
        "http://schemas.microsoft.com/windows/2004/02/mit/task")
    return $document.SelectSingleNode($xpath, $namespaceManager)
}

function Select-TaskXmlNodes([Xml.XmlDocument]$document, [string]$xpath) {
    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace("t",
        "http://schemas.microsoft.com/windows/2004/02/mit/task")
    return $document.SelectNodes($xpath, $namespaceManager)
}

$script:TargetUserSid =
    [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$viiperPath = "C:\Program Files\DS4Windows & Test\VIIPER\viiper.exe"
$arguments = "server --label '<&>'"
$workingDirectory = "C:\Program Files\DS4Windows & Test\VIIPER"

$taskXmlText = New-HighestLogonTaskXml $viiperPath $arguments `
    $workingDirectory
if ($taskXmlText -isnot [string]) {
    throw "Scheduled-task XML generation returned more than one object."
}
$taskXml = [Xml.XmlDocument]::new()
$taskXml.PreserveWhitespace = $true
$taskXml.LoadXml($taskXmlText)

Assert-Equal $taskXml.DocumentElement.GetAttribute("version") "1.2" `
    "The task schema version changed."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:RegistrationInfo/t:Description").InnerText `
    "DS4Windows managed startup task v1" `
    "The durable startup-task ownership marker changed."
$logonTriggers = @(Select-TaskXmlNodes $taskXml `
    "/t:Task/t:Triggers/t:LogonTrigger")
Assert-Equal $logonTriggers.Count 1 "Exactly one logon trigger is required."
Assert-Equal (@(Select-TaskXmlNodes $taskXml `
    "/t:Task/t:Triggers/t:LogonTrigger/t:UserId")).Count 0 `
    "The logon trigger must remain user-neutral."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Principals/t:Principal/t:UserId").InnerText `
    $script:TargetUserSid "The task XML did not preserve the exact SID."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Principals/t:Principal/t:LogonType").InnerText `
    "InteractiveToken" "The task is not limited to an interactive token."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Principals/t:Principal/t:RunLevel").InnerText `
    "HighestAvailable" "The task did not request highest privileges."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:MultipleInstancesPolicy").InnerText `
    "IgnoreNew" "The task multiple-instance policy changed."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:DisallowStartIfOnBatteries").InnerText `
    "false" "The task must be allowed to start on battery."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:StopIfGoingOnBatteries").InnerText `
    "false" "The task must remain running on battery."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:Enabled").InnerText "true" `
    "The registered task must be enabled by its XML definition."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:ExecutionTimeLimit").InnerText "PT0S" `
    "The startup task must not have an execution timeout."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:Priority").InnerText "1" `
    "VIIPER startup must match the runtime High priority contract."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Actions/t:Exec/t:Command").InnerText $viiperPath `
    "The escaped executable path did not round-trip."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Actions/t:Exec/t:Arguments").InnerText $arguments `
    "The escaped arguments did not round-trip."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Actions/t:Exec/t:WorkingDirectory").InnerText `
    $workingDirectory "The escaped working directory did not round-trip."
if ($taskXmlText -notmatch '&amp;' -or $taskXmlText -notmatch '&lt;') {
    throw "Task XML values were not escaped by XmlDocument."
}

# Ask Task Scheduler to parse the definition in memory. NewTask and XmlText do
# not register, update, enable, start, or delete any system task.
$scheduleService = $null
$taskDefinition = $null
try {
    $scheduleService = New-Object -ComObject "Schedule.Service"
    $scheduleService.Connect()
    $taskDefinition = $scheduleService.NewTask(0)
    $taskDefinition.XmlText = $taskXmlText
}
finally {
    if ($taskDefinition) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $taskDefinition)
    }
    if ($scheduleService) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $scheduleService)
    }
}

$script:FakeTasks = @()
$script:RegisterCalls = 0
$script:RegisterNames = @()
$script:RegisterForceNames = @()
$script:RegisterFailureNames = @()
$script:EnumerateCalls = 0
$script:EnumerationFailure = $false
$script:UnregisterCalls = 0
$script:DisableCalls = 0
$script:SleepCalls = 0
$script:SetupLogs = @()
$script:CapturedRegistrationXml = $null
$script:RecognizedProductPaths = @()
$script:ManagedViiperPath = $viiperPath
$script:FakeExecutableHashes = @{}
$script:FakeReparsePaths = @()
$script:TaskBackups = @{}
$script:BackupFailure = $false
$script:TaskEvents = @()

function Reset-FakeTaskState {
    $script:FakeTasks = @()
    $script:RegisterCalls = 0
    $script:RegisterNames = @()
    $script:RegisterForceNames = @()
    $script:RegisterFailureNames = @()
    $script:EnumerateCalls = 0
    $script:EnumerationFailure = $false
    $script:UnregisterCalls = 0
    $script:DisableCalls = 0
    $script:SleepCalls = 0
    $script:SetupLogs = @()
    $script:CapturedRegistrationXml = $null
    $script:RecognizedProductPaths = @()
    $script:ManagedViiperPath = $viiperPath
    $script:FakeExecutableHashes = @{}
    $script:FakeReparsePaths = @()
    $script:TaskBackups = @{}
    $script:BackupFailure = $false
    $script:TaskEvents = @()
}

function New-FakeScheduledTask([string]$taskPath, [string]$taskName,
        [string]$definitionXml) {
    $document = [Xml.XmlDocument]::new()
    $document.LoadXml($definitionXml)
    $principalUser = (Select-TaskXmlNode $document `
        "/t:Task/t:Principals/t:Principal/t:UserId").InnerText
    $description = (Select-TaskXmlNode $document `
        "/t:Task/t:RegistrationInfo/t:Description").InnerText
    $command = (Select-TaskXmlNode $document `
        "/t:Task/t:Actions/t:Exec/t:Command").InnerText
    $argumentNode = Select-TaskXmlNode $document `
        "/t:Task/t:Actions/t:Exec/t:Arguments"
    $workingDirectoryNode = Select-TaskXmlNode $document `
        "/t:Task/t:Actions/t:Exec/t:WorkingDirectory"

    return [pscustomobject]@{
        TaskPath = $taskPath
        TaskName = $taskName
        OriginalXml = $definitionXml
        Description = $description
        Actions = @([pscustomobject]@{
            Execute = $command
            Arguments = if ($argumentNode) { $argumentNode.InnerText } else { "" }
            WorkingDirectory = if ($workingDirectoryNode) {
                $workingDirectoryNode.InnerText
            } else { "" }
        })
        Triggers = @([pscustomobject]@{
            CimClass = [pscustomobject]@{
                CimClassName = "MSFT_TaskLogonTrigger"
            }
            UserId = $null
        })
        Principal = [pscustomobject]@{
            UserId = $principalUser
            RunLevel = "Highest"
            LogonType = "Interactive"
        }
        Settings = [pscustomobject]@{
            Enabled = $true
            Priority = if (Select-TaskXmlNode $document "/t:Task/t:Settings/t:Priority") {
                [int](Select-TaskXmlNode $document "/t:Task/t:Settings/t:Priority").InnerText
            } else { 7 }
        }
    }
}

function New-ForeignScheduledTask([string]$taskName) {
    $isViiper = [string]::Equals($taskName, "RunVIIPER",
        [StringComparison]::Ordinal)
    $xml = New-HighestLogonTaskXml `
        $(if ($isViiper) { "C:\Foreign\evil.exe" } else {
            "C:\Foreign\other.exe"
        }) $(if ($isViiper) { "not-server" } else { "not-minimized" }) `
        "C:\Foreign"
    $task = New-FakeScheduledTask "\" $taskName $xml
    $task.Description = "Unrelated owner"
    return $task
}

function Register-ScheduledTask {
    [CmdletBinding()]
    param(
        [string]$TaskPath,
        [string]$TaskName,
        [string]$Xml,
        [switch]$Force
    )
    $script:RegisterCalls++
    $script:TaskEvents += "register:$TaskName"
    $script:RegisterNames += $TaskName
    if ($Force) { $script:RegisterForceNames += $TaskName }
    $script:CapturedRegistrationXml = $Xml
    if ($script:RegisterFailureNames -contains $TaskName) {
        throw "simulated registration failure for $TaskName"
    }
    $existing = @($script:FakeTasks | Where-Object {
        $_.TaskPath -eq $TaskPath -and $_.TaskName -eq $TaskName
    })
    if ($existing.Count -gt 0 -and -not $Force) {
        throw "simulated same-name collision"
    }
    if ($Force) {
        $script:FakeTasks = @($script:FakeTasks | Where-Object {
            -not ($_.TaskPath -eq $TaskPath -and $_.TaskName -eq $TaskName)
        })
    }
    $task = New-FakeScheduledTask $TaskPath $TaskName $Xml
    $script:FakeTasks = @($script:FakeTasks) + $task
    return $task
}

function Get-ScheduledTask {
    [CmdletBinding()]
    param()
    $script:EnumerateCalls++
    if ($script:EnumerationFailure) {
        throw "simulated Task Scheduler enumeration failure"
    }
    return $script:FakeTasks
}

function Unregister-ScheduledTask {
    [CmdletBinding()]
    param(
        [string]$TaskPath,
        [string]$TaskName,
        [switch]$Confirm
    )
    $script:UnregisterCalls++
    $script:TaskEvents += "remove:$TaskName"
    $script:FakeTasks = @($script:FakeTasks | Where-Object {
        -not ([string]::Equals([string]$_.TaskPath, $TaskPath,
                    [StringComparison]::Ordinal) -and
            [string]::Equals([string]$_.TaskName, $TaskName,
                [StringComparison]::OrdinalIgnoreCase))
    })
}

function Enable-ScheduledTask {
    throw "Registration must not separately enable an XML-enabled task."
}

function New-ScheduledTaskPrincipal {
    throw "Registration must not normalize the exact SID through a CIM principal."
}

function New-ScheduledTaskTrigger {
    throw "Registration must use the schema-validated XML trigger."
}

function Test-RecognizedProductExecutable {
    param([string]$path, [string]$expectedProduct)
    return @($script:RecognizedProductPaths | Where-Object {
        [string]::Equals([string]$_, $path,
            [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
}

function Test-ManagedViiperPath {
    param([string]$path)
    return [string]::Equals($script:ManagedViiperPath, $path,
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force)
    return [pscustomobject]@{
        PSIsContainer = -not $script:FakeExecutableHashes.ContainsKey($LiteralPath)
        Attributes = if ($script:FakeReparsePaths -contains $LiteralPath) {
            [IO.FileAttributes]::ReparsePoint
        } else { [IO.FileAttributes]::Normal }
    }
}

function Test-FileSha256([string]$path, [string]$expectedHash) {
    Assert-Equal $expectedHash $script:LegacyPortableViiperSha256 `
        "Portable ownership did not request the historical package pin."
    return $script:FakeExecutableHashes.ContainsKey($path) -and
        [string]::Equals($script:FakeExecutableHashes[$path], $expectedHash,
            [StringComparison]::OrdinalIgnoreCase)
}

function Export-ScheduledTask {
    [CmdletBinding()]
    param($InputObject)
    $script:TaskEvents += "export:$($InputObject.TaskName)"
    return $InputObject.OriginalXml.Replace(
        "DS4Windows managed startup task v1", [string]$InputObject.Description)
}

function Write-StartupTaskBackup([string]$taskName, [string]$taskXml) {
    if ($script:BackupFailure) { throw "simulated backup failure" }
    $script:TaskEvents += "backup:$taskName"
    $script:TaskBackups["$taskName|$taskXml"] = $taskXml
}

function Start-Sleep {
    $script:SleepCalls++
}

function Disable-ScheduledTask {
    [CmdletBinding()]
    param(
        [string]$TaskPath,
        [string]$TaskName
    )
    $script:DisableCalls++
    $script:TaskEvents += "disable:$TaskName"
    $task = @($script:FakeTasks | Where-Object {
        $_.TaskPath -eq $TaskPath -and $_.TaskName -eq $TaskName
    }) | Select-Object -First 1
    if (-not $task) { throw "simulated task disappeared before disable" }
    $task.Settings.Enabled = $false
}

function Write-SetupLog([string]$message, $color) {
    $script:SetupLogs += $message
}

$ds4Path = "C:\Program Files\DS4Windows & Test\DS4Windows.exe"
$oldPortableDs4Path = "C:\Old Portable Copy\DS4Windows.exe"
$oldPortableDs4Directory = Split-Path -Parent $oldPortableDs4Path

function New-LegacyDs4ScheduledTask {
    $task = New-FakeScheduledTask "\" "RunDS4Windows" `
        (New-HighestLogonTaskXml $oldPortableDs4Path "-m" `
            $oldPortableDs4Directory)
    $task.Description = ""
    return $task
}

# Fresh registration uses XML without -Force, then verifies the durable owner
# marker and exact contract by enumeration.
Reset-FakeTaskState
$registered = Register-HighestLogonTask "RunVIIPER" $viiperPath `
    $arguments $workingDirectory
if (-not $registered) {
    throw "Exact-SID XML registration did not pass verification: " +
        ($script:SetupLogs -join " | ")
}
Assert-Equal $script:RegisterCalls 1 "Registration did not converge once."
Assert-Equal $script:RegisterForceNames.Count 0 `
    "Fresh registration unexpectedly used -Force."
Assert-Equal $script:UnregisterCalls 0 "Successful registration was rolled back."
Assert-Equal $script:SleepCalls 0 "Successful registration unexpectedly retried."
if (-not $script:CapturedRegistrationXml -or $script:EnumerateCalls -lt 1) {
    throw "Registration did not submit and enumerate the exact XML task."
}

$invalidNameRejected = $false
try {
    [void](Register-HighestLogonTask "RunUnexpected" $viiperPath `
        $arguments $workingDirectory)
}
catch {
    $invalidNameRejected = $_.Exception.Message -match "unmanaged"
}
if (-not $invalidNameRejected -or $script:RegisterCalls -ne 1) {
    throw "Startup-task registration accepted an unmanaged root task name."
}

# The first marker-aware repair may retarget a fully verified task created by
# an older portable copy. It upgrades that exact semantic legacy contract to
# the current path and durable marker.
Reset-FakeTaskState
$script:RecognizedProductPaths = @($oldPortableDs4Path)
$script:FakeTasks = @(New-LegacyDs4ScheduledTask)
$legacyRetargeted = Register-HighestLogonTask "RunDS4Windows" $ds4Path `
    "-m" (Split-Path -Parent $ds4Path)
if (-not $legacyRetargeted) {
    throw "Verified legacy portable RunDS4Windows task was not retargeted."
}
Assert-Equal $script:RegisterCalls 1 `
    "Legacy portable retarget did not register exactly once."
Assert-Equal $script:RegisterForceNames.Count 1 `
    "Verified legacy portable retarget did not use owned replacement."
Assert-Equal $script:UnregisterCalls 0 `
    "Legacy portable retarget used delete/recreate cleanup."
Assert-Equal $script:FakeTasks.Count 1 `
    "Legacy portable retarget left an ambiguous task set."
Assert-Equal $script:FakeTasks[0].Actions[0].Execute $ds4Path `
    "Legacy portable task did not move to the requested executable."
Assert-Equal $script:FakeTasks[0].Description `
    "DS4Windows managed startup task v1" `
    "Legacy portable task did not receive the ownership marker."

# Every semantic legacy field is conjunctive. Near-miss tasks remain foreign
# and cause zero registration, disable, or removal mutations.
$legacyNearMisses = @(
    [pscustomobject]@{
        Name = "nonblank foreign description"
        Recognized = $true
        Mutate = { param($task) $task.Description = "Another product" }
    },
    [pscustomobject]@{
        Name = "wrong arguments"
        Recognized = $true
        Mutate = { param($task) $task.Actions[0].Arguments = "--other" }
    },
    [pscustomobject]@{
        Name = "wrong working directory"
        Recognized = $true
        Mutate = { param($task) $task.Actions[0].WorkingDirectory = "C:\Other" }
    },
    [pscustomobject]@{
        Name = "wrong principal SID"
        Recognized = $true
        Mutate = { param($task) $task.Principal.UserId = "S-1-5-18" }
    },
    [pscustomobject]@{
        Name = "unrecognized product"
        Recognized = $false
        Mutate = { param($task) }
    },
    [pscustomobject]@{
        Name = "wrong trigger type"
        Recognized = $true
        Mutate = {
            param($task)
            $task.Triggers[0].CimClass.CimClassName = "MSFT_TaskTimeTrigger"
        }
    }
)
foreach ($nearMiss in $legacyNearMisses) {
    Reset-FakeTaskState
    if ($nearMiss.Recognized) {
        $script:RecognizedProductPaths = @($oldPortableDs4Path)
    }
    $candidate = New-LegacyDs4ScheduledTask
    & $nearMiss.Mutate $candidate
    $script:FakeTasks = @($candidate)
    $rejected = $false
    try {
        [void](Register-HighestLogonTask "RunDS4Windows" $ds4Path `
            "-m" (Split-Path -Parent $ds4Path))
    }
    catch { $rejected = $_.Exception.Message -match "foreign root task" }
    if (-not $rejected) {
        throw "Legacy near-miss was accepted: $($nearMiss.Name)."
    }
    Assert-Equal $script:RegisterCalls 0 `
        "Legacy near-miss reached registration: $($nearMiss.Name)."
    Assert-Equal $script:DisableCalls 0 `
        "Legacy near-miss was disabled: $($nearMiss.Name)."
    Assert-Equal $script:UnregisterCalls 0 `
        "Legacy near-miss was removed: $($nearMiss.Name)."
}

# Failure containment uses the same classifier as mutation preflight. If an
# owned moved-portable upgrade fails before -Force replaces the old task, that
# accepted legacy task is disabled; a near-miss remains untouched.
Reset-FakeTaskState
$script:RecognizedProductPaths = @($oldPortableDs4Path)
$movedLegacyForContainment = New-LegacyDs4ScheduledTask
$script:FakeTasks = @($movedLegacyForContainment)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 1 `
    "Containment did not disable an accepted moved-portable legacy task."
if ($movedLegacyForContainment.Settings.Enabled) {
    throw "Accepted moved-portable legacy task remained enabled."
}

Reset-FakeTaskState
$script:RecognizedProductPaths = @($oldPortableDs4Path)
$legacyNearMissForContainment = New-LegacyDs4ScheduledTask
$legacyNearMissForContainment.Actions[0].Arguments = "--other"
$script:FakeTasks = @($legacyNearMissForContainment)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 0 `
    "Containment disabled a legacy near-miss foreign task."
if (-not $legacyNearMissForContainment.Settings.Enabled) {
    throw "Containment mutated a legacy near-miss foreign task."
}

# Legacy VIIPER ownership is narrower still: both the requested and observed
# executable must be the canonical managed path and recognized product.
foreach ($legacyArguments in @("server", $script:ViiperServerArguments)) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($viiperPath)
    $legacyViiper = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $viiperPath $legacyArguments $workingDirectory)
    $legacyViiper.Description = ""
    $script:FakeTasks = @($legacyViiper)
    if (-not (Register-HighestLogonTask "RunVIIPER" $viiperPath `
            $script:ViiperServerArguments $workingDirectory)) {
        throw "Canonical recognized legacy VIIPER task was not migrated."
    }
    Assert-Equal $script:RegisterForceNames.Count 1 `
        "Canonical legacy VIIPER migration did not use owned replacement."
    Assert-Equal $script:FakeTasks[0].Actions[0].Arguments `
        $script:ViiperServerArguments `
        "Migrated VIIPER task did not use the current authority contract."
}

# Recover the exact shipped portable writer bug, preserving its original XML
# before replacing it with the protected executable and ownership marker.
Reset-FakeTaskState
$packagedPortablePath = "C:\Known RC4.5 Portable\viiper.exe"
$packagedPortableDirectory = Split-Path -Parent $packagedPortablePath
$script:RecognizedProductPaths = @($packagedPortablePath)
$script:FakeExecutableHashes[$packagedPortablePath] = $script:LegacyPortableViiperSha256
$packagedPortableTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $packagedPortablePath `
        $script:ViiperServerArguments $packagedPortableDirectory)
$packagedPortableTask.Description = ""
$script:FakeTasks = @($packagedPortableTask)
if (-not (Register-HighestLogonTask "RunVIIPER" $viiperPath `
        $script:ViiperServerArguments $workingDirectory)) {
    throw "Pinned packaged portable VIIPER task did not recover automatically."
}
Assert-Equal $script:FakeTasks[0].Actions[0].Execute $viiperPath `
    "Recovered portable VIIPER startup did not use the managed executable."
Assert-Equal $script:FakeTasks[0].Description `
    "DS4Windows managed startup task v1" `
    "Recovered portable VIIPER startup lost its ownership marker."
Assert-Equal $script:TaskBackups.Count 1 `
    "Portable migration did not preserve exactly one original definition."
if ($script:TaskEvents.IndexOf("backup:RunVIIPER") -ge
        $script:TaskEvents.IndexOf("register:RunVIIPER")) {
    throw "Portable migration changed startup before preserving the definition."
}
Assert-Equal $script:FakeTasks[0].Settings.Priority 1 `
    "Portable recovery did not produce the runtime High priority."

$portableNearMisses = @(
    @{ Name = "wrong hash"; Mutate = {
        param($task)
        $script:FakeExecutableHashes[$packagedPortablePath] = ('0' * 64)
    } },
    @{ Name = "file reparse point"; Mutate = {
        param($task) $script:FakeReparsePaths = @($packagedPortablePath)
    } },
    @{ Name = "ancestor reparse point"; Mutate = {
        param($task) $script:FakeReparsePaths = @($packagedPortableDirectory)
    } },
    @{ Name = "wrong arguments"; Mutate = {
        param($task) $task.Actions[0].Arguments += " --other"
    } },
    @{ Name = "wrong SID"; Mutate = {
        param($task) $task.Principal.UserId = "S-1-5-18"
    } },
    @{ Name = "foreign description"; Mutate = {
        param($task) $task.Description = "Another product"
    } },
    @{ Name = "wrong working directory"; Mutate = {
        param($task) $task.Actions[0].WorkingDirectory = "C:\Other"
    } },
    @{ Name = "extra action"; Mutate = {
        param($task) $task.Actions += $task.Actions[0]
    } },
    @{ Name = "wrong trigger"; Mutate = {
        param($task) $task.Triggers[0].CimClass.CimClassName = "MSFT_TaskTimeTrigger"
    } },
    @{ Name = "noninteractive principal"; Mutate = {
        param($task) $task.Principal.LogonType = "Password"
    } },
    @{ Name = "nonhighest principal"; Mutate = {
        param($task) $task.Principal.RunLevel = "Limited"
    } }
)
foreach ($nearMiss in $portableNearMisses) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($packagedPortablePath)
    $script:FakeExecutableHashes[$packagedPortablePath] = $script:LegacyPortableViiperSha256
    $candidate = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $packagedPortablePath `
            $script:ViiperServerArguments $packagedPortableDirectory)
    $candidate.Description = ""
    & $nearMiss.Mutate $candidate
    $script:FakeTasks = @($candidate)
    $rejected = $false
    try {
        [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
            $script:ViiperServerArguments $workingDirectory)
    }
    catch { $rejected = $_.Exception.Message -match "foreign root task" }
    if (-not $rejected) { throw "Portable near-miss accepted: $($nearMiss.Name)." }
    Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
    Assert-Equal $script:RegisterCalls 0 "Portable near-miss reached registration."
    Assert-Equal $script:DisableCalls 0 "Portable near-miss was disabled."
    Assert-Equal $script:UnregisterCalls 0 "Portable near-miss was removed."
    Assert-Equal $script:TaskBackups.Count 0 "Foreign task was archived as owned."
}

# Backup failure prevents all mutations, including failure-containment disable.
foreach ($operation in @("register", "remove", "disable")) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($packagedPortablePath)
    $script:FakeExecutableHashes[$packagedPortablePath] = $script:LegacyPortableViiperSha256
    $candidate = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $packagedPortablePath "server" $packagedPortableDirectory)
    $candidate.Description = ""
    $candidate.Settings.Priority = 7
    $script:FakeTasks = @($candidate)
    $script:BackupFailure = $true
    try {
        switch ($operation) {
            "register" {
                [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
                    $script:ViiperServerArguments $workingDirectory)
            }
            "remove" {
                [void](Remove-ManagedStartupTask "RunVIIPER" $viiperPath `
                    $script:ViiperServerArguments $workingDirectory)
            }
            "disable" { Set-InfrastructureStartupFailClosed $viiperPath $ds4Path }
        }
    }
    catch {
        if ($_.Exception.Message -notmatch "simulated backup failure") { throw }
    }
    Assert-Equal $script:RegisterCalls 0 "Backup failure allowed replacement."
    Assert-Equal $script:DisableCalls 0 "Backup failure allowed disable."
    Assert-Equal $script:UnregisterCalls 0 "Backup failure allowed removal."
    Assert-Equal $script:FakeTasks.Count 1 "Backup failure lost the previous task."
}

# A default-priority marked task is owned but not current. Repair upgrades it
# once; the next repair reuses it without another registration or backup.
Reset-FakeTaskState
$oldPriorityTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath $script:ViiperServerArguments $workingDirectory)
$oldPriorityTask.Settings.Priority = 7
$script:FakeTasks = @($oldPriorityTask)
foreach ($attempt in @(1, 2)) {
    if (-not (Register-ViiperRunTask $viiperPath "RunVIIPER")) {
        throw "High-priority startup repair did not converge."
    }
}
Assert-Equal $script:RegisterCalls 1 "High-priority repair recreated an exact task."
Assert-Equal $script:TaskBackups.Count 0 "Marked-task repair needed legacy archival."

# Recognition is an exact finite compatibility set, not a "server" prefix.
foreach ($unexpectedArguments in @(
        "server --api.addr=0.0.0.0:3242",
        ($script:ViiperServerArguments + " --other"),
        "server --usb.retained-import-authority-id=1")) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($viiperPath)
    $legacyArgumentNearMiss = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $viiperPath $unexpectedArguments $workingDirectory)
    $legacyArgumentNearMiss.Description = ""
    $script:FakeTasks = @($legacyArgumentNearMiss)
    $argumentNearMissRejected = $false
    try {
        [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
            $script:ViiperServerArguments $workingDirectory)
    }
    catch { $argumentNearMissRejected = $_.Exception.Message -match "foreign root task" }
    if (-not $argumentNearMissRejected) { throw "Legacy VIIPER extra arguments were accepted." }
    Assert-Equal $script:RegisterCalls 0 "Legacy argument near-miss reached registration."
    Assert-Equal $script:DisableCalls 0 "Legacy argument near-miss was disabled."
    Assert-Equal $script:UnregisterCalls 0 "Legacy argument near-miss was removed."
}

# Historical observation is allowed only for migration to the current request.
Reset-FakeTaskState
$script:RecognizedProductPaths = @($viiperPath)
$legacyDowngrade = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$legacyDowngrade.Description = ""
$script:FakeTasks = @($legacyDowngrade)
$downgradeRejected = $false
try { [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" $workingDirectory) }
catch { $downgradeRejected = $_.Exception.Message -match "foreign root task" }
if (-not $downgradeRejected) { throw "Legacy VIIPER replacement omitted the required authority." }
Assert-Equal $script:RegisterCalls 0 "Legacy downgrade reached registration."
Assert-Equal $script:DisableCalls 0 "Legacy downgrade was disabled."
Assert-Equal $script:UnregisterCalls 0 "Legacy downgrade was removed."

Reset-FakeTaskState
$foreignViiperPath = "C:\Other Portable Backend\viiper.exe"
$script:RecognizedProductPaths = @($foreignViiperPath)
$noncanonicalViiper = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $foreignViiperPath "server" `
        (Split-Path -Parent $foreignViiperPath))
$noncanonicalViiper.Description = ""
$script:FakeTasks = @($noncanonicalViiper)
$noncanonicalRejected = $false
try {
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
        $script:ViiperServerArguments $workingDirectory)
}
catch { $noncanonicalRejected = $_.Exception.Message -match "foreign root task" }
if (-not $noncanonicalRejected) {
    throw "Noncanonical legacy VIIPER task was accepted for migration."
}
Assert-Equal $script:RegisterCalls 0 `
    "Noncanonical legacy VIIPER collision reached registration."

# Normal absence is safe containment, not a failing exact CIM query. The
# enumeration mock accepts no TaskPath/TaskName parameters, so a regression to
# the old targeted query fails parameter binding here.
Reset-FakeTaskState
$absent = Test-HighestLogonTask "RunVIIPER" $viiperPath $arguments `
    $workingDirectory
if ($absent) { throw "An absent startup task was reported as registered." }
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 0 `
    "Failure containment attempted to mutate an absent startup task."
if (@($script:SetupLogs | Where-Object {
            $_ -match "Could not verify failure containment"
        }).Count -ne 0) {
    throw "Normal task absence was logged as a containment failure."
}

# A pre-existing foreign same-name task is a collision, never an overwrite or
# cleanup target.
Reset-FakeTaskState
$foreignViiper = New-ForeignScheduledTask "RunVIIPER"
$script:FakeTasks = @($foreignViiper)
$foreignRejected = $false
try {
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" `
        $workingDirectory)
}
catch { $foreignRejected = $_.Exception.Message -match "foreign root task" }
if (-not $foreignRejected) { throw "Foreign RunVIIPER collision was accepted." }
Assert-Equal $script:RegisterCalls 0 "Foreign collision reached registration."
Assert-Equal $script:DisableCalls 0 "Foreign collision was disabled."
Assert-Equal $script:UnregisterCalls 0 "Foreign collision was removed."
Assert-Equal $script:FakeTasks.Count 1 "Foreign collision was not preserved."

# Pair removal preflights both names, so a foreign second task cannot cause a
# partial first-task deletion when Run at Startup is disabled.
Reset-FakeTaskState
$ownedViiper = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$foreignDs4 = New-ForeignScheduledTask "RunDS4Windows"
$script:FakeTasks = @($ownedViiper, $foreignDs4)
$pairRemovalRejected = $false
try { Remove-ManagedStartupTaskPair $viiperPath $ds4Path }
catch { $pairRemovalRejected = $_.Exception.Message -match "foreign root task" }
if (-not $pairRemovalRejected) {
    throw "Foreign second-name collision did not block pair removal."
}
Assert-Equal $script:UnregisterCalls 0 `
    "Pair removal mutated its first task before validating the second."
Assert-Equal $script:FakeTasks.Count 2 `
    "Pair removal did not preserve both tasks after collision."

# Pair registration also preflights both names. A foreign RunDS4Windows task
# therefore causes zero RunVIIPER registration or rollback mutations.
Reset-FakeTaskState
$script:FakeTasks = @(New-ForeignScheduledTask "RunDS4Windows")
$pairRegistrationRejected = $false
try { [void](Register-ManagedStartupTaskPair $viiperPath $ds4Path) }
catch {
    $pairRegistrationRejected = $_.Exception.Message -match "foreign root task"
}
if (-not $pairRegistrationRejected) {
    throw "Foreign RunDS4Windows collision did not block pair registration."
}
Assert-Equal $script:RegisterCalls 0 `
    "Pair registration mutated RunVIIPER before validating RunDS4Windows."
Assert-Equal $script:UnregisterCalls 0 `
    "Foreign pair collision triggered rollback deletion."

# A genuine second-task provider failure rolls back only RunVIIPER created by
# this pair transaction; no unowned task is touched.
Reset-FakeTaskState
$script:RegisterFailureNames = @("RunDS4Windows")
$partialFailureObserved = $false
try { [void](Register-ManagedStartupTaskPair $viiperPath $ds4Path) }
catch {
    $partialFailureObserved = $_.Exception.Message -match `
        "Could not register the elevated RunDS4Windows"
}
if (-not $partialFailureObserved) {
    throw "Simulated second-task registration failure was not propagated."
}
Assert-Equal $script:UnregisterCalls 1 `
    "Pair rollback did not remove exactly its newly created RunVIIPER task."
Assert-Equal $script:FakeTasks.Count 0 `
    "Pair rollback left a partial startup-task set."

# Failure containment may disable a marker-owned task even when a failed
# update left its action malformed. The marker establishes ownership; a
# foreign same-name task remains untouched.
Reset-FakeTaskState
$markerTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$markerTask.Actions[0].Execute = "C:\Broken\unexpected.exe"
$script:FakeTasks = @($markerTask)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 1 `
    "Containment did not disable a marker-owned malformed task."
if ($markerTask.Settings.Enabled) {
    throw "Marker-owned malformed task remained enabled after containment."
}

Reset-FakeTaskState
$script:FakeTasks = @(
    (New-ForeignScheduledTask "RunVIIPER"),
    (New-ForeignScheduledTask "RunDS4Windows")
)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 0 `
    "Containment disabled a foreign same-name task."
Assert-Equal $script:UnregisterCalls 0 `
    "Containment removed a foreign same-name task."

# Enumeration/service failures remain real failures and must never be
# translated into normal absence or followed by a mutation.
Reset-FakeTaskState
$script:EnumerationFailure = $true
$enumerationFailureObserved = $false
try {
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" `
        $workingDirectory)
}
catch {
    $enumerationFailureObserved = $_.Exception.Message -match `
        "enumeration failure"
}
if (-not $enumerationFailureObserved) {
    throw "Task Scheduler enumeration failure was masked as task absence."
}
Assert-Equal $script:RegisterCalls 0 `
    "Registration continued after enumeration failed."
Assert-Equal $script:DisableCalls 0 `
    "Enumeration failure triggered a disable mutation."
Assert-Equal $script:UnregisterCalls 0 `
    "Enumeration failure triggered a removal mutation."

Write-Host (
    "Exact-SID startup-task XML, schema, ownership, collision, rollback, " +
    "containment, and absence simulations passed without changing Task " +
    "Scheduler state."
)
