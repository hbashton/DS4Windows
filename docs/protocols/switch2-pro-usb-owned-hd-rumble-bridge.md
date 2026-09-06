# Switch 2 Pro USB owned HD-rumble bridge

Status: **dormant, internal, offline-tested adapter seam**. The dormant feedback
activation lifetime is its only non-test constructor. No production factory,
registration participant, `ControlService` path, or hardware verifier
constructs this bridge. This tranche performed no controller I/O and adds no
output cadence.

## Scope and provenance

`Switch2ProUsbOwnedHdRumbleTransportBridge` composes two already canonical
DS4Windows boundaries:

- `Switch2ProUsbHdRumblePhysicalWriter` continues to own mapping-free 64-byte
  encoding and the modulo-16 packet counter; and
- the one-shot adopted `ISwitch2ProUsbOwnedFeedbackOutputLease` remains a
  narrow capability over the sole MI_00 output handle, bounded native
  submission, exact retained-operation claim, and cancel/drain mechanics.

The bridge neither copies the feedback translator nor introduces another
mapping stack. It does not interpret actuator values or construct protocol
bytes. It forwards the exact codec-produced report span to the owned lease.

No new hardware or wire-format claim is made here. The underlying codec/writer
evidence was reread at the clean local reference pins already recorded by
`switch2-pro-usb-hd-rumble-physical-writer.md`:

| Reference | Rechecked pin |
| --- | --- |
| SDL current | `c71abd08605b8bb7078372307a93274725c99fe0` |
| SDL hifihedgehog fork | `d98c5804a9d20b0d96e993741797878c86b8f1e1` |
| Switch2Connect | `4487322a306f04efa27682e3f3a508635a84fd98` |
| HIDMaestro current | `9df50410230c11b410f43909ede0e5fc8b23d15b` |
| PadForge | `0794fd01bd19f4c096b982ffc824b88bce5ed743` |

All five local reference trees were clean during this pass. Their licenses and
the exact behavioral facts used by the codec/writer remain documented in the
writer document. This bridge is original DS4Windows GPL code and copies no
reference implementation.

## Exact retained-operation flow

Construction authenticates one exact adopted output reference, Pro Controller
2, both nonzero generations, the lease's immutable maximum managed wait budget,
and one fixed positive per-operation budget. The bridge's public transport
authentication is then a pure local identity check. Every native write still
rechecks the owned lease's promised immutable authentication and maximum before
starting.

One call has these outcomes:

1. A completed or proven-rejected owned attempt is normalized through the
   existing transport result without inventing completion evidence.
2. An outcome-uncertain but already quiescent attempt carries no retained
   claim. The canonical writer retains its logical report under its existing
   rules, but this bridge owns no native operation.
3. An outcome-uncertain, non-quiescent attempt must carry one valid exact
   claim. Numeric generations and sequence are insufficient: before storing it,
   the bridge requires the lease's pure
   `AuthenticatesOutputOperationClaim` seam to prove the private lease fence,
   generations, sequence, and current retained state. The bridge then stores
   that claim, the exact 64 bytes, and the exact transport result. No
   replacement report may start.
4. An exact retry first calls `TryRetireOutputOperation` with the stored claim
   and fixed budget, but only after the pure seam proves that exact claim is
   still the lease's current retained operation. `RetainedForRetry` is accepted
   only if a second pure check proves the same current provenance; it preserves
   the same claim, bytes, counter, and failure evidence.
   `ExactOperationQuiescent` must echo the exact pre-authenticated claim and a
   post-retirement pure probe must return false, proving the lease no longer
   presents it as current. A true post-probe (the operation was not cleared) or
   a thrown probe is a terminal contradiction. Only the false shape clears the
   bridge's retained claim and permits the same cached report to be submitted
   again.

The optional `ISwitch2ProUsbHdRumblePendingReportFence` prevents the canonical
writer from replacing its cached submission while the bridge retains a native
operation or terminal quarantine. This closes a semantic gap between the older
synchronous writer contract and the newer owned lifetime: otherwise a newer
delivery could overwrite the writer cache while the old native buffer was
still live, and the old operation's uncertainty could be misattributed to the
new delivery. A direct bridge caller which supplies different bytes while an
operation is retained receives a proven `Busy` rejection and performs no
retirement or write.

