# Switch 2 runtime registration boundary

Status: dormant and offline-verified. One mixed-transport service now owns one
transaction core for Switch 2 Pro USB, standalone Switch 2 Bluetooth, and an
explicitly joined Joy-Con 2 pair. This boundary still has no `ControlService`,
`DS4Devices`, discovery, output, or hardware call site.

## Purpose

Physical Switch 2 transports do not own DS4Windows controller slots. A
transport produces validated, generation-bound canonical input; one runtime
device projects that input into the existing `DS4State`/`Report` mapping seam;
and the service owns slot admission, mapping callbacks, profile actions, and
removal. `InputControllerRegistrationTable` is the transaction boundary
between those owners.

The table deliberately does not identify devices by serial number, path, or
display name. Its authority is an exact tuple of private table issuer, service
generation, slot generation, `DS4Device` reference, logical runtime generation,
and registration-owner reference. This permits identical or deliberately
anonymous controllers without making a mutable identity string an ownership
credential.

## Attach transaction

`Switch2RuntimeRegistrationTransactionCore` executes these steps in order for
the participant selected by `Switch2RuntimeRegistrationService`. The older
`Switch2ProUsbRuntimeRegistrationCoordinator` remains a USB compatibility
facade over the same core rather than a second implementation:

1. Validate the exact owner-created, owner-authenticated
   `InputControllerRegistration`.
2. Atomically reserve and bind an available slot, retaining both the exact
   token and `SetupRollbackClaim`. A retained setup-publication epoch prevents
   service close from passing this operation while the table call itself runs
   outside the core lifecycle gate.
3. Ask the physical owner to adopt that exact bound token. The first distinct
   table/token wins and returns a private-fenced adoption credential. A
   competing table that loses adoption can roll back only its own Bound slot;
   it receives no authority to abort or prepare the winning physical owner.
4. Publish a binding which retains the exact setup rollback and owner-adoption
   credentials, then subscribe one exact `ReportHandler<EventArgs>` delegate.
   Complete all
   other external setup outside the table lock.
5. Prepare the physical owner: arm the runtime and start its worker parked
   behind an exact, single-use commit credential. Every fallible worker-start
   operation occurs here, and no native read/report is permitted yet.
6. Acquire a retained activation epoch under the coordinator gate, begin table
   activation, and acquire the table-issued activation-commit credential.
   Release all coordinator/table locks before invoking the fallible owner
   commit. Complete table activation only with that exact commit credential.
   Close waits for the retained activation epoch, so it cannot manufacture a
   retirement claim in the middle of this external commit transaction.

If table admission fails, the coordinator has received no ownership credential
and therefore does not mutate the supplied runtime owner. If table admission
succeeds but owner adoption loses, it rolls back the local table claim without
calling any owner cleanup method. This is essential when another coordinator
already owns that same physical lifetime. If any later step before table
activation fails, the coordinator boundedly aborts the provably unpublished
physical/runtime lifetime with the exact adoption or prepare credential,
unsubscribes that exact delegate, then rolls back the table and clears the
exact binding in one lifecycle-gated transaction. The abort path proves
zero native reads and zero reports and therefore neither publishes nor
acknowledges terminal neutral. A commit failure after table activation is an
invariant failure and quarantines the exact slot; it must not be disguised as
a Bound rollback. `TryCancel` is valid only before binding. A service close
revokes raw reservations, preserves bound setups for exact rollback, and moves
attached slots to `Retiring`.

## Report admission

`Switch2RuntimeInputDevice` reuses two preallocated, immutable
`Switch2RuntimeReportEventArgs` instances. Every event identifies either
`Regular` or `TerminalNeutral` and carries the device's exact nonzero runtime
generation. The coordinator rejects any sender, event type, report kind,
or runtime generation that does not match its token before calling the mapping
callback.

For an accepted regular event, the coordinator acquires a normal report lease,
calls the existing service mapping callback outside table locks, and disposes
the lease in `finally`. Retirement closes ordinary report admission before the
transport is stopped.

Before that regular `Report`, the runtime publishes its already decoded,
oriented, family-calibrated `DS4State.Motion` through the established
`DS4SixAxis.SixAccelMoved` event. Switch 2 runtimes are primary gyro sources,
so normal profile setup attaches the existing Gyro Controls, Gyro Mouse, Gyro
Mouse Joystick, and steering consumer. The runtime does not invoke the legacy
raw-report calibration a second time. One per-device borrowed event envelope
is refreshed in place, keeping the subscribed steady-state path allocation-
free. A terminal-neutral report never manufactures a motion event. A throwing
motion observer makes the regular publication fail visibly while the runtime
still invokes ordinary Report observers, releases its publication gate, and
retains terminal-neutral progress.

An exclusive action is not permission to discard a physical report. Before
requesting an action lease, the future orchestrator must suspend the exact
producer generation at a boundary which preserves its bounded transition
journal; after the lease is released it must resume or explicitly retire that
generation. The USB runtime owner now distinguishes temporary
`PublicationBusy` internally and waits boundedly before retrying the same exact
frame, so its outer Boolean sink returns `false` only for a terminal refusal.
Profile/action wiring still requires a synchronous adapter operation that owns
the runtime publication gate before acquiring the table action lease, releases
the table lease before reopening publication, and never falls back to an
asynchronously queued action.

