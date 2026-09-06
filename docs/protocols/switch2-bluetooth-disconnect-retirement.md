# Bluetooth disconnect retirement — September 5, 2026

## Observed failure

The user disconnected the Pro Controller 2 from Bluetooth in Desktop b67 and
reported a stale controller row and frozen program. A private local heap of
DS4Windows PID 27368 proves that the input owner saw `Disconnected`, published
5,998 reports with zero queue overflows/session errors, and exited its drain
worker. The runtime owner then entered `Quarantined`, with
`TerminalDeliveryRejected`; its feedback lifetime was stopping but not retired.
The UI dispatcher was in its normal message wait at capture, not a demonstrated
UI-thread deadlock. VIIPER's virtual DualSense remained attached.

`TryStopCore` demanded a physical rumble Stop before any transport release proof.
The already-disconnected lease rejected this impossible write. Consequently the
owner never reached virtual terminal-neutral publication or controller removal.
The lease also began remote CCCD-None writes after disconnect, creating further
GATT work against an absent target. The heap's exact resource-release task was
still pending while its bounded observer had returned false.

## Correction and authority

- Windows' exact connection-status callback latches disconnect on the old lease.
  An exception reading status is not upgraded to definite disconnect evidence.
- On that path, command/input notification teardown detaches and drains local
  callbacks and existing operations without starting another remote CCCD write.
  Connected shutdown and ambiguous failures keep their prior compensation rules.
- The standalone runtime drains input, waits for exact native resource release,
  then asks that same output lease for model/device/transport-bound disconnect
  proof. Timeout, failed disposal, uncertain callback removal, and another
  generation cannot grant that proof.
- The feedback owner reuses the existing disconnected-target pump/sink retirement
  primitives. It fences the session, clears delayed/impulse timers, and retires
  locally without fabricating a physical Stop receipt. Virtual input must still
  deliver and acknowledge its own terminal neutral before removal.
- VIIPER's Xbox delivered-Stop shortcut explicitly excludes disconnected local
  retirement. A failed physical broker Stop is not positively acknowledged.

This implements the same transport-ended distinction already used by the USB
feedback owner, not a new mapping or rumble stack. No input cadence changes.
Joined Joy-Con feedback does not use the standalone shortcut: losing one half
does not prove its surviving actuator is neutral. That separate retirement path
and permanently ambiguous native failures remain open work.

Windows documents reading ConnectionStatus on the event's sender:
[ConnectionStatusChanged](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.connectionstatuschanged).
Its [GATT client guidance](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)
also explains that GATT operations can initiate a connection and incur queued,
variable waits. Avoiding fresh writes after confirmed disconnect is a local
lifecycle policy; no physical actuator-state measurement is inferred from it.

## Verification and limits

`ble-disconnect-retirement-red-20260905.trx`: five regressions failed before
production correction. `ble-disconnect-retirement-green-20260905.trx`: five pass.
`ble-disconnect-retirement-focused-20260905.trx`: 387 related tests pass.
`ble-disconnect-runtime-integration-20260905.trx`: 90 adapter/runtime tests pass,
including the real duplex lease and runtime with synthetic Windows devices for
both DualSense and Xbox feedback. Tests check local teardown, exact generation,
terminal virtual input, session rejection/idempotence, zero post-disconnect
writes, and disposal/callback ambiguity. Connected Stop delivery/rejection cases
are also included in the final full run, recorded in the platform ledger.
Final `ble-disconnect-retirement-full-20260905.trx`: 3,644 passed, three
existing live-audio skips, zero failures (62 seconds). Desktop b68 launched
and was independently observed disconnected with discovery active; user cued
to repeat wake/disconnect/reconnect. The existing b67 private heap is retained.

No new physical disconnect/reconnect acceptance is claimed by these tests.
Xbox broker terminal-failure quarantine remains honest and separate from local
input removal; joined-pair cleanup and Hades II/haptic A/B are not closed here.
The private heap can contain secrets and must not be uploaded or packaged.
