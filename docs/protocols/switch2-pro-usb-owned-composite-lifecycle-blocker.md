# Switch 2 Pro USB owned-composite lifecycle composition blocker

Status: **historical blocker audit, superseded by the production-owned
composition**. The original tranche added only negative characterization. The
later participant, transport, feedback lifetime, and coordinator close the
managed contract gaps and are now selected through `ControlService`; this
document retains the reasoning that shaped those boundaries.

Update: this historical blocker drove the later prerequisite and dormant
registration-participant slices. Exact input adoption and a credentialed
feedback contract now exist, and their offline composition is documented in
`switch2-pro-usb-owned-composite-registration-participant.md`. One production
path now constructs the owned Windows native lease and canonical feedback
activation/neutralization lifetime. There is still no cross-facet atomic commit
claim; a split outcome is retained and quarantined instead of being described
as rollback. Transport mechanics are
documented in
`switch2-pro-usb-windows-owned-composite-transport.md`.

## Decision

A new-only lifecycle decorator could mechanically call the startup transaction
before the existing participant's `TryPrepareActivation`, and it could call the
terminal feedback hook plus command retirement before the participant's
`TryStopAndQuiesce`. That call order alone is not sufficient proof.

The current interfaces leave two authority gaps. Filling either gap inside a
decorator by comparing numeric generations would allow a different same-
generation lifetime to impersonate the physical owner. The safe decision is to
leave composition dormant until the producer of each dependency issues an
exact, opaque adoption credential.

## Historical gap 1: inner-participant input adoption

`Switch2ProUsbOwnedCompositeLeaseBundle` can issue one exact authority and can
return its reference-identical input, startup, and bounded-output views. The
existing `Switch2ProUsbRuntimeOwner.TryCreate` does not consume that bundle or
authority. It receives `ISwitch2ProUsbNativeAdapter`, whose open operation takes
only `Switch2PhysicalInputRegistration` and returns a read-only lease.

After creation, `Switch2ProUsbRuntimeRegistrationParticipant` exposes an
`InputControllerRegistration`, but neither it nor `Switch2ProUsbRuntimeOwner`
can authenticate `Switch2ProUsbOwnedCompositeAuthority`. The participant's
only constructor takes the runtime owner. Consequently, a decorator cannot
distinguish these cases:

1. the inner runtime owner consumed the exact input view from its bundle; or
2. an unrelated runtime owner has coincidentally equal device and transport
   generations and registration-shaped metadata.

Both would pass numeric checks. Only the first may share startup/output
authority. The later adoption issuer/credential closes this process-local
managed-object gap for the dormant participant. A dormant sole-handle Windows
acquisition boundary now exists, but is not registered by the live production
path.

### Input-adoption hook now used offline

The native handoff which gives the exact bundle input view to the runtime owner
must mint a one-shot opaque credential containing, without exposing identity:

- the issuing bundle/lease fence;
- the exact bundle authority;
- the exact `Switch2PhysicalInputLifetime` including both generations;
- the exact runtime owner/registration which consumed the input view; and
- a one-time consumption sequence.

The runtime owner or USB registration participant must authenticate that
credential purely, by reference and generation, before a lifecycle decorator
can be created. A failed runtime-owner construction must either return exact
proof that the composite was quiesced/disposed or return a retained quarantine
owner; it must never leave the bundle independently reusable.

The later hook is placed at that native-lease-to-runtime-owner handoff and is
consumed before the decorator escapes. It is not reconstructed afterward from
numeric generations.

## Historical gap 2: feedback activation credential

The base `ISwitch2ProUsbOwnedFeedbackLifetime` still provides only
exact-authority authentication and bounded terminal neutral/quiescence. The
later `ISwitch2ProUsbOwnedFeedbackActivationLifetime` extension adds the
prepare/commit/abort credential shape required by dormant composition. One
internal manually driven implementation now composes the canonical pump/sink,
physical writer, and owned bridge. It has no production constructor or cadence
worker.

A caller could retain another reference to the feedback producer and submit an
output before or during startup. A decorator which merely stores the terminal
hook cannot disprove that. It also cannot make feedback publication eligible
atomically after the existing participant's exact successful commit.

### Feedback-activation hook now implemented offline

