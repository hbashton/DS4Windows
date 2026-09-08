# Reported-issue follow-up, 2026-09-08

Starting checkpoints: DS4Windows `ed0e983`, VIIPER `07db0a8`.
This follows the [first reported-bug pass](2026-09-08-github-reported-bugs.md).
The user authorized cautious implementation of the four follow-up candidates.
Three matching source-level defects are repaired; the fourth remains an
investigation. This is not reporter acceptance or a release qualification.

## Delivered scope

| Report | Result | Evidence and acceptance limits |
| --- | --- | --- |
| [#94: startup preference](https://github.com/hbashton/DS4Windows/issues/94) | Owned read-only shortcut removal, disabled-task preservation, verified setting changes, and read-only Settings initialization. | [Startup ledger](2026-09-08-issue94-startup-preference.md); 31 startup cases, including a real isolated WPF checkbox binding. No live task/UAC test. |
| [#80: renamed executable setup](https://github.com/hbashton/DS4Windows/issues/80) | Exact-host package staging and process identification; correct, failure-contained alias sidecar creation. Existing package/path/hash protections retained. | [Renamed-host ledger](2026-09-08-issue80-renamed-executable-setup.md); 24 C# cases and eight mocked PowerShell cases in both Windows PowerShell 5.1 and PowerShell 7. Disposable install/migration acceptance remains. |
| [#69: blank VIIPER tray](https://github.com/hbashton/DS4Windows/issues/69) | Local tray initialization no longer depends on Explorer accepting the initial icon. Recovery is bounded and owned by the existing window thread. | VIIPER `docs/architecture/viiper-tray-startup-validation-2026-09-08.md`; original ordering reproduced as a failing fixture, repeated/race tests and lint passed. Interactive scheduled-login acceptance remains. |
| [#82: Sonic UltraSaturn slowdown](https://github.com/hbashton/DS4Windows/issues/82) | Not confirmed or marked fixed. Added bounded, read-only PnP/process-handle diagnostics and CI fixtures. | [Exposure ledger](2026-09-08-issue82-legacy-joystick-exposure.md); upstream duplicate-model/legacy-polling evidence is a lead, not a local reproduction. No guessed controller or service changes. |

## Additional defect found during validation

A full-suite rerun failed the existing queued gyro-calibration save test.
Investigation established that our own reader could prevent publication and
exhaust all three existing write attempts. A held-reader regression reproduced
the problem. Read/delete sharing alone did not fix Windows' overwrite-move
failure. Existing records now use `File.Replace`; first publication uses a
non-overwriting move. No delete-first or in-place fallback was added.

The [calibration ledger](2026-09-08-gyro-calibration-reader-contention.md)
records the failed intermediate approach, direct exception evidence, final
regression tests, and remaining best-effort durability limits under external
locks or filesystem failures. This is a persistence fix, not a claim of
improved active controller latency.

## Integration verification

- `reported-issues-second-pass-complete.trx` and the independent repeat
  `reported-issues-second-pass-repeat.trx`: **4,247 passed, zero failed,
  11 existing skips each**. Uses CI's existing exclusions for `CheckSettingsSave`,
  `CheckWriteProfile`, and `CheckJaysProfileRead`; no new exclusions.
- `reported-issues-second-pass-allocation.trx`: **203 passed** (31 startup,
  24 renamed-host, and all 148 allocation-named cases).
- `gyro-reader-replace-and-allocation.trx`: **157 passed** (nine calibration
  persistence cases and all 148 allocation-named cases), on the final
  replacement implementation.
- Installer state-machine, exact-SID startup registration, localization package,
  renamed-process guards, and read-only joystick diagnostic fixtures passed.
  The two new PowerShell fixture suites are wired into CI.
- VIIPER tray: normal tests repeated 50 times, race tests repeated ten times,
  `go vet`, selected command/startup tests, and Linux no-op tray cross-build
  passed. Final tray lint reported **zero issues**. The root review separately
  reran the final race tests and lint successfully. The pinned upstream files
  retain provenance/license; only their Win32 naming style has a new lint
  exception, not correctness/error checks.
- Independent source reviews found no material blockers in startup changes,
  exact-host setup, tray recovery, diagnostic scope, or final calibration
  replacement. Existing application compiler warnings were not suppressed.

No active application was restarted or replaced. No controller, Task Scheduler
entry, registry setting, startup folder, HidHide policy, installed driver,
Program Files content, or user's calibration file was changed by validation.
No issue was closed/commented on, source pushed, installer built, or release
published in this pass. Live/disposable-environment acceptance above remains
necessary before claiming these changes validated in production.
