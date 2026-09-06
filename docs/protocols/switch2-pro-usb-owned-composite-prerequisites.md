# Switch 2 Pro USB owned-composite adoption prerequisites

Status: dormant, offline prerequisite, now consumed by the separate
`switch2-pro-usb-owned-composite-registration-participant.md` composition.
This file still describes the adoption and feedback contracts themselves. It
has no `ControlService`, `DS4Devices`, production native-adapter registration,
physical-writer, or hardware call site. A separate dormant internal Windows
transport is documented in
`switch2-pro-usb-windows-owned-composite-transport.md`.

## What is now executable

`Switch2ProUsbOwnedCompositeRuntimeAdoptionFactory` derives device generation,
transport generation, QPC frequency, and physical registration from the one
admitted `Switch2PhysicalInputLifetime`. Its narrow internal runtime seam takes
one concrete adoption issuer which both binds the exact
`Switch2ProUsbRuntimeOwner`/`InputControllerRegistration` and hands off the
mediated input facet. It cannot accept a separate OS discovery adapter, general
Windows native adapter, or unrelated binder. Existing public runtime
construction retains its legacy discovery/open behavior.

The adoption issuer takes an irreversible process-local claim keyed by exact
`ISwitch2ProUsbOwnedCompositeLease` object reference. Repeated or concurrent
issuers, including issuers reached through separate bundle objects wrapping the
same lease, cannot create a second mediated input owner. A failed construction
never releases this claim.

At the adapter handoff, the issuer revalidates the bundle authority outside its
private gate, checks the exact lease reference, and mints a pending credential
which binds:

- the private issuer and mediated-facet references;
- the exact bundle authority;
- the full physical lifetime, including QPC frequency;
- the exact runtime-owner reference and runtime registration; and
- a nonzero, non-recycled handoff sequence.

The pending credential is not returned until ordinary runtime construction has
succeeded and `owner.TransportOwner.Lifetime` equals the bundle lifetime. One
exact copy can then be consumed. Concurrent copies, stale copies, foreign
same-numeric authorities, and foreign same-generation runtime owners fail
closed without consuming the winning copy.

## Mediated input-facet retirement

The runtime transport receives a non-downcastable read-only facet, not the raw
full-duplex lease. The facet forwards the sole input stream and proxies native
completion callbacks without allocating on the warmed steady path. Its native
control calls are serialized while dependencies run outside private gates.

A retirement attempt seals new reads before its first native quiescence call.
While that call is in progress, or after it returns false, a completion for the
already-submitted read can still drain to its exact target; this is not treated
as a late callback. Cancel/completed-read retirement remain available only as
part of draining that retained read. A successful native input-quiescence wait
then seals every input control operation. The single subsequent
`DisposeQuiesced` call permanently retires the logical input facet and emits a
one-shot proof bound to the issuer, facet, bundle authority, full lifetime,
handoff sequence, and phase. A callback observed after that true result is
suppressed and quarantines the issuer. The facet intentionally does **not**
call `DisposeQuiesced` on the shared physical composite. The eventual
coordinator must dispose that composite only after feedback and command
retirement.

A false native wait permits only the exact bounded cleanup path and a retry of
the same logical wait/dispose retirement; it never reopens input. A thrown
wait, invalid/repeated dispose, or callback after proven quiescence quarantines
the facet and issuer. Retirement phase starts as `ConstructionRollback` at the
exact handoff, may be promoted only by successful credential publication, and
is permanently sealed before the first native wait begins. The proof uses that
sealed value rather than sampling the issuer's later state, so delayed cleanup
cannot be relabeled as `RuntimeRetirement`.

Every post-claim construction failure retains the exact bundle, issuer, bound
runtime candidate when one exists, nested runtime failure, and any exact
logical-retirement proof. It performs no new cleanup I/O beyond the existing
runtime factory's bounded rollback. Lack of a proof means only retention, never
an inferred release or permission to reacquire.

## Feedback contract and dormant implementation

