# Legacy HID worker-lifecycle boundary

## Status and scope

This is a dormant prerequisite. `ControlService`, `DS4Devices`, hot-plug,
removal, and service-stop do not call the typed boundary. Existing callers
still use the public `StartUpdate()`, `StopUpdate()`, and
`StopOutputUpdate()` paths.

The implementation adds a post-`Thread.Start` ownership witness to the exact
legacy DS4 and DS3 starts. The witness is non-throwing and occurs only after
the existing `Thread.Start` returns; the legacy start order and every HID call
remain in their original order. The public stop paths are unchanged. The new
typed stop is separate and bounded.

No hardware was opened or exercised for this tranche.

## Proof contract

`DS4DeviceWorkerLifecycleBoundary` owns one monotonic generation and issues an
exact issuer/device/generation lease. Its rules are:

- A successful start requires an input-worker commit witnessed on the typed
  start thread immediately after `Thread.Start` returned.
- The device-facing support classifier is non-virtual and exact-type based.
  A derived class cannot opt itself into the base DS4/DS3 proof.
- An output commit followed by a missing or failed input start is a partial
  start. The result is uncertain and the exact cleanup lease remains valid.
- An exception before any worker commit is still uncertain because arbitrary
  pre-worker device effects cannot be disproved. It retains a cleanup lease.
- A normal return with no committed worker is a clean rejection and publishes
  no lease.
- A worker started through the ordinary public path cannot be adopted later.
  It permanently quarantines the dormant boundary as untracked.
- A worker commit arriving from another thread during typed start makes the
  start uncertain; it is never credited to the typed generation.
- Start and stop callbacks run outside the private lifecycle gate. Recursive
  or concurrent lifecycle entry poisons the outer transition rather than
  allowing a callback to overtake ownership. Reentrancy and foreign-thread
  interference have distinct uncertain results; a foreign `Busy` start does
  not receive the current cleanup lease.
- Stop accepts only the current exact lease. Timeout, exception, malformed
  stop result, final-output uncertainty, or reentrancy retains that lease and
  prevents a later generation. The same lease may retry cleanup.
- Stop success is based on a successful bounded `Thread.Join` (or a completed
  `Join(0)`), never on `Thread.IsAlive` alone.
- A completed start and completed stop are exactly idempotent.

The typed input stop deliberately does not call `HidDevice.CancelIO()`.
`CancelIO()` takes `HidDevice.handleLock` without a timeout, so it cannot be
part of a bounded proof. The typed path instead sets the exit request, spends
the caller's remaining deadline only in `Join`, and reports `StopTimedOut` if
the current HID operation has not returned. This remains truthful even for
the DS4 USB infinite read and DS3 synchronous feature read: neither can
produce a false stop-success result, and the exact lease remains quarantined
for retry. The legacy public stop continues to call `CancelIO()` in its
existing order.

The caller timeout is validated against
`InputControllerRegistration.MaximumStopTimeoutMilliseconds`. Input join,
output-lock acquisition, output join, and DS3 terminal output all consume the
same remaining deadline rather than each receiving a new full timeout.

## Audited subtype matrix

| Exact runtime type | Typed support | Start/stop ownership finding |
|---|---|---|
| `DS4Device` | Supported | USB owns output-copy then input; Bluetooth owns input. Each `Thread.Start` return is witnessed. Typed stop proves input and output completion with bounded joins. |
| `DS3Device` | Supported | Owns one input thread. Stop proves its join and requires the neutral interrupt output write to return success within the remaining timeout. |
| `DualSenseDevice` | Fail closed | `StartPhysicalWorkers()` can publish physical-output, Bluetooth-observation, microphone-dispatch, device-command, lifecycle, recovery, audio-pacer, timeout, and input owners across several generations. Its retirement/final-HID protocol contains deliberate unbounded waits and cannot be represented by the base two-worker proof. |
| `SwitchProDevice` | Fail closed | `SetOperational()` performs synchronous HID setup before input publication, catches I/O failure, and can invoke `Removal` when `connectionOpened` is false. There is no exact rollback token for those effects. |
| `JoyConDevice` | Fail closed | It has the same `SetOperational()`/early-`Removal` ambiguity as Switch Pro and can fail before `ds4Input` exists. |
| `Switch2RuntimeInputDevice` | Fail closed | It is a no-HID logical adapter owned by the Switch 2 registration transaction and terminal-neutral protocol, not by legacy worker threads. |
| Any other derived type | Fail closed | Exact-type allowlisting prevents an unaudited override from inheriting a false base proof. |

DualSense, Switch Pro, Joy-Con, Switch 2 runtime, and unknown subtype rejection
happens before `StartUpdate` or the typed stop core is called, so the rejection
has zero lifecycle side effects.

## Failure-injection coverage

`DS4DeviceWorkerLifecycleTests` deterministically covers:

- exact, non-overridable subtype allowlisting and every unsupported class's
  zero-call/zero-state-mutation rejection;
- clean start rejection without a cleanup lease;
- a pre-worker start exception retaining its exact cleanup lease;
- input-start then exception and output-start then input-failure partial-start
  cleanup with no running worker left;
- a foreign-thread start that cannot be claimed by the typed generation;
- bounded stop timeout, retained lease, and exact cleanup retry;
- invalid stop deadlines rejected before the stop dependency or state change;
- stop exception or malformed dependency result, retained lease, and exact
  cleanup retry;
- stale/wrong-device lease rejection before the stop core;
- start and stop idempotence;
- recursive and concurrent start/stop poisoning without ownership overtake;
- refusal to adopt a worker started through the public path;
- start/stop callbacks outside the lifecycle gate; and
- operation-specific result-shape validation that prevents false certainty.

The tests use inert fake devices and controlled worker threads. `IsAlive` is
used only after cleanup as a test observation, never as the production proof.

## Production integration blocker

The typed seam is not safe to mix with an ordinary `StartUpdate()` call for
the same device. A future legacy admission owner must make the typed call the
sole initial worker-start path and bind its worker lease to the exact table and
ControlService slot generation. Attach abort, terminal removal, hot-unplug,
and service-stop must all drive the same worker lease until stop is proved.

That integration also needs subtype policy at admission. The supported base
DS4/DS3 path can enter the shared registration lifecycle. Unsupported
DualSense, Switch Pro, Joy-Con, and no-HID Switch 2 runtime devices must remain
on their existing owners until each has an exact typed owner for all of its
effects. Silently falling back after a shared slot/table reservation would
split authority and is prohibited.
