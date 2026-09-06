# Legacy Joy-Con report ownership audit, 2026-09-02

This is a source-level audit of `InputDevices.JoyConDevice` (the original
Joy-Con path), not the separate Switch 2 runtime. It is not hardware latency
evidence. The priority is correct, prompt delivery through the existing mapper
and VIIPER transport, without introducing a second mapping stack.

## Narrow peer-association fix

`JoyConDevice.JointDevice` now publishes/reads its peer reference with volatile
semantics. `JointDeviceSlotNumber` captures that reference once, so removal
cannot null the field between a check and a second dereference. The secondary
`ControlService.On_Report` path also uses its captured slot for gyro-mode lookup.

All six pairing-removal callbacks use `TryDetachJointDevice(expectedPeer)`.
Its compare/exchange removes only the expected peer object. A late old-peer
removal cannot unlink a different successor, including one at the same numeric
slot. This is not a pair-generation lease: reuse of the same object in a later
pairing is not distinguished, and a captured slot is not publication authority.

`JoyConPeerAssociationTests` verifies exact/idempotent removal, null rejection,
same-slot successor preservation, concurrent lookup/detach/replacement, and
zero warmed allocations for peer access. The initial focused run passed 4/4.

## Captured Mouse callback lifetime

`ControlServiceMouseCallbackSubscription` owns the exact Mouse, its logical
slot, physical event source and original touch/gyro event surfaces. Each owner
also has a cold monotonic generation. The unique wrapper object is the callback
capability; it cannot be made valid again after retirement.

All ten existing event subscriptions are wrapped, including both occurrences
of `TouchStartedOrEnded` and `PreTouchProcess`. The latter now targets its
captured Mouse instead of looking up whichever Mouse later occupies the slot.

An atomic word combines callback admission count and a retired bit. Retirement
closes admission before exact unsubscription, then waits for already-admitted
Mouse invocations to finish before reset/replacement. A multicast delegate
captured before unsubscription is therefore still rejected if invoked later.
This closes the gap that a per-operation mapping epoch alone cannot close:
an obsolete Mouse entering after reset could otherwise acquire a fresh epoch.

The registry has separate exact-source retirement and exact-Mouse replacement
operations. Removing the logical owner also retires its surviving peer's gyro
subscription; removing an obsolete source does not retire a different source
or Mouse that has reused the slot. An undrained owner remains a tombstone and
replacement/slot reuse cannot report success.

After a successful exact-owner drain, the registry resets the owner's logical
post-map accumulator before relinquishing that incarnation. This matters when
a removed secondary supplied gyro to a surviving primary: resetting only the
secondary's physical slot would let its stronger old contribution suppress a
weaker new peer. A replayed stale retirement cannot reset a successor.

Ordinary wrapper admission/exit uses atomics, not a monitor, timer or worker.
Mouse code runs outside internal gates. The final retiring callback can signal
the cold drain waiter. Lifecycle entry points reject reentrant callback calls
before taking the service lifecycle lock, avoiding a closer/callback inversion.
The drain timeout is not a hard bound for acquiring all surrounding lifecycle
locks, nor a claim about Windows scheduler or input latency tails.

This guard is narrower than whole-report serialization. It does not stabilize
raw `JointState`, shared `MappedState`, concurrent same-Mouse profile edits, or
the separate Switch 2 runtime's existing ownership machinery.

Final central verification: 47 focused tests passed, then the full suite passed
2,815 tests with 3 live-audio tests skipped (0 failures). Five additional runs
of the 14 callback tests plus 4 peer-association tests passed, 90 executions in
total. This verifies software behavior, not live latency or full feature parity.

## Whole-report race remains open

The following production paths still require a coherent ownership change:

- `JoyConDevice.MergeStateData` locks a different `ReaderWriterLockSlim` on
  each physical device while both mutate the same `JointState`. The physical
  decoder itself does not use that lock for all source-state writes.
- `ControlService.On_Report` maps that shared raw state. The secondary gyro
  branch separately mutates and submits the primary's `MappedState` while the
  primary can be mapping/submitting it.
- `ViiperOutDevice.ConvertandSendReport` converts the supplied mutable state
  before enqueueing a value report. Its downstream scheduler cannot repair
  torn source data or determine that an older primary snapshot, submitted by
  the secondary later, should not replace a newer primary report.