`ISwitch2ProUsbOwnedFeedbackActivationLifetime` adds a state and tagged
prepare/commit/abort result shape to the existing terminal feedback contract.
Before lifecycle composition is created, it must issue exactly one dormant-
quiescence proof bound to its issuer, private fence, exact authority, full
lifetime, and a nonzero sequence. Success certifies that publication never
opened, logical output is neutral, no physical output is queued or in flight,
and no other coordinator can adopt that lifetime. `Dormant` state by itself is
not this proof. Prepare authenticates the exact proof and consumes copied
proofs at its linearization point (or quarantines them on uncertainty).
The participant delays this acquisition until every input/startup/runtime
construction prerequisite has succeeded. From the first acquisition attempt,
its create-failure evidence retains the exact feedback lifetime: a false or
foreign proof cannot publish authority, and a throw after internal adoption is
outcome-uncertain rather than a clean reusable rejection.
The opaque prepare credential binds issuer/fence references, exact bundle
authority, full lifetime, and a nonzero sequence. Results distinguish success,
proven pre-linearization rejection, and outcome uncertainty. Uncertainty
requires retaining the full composite and forbids retry or input commit.

There is deliberately no **production construction** of this feedback
lifetime. One dormant internal factory now wraps the existing canonical
feedback runtime/pump, Switch 2 sink, physical writer, and owned-output bridge;
it introduces no second queue, mapping stack, worker, timer, or cadence. Its
private one-shot output adoption requires the concrete output sequence to be
zero and transfers the never-started lane into a non-downcastable narrow
capability. Copied full-lease aliases reject every output method after that
linearization point. Prepare remains sealed, Commit alone admits lane creation
and manual pumping, and committed retirement drains retained output before an
exact canonical Stop. See
`switch2-pro-usb-owned-feedback-activation-lifetime.md`.

## Explicit limits and ownership precondition

The `ConditionalWeakTable` exact-lease claim is process-local reference proof:
it prevents competing issuers and factories through this new seam for the same
managed lease object. It is not a bundle-issued
`Pending -> Adopted | FailedRetained` proof for input, or a way to invalidate
copies of the older bundle authority or raw/downcastable input/startup facet
views already obtained from that bundle. The concrete feedback-output adoption
is stronger for its own facet: it atomically invalidates all direct full-lease
output methods after a never-started, one-shot adoption. Calling the input
adoption factory remains an ownership-transfer precondition for copied input
and startup views; callers must abandon those copies and use only the returned
owner, credential, or retained failure evidence.

The mediated wrapper also relies on the native owned-composite acquisition
contract to reserve one physical container and return the lease only once.
Distinct managed objects representing the same native lifetime cannot be
deduplicated by reference identity. The live read-only Windows adapter does not
implement that stronger acquisition contract. A separate internal dormant
adapter now does, without a production callsite.

The separate dormant participant now proves the executable call ordering from
startup through input commit and from terminal feedback through exact
whole-composite disposal for contract-conforming dependencies. This prerequisite
plus the dormant feedback owner still does not provide production registration
of the sole-handle Windows implementation, production feedback
construction/cadence, cross-facet atomic commit, or hardware behavior. No device rate,
output cadence, haptic fidelity, or hardware behavior is claimed.

## Offline validation

The focused suite covers full-lifetime/QPC mismatch, copied and forged
credentials, concurrent consumption, copied-authority and cross-bundle
exact-lease races, reentrant and concurrent handoff, sequence exhaustion before
dependency calls, pump rejection/throw, attention-handler rejection,
false/throwing native quiescence, delayed rollback, exact-lifetime adoption
without rediscovery or an external Windows open, construction/runtime phase
binding, draining one outstanding completion after a false wait, terminal
facet behavior, callback after retirement, a blocked-wait race, feedback
result invariants, never-started one-shot output adoption, exact Stop delivery,
retained-operation drain/retry, issuer-bound terminal evidence, and zero
allocations across 20,000 warmed mediated report callbacks and the warmed
manual idle feedback pump.
