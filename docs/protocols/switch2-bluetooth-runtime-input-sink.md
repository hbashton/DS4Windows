# Switch 2 Bluetooth runtime input sink

`Switch2BluetoothRuntimeInputSink` bridges canonical Common05 frames owned by
`Switch2BluetoothInputOwner` into the existing Switch 2 profile mappers and
`Switch2RuntimeInputDevice.Report` boundary. It has no discovery, WinRT, GATT,
registration-table, controller-slot, pairing, command, feedback, LED, haptic,
or physical-output authority. Activation is now composed by the production
coordinator and ControlService transaction; the sink itself remains the same
narrow, independently tested seam.

## Exact source binding

Creation accepts only one exact Bluetooth LE Common05 descriptor and a
`Created` Pro or standalone Joy-Con runtime whose immutable model/mode,
Bluetooth transport, device generation, and transport generation all match.
The runtime exposes only an internal boolean authentication predicate; it does
not expose mutable source identity. Joined Joy-Con runtimes are deliberately
excluded because they require two independently fenced source bindings.

Every canonical publication must carry the complete descriptor used at sink
creation. Terminal calls separately authenticate their kind (Pro clear versus
the exact standalone Joy-Con side), both generations, and a post-commit reason.
`ActivationAborted`, `None`, unknown reasons, cross-kind calls, and wrong sides
fail closed without requesting neutral state.

## Activation and terminal ordering

The sink does not manufacture a second activation credential. Composition must
perform the following order:

1. Create the runtime and sink while the runtime is `Created`.
2. Prepare the Bluetooth input owner. Inline notifications may enter its fixed
   queue, but `DrainOne` remains parked.
3. Bind the exact registration-table slot token, call `StartUpdate`, and park
   the drain worker.
4. Begin the table's exact activation epoch. The table is report-admissible and
   blocks retirement/close from overtaking that epoch.
5. Commit the input owner's exact single-use prepare credential using the
   table's live exact-token activation claim, then complete that epoch.

An aborted or interrupted prepared owner never calls this sink's Pro clear or
Joy-Con half-loss boundary. Publication requires an `Active` runtime, and a
terminal call against a `Created` runtime is rejected as lifecycle-closed.
After owner commit, the input owner serializes publication and physical
lifecycle calls; disconnect or overflow during a publication is observed only
after that call returns.

`ClearPro` and `LoseJoyConHalf` no longer publish runtime terminal neutral.
They record one exact generation/reason-fenced terminal request and return.
This is essential: a physical callback may arrive while the authoritative
registration-table slot is still `Attached`, where terminal-report admission
is deliberately impossible. Repeating the same request is idempotent; changing
kind, side, generation, or reason fails closed.

Only the composition owner's private terminal credential may schedule and wait
that recorded request, and only after service-owned retirement has begun,
ordinary table leases have drained, the input worker has actually joined, and
the concrete lease has returned exact platform-release proof. The scheduler
task and the runtime's first accepted reservation are retained across timeout
retry. A retry waits that same logical terminal epoch; it never calls
`RequestTerminalNeutral` a second time. Success requires both completion and
delivery to the exact report-subscriber snapshot. No subscribers or a throwing
subscriber is visible as terminal-delivery rejection. Timeout and rejection
remain sticky quarantine evidence even if later diagnostic cleanup completes.

## Runtime publication admission

The runtime publication gate is shared with profile actions. A temporary
`PublicationBusy` result is therefore backpressure, not proof of a bad BLE
lifetime. Sink creation requires an explicit bounded runtime-operation timeout
used for publication admission and any unexpected pending terminal delivery. For
both Pro and standalone Joy-Con publication, the sink waits with the runtime's
monitor-based notification seam and retries only `PublicationBusy` until the
deadline. It does not poll.

Lifecycle closure, frame rejection, subscriber rejection, dependency
exceptions, and admission timeout remain distinct diagnostics and are never
retried as backpressure. A false wait is followed by one detailed admission
attempt so a concurrent lifecycle transition is reported as lifecycle closure
rather than mislabeled as timeout. Joy-Con mapper state advances only after the
runtime returns `Published`; rejected delivery cannot consume timestamp/counter
state.

The successful steady-state canonical-to-profile-to-runtime path is allocation
free for Pro and standalone Joy-Con frames. The diagnostic wait counter changes
only when actual runtime contention is observed.
