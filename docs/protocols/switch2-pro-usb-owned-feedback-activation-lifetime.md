# Switch 2 Pro USB owned feedback activation lifetime

Status: **production-composed and offline tested**. The Switch 2 Pro USB
production coordinator constructs this lifetime over the exact owned composite
lease after startup and calibration. It creates no timer or cadence worker and
does not claim a universally safe physical output rate. It composes the
existing canonical feedback runtime, state-lane pump, Switch 2 HD-rumble
delivery sink, physical writer, and owned-composite bridge; it does not add
another mapper, queue, or wire encoder.

The unconfigured sink fallback is `SdlBodyOnlyCompatibility`, matching the
Bluetooth lifetime, so no caller can obtain trigger conversion by omission.
The normal profile route explicitly selects the persisted **Preserve Xbox
impulse-trigger detail in HD rumble** setting on every frame; that setting is on by
default. When enabled, each trigger controls the high-frequency field in only
its corresponding group while body-low remains independent. Dynamic frequency
maps intensity across 300–481 Hz; profiles can select a fixed 1-through-10
frequency level and a bounded 1-through-10 strength instead. High-band overlap
uses saturating addition capped at the 10-bit field maximum, retaining both
contributions without wraparound. This is a side-local spectral conversion and
never claims a physical trigger actuator.

The current profile value is selected on the dedicated feedback-delivery path
for every accepted canonical Xbox frame. A policy transition explicitly
re-presents the newest canonical frame once without minting a new owner,
effect, or delivery epoch; ordinary same-effect TTL renewals remain deduped.
An uncertain delivery retains its original policy, tuning, and byte-exact
retry. A new selection can affect only the next safely admitted presentation.
Tuning-only edits are inert while the body-only policy is selected. None of
this adds work to the controller-input hot path.

## Structural output adoption

`Switch2ProUsbOwnedFeedbackActivationLifetime.TryCreate` is the only factory
for the dormant composition. Before the lifetime escapes it creates a private
owner fence and asks the concrete owned-composite output lane to perform one
irreversible adoption. Adoption succeeds only while the exact output lane has
never minted a sequence and has no start, retirement, active native operation,
quarantine, or terminal seal. The shared composite terminal fence and output
lane gate establish that decision atomically.

The returned capability implements only
`ISwitch2ProUsbOwnedFeedbackOutputLease`. It is retained inside the existing
owned HD-rumble bridge and is never returned to the caller. The bridge
constructor cannot accept the full composite lease. After adoption, every
direct full-lease output write, retirement, and claim probe rejects before it
can advance the sequence, touch a report buffer, or invoke native I/O. Copied
full-lease aliases therefore remain useful only for the pre-adoption
compatibility/test boundary; they cannot bypass the adopted feedback owner.
Terminal sealing invalidates the private fence and the one-shot adoption latch
does not recycle.

A failure before adoption is a clean rejection. A throw or contradictory
result after adoption returns the exact bundle as terminal-attention evidence.
It never reports the output lane reusable merely because the private narrow
capability did not escape.

## Prepare, commit, and abort

The lifetime issues one dormant-quiescence proof bound to its exact issuer,
private fence, bundle authority, complete physical lifetime, and nonzero
sequence. Copied, forged, stale, foreign-fence, and same-generation foreign
proofs fail closed.

Prepare consumes the exact proof once and leaves lane creation and manual
`PumpOnce` sealed. Commit consumes the exact prepare credential and is the sole
point which permits either operation. Commit and abort race through one
single-flight boundary, so exactly one copied credential can win. Public
operations are serialized, but dependency calls execute outside the
lifetime's private state gate; reentry reports a typed incomplete/busy result
without starting dependency I/O.

Pre-commit abort is a distinct structural no-write branch. Since neither a
lane nor `PumpOnce` could have been admitted, abort seals the pump, proves the
bridge has no retained operation, retires the empty pump and sink, and reaches
`Aborted` without fabricating or sending a Stop report.

## Committed terminal neutralization

Every still-connected committed lifetime, including one with zero lanes and no prior frame,
must deliver and record one exact canonical Stop before it can issue terminal
quiescence. The zero-state path asks `ControllerFeedbackRuntime` to create a
runtime-owned lifecycle-neutral delivery with a nonzero epoch; it does not
invent a raw report, feedback frame, rumble amplitude, or protocol byte. The
existing pump sends that Stop through the existing sink, physical writer, and
owned bridge.

September 5 source follow-up: the shared virtual-feedback session routes an
explicit broker Stop directly to this owner, bypassing the profile's 0..9999 ms
effect delay and effect-tuning validation. An admitted Stop cancels queued Apply
and impulse-release presentation; rejected stale/foreign frames cannot clear
that queue or alter sink tuning. A configuration change must not require a
Frame-only refresh after the runtime has already scheduled its Stop event.
The existing retained-output/retry and exact-neutral proof rules below still
apply: a successful session admission, including USB `RetryPending`, is not by
itself proof that the physical controller received neutral. This repair is not
in the currently running b56 portable payload.

