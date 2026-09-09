# Read failure before Stop: recovery validation, 2026-09-09

## Corrected report and scope

The user clarified the sequence: a controller logs read failure 995 first, and
pressing Stop afterward leaves the application unable to restart its controller
service. RC4.3 reportedly works and RC4.5 does not. This supersedes the earlier
assumption that pressing Stop initiated the reporter's first read cancellation.

The reporter's controller model, transport and cancellation source are unknown.
The identical GUI failure text is used by DS4 and DualSense Bluetooth paths.
Windows defines 995 as an aborted I/O operation; it does not identify the caller
or prove physical unplugging. We must distinguish the original read failure
from the application's response to it.

## Reproduced corrected chronology

`ControlServiceReadFailureBeforeStopTests` injects a completed unexpected 995
through the actual pipelined-reader backend boundary, then runs the real
DualSense physical lifecycle, failure logger and removal notification. A bounded
event gate holds notification immediately before the real service handler.
Only after the failure has been logged does the fixture enter public
`ControlService.Stop`, release that gate and execute the real DualSense
lifecycle completion wait.

- Restoring the RC4.5 handler's synchronous retirement call reproduces the
  service-lock/lifecycle-completion deadlock: **one test fails**. The fixture
  interrupts only its own Stop thread to release the old cycle and drains its
  remaining workers.
- The queued handler shipped in RC4.5.3 passes: failure log precedes pending
  removal and Stop; one removal returns; the lifecycle completion is signalled;
  cold cleanup retires its empty synthetic HidHide binding.
- The combined chronology/removal/cancellation/input-pipeline checks passed
  **44 tests**. The temporary production baseline was restored byte-for-byte.

This establishes that failure-before-Stop can encounter the already-fixed
deadlock. It does not show Stop caused the initial failure. The fixture uses
real fallback service/lifecycle entry points with controlled read completion
and final-output boundaries; it deliberately stops before unrelated full-app
teardown and performs no native controller I/O.

## Additional DS4 Bluetooth recovery defect

The terminal DS4 Bluetooth read-error/nonrecoverable-timeout branch logged the
failure, then forced a new output report before notifying removal. RC4.5 commit
`ed0e9836d40dd6d88a589ef6891331b2eda0d7c5` restored synchronous control-pipe
effects for issue #84. That normal routing has physical A/B evidence and is
preserved. However, the forced post-failure report could now put a synchronous
control request on the failed input worker before retirement. A blocked output
would prevent removal and any Stop waiting for that worker from completing.

The new `RetireAfterTerminalBluetoothReadFailure` helper is called by the real
input-loop failure branch. It retains read-wait reset, output-worker stop,
disconnecting state, controller-clock reset and removal order, but makes no new
physical effect write or audio-effect publication. Failure diagnostics and the
existing timeout-recovery decision remain unchanged. Ordinary effects, CopyCat
selection, audio routing and CRC-failure handling are not modified.

A behavior-preserving extraction first reproduced **two failing tests**: a
fixture-owned blocked effect prevented removal, and an active audio lane
received a fresh terminal effect. Three controls passed. Removing only the
forced send made all five new checks pass. A combined focused run passed
**162 tests**. The fixture drives the actual production helper and effect
composition, not a duplicate retirement algorithm; native output is intercepted.

## Evidence and limits

Ignored local evidence directories:

- `isolated_results/read-failure-before-stop/`: old-handler red and current
  queued-handler green results.
- `isolated_results/read-failure-retirement/before/`: behavior-preserving
  forced-write baseline, two failures and three controls.
- `isolated_results/read-failure-retirement/after/`: 162 passing focused cases.
- `isolated_results/rc454-release-verification/`: versioned full-suite results.

The versioned, unfiltered Release/x64 full suite passed **4,927 tests, zero
failures, with 11 existing opt-in skips** (4,938 total), including the allocation
assertions. Result: `rc454-versioned-full.trx` in that final evidence directory.
Exact-source GitHub CI and release artifact verification remain publication gates.

Independent review found no additional blocker in these changes. Neither test
series captures the reporter's driver or establishes who originally aborted
that read. No retry, blanket 995 suppression, transport-policy reversal or
controller-session restart is introduced. The correction fixes a concrete
post-failure recovery hazard; it does not promise that Windows I/O can never fail.

RC4.5.3 and updater 2.0.6 were already published and remain immutable. This new
production correction is prepared separately for RC4.5.4, with the same broker
and the already-published generic updater. No installed application, driver or
physical controller was modified during this investigation.

References: [Microsoft error 995](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--500-999-)
and [the tested #84 transport requirement](https://github.com/hbashton/DS4Windows/issues/84).
