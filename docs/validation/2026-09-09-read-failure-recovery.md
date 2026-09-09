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

## Published result

The pending-publication statements above are historical validation stages.
[VIIPERRC4.5.4](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.4)
was published on 2026-09-09 at 23:05:07 UTC as an unsigned prerelease, release
`385896773`, from immutable tag/source `f15c3001ccb5ba577a20c9174e252e2835383631`.

- Exact-source [CI 34413872665](https://github.com/hbashton/DS4Windows/actions/runs/34413872665)
  passed: 4,924 tests and 11 opt-in skips, with the workflow's existing three
  snapshot exclusions. Those three also passed in the unfiltered local full run.
  Offline installer layout and hosted MSI install/repair/uninstall gates passed.
- Twenty additional runs of the six new recovery cases passed: **120 cases,
  zero failures**. Results are under `rc454-release-verification/repeated/`.
- [Draft build 34414663236](https://github.com/hbashton/DS4Windows/actions/runs/34414663236)
  and [public verification 34415309349](https://github.com/hbashton/DS4Windows/actions/runs/34415309349)
  passed. The public workflow verified the draft's bytes without rebuilding or
  overwriting assets.
- All 13 release assets were downloaded and verified. The portable ZIP contains
  551 files, 296 runtime dependency assets and 23 language satellites. Application
  and installer versions are `5.0.5.4`; application product/marker is
  `VIIPERRC4.5.4`. VIIPER, Xbox persona, notices and dependency installers retain
  their pinned identities; .NET/Desktop runtime is 8.0.30.
- Portable SHA-256: `3BFCD863B1A4933980091099ADE30B10D6D15406BAC626D2231AC8953546077E`.
  Installer SHA-256: `B62B456C7542CBD8633D3545BFBE8B5E47660CA91C39A5D4862D09C13F08B04E`.
- The unchanged compiled updater 2.0.6 resolver accepted **unmodified public**
  RC4.5.4 metadata and verified the actual receipt, ZIP, EXE/DLL and release marker.
  No simulated publication envelope was used for this public check. Earlier
  draft and untagged-URL negative controls both rejected their inputs. No updater
  worker was launched; this is package acceptance, not a live update transaction.

Local proof: `isolated_results/rc454-verification/final/VERIFICATION.json`
(SHA-256 `FAABEF99798D29EAA46B92B0C44B7C454F98CDD440A4C38C97DFFB79F9653C06`),
plus `isolated_results/updater-2.0.6/PUBLIC-RC454-UPDATER-VERIFICATION.json`
in the sibling updater repository. Downloads are in the Desktop folder
`DS4Windows-RC4.5.4-Release`. These files were not installed or executed.

The final source review still did not attribute the initiating cancellation.
The pipelined backend can synthesize 995 for a closed/stale-generation handle;
DS4's ordinary wrapper can return a close-generation error without explicitly
setting the caller's ambient native error. Both observations limit diagnostic
attribution; neither proves a spontaneous active-read cancellation or justifies
a speculative retry/transport change. Exact-DS4 cleanup coverage is compositional
(retirement helper plus typed lifecycle tests), not a native end-to-end hardware
reproduction. The recovered application deadlocks and the initiating read error
remain separate claims.