- `JoyConDevice` uses the legacy untyped Report subscription. The exact
  DS4/DS3 worker-lifecycle authority does not cover this subtype.

Making a copy immediately before output is insufficient. A secondary can copy
primary report A, pause, then submit A after the primary has already submitted
newer report B. Locking only the final encoder is also insufficient.

Motion requires particular care: `DS4State.CopyTo` aliases `Motion`.
`SixAxis.copy` is not an exact scalar snapshot: it omits some full gyro fields,
changes output-acceleration values, and aliases `previousAxis`. The physical
input history now uses an exact bounded `DS4StateOwnedSnapshot` instead, and
UDP observation has independent scratch; see `owned-motion-observations.md`.
The shared pair merge/publication path is still not serialized by those fixes.
Do not recursively clone a previous-motion graph or assume a shallow copy
creates ownership.

## Next cohesive implementation boundary

Use a pair-owned, fixed-capacity input journal and an inline single-drainer
claim, following the existing Switch 2 runtime's publication-ownership pattern
without borrowing its protocol identities or creating another mapping stack.

1. Capture an exact source/pair lifetime and owned physical snapshot before
   paired callbacks. Snapshot all relevant motion scalars; previous motion is
   maintained by the pair owner in a bounded separate slot.
2. Enqueue both halves in admission order. An idle producer drains immediately;
   another producer only hands off its snapshot. Do not wait for the next
   primary report to present a secondary control or gyro update.
3. Keep one drainer's ownership across merge, selected gyro callback, canonical
   mapping and virtual submission. Run callbacks and output outside the queue
   gate. Merge against already-processed side caches, not future queued states.
4. Remove the competing secondary mapped-state submission path for bound
   pairs. Preserve button press/release transitions rather than replacing
   edge-bearing queued records with a latest-value snapshot.
5. Retire/neutralize explicitly on overflow or lifecycle failure. Reject stale
   pair/connection receipts and do not let a successor reuse views while an
   older drainer is still active. No new polling timer or worker is needed.

Reusable source patterns are `Switch2BluetoothInputOwner.DrainOne` and
`Switch2RuntimeInputDevice.TryReserveStagingNoLock` /
`InvokeAndCommitPublication`. Neither is a drop-in generic component. The
existing joined Switch 2 sink uses protocol-specific serialization, and the
Bluetooth drain pump creates a worker; neither should simply be grafted onto
legacy Joy-Cons.

Required integration tests must force paused-primary/secondary interleavings,
secondary-only button edges, source mutation after enqueue, asymmetric motion
values, re-pair/slot reuse, exact callback retirement, overflow and terminal
neutralization. Source tests do not establish real Windows/game presentation
latency, contention tails or hardware behavior.

Other legacy lifecycle limitations remain separate, including queued companion
Bluetooth disconnects and raw-state reset during removal. They must participate
in the same exact-pair retirement contract before whole-report safety is claimed.

### Activation, cleanup and profile boundaries confirmed by follow-up audit

- Initial attach and hotplug publish peer links/shared `JointState` before the
  second slot/profile/Mouse is prepared, while the first reader may be running.
  A new journal must not activate until all exact participants are prepared and
  predecessor solo/pair dispatch has drained.
- The constructor-installed removal callback executes before service removal
  and clears `getCurrentStateRef()` (shared state in joined mode). Retirement
  must replace that mutation, not happen after it.
- Raw SixAxis currently precedes Report; merely queueing Report leaves a
  parallel Mouse producer. Bound-pair gyro must run under the same drainer.
- `HaltReportingRunAction` gates only one physical reader, while the event
  queue invokes callbacks under its own lock. Pair-owned cold-action admission
  must serialize the existing profile action with both producers and drainer.
  Global/UI profile writes outside that boundary remain separately auditable.
- The drainer must own the complete merged mapping and virtual submission,
  then ordered UDP observation. Independent UDP scratch removes mutation of
  source motion but does not order an observer with a queued pair report.
- Companion disconnect must carry and revalidate an exact pair/connection
  receipt at execution under the retirement contract. Checking only a peer
  object or numeric slot does not protect same-object re-pairing.
