# Switch 2 ControlService reversible profile staging and gyro dispatch

Status, 2026-09-02: source-wired production host, software integration verified.
`ControlService` constructs `Switch2ControlServiceReversibleProfileSlotHost`
with `Switch2ControlServiceProfileStage` and its existing report pipeline.
The latest change connects the real Mouse gyro producer inside admitted report
dispatch. This is not a hardware, game-compatibility or latency certification.

## What is admitted

The stages retain exact inverses for registration-owned resources. Loading
user profile configuration is not described as a global configuration rollback.

| Mutation | Admitted now | Exact inverse |
| --- | --- | --- |
| `DS4Controllers[token.Slot]` | Yes | Clear only while it still references the exact runtime device. |
| `Switch2RuntimeInputDevice.DeviceSlotNumber` | Yes | Restore the retained prior value only while the exact array occupant is still installed. The sealed runtime type has no subtype slot-change hook. |
| `ControllerSlotManager.ControllerColl` | Yes | Remove the one retained device reference only after exact identity/count proof. |
| `ControllerSlotManager.ControllerDict` | Yes | Remove only the exact `slot -> device` mapping. |
| `ControllerSlotManager.ReverseControllerDict` | Yes | Remove only the exact `device -> slot` mapping. |
| `ControlService.touchPad[token.Slot]` | Yes, as a separately composable exact stage | Construct before mutation; clear only while it still references the exact retained `Mouse`, the table epoch is `Bound`/`Quiesced`, and the exact runtime device remains installed. |
| Mouse gyro callback ownership | Yes, direct publication mode | Close exact callback admission and prove it drained before profile reset; no raw SixAxis subscription. |
| Existing `ControlService.On_Report` method group | Yes | Invoke synchronously after gyro with the original `Switch2RuntimeReportEventArgs`; no report wrapper, second handler, queue, or mapper is created. |
| Registration-owned profile/output setup | Yes, through the ControlService facet | Retain exact output and prior output-selection/lightbar state; inverse rejects changed ownership and retains uncertain cleanup for retry. This does not revert user configuration. |

The three slot-manager indexes are changed under the manager's existing write
lock by a tokenized transaction. The prerequisite deliberately does not call
`ControllerSlotManager.AddController` or `RemoveController`: `AddController`
can fail after changing only a prefix of its three collections, and
`RemoveController` does not verify that all three entries still identify the
same lifetime. Each admitted mutation has an individual retained flag, so an
inverse can resume after a partial failure. Before the first inverse mutation,
all still-owned values are compared with the retained device, slot, and table
epoch. A mismatch leaves every value untouched and retains the inverse for
attention or retry.

## Table authority and lock order

The host receives the same `InputControllerRegistrationTable` used by the
registration transaction. It never derives authority from a MAC address,
runtime generation, or slot number.

Prepare holds the registration service's shared `LifecycleGate` and calls the
allocation-free `TryAuthenticateBoundExternalStage`. That table method proves
the private table issuer, complete registration, service generation, slot
generation, current open service epoch, and `Bound` state before any external
array change. Cleanup similarly proves the exact retained entry in `Bound`
(abort) or `Quiesced` (remove), including after service admission closes. The
one terminal-neutral dispatch separately proves the exact `Retiring` table
entry; ordinary reports rely on their existing allocation-free table report
lease and do not add a second table lock.

The lock order is:

1. Switch 2 registration service lifecycle gate (also supplied to the host);
2. registration-table gate for a pure, non-callback proof;
3. `ControllerSlotManager.CollectionLocker` for the exact collection
   transaction.

The table authentication methods invoke no external code, and the table never
acquires the host gate. Profile facet calls occur under the lifecycle
gate and are contractually forbidden from acquiring those gates out of order.
The existing mapping pipeline runs outside all three gates, as ordinary
`On_Report` processing does.

## Prepare, dispatch, and retirement

Prepare first installs the exact reversible slot subset, then the exact Mouse
slot, then asks the profile facet to prepare. After success it retains and
activates an exact direct gyro callback owner before marking the host prepared. A
successful or outcome-uncertain facet must return
an authenticated inverse for that exact token. A clean rejection must return
no inverse. `ProvenRejected` plus an inverse is contradictory evidence: the
host does not invoke that alleged inverse, retains the slot, and reports an
uncertain outcome so the registration slot cannot be reused.

