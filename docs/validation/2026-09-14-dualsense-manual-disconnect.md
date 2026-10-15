# DualSense manual Bluetooth disconnect, 2026-09-14

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Report and evidence

The user reported that pressing Disconnect turned off a Bluetooth DualSense,
but its controller entry remained in DS4Windows. Inspection and tests were
hardware-free; the running application and controllers were not changed.

The September 13 log contains a natural read failure (1167) at 23:19:54 followed
by successful controller removal. That is not the reported manual-disconnect
failure. A later virtual-device removal at 23:21:38 is explicitly a profile
transition, not physical controller removal. The log does not timestamp the
manual button press, so it cannot establish the exact failing interleaving.

## Reproduced cause and fix

Both the controller-row action and tray action called `DisconnectBT()` with its
default `callRemoval: false`. This relies on a later physical read failure to
publish Removal. DualSense's one-shot lifecycle retires output/command workers
and consumes pending removal before disconnecting the radio. A read failure
arriving afterward has no remaining lifecycle consumer to deliver Removal.

Both manual UI callers now explicitly pass `callRemoval: true`. This reuses
the existing retirement path: output workers stop before radio I/O; Removal
revokes presentation and queues exact-device service cleanup. The controller
list removes the exact device and unsubscribes. Delayed old notifications
cannot remove a replacement in the same slot.

The optional default and transport lifecycle were not rewritten. Input,
haptics, audio, triggers, packet formats, Nintendo paths and reconnection
policies have no new changes in this fix. Previously pending workspace changes
were preserved. Mapping's separate special-action disconnect path was not
changed by this manual-UI fix.

## Validation

- `DualSenseManualDisconnectTests` invokes the actual controller-row and tray
  handlers plus real DualSense workers. Only hardware write/finalize/radio
  boundaries are substituted. Both tests failed before the fix because removal
  was not requested, and both passed afterward.
- Tests verify removal with no HID read-failure notification, one removal
  callback, output/command retirement before radio I/O, retention of another
  controller, and protection of a replacement occupying the reused slot.
- The targeted lifecycle/read-failure/controller-list run passed all 36 tests.
- Full Release/x64 suite: 5,698 passed, 11 hardware-gated tests skipped,
  0 failed (5,709 total). The build retained six existing compiler warnings;
  no new compile errors were introduced.
- Independent read-only review checked cleanup ordering, callback locks,
  duplicate removal, and the hardware-free fixture.
- `git diff --check` passed.

Evidence: `isolated_results/dualsense-manual-disconnect-20260914/red.trx`,
`green-targeted.trx`, and `full.trx`.

## Boundaries

No live Bluetooth disconnect or installed-build replacement was performed.
The production base radio routine's existing forced-success behavior remains
unchanged; these tests establish software cleanup following its accepted
disconnect contract, not the correctness of native radio return codes.

The compiled DS4Windows.dll SHA-256 is
`885A5EA767C46CE3B5D80E494B3C87AB046FC8A5715D7F5FE4A41FA3BD24AC5F`.
This is a workspace build, not a new public release or portable deployment.
