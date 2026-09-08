# Pure fixtures only: no PnP/process queries, timing waits, or hardware access.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Measure-LegacyJoystickExposure.ps1') -DefinitionsOnly
function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -ne $actual) { throw "$message : expected $expected, got $actual" }
}
function New-Node([string]$id, [string]$usage, [string]$container = '') {
    [pscustomobject]@{ InstanceId = $id; MetadataKnown = $true;
        HardwareIds = @(); CompatibleIds = @($usage); ContainerId = $container; ParentInstanceId = '' }
}
$a = New-Node 'HID\VID_054C&PID_0CE6\A' 'HID_DEVICE_UP:0001_U:0005' 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
$b = New-Node 'HID\VID_054C&PID_0CE6\B' 'HID_DEVICE_SYSTEM_GAME' 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
$mouse = New-Node 'HID\VID_054C&PID_0CE6\M' 'HID_DEVICE_UP:0001_U:0002'
$service = New-Node 'BTHENUM\VID_054C&PID_0CE6\S' 'HID_DEVICE_SYSTEM_GAME'
$unknown = New-Node 'HID\VID_054C&PID_0CE6\U' 'HID_DEVICE_SYSTEM_GAME'
$unknown.MetadataKnown = $false
$groups = @(Get-LegacyJoystickExposureGroups @($a, $a, $b, $mouse, $service, $unknown))
Assert-Equal 1 $groups.Count 'Same model grouped once'
Assert-Equal 2 $groups[0].PresentGameHidCollections 'Repeated node/mouse/service/unknown excluded'
Assert-Equal 2 $groups[0].KnownDistinctContainers 'Physical identities not collapsed by model'
Assert-Equal $true $groups[0].SameModelMultipleCollections 'Exposure candidate labeled'
$sameContainer = New-Node 'HID\VID_054C&PID_0CE6\C' 'HID_DEVICE_UP:0001_U:0004' $a.ContainerId
$groups = @(Get-LegacyJoystickExposureGroups @($a, $sameContainer))
Assert-Equal 2 $groups[0].PresentGameHidCollections 'Separate collections preserved'
Assert-Equal 1 $groups[0].KnownDistinctContainers 'Collections not falsely called two devices'
$sameContainer.ContainerId = '{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}'
$groups = @(Get-LegacyJoystickExposureGroups @($a, $sameContainer))
Assert-Equal 1 $groups[0].KnownDistinctContainers 'Container formatting does not split one identity'
$zero = New-Node 'HID\VID_045E&PID_028E\Z' 'HID_DEVICE_SYSTEM_GAME' '00000000-0000-0000-0000-000000000000'
$groups = @(Get-LegacyJoystickExposureGroups @($a, $zero))
Assert-Equal 2 $groups.Count 'Distinct models separate'
Assert-Equal 0 $groups[0].KnownDistinctContainers 'Zero container is unknown'
Assert-Equal 0 @(Get-LegacyJoystickExposureGroups @()).Count 'Empty inventory'
$bt = New-Node 'HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\BT' 'HID_DEVICE_SYSTEM_GAME'
$btCompany = New-Node 'HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0001054c_PID&0ce6\SIG' 'HID_DEVICE_SYSTEM_GAME'
$groups = @(Get-LegacyJoystickExposureGroups @($a, $bt, $btCompany))
Assert-Equal 2 $groups[0].PresentGameHidCollections 'Bluetooth USB-vendor PnP form grouped; SIG namespace not conflated'

function New-Snapshot([long]$stamp = 42, [int]$handles = 100) {
    [pscustomobject]@{ ProcessId = 123; StartTimeUtcTicks = $stamp; HandleCount = $handles;
        PrivateBytes = 500; CpuTicks = 10000000 }
}
Assert-Equal $true (Test-LegacyJoystickProcessIdentity (New-Snapshot) (New-Snapshot)) 'Same process'
Assert-Equal $false (Test-LegacyJoystickProcessIdentity (New-Snapshot) (New-Snapshot 43)) 'PID reuse rejected'
Assert-Equal $false (Test-LegacyJoystickProcessIdentity (New-Snapshot 0) (New-Snapshot 0)) 'Missing start time rejected'
Assert-Equal $false (Test-LegacyJoystickProcessIdentity $null $null) 'Missing process rejected'
$state = @{ Clock = 0L; Reads = 0 }
$read = { param($id) $state.Reads++; New-Snapshot 42 (100 + $state.Reads) }
$clock = { $state.Clock }
$delay = { param($ms) $state.Clock += $ms }
$result = Invoke-LegacyJoystickProcessSampling 123 1 500 $read $clock $delay
Assert-Equal 'DurationComplete' $result.Status 'Normal duration bound'
Assert-Equal 3 $result.Samples.Count 'Inclusive final sample'
Assert-Equal 2 $result.Summary.ProcessHandleCountDelta 'Delta uses samples, not initial identity read'
Assert-Equal 2 $result.Summary.ProcessHandlesPerSecond 'Rate based on actual elapsed time'
Assert-Equal $false $result.Summary.RegistryHandleLeakConfirmed 'No registry leak inference'
$state.Clock = 0; $state.Reads = 0
$reuse = { param($id) $state.Reads++; if ($state.Reads -gt 2) { New-Snapshot 43 } else { New-Snapshot } }
$result = Invoke-LegacyJoystickProcessSampling 123 1 100 $reuse $clock $delay
Assert-Equal 'ProcessIdentityChanged' $result.Status 'Reused PID ends capture'
Assert-Equal 1 $result.Samples.Count 'Replacement process never sampled'
$state.Clock = 0; $state.Reads = 0
$exit = { param($id) $state.Reads++; if ($state.Reads -gt 2) { throw 'exited' }; New-Snapshot }
$result = Invoke-LegacyJoystickProcessSampling 123 1 100 $exit $clock $delay
Assert-Equal 'ProcessExitedOrUnreadable' $result.Status 'Exit bounded without reuse'
$result = Invoke-LegacyJoystickProcessSampling 123 1 100 { throw 'access denied' } $clock $delay
Assert-Equal 'Unavailable' $result.Status 'Unreadable is not zero handles'
$result = Invoke-LegacyJoystickProcessSampling 124 1 100 { New-Snapshot } $clock $delay
Assert-Equal 'IdentityUnavailable' $result.Status 'Wrong initial PID rejected'
$state.Clock = 0; $state.Reads = 0
$result = Invoke-LegacyJoystickProcessSampling 123 60 100 $read $clock $delay
Assert-Equal 'SampleLimitReached' $result.Status 'Sample cap explicit'
Assert-Equal 121 $result.Samples.Count 'Sample count hard bound'
Assert-Equal $null (Get-LegacyJoystickProcessSummary @()) 'No invented empty summary'
Write-Output 'PASS: pure identity grouping, process-generation guards, bounded sampling and no leak inference.'