After explicit exact retirement, a newer canonical report may be admitted and
uses the writer's next counter. Exact same-report retry and newer-report
supersession therefore remain distinct and ordered.

## Failure, concurrency, and evidence

One interlocked lane serializes write and retirement calls. A concurrent or
same-thread reentrant call receives a local proven `Busy`/typed busy result and
cannot enter the owned lease twice. The warm completed path uses preallocated
report storage and allocates zero managed bytes after warmup.

The bridge permanently quarantines on:

- a thrown write or retirement dependency;
- malformed or foreign write evidence after a dependency call;
- a false, throwing, same-generation foreign-fence, or no-longer-current claim
  provenance check;
- an immutable authentication/maximum-budget contradiction;
- a rejected, malformed, foreign, or wrong-claim retirement result; or
- any retained-claim sequence at or below the bridge's accepted high-water
  mark, including delayed A/B/C then A or B replay (ABA contradiction).

Quarantine never retries an ambiguous native effect. If an exact claim exists,
it remains strongly referenced for terminal attention, is intentionally not
drained again, and keeps the whole owned composite from disposal/reuse. No
guessed claim or guessed byte count is synthesized.

`TryRetireRetainedOperation` exposes typed, bridge-reference- and
generation-authenticated point-in-time evidence for:

- no retained operation;
- exact operation quiescence;
- the exact operation retained for another bounded retry;
- a competing/reentrant operation; and
- permanent quarantine.

`NoRetainedOperation` means only that this bridge owns no retirement claim. It
does not promote the owned attempt contract's `NoOperationOwnedByAttempt` into
whole-lane quiescence and says nothing about an operation started before a
future composition owner constructs the bridge. Only the exact retirement
path returns `ExactOperationQuiescent` for a claim the bridge itself retained.

That result is deliberately not a lifecycle credential. A later writer call
can make earlier point-in-time quiescence stale because this dormant seam does
not seal producers. `Authenticates(result)` acquires the same local operation
fence and accepts only the exact bridge reference, generations, current state
revision, and compatible current state. Beginning any later native write
attempt advances the revision before invoking the dependency, so stale
`NoRetainedOperation` and `ExactOperationQuiescent` evidence cannot be promoted
after a later write, retained claim, or quarantine.

The fixed timeout is a cumulative **managed native-quiescence wait budget** for
each owned write or retirement call. A retry call may spend one retirement
budget and, only after exact quiescence, one new-write budget. These are not
hard whole-call wall-clock bounds: synchronous Win32 begin, cancel, free, and
handle-close APIs still expose no nonblocking deadline contract.

## Terminal composition relationship

This bridge does **not** implement `ISwitch2ProUsbOwnedFeedbackLifetime` and its
drain result alone does not prove terminal neutralization. Exact drain proves
only that one retained native operation can no longer complete.

The separate dormant
`Switch2ProUsbOwnedFeedbackActivationLifetime` now owns the structural producer
seal and composes this bridge with the canonical pump, sink, and writer. It
drains any exact retained bridge operation before submitting the canonical
Stop, then requires pump retirement, exact sink Stop evidence, sink retirement,
and a fresh authenticated bridge no-retained revision. Its issuer-bound result,
not this point-in-time drain result, is the terminal lifecycle credential. See
`switch2-pro-usb-owned-feedback-activation-lifetime.md`.

Neither type adds cadence, a watchdog assumption, production registration, or
a hardware path.

## Offline verification

The focused suite covers the canonical sink/writer chain, identical-byte and
counter retry, newer-report fencing, exact retained-claim retry, delayed ABA,
same-generation foreign-fence/high-sequence admission and echoed-retirement
attacks, thrown/malformed/terminal outcomes, dependency immutability,
concurrent and same-thread reentrant calls, point-in-time evidence
authentication, and zero-allocation warm writes.

```powershell
dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Switch2ProUsbOwnedHdRumbleTransportBridgeTests|FullyQualifiedName~Switch2ProUsbHdRumblePhysicalWriterTests|FullyQualifiedName~Switch2HdRumbleDeliverySinkTests"
dotnet build DS4Windows/DS4WinWPF.csproj -c Release -p:Platform=x64 --no-restore
```
