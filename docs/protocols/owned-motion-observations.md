# Owned motion observations, 2026-09-02

This is a software ownership change, not proof of physical latency, complete
joined-controller serialization, or automatic Switch 2 UDP registration.

## Confirmed faults and production changes

`DS4State.CopyTo` intentionally carries `Motion` by reference. It is suitable
for borrowed views inside one owned report, but is not an independent snapshot.
`SixAxis.copy` additionally omits full pitch/roll and replaces mapped output
acceleration with unmodified acceleration. Neither operation is an exact
owned history copy.

The original Joy-Con physical reader previously called `pState.Motion.copy`
and then `cState.CopyTo(pState)`. The latter discarded the separate motion
storage, so subsequent decoding could overwrite previous-frame motion and
construct a self-referencing `previousAxis`. It now commits physical history
through `DS4StateOwnedSnapshot`. The physical current, committed previous, and
one older motion sample have independent fixed storage. This does not give
ownership to shared `JointState` or primary/secondary mapper dispatch.

The UDP report handler previously used the mapper's `TempState` as scratch,
shallow-copied `CurrentState.Motion`, and then modified acceleration/angular
velocity for smoothing and Switch 2 Cemuhook yaw sensitivity. UDP policy could
therefore modify physical/runtime/mapping motion or compound yaw on repeated
observations. Each handler now has its own cold-allocated snapshot. Smoothing
and yaw policy affect only that snapshot, not mapper scratch or source history.
Wrong senders and already-replaced array occupants are rejected. These checks
are not a substitute for the surrounding registration/report lifetime lease.

The Joy-Con reader also uses the existing reusable projected SixAxis envelope
instead of allocating an envelope per sample. `FireProjectedSixAxisEvent`
captures the event delegate once before checking/invoking it: retirement cannot
clear the last subscriber between a field null check and a second field read.
Exact callback wrappers still determine whether a copied invocation is admitted.

## Snapshot contract

`DS4StateOwnedSnapshot` owns one state, one current motion and one previous
motion. `Capture`:

- requires the caller to own a stable source throughout capture;
- preserves controls, typed stick precision, timestamps, touch and physical
  sidecars through the canonical `CopyTo` implementation;
- copies all public motion scalars exactly, including full gyro axes, mapped
  acceleration, gyro-control flag and elapsed time;
- captures current and previous scalars into value storage before writing
  either destination slot, so self-capture and cross-aliasing are safe;
- copies at most one previous level, setting its predecessor to null rather
  than following an unbounded/cyclic source graph;
- preserves null motion, safely reuses its original buffers, and allocates
  nothing after construction.

The borrowed `State` is valid until the owner captures again. Consumers must
not retain it past that boundary, and concurrent capture/consumption requires
external ownership. This helper is not a lock, queue, pair token or worker.
The legacy `CopyTo` and `SixAxis.copy` contracts elsewhere are unchanged.

## Remaining integration gates

The joined Joy-Con journal must own source admission, immutable queued/cache
snapshots, a separate mutable mapping view, gyro invocation, mapper and virtual
submission. Both halves must drain promptly in admission order without an
additional timer. Keep ownership across the UDP observation facet too.

Pair activation currently exposes peer/shared-state references before the
second slot/profile is ready. Constructor-registered removal currently clears
shared state before service cleanup. Profile actions and queued companion
disconnects need exact pair/lifetime ownership. A journal alone does not solve
those boundaries. Details remain in `legacy-joycon-report-ownership.md`.

The later [Switch 2 observation integration](switch2-udp-observation.md) closes
the source-level automatic registration gap: the exact host captures an owned
observation after `On_Report` and sends it through a separate bounded worker.
It does not add another raw event subscriber or depend on legacy device
enumeration. That document defines the optional coalescing and per-registration
DSU identity tradeoffs and distinguishes production-composition tests from
hardware/UI acceptance.

The UDP metadata/network path still has existing string/address conversions,
client-list allocations and network work. Removing the gyro-envelope allocation
does not establish zero allocation for the entire controller/UDP hot path.
The later sender-safety change in `udp-send-ownership.md` removes the send-pool
capacity wait, gives pending packets exclusive buffers, fixes control-reply
lengths and isolates port sessions. Those sender changes alone did not close
automatic registration or off-thread observation; the subsequent Switch 2
integration does so for Switch 2 sources, not existing legacy HID handlers.

## Verification

`owned-motion-integration-20260902.trx` passed 42 tests: 13 exact snapshot,
5 physical Joy-Con motion/history, 7 actual UDP-handler isolation, 5 runtime
publication-authority and 12 admitted gyro/mapper integration cases. Tests use
no sockets, controller I/O, drivers or installed applications.

The first full suite had one existing stick-filter zero-allocation failure
(864 bytes, 2,862 other passes, 3 opt-in live-audio skips). Ten isolated reruns
passed without source or threshold changes; an unchanged full rerun then passed
2,863 tests with 3 skips and no failures. The initial allocation is **not
attributed**. Keep it as a diagnostic/release-risk item rather than claiming
the successful repeats explained or fixed it. The exact results and follow-up
diagnostic are in the platform validation ledger.
An additional unchanged full confirmation also passed 2,863 tests with 3 skips
and no failures; the attribution caveat remains.