The later September 5 live Xbox policy follow-up also uses this same owner.
Profile output/impulse enablement changes stage a restriction for the exact
accepted game frame, without a new broker sequence or expiry. The USB owner's
activation-operation fence encloses refresh and pump work. Retained output must
complete with its original bytes/counter before the zero-amplitude presentation
can be acknowledged locally. A profile edit does not retire the broker epoch:
its zero-amplitude compatibility groups retain legal carrier codes, unlike the
all-zero groups used by terminal Stop. This follow-up is source-only too.

### Definite physical disconnect (2026-09-02)

USB surprise-removal is a separate terminal path, not a successful rumble Stop.
The exact MI_00 native read/output owner latches only `ERROR_NO_SUCH_DEVICE`
(433) or `ERROR_DEVICE_NOT_CONNECTED` (1167). Generic failure (31), cancellation
(995), timeout, and a missing discovery candidate do not establish this proof.
The adopted output lane must have no pending start, retirement, claim or native
operation and no quarantine before it can seal itself against all future writes.
The bridge authenticates that private adoption and still drains any retained
operation first. The feedback pump and sink then retire without acknowledging
a physical Stop. `ExactDisconnectedAndQuiescent` and the corresponding terminal
state are authenticated separately from `ExactNeutralAndQuiescent`; foreign
issuer/fence/revision results remain rejected.

The existing participant still retires the command facet, drains input,
publishes and acknowledges one virtual-input neutral, disposes the whole
composite, and removes the exact slot. Discovery reconciliation now requests
this normal transaction for missing registrations after a successful scan.
Failed/incomplete scans do not imply removal. Repeated scans can retry retained
cleanup, while the slot and native reservation remain owned until success.
An unsuccessful Start/Stop action restores the UI button without claiming that
the service stopped.

One neutralization attempt uses one monotonic managed-wait deadline and follows
this order:

1. seal lane publication and join the single-flight pump boundary;
2. inspect and authenticate the bridge's current revision;
3. explicitly retire an exact retained bridge operation before creating or
   sending a terminal Stop;
4. submit or retry the canonical Stop through the existing pump/sink/writer;
5. prove the pump is retired and the sink recorded an exact nonzero-epoch Stop;
6. retire the sink; and
7. authenticate a fresh current bridge revision with no retained operation.

The bridge's immutable operation-wait value is included in caller-visible
managed wait accounting. An attempt does not enter a fixed bridge wait unless
the remaining budget can cover it. Synchronous Win32 begin, cancellation,
free, and handle-close calls still have no hard wall-clock bound; this lifetime
does not claim otherwise.

If an earlier non-Stop report remains uncertain after its normal 250 ms state
TTL, producer sealing does not overwrite or age it away. Neutralization first
drains that exact native operation, then submits the later Stop. If the Stop
itself is retained, draining it on the next attempt is not treated as delivery:
the writer retries the byte-identical cached Stop with the same packet counter,
and only its exact successful delivery establishes terminal neutralization.
An insufficient budget or `RetainedForRetry` result preserves every owner,
report, counter, and claim for the next caller-managed attempt.

Malformed, thrown, foreign, stale-revision, ABA, contradictory current-state,
or sequence-saturation evidence permanently quarantines the lifetime. It does
not infer quiescence, replace an uncertain report, or release the composite.

## Terminal proof

`ExactNeutralAndQuiescent` is not authorized by numeric generations alone. The
result contains the private feedback issuer, terminal proof fence, exact
authority generations, and current state revision. The lifetime's pure
`AuthenticatesQuiescenceResult` check accepts it only while the same issuer is
still in the matching `Aborted` or `NeutralAndQuiescent` state and revision.
The owned-composite registration participant invokes that check immediately
before it accepts feedback retirement. Same-generation foreign results and
stale results from an earlier revision cannot authorize composite disposal.

## Offline evidence and limits

The focused tests cover never-started one-shot adoption, copied and foreign
credentials, direct-alias rejection, adoption/write races, pre-commit no-write
abort, committed zero-lane Stop, retained non-Stop drain after TTL, retained
Stop byte/counter-identical retry, insufficient-budget retry, copied-lane
sealing, commit/abort concurrency, reentrancy, malformed/ABA quarantine,
post-adoption factory failure retention, exact terminal-result binding, and a
zero-allocation warmed manual idle pump.

These are managed ownership, lifecycle, and byte-level sidedness proofs. They
do not establish a universal feedback cadence, watchdog interval, measured USB
completion rate, subjective controller haptic fidelity, physical onset latency,
or hardware safety. Authorized hardware verification remains separate work.
