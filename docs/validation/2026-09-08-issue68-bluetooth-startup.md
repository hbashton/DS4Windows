# Issue #68: Bluetooth Switch Pro startup, 2026-09-08

Status: the source-backed calibration null-reference failure class is covered
by the existing `ed0e983` repair, now verified through additional Bluetooth
initialization regressions. This pass adds tests, not another transport rewrite.
The historical reporter's exact exception and hardware acceptance remain
unconfirmed because the supplied log contains no exception stack.

## Report and source evidence

[Issue #68](https://github.com/hbashton/DS4Windows/issues/68) contains an RC4.3
Bluetooth original Switch Pro log with three attempts. Each reaches virtual
Xbox 360 association and then logs a null-reference message. One attempt later
also loses the feedback stream and receives a connection refusal from the local
VIIPER endpoint. A comment reports the same symptom on a GuliKit KingKong 2 Pro
in its Switch-compatible mode, but supplies no extra stack or packet capture.
No claim is made that the subsequent broker refusal is caused by calibration.

The earlier implementation dereferenced absent SPI calibration replies.
`SwitchProDevice.StartUpdate` caught `IOException`, not that null reference, so
the ordinary failed-initialization removal path could be bypassed after output
association. The earlier #75 repair already replaced this with validated
report/ACK/subcommand/address/length checks, real user-to-factory calibration
fallback, three bounded factory attempts, and `IOException` failure through
normal removal before workers start. Invalid calibration is not replaced with
invented values. The prepared-hotplug publication guard also checks that the
same device still occupies its slot after preparation returns.

That mechanism matches the location and symptom of #68, but the stackless log
does not prove it was the reporter's exact null reference. The Bluetooth
transport tests below exercise the existing production fix rather than treating
USB-only coverage as sufficient or changing clone-controller protocol policy
without a capture.

## Additional regressions

Seventeen new cases in `SwitchProCalibrationProtocolTests`:

- Twelve call actual `StartUpdate`, after valid earlier initialization commands,
  with a missing, truncated, negative-ACK, or wrong-address factory reply at each
  of the left stick, right stick, and IMU stages. Each exhausts exactly three
  attempts, raises one removal, starts neither input nor rumble workers, and
  cannot reannounce the removed controller through the actual
  `PublishPreparedHotplug` guard.
- Two verify complete Bluetooth `SetOperational` with valid synthetic user or
  factory data, preserving axis interpretation and finite IMU coefficients.
- Three verify that a missing first factory reply recovers on the second
  attempt at each calibration stage.

`issue68-bluetooth-startup-regressions.trx`: **54 passed, zero failed** — the
expanded 51 calibration/startup cases plus three existing publication cases.
Independent review found no material issue in the new tests or fake seam.

## Boundaries

All HID reads/writes, reply data, and time progression are fake. The service
constructor is not run; the removal subscriber clears a synthetic slot. Actual
service removal, virtual-pad teardown, radio pairing, input-worker gameplay,
and either reporter's physical controller have not been exercised. Successful
initialization cases intentionally stop before spawning workers.

No controller or app was restarted, no calibration/profile file was read or
changed, and no broker/driver setting was modified. This is not a deployed
release, a closed GitHub issue, or proof that all possible causes of Connecting
are eliminated. A current failure needs the full exception stack and, for the
separate connection-refused symptom, the matching VIIPER log.