For the one terminal event, the coordinator acquires the retirement claim's
terminal report lease, calls the same mapping callback with the neutral
`DS4State`, and acknowledges terminal neutral only after that callback returns
successfully. Event publication is not itself service acknowledgement. The
lease is then disposed, allowing the retirement drain to complete.

## Stop and removal transaction

The coordinator uses one outer monotonic deadline:

1. `TryBeginRetire` before asking the owner to stop. This closes ordinary
   report and new action admission.
2. Wait for every already-admitted ordinary report/action lease to drain, then
   wait for the runtime publication tail to commit after the mapping handler
   returns. Terminal admission is deliberately exclusive and rejects while
   any lease remains. The second boundary is required because the runtime
   retains its publication gate while it drains post-handler actions and
   commits state.
3. Cancel and drain the exact physical input generation.
4. Publish exactly one typed terminal-neutral report.
5. Wait again for the acknowledged terminal report lease to drain.
6. `TryMarkQuiesced` only after terminal acknowledgement and both drains.
7. Ask the exact registration owner to remove its runtime objects without
   blocking.
8. Only after external removal succeeds, perform `TryCompleteRemoval` and
   clear the exact token/reference-matched binding in one lifecycle-gated
   transaction. A new attach therefore cannot observe a reusable table slot
   while the prior binding is still installed.

A removal request made reentrantly from the exact report/terminal callback
must fail boundedly rather than waiting on its own lease. A service-close
snapshot may supply the existing exact retirement claim; it must not begin a
second retirement lifetime.

Callback admission and removal/close ownership are atomic per binding. A
cross-thread removal request made while a mapping callback is active is also
rejected immediately; this prevents a callback from waiting on a task whose
removal is itself waiting for that callback's lease.

A timeout, owner exception/authentication loss, missing terminal
acknowledgement, drain failure, or uncertain removal quarantines that exact
slot lifetime. Quarantine persists across later service generations; healthy
slots remain reusable. The service must never infer successful teardown from a
missing controller path or a disconnected transport.

After `TryClose` has successfully closed the table, the coordinator retains an
exact service-generation close epoch containing immutable slot snapshots and
binding identities. One teardown owner advances that epoch. A short observer
timeout releases only execution ownership, not close intent or teardown state;
the same-generation retry joins or resumes the epoch and receives its cached
terminal result. `TryOpen` remains fenced while an epoch is incomplete, so a
new service generation cannot overtake old teardown.

## Automatic lifecycle attention

The owner exposes one preallocated, immutable, exact-generation lifecycle
attention envelope. The first malformed or cross-generation input rejection,
mapping-subscriber rejection, or native read/retirement failure wins; later
sources are coalesced. Pump and owner callbacks run outside their locks and the
binding only queues service-owned retirement, so the pump worker never joins
itself. Automatic retirement waits until the attach transaction has completed:
a successful setup proceeds through normal retirement, while failed setup or
post-commit quarantine retains ownership of its own result.

This wake-up is not teardown evidence. The coordinator still performs
`TryBeginRetire`, both drains, owner stop and typed terminal publication,
terminal acknowledgement, exact delegate removal, owner removal, and table
completion. Failure at any stage quarantines the exact token.

## Proven offline properties

- Cross-table, copied, default, stale, and ABA credentials fail closed.
- Service, slot, report-lease, and action-lease counters never wrap.
- Report leases use fixed per-slot cells and allocate no managed memory after
  construction.
- Exclusive actions close report admission and drain existing reports with a
  bounded wait.
- Close versus activate, close versus action wait, concurrent anonymous
  registration, hostile owner callbacks, and per-slot quarantine are covered
  by deterministic tests.
- The typed regular and terminal report paths use preallocated envelopes and
  allocate zero bytes after warm-up.
- Projected Pro, standalone Joy-Con 2, and joined Joy-Con 2 motion reaches the
  existing gyro mapping event before the same regular Report; the subscribed
  steady-state path also allocates zero bytes after warm-up.
- Bind plus binding insertion and Attached plus parked-worker commit are each
  serialized against close by retained setup/activation epochs and the narrow
  coordinator lifecycle gate; fallible owner operations execute outside it.
- Two independent tables racing to attach the same physical owner have exactly
  one adoption winner. The loser removes its own Bound slot without aborting
  the winner.
- USB, standalone Bluetooth, and joined Joy-Con participants can attach in
  parallel to one table and coexist in `Attached`. One service close retires
  every exact owner through the same core and observes one terminal neutral and
  one removal for each lifetime.
- The mixed service owns no second gate, table, mapping callback, or transport
  lifecycle. Participant factories, participant methods, and mapping callbacks
  are characterized outside the core lifecycle gate.
- A service-close observer timeout after table closure preserves an exact
  resumable close epoch; a new open cannot overtake it.
- Deterministic barriers cover close during parked prepare, attention arriving
  before commit returns, active-callback close/removal, and the runtime
  post-handler publication tail.

These properties do not establish live controller compatibility, application
visibility, latency, reconnect behavior, or safe haptic output. Production
wiring and hardware validation remain separate gates.
