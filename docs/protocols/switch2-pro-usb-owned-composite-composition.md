# Switch 2 Pro USB owned-composite composition boundary

Status: **historical composition audit, now production-wired and
offline-tested**. `Switch2ProUsbProductionCoordinator` selects the owned
MI_00/MI_01 transport, registration participant, and canonical feedback owner
through `ControlService`. No hardware I/O was run for the current revision.

Update: the input-adoption, feedback-activation, exact feature/LED response,
sole-handle transport, and shared-registration prerequisites identified by this
audit are now composed by the production participant documented in
`switch2-pro-usb-owned-composite-registration-participant.md`. The executable
slice commits feedback first and quarantines any split outcome. The historical
analysis below is retained to explain those design constraints.

## Audit result

At the time of this audit, the pieces could not be joined safely by adding a
coordinator alone. The prerequisite ownership and retirement work described
below was completed before the production coordinator was added.

The existing Windows adapter already has a valuable ownership invariant: one
opaque container reservation encloses its MI_00 HID input handle and MI_01
WinUSB presence handle. However, the escaped lease is intentionally read-only.
MI_00 is opened for `GENERIC_READ` with a share mode that specifically excludes
a coexisting output-capable HID handle. `ISwitch2ProUsbWindowsPresenceHandle`
exposes only `Dispose`, and MI_01 is also opened with read-only desired access.
`ISwitch2ProUsbReadOnlyCompositeLease` explicitly exposes no command, feature,
LED, or haptic operation. Opening a second MI_00 haptic writer or a second
MI_01 command owner would therefore defeat the exact single-owner proof even if
numeric generations happened to match.

The dormant startup transaction and direct haptic writer also have different
lifecycle strengths:

- `Switch2ProUsbStartupTransaction` accepts caller-supplied command and
  retirement bounds, exact claims, and exact generation-bound response proof.
- `Switch2ProUsbHdRumblePhysicalWriter` still consumes its narrow synchronous
  transport contract, but the dormant owned bridge now supplies that contract
  from the adopted output lane and retains exact uncertain-operation evidence.
- `Switch2HdRumbleDeliverySink.TryRetire` correctly refuses retirement until a
  terminal Stop is delivered and uncertainty is resolved, but it cannot prove
  that a stalled physical call will return within the registration teardown
  deadline.

That last point was a hard lifecycle blocker, not merely missing plumbing. The
dormant feedback lifetime now seals producers, drains retained output, delivers
an exact canonical Stop, and retires pump then sink before authenticating a
fresh bridge revision. It truthfully budgets managed native-quiescence waits;
synchronous Win32 calls still do not have a hard wall-clock bound.

## New offline contract

`Switch2ProUsbOwnedCompositeContract.cs` records the minimum stronger seam:

1. `ISwitch2ProUsbOwnedCompositeNativeAdapter` must acquire one object for one
   admitted registration and exact `Switch2PhysicalInputLifetime`.
2. That one object implements `ISwitch2ProUsbOwnedCompositeLease`, so the input
   lease, startup lease, and bounded output lease are reference-identical.
3. Output consumes the report before returning and applies an explicit
   cumulative managed quiescence-wait budget. Completion proves exact
   quiescence; budget expiry retains an exact operation claim, buffer, native
   storage, full lease, and replacement-write fence until explicit retirement.
   Synchronous Win32 begin/cancel/free/close calls have no hard deadline.
4. `Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit` validates the exact
   registration, lifetime, Pro/USB model, both generations, pure lease
   authentication, and a positive output-operation ceiling no greater than the
   existing USB lifecycle ceiling.
5. The admitted bundle issues one opaque authority. Matching numeric
   generations from a second bundle do not authenticate it, and all three
   facet accessors return the same object.
6. `ISwitch2ProUsbOwnedFeedbackLifetime` describes the bounded terminal hook.
   The dormant canonical implementation proves new feedback is sealed, the
   exact neutral report completed, and no output operation remains in flight.
   An incomplete or uncertain result cannot authorize command retirement or
   input disposal.
7. Its activation extension must issue a one-shot dormant-quiescence proof
   bound to the exact authority/lifetime before composition. `Dormant` state
   alone is not proof of neutral output or absence of queued/in-flight writes.

These types now have one production callsite in
`Switch2ProUsbProductionCoordinator`. The bundle itself remains an admission
shape rather than a native opener or registration coordinator. It
proves admission shape only: it does not itself enforce startup-before-input,
neutral-before-retirement, or lifecycle state transitions.

Registration, lifetime, output bound, and authentication facts are required to
be immutable through exact retirement. Bundle accessors revalidate those pure
facts and fail closed if they change or throw before a view escapes. That check
cannot revoke a view already handed to the sole authority owner; the future
coordinator must retain the authority and treat any later authentication loss
as a full-composite quarantine condition.