If a later prepare step rejects cleanly, inverses run in reverse order. If an
inverse rejects, throws, or becomes uncertain, earlier inverses remain retained
and the exact slot remains occupied. A retry continues from the retained
one-shot records. An unknown mutation without an inverse is terminal attention,
not a false rollback.

Regular and terminal reports use the construction-time existing-pipeline
delegate. The participant/table report lease remains the authority for report
timing; the host adds no second table lookup or allocation to the regular-report
path. Sender reference, runtime generation, slot token, retained occupant, and
callback exclusivity are checked before the direct method-group call. A
terminal-neutral envelope must succeed through that same pipeline before
`TryRemove` will run the inverses. Remaining-profile inverse precedes Mouse
inverse, which precedes the slot-collection inverse; table completion occurs
only after the host reports successful removal.

## Same-report gyro without another scheduling hop

The runtime's raw SixAxis event runs before registration acquires its report
lease. Subscribing Mouse there would permit mouse output from a report that
registration subsequently rejects. Instead, `Binding.HandleReport` acquires
the existing lease and invokes this host; the host borrows the current state,
calls the exact Mouse's `sixaxisMoved`, and then calls the existing mapper once.
USB Pro, BLE Pro and joined/fused Joy-Con reports use the same path.

`TryBorrowCurrentPublication` accepts only the publishing thread inside actual
Report callbacks with the correct cached regular/terminal envelope. Raw gyro,
queued actions, `HaltReportingRunAction`, fabricated envelopes and other threads
cannot borrow the state. The cached envelope is not a unique per-report ID:
authority comes from current callback scope plus the existing registration
lease. No new table acquisition, timer, worker, raw subscription, allocation or
per-report log is introduced. Existing host entry/exit lifecycle gates remain;
gyro and mapping callbacks execute outside those gates.

Terminal dispatch closes direct gyro admission and clears pending stick gyro,
active flags and current swipe state before mapping neutral. Prior swipe flags
remain available for release and retry. Runtime terminal reservation already
stops high-rate mouse presentation, so terminal preparation emits no new cursor
output. A valid regular report without motion clears transient output but
preserves the user's gyro-toggle latch; invalid publication is rejected instead
of being treated as no motion.

Cleanup closes and drains the exact gyro owner before profile Undo. It does not
wait for callbacks under the lifecycle gate. Once cleanup starts, the host is
no longer prepared, even if an inverse needs retry. Every profile Undo attempt
revalidates the exact Mouse, including retries after gyro ownership has already
been retired. A replacement Mouse cannot be reset by an old profile inverse.

## Deterministic coverage

`Switch2ControlServiceReversibleProfileStagingTests` proves:

- copied exact lease idempotence and foreign/stale table rejection;
- prior `DeviceSlotNumber` restoration and all-three manager-index symmetry;
- same-slot newer-generation fencing and no cleanup of a newer array occupant;
- clean later-step rollback, retained partial inverse retry, and fail-closed
  unknown mutation;
- rejection of contradictory proven-rejection/inverse evidence without
  invoking the alleged inverse;
- synchronous regular and terminal delivery of the exact report object, with
  rejection if the retained Mouse occupant has been replaced;
- terminal-before-remove enforcement independent of table acknowledgement;
- reentrant and concurrent callback rejection without a second dispatch;
- zero managed allocation in the host's warmed regular-report path; and
- exact Mouse construction before mutation, factory-failure rejection, one-shot
  undo, and preservation of a newer per-slot occupant.

`Switch2ProductionGyroMappingIntegrationTests` uses actual registration owners,
the shared production lifecycle gate, real Mouse, raw Common05 decoding,
canonical profile mapping and Xbox/Switch encoders. OS transports and profile
persistence are replaced with in-memory test adapters. Its 12 cases cover
same-report activation/release on USB, BLE and joined input, independent stick
precision, terminal pending-gyro/swipe release, raw-event isolation, table
retirement before runtime stop and same-slot successor isolation.

`Switch2RuntimePublicationBorrowTests` and `MouseCallbackLifetimeTests` cover
callback-phase/thread authority, exceptions, partial subscription retry,
direct-mode retirement, no-motion/terminal release semantics and warmed
zero-allocation publication. Repeating a cached synthetic frame in allocation
tests does not measure physical cadence or hardware latency.

Final software verification: 56 focused tests; five further repetitions of all
56; full Release x64 suite **2,838 passed, 3 opt-in live-audio tests skipped,
0 failed**. No installed application, driver or controller configuration was
changed for these tests.
