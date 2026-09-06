# Report diagnostics ownership

Status: implemented and software-tested, 2026-09-02. This is not a hardware
latency measurement or a claim that every legacy report callee is nonblocking.

## Boundary

All physical-controller/virtual-output combinations entering the canonical
`ControlService.On_Report` path defer its direct device-error, lag-log,
first-profile, startup-summary and tray-battery diagnostics. Mapping, virtual
submission, synthetic input commit and lightbar state updates remain on the
existing path. The primary path publishes diagnostics after those operations.
A secondary joined controller publishes its own diagnostics before its early
return, after any existing secondary gyro mapping.

The worker never queues or coalesces canonical input. Only optional diagnostics
are latest-value observations. Error/lag/profile/startup updates within a facet
can be coalesced while logging is slow; this is not a lossless diagnostic log.
Startup details are one deferred summary, not inline per-stage formatted logs.

## Identity and ownership

Cold registration creates one `ReportDiagnosticsWorker.Source` for an exact
controller lifetime. Legacy report/removal delegates capture it. Typed legacy
bindings also retain it for exact handler detachment and retirement. Switch 2's
full-token authenticated reversible StageRecord owns it and passes it to the
same canonical mapper; terminal publication and cleanup retire that exact
handle. No report-time slot/device lookup grants a new source to an old callback.

Registering a successor revokes the old source, including same-device-object
ABA reuse. Retiring an old source uses compare/exchange and cannot remove a
successor. Pause revokes every handle; Resume permits new registrations but
does not revive any old handle. Diagnostics availability is optional to Switch 2
canonical preparation/dispatch and registration failures are counted.

Each source has three preallocated value buffers. A serialized producer and
single worker exchange buffer ownership. Neither reads the other's writable
buffer. Snapshots retain immutable string references, scalar observations and
exact source identity, not a borrowed mutable `DS4State`. Accidental concurrent
producers are rejected/counted rather than serialized behind a monitor.

Producer-owned cumulative facet revisions and consumer delivery cursors ensure
that an initial-profile observation survives later battery/startup coalescing
without replaying on every subsequent publication. Initial battery and current
battery are distinct; lag-event latency and startup latency are distinct.
Unchanged battery observations do not wake the worker, but a tray-icon policy
revision re-arms an unchanged percentage when returning to battery mode.
Switch 2 uses its runtime's compatibility battery, not the unpopulated legacy
state battery field; percentages remain the existing compatibility bands, not
newly inferred charge measurements.

## Teardown and UI

No report-side monitor, formatting, filesystem probe, logger or UI callback is
used by this diagnostics lane. Cold lifecycle transitions use a short lock,
but no callback runs under it. Pause, exact retirement and Dispose never wait
for the worker's logging/UI callback. Dispose is idempotent and does not join
itself. Active publisher accounting and worker exit jointly govern wake-event
disposal. Dispatch and failure-logger exceptions cannot kill the consumer.

Already-admitted historical logs may finish after retirement; they cannot be
recalled. Pending stale sources are rejected. Battery events carry the exact
source through the UI queue, and the tray checks both its validity and current
icon policy when the dispatcher executes the action.

## Verification and limits

`ReportDiagnosticsWorkerTests` covers facet revisions, initial/current battery,
independent latencies, source replacement/ABA, pause/resume, slot independence,
blocked dispatch, shutdown and active-publisher races, failure recovery,
dispose-from-dispatch, zero managed allocation in a warmed producer and coherent
concurrent snapshots/final delivery. Integration tests exercise the actual
secondary `On_Report` return, Switch 2 canonical host admission/terminal/cleanup,
optional-observer refusal, runtime battery capture and stale tray callbacks.

The final full default-runtime suite passed 2,949 tests, with 3 opt-in live-audio
skips (`report-diagnostics-full-20260902.trx`). This does not establish physical
latency, Bluetooth reliability, game compatibility, haptic/LED onset or the
unrun production matrix. Legacy touch-toggle/mapping-action notifications,
other physical-device events, legacy DSU and OSC lifecycle paths remain separate
audit boundaries; this change does not certify the complete transitive callback.
