# VIIPER Windows tray dependency patch

This directory contains the Windows-used portion of `fyne.io/systray` v1.12.1:

- Upstream: https://github.com/fyne-io/systray
- Pinned commit: `3266b44d2302c29b14a67b7f476fe0c1b05cb5b6`
- Original files: `systray.go` (325 lines), `systray_windows.go` (1,147 lines),
  and the complete upstream `LICENSE` (Apache-2.0).
- `systray.go` gains a Windows build constraint and a modification notice.
  No other platform implementation is copied or changed.
- The VIIPER Windows tray imports this internal package. The upstream module
  pin remains unchanged; this is not a general dependency upgrade.

## Why this patch exists

[DS4Windows issue #69](https://github.com/hbashton/DS4Windows/issues/69)
reports a blank, noninteractive VIIPER icon during scheduled startup. In the
pinned dependency, `initInstance` created the HWND and a notification record
containing only `NIF_MESSAGE`, then returned an initial `NIM_ADD` failure.
`registerSystray` consequently skipped menu creation, `initialized`, and
`systrayReady`. A later `TaskbarCreated` handler could add that same blank
notification record without ever completing initialization. This is a
source-demonstrated mechanism matching the symptom; the reporter's actual
Task Scheduler/Explorer timing has not been reproduced on their machine.

The upstream Windows implementation still contained that sequence at
`f60f01be81c6` when checked on 2026-09-08. The existing VIIPER
`runtime.LockOSThread` fix is retained; this addresses a separate failure.

## Minimal behavioral changes

1. Create the window and menu and publish local readiness independently of
   Explorer's current ability to register a notification icon.
2. Retain icon and tooltip updates while shell registration is pending.
3. Try `NIM_ADD`, then `NIM_MODIFY` for the same HWND/ID when ADD fails. The
   latter handles a queued `TaskbarCreated` after an already successful ADD
   without duplicating or forgetting the existing icon.
4. On failure, use an HWND-owned 500 ms `WM_TIMER`, capped at 60 attempts per
   recovery cycle. Stop the timer on success, exhaustion, or shutdown. A later
   `TaskbarCreated` starts a new bounded cycle; retries never recreate the
   window, menu, or callback set. No forever-polling goroutine is introduced.
5. Quit posts `WM_CLOSE`; timer cancellation, icon deletion, and exit callback
   run on the existing window thread. Native resource initialization errors
   retire any partial window rather than pretending the tray is ready.

The changed upstream file has a prominent modification notice. The new
`registration_windows.go` contains the recovery state; its tests call the same
initialization, registration, and window-message dispatch methods with mocked
Shell/timer callbacks. Tests create no real windows, tray icons, controller
connections, registry values, or scheduled tasks.

Minimal local lint maintenance also replaces deprecated `ioutil.WriteFile`
with `os.WriteFile`, removes redundant casts and an unused lazy procedure,
records intentional Win32 return-value discards, reports owned cleanup errors,
and checks failed taskbar-message registration and bitmap creation. The only
new lint exclusion is ST1003 naming in the two pinned upstream-copy filenames;
correctness/vet/error checks on the repair code remain enabled, and the
existing test lint policy is unchanged.

## Validation

The regression test `TestExplorerUnavailableDoesNotSkipMenuOrReady` first ran
against the extracted original ordering and failed with:

```text
shell failure poisoned local tray initialization:
err=Explorer not ready window=1 menu=0 ready=0 add=1
```

The corrected ordering passes that same test. Additional cases cover complete
icon recovery, no duplicate local initialization, retry exhaustion, later
Explorer arrival, duplicate ADD/MODIFY recovery, unrelated/stale timers,
same-thread callbacks, shutdown cancellation, timer-arm failure, and native
resource failures. See the repository validation ledger for the exact test
commands/results. No live scheduled-startup acceptance is claimed.