## Required lifecycle order

A future registration-participant decorator must use the shared registration
transaction and preserve this order. Calls to dependencies must remain outside
the decorator's private gate.

### Activation

1. Admit one owned composite and take its sole authority.
2. Adopt the exact slot and subscribe the exact shared-service callbacks.
3. During `TryPrepareActivation`, execute the five audited volatile startup
   steps to `Completed` with one overall bounded deadline retained through
   both prepares, both commits, and any split-commit recovery.
4. Only after startup is complete prepare the credentialed feedback lifetime;
   publication remains sealed.
5. Prepare the existing USB participant. The existing owner proves its worker
   is parked with zero reads at this boundary.
6. Commit feedback while input remains parked.
7. Commit the existing participant with the exact table credential. Only after
   both exact commits succeed may the transaction report activation success.

This is the smallest seam that establishes startup-before-input. Running
startup after commit is invalid because the worker may begin its first read as
soon as the commit gate opens. The two commit operations are not claimed to be
atomic: any split result is retained/quarantined, never rolled back or replaced.
Commit budgets reserve part of the remaining deadline for cleanup. A
dependency that returns after its explicit bound is a contract violation; if
feedback has already opened, quarantine prevents replacement but cannot prove
terminal neutral. A bounded terminal-attention owner or shared cross-facet
gate remains required before production wiring.

### Abort and normal removal

1. Seal feedback publication for the exact authority.
2. Boundedly deliver the terminal neutral and prove output quiescence.
3. Retire the exact startup/command lease with its retained typed retirement
   claim. A proven-not-released result may retry the same claim; an uncertain
   result quarantines the full composite.
4. Only after steps 1-3 succeed call the existing participant's abort or
   `TryStopAndQuiesce`. That inner call retires the mediated MI_00 input facet;
   it deliberately cannot dispose the raw shared composite.
5. Consume the exact runtime-retirement proof and dispose the whole composite
   once.
6. Unsubscribe and remove through the existing shared transaction.

If neutralization, output quiescence, or command retirement is unproven, retain
the exact full composite and quarantine it. Do not dispose its input facet and
do not admit a replacement physical owner.

## Remaining production evidence

The one-MI_00/one-MI_01 transport, exact response validation, canonical
writer/bridge/feedback lifetime, shared registration transaction, terminal
fence, and cleanup ledger are now production-composed. The managed wait budget
is explicit: synchronous Win32 begin/cancel/free/close calls are not falsely
described as hard-deadline APIs. Authorized hardware validation remains needed
for input cadence, disconnect/reconnect, LED state, terminal neutral, and
physical haptic delivery.

No cadence or background timer is implied. The 0x27 startup feature mask is a
volatile mode request, not proof of 500 Hz input. The 64-byte haptic report is a
framing fact, not proof of delivery latency or physical onset.

## Hardware verifier relationship

The separate `Switch2UsbHardwareVerify` utility is a test owner, not a facet of
the production lifetime. It must run only when no DS4Windows runtime owner holds
the controller. It cannot validate this composition by opening the same MI_00
or MI_01 interfaces concurrently.

After the production hooks and offline lifecycle tests exist, an authorized
hardware run may measure exact host input completions and documented LED
operations. Nonzero haptics remain gated on exact neutralization and bounded
teardown proof. Any measured host completion rate must be reported as a
measurement for that run, never as a 500 Hz implementation claim.

## Offline evidence

`Switch2ProUsbOwnedCompositeContractTests` covers:

- one concurrent authority winner;
- reference-identical input/startup/output facets;
- foreign-bundle rejection despite equal generations;
- registration, lifetime, authentication, and operation-bound rejection;
- fail-closed dependency exceptions;
- no acquisition, startup, read, output, retirement, disposal, or quiescence
  call during admission;
- mutable or throwing candidate facts rejected before a facet escapes;
- exact-generation terminal-result invariants; and
- negative characterization that the live read-only adapter and production
  registration path have not silently crossed this boundary, while the
  dormant owned transport and feedback lifetime remain internal.

The separate `Switch2ProUsbWindowsOwnedCompositeAdapterTests` and legacy
`Switch2ProUsbWindowsAdapterTests` cover the dormant transport mechanics and
cleanup/retirement adversaries listed in
`switch2-pro-usb-windows-owned-composite-transport.md`.

`Switch2ProUsbOwnedFeedbackActivationLifetimeTests` covers never-started
one-shot output adoption, no-output Prepare/Abort, committed zero-state Stop,
retained non-Stop and retained Stop drain/retry ordering, bounded retry,
producer sealing, concurrency/reentrancy, issuer-bound terminal evidence,
quarantine, and the warmed allocation-free manual idle path.
