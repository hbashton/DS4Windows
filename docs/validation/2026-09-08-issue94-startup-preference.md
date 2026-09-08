# Issue #94: startup preference handling, 2026-09-08

Status: deterministic startup-state defects corrected and covered by isolated regressions. The original reporter's Windows task/shortcut state is unavailable; this does not establish which defect caused that specific report.

[Issue #94](https://github.com/hbashton/DS4Windows/issues/94) reports that turning off **Run at startup** does not survive reopening DS4Windows. Source inspection identified an owned read-only shortcut being silently left behind, task repair recreating disabled tasks, and the Settings view updating its checkbox before confirming the Windows registration change. Opening Settings also performed startup mutations.

## Changes

- Explicit removal handles an owned read-only shortcut and verifies the resulting registration. A rejected, canceled, or ineffective change reports failure and displays the last verified Windows state; it does not pretend the setting was saved.
- Opening Settings only reads registration state. Automatic task retargeting only repairs an enabled, recognized task for the current Windows account; disabled/pending and foreign tasks are not enabled or deleted.
- Task replacement no longer deletes the working registration before registering its replacement. Explicit elevated removal uses the existing fixed-command helper, validates elevation and the same user's SID, and does not accept an arbitrary task name or executable path.
- Passive VIIPER startup maintenance leaves disabled/pending startup state alone. Explicit startup disable still requests VIIPER task removal and reports an unsuccessful removal. A VIIPER failure does not falsify the already-verified DS4Windows setting.

Ownership checks intentionally refuse a same-named shortcut/task with another target/account or an unrecognized contract. Moving a portable copy and merely opening Settings no longer rewrites its old startup shortcut. An explicit startup-mode change updates/consolidates recognized registrations; startup task retargeting remains launch maintenance. Cross-registration changes are not atomic: on partial failure the UI rereads and reports the actual surviving state.

## Validation

The root build lane recorded `DS4WindowsTests/TestResults/issue94-before.trx`: **5 failed, 3 passed, 8 total** before the fixes. The expanded production-policy/ViewModel regression run, including an isolated STA WPF bound checkbox rejecting an ineffective disable, recorded `issue94-binding.trx`: **29 passed, 0 failed**.

Tests use injected registration operations and temporary inert shortcut fixtures. The bound-checkbox check opens no window and creates no application. Two additional source-bound guards check that passive VIIPER launch has no task-removal call and explicit disable checks removal failure; their subsequent result is not included in the 29-test count above. These wiring checks read source rather than invoking Task Scheduler.

The expanded 31 startup cases subsequently passed in
`reported-issues-second-pass-allocation.trx` together with 24 renamed-executable
tests and all 148 allocation-named tests (**203 passed, 0 failed**).

No test changed this machine's actual startup folder, scheduled tasks, controller state, or running DS4Windows/VIIPER instance. UAC interaction and the reporter's exact installed configuration have not been exercised live. The full suite and final integration status are recorded separately by the root build lane.
