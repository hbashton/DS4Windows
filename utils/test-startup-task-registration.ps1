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

foreach ($functionName in @(
        "Assert-ManagedStartupTaskName",
        "Get-RootScheduledTask",
        "Convert-AccountToSid",
        "Test-HighestLogonTaskDefinition",
        "Test-HighestLogonTask",
        "Test-ManagedStartupTaskMarker",
        "Test-LegacyManagedStartupTask",
        "Test-ManagedStartupTaskOwnership",
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
Reset-FakeTaskState
$script:RecognizedProductPaths = @($viiperPath)
$legacyViiper = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$legacyViiper.Description = ""
$script:FakeTasks = @($legacyViiper)
if (-not (Register-HighestLogonTask "RunVIIPER" $viiperPath "server" `
        $workingDirectory)) {
    throw "Canonical recognized legacy VIIPER task was not migrated."
}
Assert-Equal $script:RegisterForceNames.Count 1 `
    "Canonical legacy VIIPER migration did not use owned replacement."

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
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" `
        $workingDirectory)
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