The sole owner of the existing canonical state-lane pump and delivery sink must
provide a credentialed lifecycle with these semantics:

1. `TryTakeDormantQuiescenceProof(authority)` first transfers the exact dormant
   lifetime and proves neutral/no queued-or-in-flight output; `Prepare` must
   authenticate that proof, keep delivery sealed, and return an exact one-shot
   prepare credential.
2. Only after the inner participant commit succeeds may
   `CommitPrepared(credential)` expose the existing canonical lanes.
3. `AbortPrepared` proves the producer remained sealed and quiescent without
   emitting a non-required physical report.
4. `TryNeutralizeAndQuiesce` atomically seals new publications, resolves the
   exact terminal neutral under one overall deadline, and proves no writer call
   or callback can occur later.
5. Proven-incomplete terminal evidence is not retried unless it includes an
   exact retained operation claim which proves the prior attempt consumed
   nothing. Outcome uncertainty quarantines and retains the full composite.

The dormant implementation structurally adopts a never-started owned output
lane into a private narrow bridge capability. After adoption, copied full-lease
aliases reject output, retirement, and claim probes before native state changes.
Commit alone opens lane creation/manual pumping. Committed neutralization seals
all producers, drains an exact retained bridge operation before the canonical
Stop, records exact Stop delivery, retires pump then sink, and authenticates a
fresh no-retained bridge revision. Adding a second feedback queue or mapping
path remains unacceptable. The exact offline mechanism is documented in
`switch2-pro-usb-owned-feedback-activation-lifetime.md`.

## Ordering made executable by the dormant participant

The existing shared registration transaction already has the right outer
linearization points. A future decorator can remain one participant inside that
transaction and use one overall deadline.

Activation:

1. authenticate input-adoption and feedback-preparation credentials;
2. adopt the table slot and forward the exact callbacks unchanged;
3. advance the existing five-step startup transaction;
4. retry only its exact `ProvenNotConsumed` claim, within a bounded attempt and
   deadline budget;
5. call the inner participant prepare only after startup reaches `Completed`;
6. commit feedback while input remains parked; and
7. commit the inner participant with the exact table credential.

The later participant chooses feedback-first because input commit may release
the worker immediately. It reports success only after both commits; a split
commit is retained/quarantined and is not called rollback. A shared atomic
cross-facet publication gate or separately owned bounded terminal-attention
cleanup path remains a production blocker. Contract-conforming commit calls
share one retained deadline and reserve recovery budget. A dependency that
violates its bound can nevertheless return after feedback has opened and after
that deadline; quarantine prevents replacement but is not terminal-neutral
proof.

Abort/removal:

1. seal and exactly neutralize/quiesce feedback;
2. retire the startup command lease;
3. retry only the transaction's retained exact `ProvenNotReleased` claim;
4. call inner abort/stop only after command retirement is exact; and
5. delegate unsubscribe/remove to the shared transaction.

Any malformed, thrown, wrong-authority, wrong-generation, timed-out, or
outcome-uncertain dependency result retains the composite and requires
quarantine. External calls must run outside the decorator's private gate.
Reentrancy sees `OperationAlreadyInProgress`; no task, timer, cadence worker, or
sleep is required.

## Report-path constraint

Lifecycle composition does not require a report wrapper. The existing
`Switch2RuntimeRegistrationCallbacks` carrier retains the exact report delegate
and can be forwarded unchanged. The negative characterization test executes
20,000 warmed steady-state callback invocations with zero managed allocation.
The future decorator must preserve that direct path.

## Executable negative evidence

`Switch2ProUsbOwnedCompositeLifecycleBlockerTests` now preserves the still-live
negative boundaries by asserting that:

- neither the shared participant contract nor the USB participant can
  authenticate an owned-composite authority;
- the public legacy runtime factory still consumes only the read-only native
  adapter;
- the production coordinator is the sole callsite which constructs the
  owned-composite native adapter;
- the terminal base feedback lifetime remains narrow and the activation
  extension has exactly one production implementation; and
- the shared exact report callback carrier remains zero-allocation after
  warmup.

These tests are intended to fail when the missing production hooks are
deliberately added. At that point they must be replaced by the adversarial
ordering, forged-authority, reentrancy, concurrency, deadline, exact-retry, and
quarantine tests required for the actual lifecycle decorator.
