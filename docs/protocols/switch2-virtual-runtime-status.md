# Switch 2 virtual runtime status

DS4Windows mirrors validated physical Switch 2 power telemetry into a VIIPER
Switch 2 Pro virtual controller without changing the canonical input hot path.

## Contract boundary

VIIPER's `ns2pro` streaming input packet remains exactly 24 bytes. Runtime
power is a separate, complete JSON control snapshot sent to the explicitly
versioned endpoint:

`bus/{busId}/{devId}/ns2pro-status-v1`

The v1 fields are `version`, `batteryLevel`, `charging`, `externalPower`, and
`batteryVolts`. VIIPER requires all fields, accepts only version 1, levels
`0..9`, and `2500..5000` mV, and rejects unknown/incomplete payloads and wrong
device types. Runtime status cannot change virtual serial identity.

## Physical projection

Switch 2 common reports establish validated millivolts and the pinned low,
medium, or high battery band. DS4Windows maps those categorical bands to
virtual levels 1, 5, and 9 and carries the validated millivolts unchanged. It
does not claim a more precise state of charge.

The pinned current field does not yet establish direction or charge-state
semantics, so `charging` remains false. A physical USB transport establishes
`externalPower=true`; Bluetooth remains false. Joined Joy-Cons continue to use
the lower valid half, matching the existing compatibility battery policy.

## Lifecycle and latency

Initial status is included in virtual-device creation when a valid physical
snapshot already exists. Later band changes enter one latest-wins background
control worker. The physical report callback only copies the small value and
signals the worker: it performs no JSON serialization, socket I/O, retry, or
virtual-report mutation.

Disconnect unsubscribes the exact physical source and synchronously drains the
status worker before the virtual device is removed. A stale worker therefore
cannot update a successor that reuses the same bus/device numbers. VIIPER
linearizes the status against report encoding but does not retire the input
scheduler or manufacture a neutral controller report. Immutable bytes already
selected for USB may complete with the preceding status; the next newly
encoded report uses the new snapshot.
