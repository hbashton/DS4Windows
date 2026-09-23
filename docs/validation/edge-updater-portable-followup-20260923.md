# Edge, updater and portable startup follow-up — 2026-09-23

## Requests and acceptance

1. **Physical and emulated DualSense Edge fidelity.** Compare independent
   implemented report formats; preserve normal DualSense feedback and input.
   Correct established differences without inventing profile/firmware behavior.
   Existing integrated Edge audit is in `dualsense-edge-fidelity-audit.md`.
2. **Auto-update / Check for updates failure.** Screenshot reports
   `Unrecognized portable update option: -autolaunch`. Read-only local inspection
   confirms registered `C:\Program Files\DS4Windows` RC4.6.4 contains a stale
   portable marker; updater2.0.7 classifies it differently from the application.
   Installed updates must use the verified AIO installer, portable updates must
   stay transactional in place, and errors must not enter legacy destructive
   cleanup. Test real emitted arguments against the receiving parser.
3. **Intermittent portable VIIPER startup / repair.** User reports RC4.6.4 and
   RC4.6.5 fail in writable `C:\Tools\PlayStation 4 Controller\DS4Windows VIIPER\`
   with a fresh configuration and intact files. Manual VIIPER launch also
   appears inert; reboot clears the condition without file changes. Audit
   process/port/single-instance ownership and cleanup, preserve actual failure
   evidence, reproduce confirmed defects with simulations. Do not infer a driver
   fault or perform destructive driver/service resets from this symptom alone.

## Current evidence

- Edge report 02 is 64 bytes including ID, versus 48 for the ordinary common
  prefix. The tail is not always padding: Edge profile previews and extension
  controls carry parameters beyond the V5 common prefix. Partial forwarding
  would preserve authorization but lose its parameters. Both broker and app
  guards now reject unsupported native configuration commands atomically;
  ordinary game effects and independent PCM remain intact. Common game feedback
  is supported in all four standard/Edge virtual-to-physical combinations. The
  physical target must still be a verified genuine Sony device. Virtual onboard
  profile storage and Edge feature reports are not fabricated or claimed complete.
  Unsupported configuration commands are rejected as a whole, never truncated
  and partially applied. No physical Edge hardware pass is claimed.
- Updater deployment routing now gives the registered installation precedence
  over a leftover ZIP marker, while explicit portable requests retain strict
  portable validation. Registered updates use the existing AIO installer after
  release-receipt, origin, size, hash, PE and version verification. Portable
  updates remain transactional in their selected folder. A staged updater 2.0.8
  avoids replacing the running Program Files image or using the old copy batch.
  The application remains open while a managed update is prepared. Installer
  opening is not reported as installation success. These changes are not released.

### Portable startup findings and fixes

The report does not prove a particular driver or process was stuck on the
reporter's machine. These concrete defects were established from source and
tested failure sequences:

1. **Mismatched startup deadlines.** DS4Windows waited 8 seconds for readiness.
   VIIPER first runs two serial USB/IP probes, each allowing 10 seconds, before
   opening its tray/API. A still-valid cold startup could be killed early. The
   shared startup/repair admission budget is now 25 seconds, with immediate
   success on authenticated readiness and no new input or feedback delay.
2. **Abandoned prerequisite helper.** Cleanup killed only VIIPER, not its helper
   tree. The helper's context-timeout watcher dies with its parent. Cleanup now
   requests termination of the verified broker's process tree, with no wildcard
   or unrelated process termination. A real disposable PowerShell parent/child
   test proves the changed path stops its helper. This does not prove every
   descendant's driver handles have drained: .NET's parent WaitForExit does not
   wait for all descendants.
3. **Lost cleanup ownership.** A failed stop was swallowed, the only retained
   process handle was disposed, and owned was cleared. Explicit repair could
   then reject the same surviving child as an unexpected owner. Failed stop now
   retains exact PID/start-time ownership and file pins; repair can retry. A
   successor or reused PID never inherits authority. Exit must be observed
   before pins are released. Startup/shutdown callers handle retirement errors.
4. **Unbounded redirected-output drain.** A child inheriting an output pipe
   could keep prerequisite completion waiting even after the direct process
   exited. VIIPER now bounds redirected-pipe drain to 250 ms. The corresponding
   DS4Windows prerequisite probe now also limits output completion to its
   remaining 3-second probe budget or 250 ms, whichever is less. It cancels and
   disposes its readers and observes late faults; valid completed output remains
   immediate, and partial output never passes the existing ABI gate.
5. **Lost failure evidence.** New broker exit codes 70–76 identify missing/query
   failure, version mismatch, timeout, driver/ABI, key setup and listener phases.
   DS4Windows displays only this bounded classification (or an unknown numeric
   code), not raw stderr, key contents, command lines or driver output.

The 8-second startup behavior predates RC4.6.4; RC4.6.5 repair inherited it. The
review found no VIIPER machine-persistent singleton, and server/tray lifecycle
did not change between the compared RC4.6 and RC4.6.5 tags. Consequently these
fixes address demonstrated failure paths consistent with the symptoms, not a
proven unique regression introduced on the reporter's machine in RC4.6.4.
USB/IP version/ABI gates, verified ownership and authentication remain required.

### References and validation

- Edge ordinary effects: SDL source `c71abd08605b8bb7078372307a93274725c99fe0`,
  `src/joystick/hidapi/SDL_hidapi_ps5.c`.
- Edge preview/extension distinction: dualsense-tester
  `f6e6247fd66ada9c8b63f3ba62c6d72945c0da53`, `JoystickSensitivity.vue` and
  `TriggerDeadZone.vue`; Titania `9904458f98fe5e37d1ae883acbecdc5661a4101c`,
  `src/structures.h` and `src/hid.c`.
- [Microsoft Process.Kill semantics](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill)
  document the distinction between stopping a process tree and observing its
  descendants' exits.
- Edge targeted DS4 tests: 207 passed; broker full suite and Edge race tests pass.
- Updater 2.0.8 complete suite: 410 passed, 3 opt-in archive tests skipped.
  Evidence: `DS4Updater/isolated_results/managed-update/updater208-final.trx`.
- Compiled cross-repository updater handoff: 5 checks use the actual emitters,
  classifier and parsers; the published RC4.6.5 receipt resolves the exact AIO
  identity without downloading or executing it. Reproduction script:
  `Test-CompiledUpdaterHandoff.ps1`.
- Startup cleanup focused suite: 75 passed, including 8 failure/retry cases and
  the disposable process-tree test.
- DS4 bounded USB/IP output probe and related startup focused tests: 90 passed.
- Final broker race suite passes in `internal/cmd`, `cmd/viiper`,
  `device/dualsense` and `internal/server/usb`; full broker suite also passes.
- Fresh compiled handoff evidence after the final application/updater rebuild:
  `artifacts/compiled-updater-handoff/compiled-handoff-a3c6619931c848379b04e5dd08ece4fa/compiled-updater-handoff.json`.
- Final combined DS4Windows suite: **7,092 passed, 12 opt-in tests skipped,
  0 failed**. Evidence:
  `isolated_results/portable-startup/edge-updater-startup-bounded-full.trx`.
- Related source checkpoints: updater `737618f`; broker Edge guard `0d93b1f`
  and startup diagnostics `eebc066`; application Edge guard `8c1f59f`.

## Delivery state

This is a source/test checkpoint, not a release or an installed runtime repair.
Updater 2.0.8 must be published before distributing an application that requires
its managed-update protocol. New broker startup phase diagnostics require the
matching updated VIIPER; older brokers retain a generic numeric exit reason.
Build/package verification must update the application's pinned broker hash
when the new broker binary is produced. No old package has been relabeled.

## Test boundaries

No installed app/broker restarts, physical output writes, driver mutations or
installer execution for this work. Unit/integration tests use fake processes,
temporary fixtures, disposable PowerShell helpers and loopback transport. Hardware fidelity remains a separate
acceptance gate; this document does not claim an Edge hardware pass.
