#requires -Version 7.0
# Read-only issue #82 evidence collection. No HID reports, registry writes,
# driver-control IOCTLs, game launch, device disconnect, or service changes.
[CmdletBinding()]
param(
    [ValidateRange(0, 2147483647)][int]$TargetProcessId = 0,
    [ValidateRange(1, 60)][int]$DurationSeconds = 15,
    [ValidateRange(100, 5000)][int]$SampleIntervalMs = 1000,
    [switch]$DefinitionsOnly
)

function Get-LegacyJoystickExposureGroups {
    param([object[]]$Nodes)
    $seen = @{}
    $accepted = @()
    foreach ($node in $Nodes) {
        # Device names and Bluetooth service nodes are not proof of a joystick.
        if ($node.InstanceId -notmatch '^HID\\' -or -not $node.MetadataKnown) { continue }
        $ids = @($node.HardwareIds) + @($node.CompatibleIds)
        $isGame = @($ids | Where-Object {
            $_ -match '^HID_DEVICE_SYSTEM_GAME$|^HID_DEVICE_UP:0001_U:000[458]$'
        }).Count -gt 0
        if (-not $isGame) { continue }
        $identity = $null
        foreach ($id in (@($node.InstanceId) + $ids)) {
            if ($id -match 'VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})') {
                $identity = ($Matches[1] + ':' + $Matches[2]).ToUpperInvariant()
                break
            }
            # Bluetooth HID nodes also use the PnP ID form _VID&0002vvvv_PID&pppp.
            # 0002 means the USB vendor-ID namespace; do not conflate a Bluetooth
            # SIG (0001) company identifier with the same numeric USB VID.
            if ($id -match '_VID&0002([0-9A-Fa-f]{4})_PID&([0-9A-Fa-f]{4})') {
                $identity = ($Matches[1] + ':' + $Matches[2]).ToUpperInvariant()
                break
            }
        }
        if (-not $identity -or $seen.ContainsKey([string]$node.InstanceId)) { continue }
        $seen[[string]$node.InstanceId] = $true
        $parsedContainer = [Guid]::Empty
        $container = $null
        if ([Guid]::TryParse([string]$node.ContainerId, [ref]$parsedContainer) -and
            $parsedContainer -ne [Guid]::Empty -and
            $parsedContainer -ne [Guid]'00000000-0000-0000-ffff-ffffffffffff') {
            $container = $parsedContainer.ToString('D')
        }
        $accepted += [pscustomobject]@{
            VidPid = $identity
            InstanceId = [string]$node.InstanceId
            ContainerId = $container
            ParentInstanceId = [string]$node.ParentInstanceId
        }
    }
    @($accepted | Group-Object VidPid | Sort-Object Name | ForEach-Object {
        $containers = @($_.Group.ContainerId | Where-Object { $_ } | Sort-Object -Unique)
        [pscustomobject]@{
            VidPid = $_.Name
            PresentGameHidCollections = $_.Count
            SameModelMultipleCollections = $_.Count -gt 1
            KnownDistinctContainers = $containers.Count
            Collections = @($_.Group)
        }
    })
}

function Test-LegacyJoystickProcessIdentity {
    param($Expected, $Actual)
    return $null -ne $Expected -and $null -ne $Actual -and
        $Expected.ProcessId -eq $Actual.ProcessId -and
        $Expected.StartTimeUtcTicks -gt 0 -and
        $Expected.StartTimeUtcTicks -eq $Actual.StartTimeUtcTicks
}

function Get-LegacyJoystickProcessSummary {
    param([object[]]$Samples)
    if ($Samples.Count -lt 2) { return $null }
    $first = $Samples[0]
    $last = $Samples[-1]
    $elapsed = [double]$last.ElapsedMs - [double]$first.ElapsedMs
    if ($elapsed -le 0) { return $null }
    [pscustomobject]@{
        ObservedSeconds = $elapsed / 1000.0
        ProcessHandleCountDelta = [long]$last.HandleCount - [long]$first.HandleCount
        ProcessHandlesPerSecond = ([long]$last.HandleCount - [long]$first.HandleCount) * 1000.0 / $elapsed
        PrivateBytesDelta = [long]$last.PrivateBytes - [long]$first.PrivateBytes
        CpuSecondsDelta = ([long]$last.CpuTicks - [long]$first.CpuTicks) / 10000000.0
        RegistryHandleLeakConfirmed = $false
    }
}

