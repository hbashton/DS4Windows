# Switch 2 Pro USB owned-composite registration participant

Status: **production-wired lifecycle composition, offline-tested**.
`Switch2ProUsbProductionCoordinator` now acquires and registers this participant
through `ControlService`. The warmed input-report callback remains unchanged;
no controller or nonzero-haptic hardware test was run for this revision.

## Exact authority composition

`Switch2ProUsbOwnedCompositeRegistrationParticipant` is one participant in the
existing shared registration transaction. It composes, rather than duplicates:

- the admitted `Switch2ProUsbOwnedCompositeLeaseBundle` and sole opaque
  authority;
- the five-step `Switch2ProUsbStartupTransaction`, including Player-1 LED;
- the existing `Switch2ProUsbRuntimeOwner` and registration participant;
- the one-shot owned input-adoption credential and retirement proof; and
- one `ISwitch2ProUsbOwnedFeedbackActivationLifetime` plus its one-shot,
  authority/lifetime-bound dormant-quiescence proof.

Owned runtime creation performs no second OS observation. Its internal entry
point accepts one concrete `Switch2ProUsbOwnedCompositeInputAdoptionIssuer`,
which is both the exact runtime binder and the native-adapter-shaped mediator.
It cannot accept a general Windows adapter or a separate unrelated binder. The
mediator hands the transport a read-only logical facet over the exact
reference-identical bundle lease; it does not open another physical handle.
The legacy read-only factory still performs its original discovery and native
open and is otherwise unchanged.

Before a participant escapes construction, the input adoption credential is
consumed against the exact authority, lifetime, runtime-owner reference, and
registration. Any failure after the process-local owned-composite claim is
taken retains/quarantines the full bundle and runtime evidence. It never
returns partial authority or performs speculative cleanup I/O.

Dormant-feedback proof acquisition is the final construction linearization
point, after startup, runtime adoption, input-credential consumption, and
inner-participant construction have succeeded. Once that acquisition is
attempted, a false, malformed, or thrown result retains the exact feedback
lifetime in the create-failure evidence alongside the bundle and runtime
owner. This includes an implementation which throws after internally adopting
the lifetime; that path is outcome-uncertain and is never described as clean
rejection. If participant publication itself then throws, the same retained
feedback owner is returned.

## Activation transaction

The outer shared table remains the slot and commit-credential authority. The
participant forwards the exact callback carrier directly to the existing USB
participant; it adds no callback wrapper, queue, worker, or report-path branch.

One retained caller deadline covers startup, both prepares, both commits, and
split-commit recovery for contract-conforming dependencies. Commit receives a
reserved portion of the remaining budget so terminal recovery keeps a
positive share:

1. authenticate the exact input/startup/output lease identity and feedback
   authority;
2. advance the five audited volatile startup steps to `Completed`;
3. prepare feedback while publication remains sealed;
4. prepare the existing input participant, which parks input with no reads;
5. after the table issues its exact activation credential, commit feedback;
6. commit input with that exact table credential; and
7. report success only after both commits succeed.

The `Dormant` enum value alone is not retirement evidence. Construction must
take one exact dormant-quiescence proof from the feedback issuer. Its success
certifies logical neutral, no admitted/queued/in-flight physical output, and
exclusive coordinator adoption for the exact authority and lifetime. Prepare
must consume that proof at its linearization point. Successful prepare clears
the participant's terminal-retirement evidence because an exact
credential-consuming abort is then required. A proven pre-linearization
prepare rejection leaves the certified dormant condition intact, so attach
abort can retire the remaining facets without fabricating a feedback abort.

Feedback commits before input because a successful input commit may release
the worker immediately, while feedback is still required to remain sealed.
This is not a claim of cross-object atomic commit. A malformed, thrown,
uncertain, or rejected feedback commit never releases input. If feedback
commits but input commit does not, the participant best-effort seals and
neutralizes feedback and permanently quarantines the complete composite. It
never labels that split outcome a rollback, disposes the owner, or permits a
replacement registration. True cross-facet commit linearization remains a
production blocker.

## Abort and removal order

Attach rollback follows this exact order:

1. abort a successfully prepared feedback lifetime, if one exists;
2. retire the startup/command transaction;
3. abort the existing prepared or unpublished input participant;
4. consume the exact `RuntimeRetirement` input-facet proof; and
5. invoke whole-composite `DisposeQuiesced` once.

Normal removal follows this exact order:

1. atomically seal, neutralize, and quiesce feedback;
2. retire the startup/command transaction;
3. stop and quiesce the existing input participant;
4. consume the exact `RuntimeRetirement` input-facet proof; and
5. invoke whole-composite `DisposeQuiesced` once.

The participant's end-to-end lifecycle deadline may be wider than the owned
command facet's authenticated per-operation maximum. Startup retirement is
therefore given the smaller of the remaining lifecycle budget and
`MaximumOutputOperationMilliseconds`; the wider deadline must never be passed
through as one WinUSB operation timeout.

Only after those steps may the outer transaction unsubscribe and remove the
slot. The mediated input facet deliberately never disposes the raw composite.
Whole-composite disposal is attempted at most once: an exception makes its
outcome uncertain and permanently quarantines the retained owner.

If input prepare itself cleanly retires the parked runtime, its exact owner
state is `AbortedUnpublished`. Rollback accepts that already-completed input
retirement instead of issuing a second abort, then consumes the already-minted
mediated-facet retirement proof before whole-composite disposal.

All dependency calls run outside the participant's private gate. One lifecycle
operation may be in progress; reentrant or racing calls fail closed. Once an
uncertain result is observed, no later lifecycle retry can silently publish,
retire, or replace the authority.

## Offline evidence

Focused tests cover:

- exact activation and removal ordering through the real shared transaction;
- abort before feedback prepare and proven-rejected feedback prepare;
- clean input-prepare rejection with already-aborted facet retirement and
  whole-composite disposal;
- throw-after-adoption and foreign/copied dormant-proof creation failures,
  with exact feedback-lifetime retention;
- feedback-commit uncertainty without input release;
- delayed input-commit failure after feedback commit, including retained
  deadline propagation, neutralization, and permanent retention;
- concurrent removal with one whole-composite disposal;
- one-shot disposal when the dependency throws;
- no owned API accepting fresh OS discovery, a general native adapter, or a
  separate binder;
- a present second-controller observation and throwing external adapter that
  remain uncalled; and
- reference identity from runtime transport to mediated facet to the exact
  admitted bundle lease.

## Remaining release gates

The sole-handle Windows acquisition, exact feature/LED ACK validation,
canonical feedback lifetime, shared registration transaction, and
`ControlService` integration are now constructed in production. Remaining
claims require authorized hardware validation of input completions, LED and
terminal-neutral behavior, disconnect/reconnect, and physical haptic delivery.
No 500 Hz, latency, or haptic-fidelity claim follows from offline composition.

Feedback commits before input under one retained deadline. If a dependency
violates its bound after feedback activates, the participant permanently
quarantines the full composite and forbids replacement; this is a documented
lifecycle constraint, not a fabricated rollback.