function Get-LegacyJoystickPnpInventory {
    $nodes = @()
    $errors = @()
    try { $devices = @(Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction Stop) }
    catch {
        return [pscustomobject]@{ Available = $false; Truncated = $false; MetadataFailures = 0;
            Groups = @(); Errors = @($_.Exception.Message) }
    }
    $collections = @($devices | Where-Object { $_.InstanceId -match '^HID\\' })
    $truncated = $collections.Count -gt 256
    $failures = 0
    foreach ($device in ($collections | Select-Object -First 256)) {
        try {
            $properties = @(Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName @(
                'DEVPKEY_Device_HardwareIds', 'DEVPKEY_Device_CompatibleIds',
                'DEVPKEY_Device_ContainerId', 'DEVPKEY_Device_Parent') -ErrorAction Stop)
            $values = @{}
            foreach ($property in $properties) { $values[$property.KeyName] = $property.Data }
            $nodes += [pscustomobject]@{
                InstanceId = $device.InstanceId; MetadataKnown = $true
                HardwareIds = @($values['DEVPKEY_Device_HardwareIds'])
                CompatibleIds = @($values['DEVPKEY_Device_CompatibleIds'])
                ContainerId = [string]$values['DEVPKEY_Device_ContainerId']
                ParentInstanceId = [string]$values['DEVPKEY_Device_Parent']
            }
        }
        catch {
            $failures++
            if ($errors.Count -lt 8) { $errors += $_.Exception.Message }
        }
    }
    [pscustomobject]@{ Available = $true; Truncated = $truncated; MetadataFailures = $failures;
        Groups = @(Get-LegacyJoystickExposureGroups -Nodes $nodes); Errors = $errors }
}

function Get-LegacyJoystickProcessSnapshot {
    param([int]$SelectedProcessId)
    $process = Get-Process -Id $SelectedProcessId -ErrorAction Stop
    try {
        [pscustomobject]@{
            ProcessId = $process.Id; StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
            HandleCount = $process.HandleCount; PrivateBytes = $process.PrivateMemorySize64
            CpuTicks = $process.TotalProcessorTime.Ticks
        }
    }
    finally { $process.Dispose() }
}

function Invoke-LegacyJoystickProcessSampling {
    param(
        [int]$SelectedProcessId,
        [ValidateRange(1, 60)][int]$Seconds,
        [ValidateRange(100, 5000)][int]$IntervalMs,
        [scriptblock]$ReadSnapshot = { param($id) Get-LegacyJoystickProcessSnapshot $id },
        [scriptblock]$ReadClock = { [Environment]::TickCount64 },
        [scriptblock]$Delay = { param($ms) Start-Sleep -Milliseconds $ms }
    )
    $samples = @()
    $reason = 'DurationComplete'
    try { $expected = & $ReadSnapshot $SelectedProcessId }
    catch { return [pscustomobject]@{ Status = 'Unavailable'; Samples = @(); Summary = $null; Error = $_.Exception.Message } }
    if (-not (Test-LegacyJoystickProcessIdentity $expected $expected) -or
        $expected.ProcessId -ne $SelectedProcessId) {
        return [pscustomobject]@{ Status = 'IdentityUnavailable'; Samples = @(); Summary = $null }
    }
    $started = & $ReadClock
    for ($i = 0; $i -lt 121; $i++) {
        try { $snapshot = & $ReadSnapshot $SelectedProcessId }
        catch { $reason = 'ProcessExitedOrUnreadable'; break }
        if (-not (Test-LegacyJoystickProcessIdentity $expected $snapshot)) {
            $reason = 'ProcessIdentityChanged'; break
        }
        $elapsed = [long](& $ReadClock) - [long]$started
        $samples += [pscustomobject]@{
            ElapsedMs = $elapsed; ProcessId = $snapshot.ProcessId
            StartTimeUtcTicks = $snapshot.StartTimeUtcTicks
            HandleCount = $snapshot.HandleCount; PrivateBytes = $snapshot.PrivateBytes
            CpuTicks = $snapshot.CpuTicks
        }
        if ($elapsed -ge $Seconds * 1000) { break }
        if ($i -eq 120) { $reason = 'SampleLimitReached'; break }
        & $Delay ([int][Math]::Min($IntervalMs, $Seconds * 1000 - $elapsed))
    }
    [pscustomobject]@{ Status = $reason; Samples = $samples;
        Summary = Get-LegacyJoystickProcessSummary -Samples $samples }
}

if (-not $DefinitionsOnly) {
    $inventory = Get-LegacyJoystickPnpInventory
    $sampling = $null
    if ($TargetProcessId -gt 0) {
        $sampling = Invoke-LegacyJoystickProcessSampling -SelectedProcessId $TargetProcessId `
            -Seconds $DurationSeconds -IntervalMs $SampleIntervalMs
    }
    [pscustomobject]@{
        SchemaVersion = 1; CollectedUtc = [DateTime]::UtcNow.ToString('o')
        ReadOnly = $true; HidHidePolicy = 'NotQueried'
        GameVisibilityConfirmed = $false; RegistryHandleLeakConfirmed = $false
        Caveats = @(
            'Present PnP nodes are not proof of an active Bluetooth link or visibility to this game.',
            'Several HID collections may belong to one device; equal VID/PID is a risk candidate, not a defect.',
            'HidHide per-process rules/session entries are not queried; no visibility conclusion follows from this inventory.',
            'HandleCount includes all process handles, not specifically registry keys. API/handle-type tracing is still required.',
            'Sampling is limited to 60 seconds and 121 samples; PnP/process OS queries may add time.',
            'Instance/container identities can identify your devices. Review the JSON before sharing.')
        Inventory = $inventory; SelectedProcess = $sampling
    } | ConvertTo-Json -Depth 9
}
