# Log navigation stall — 2026-09-22

## Observed failure

An installed RC4.6.4 session intermittently became unresponsive when opening Log.
The process stayed alive and later recovered. A local heap dump captured the STA
thread inside `ItemContainerGenerator.DoLinearSearch` / `ContainerFromItem`,
called by `GridViewItemAutomationPeer.GetChildrenCore` while WPF updated layout
and accessibility during tab focus. The dump showed no held monitor or log-list
lock responsible for the stall.

The GUI collection held 31,436 entries (including repeated temporary Auto Profile
diagnostics). The heap contained 15,770 `ListViewItem` objects and 15,753 grid-row
automation peers. Those are heap counts, not a claim that every object was an
onscreen visual. The file logger excludes temporary messages, so disk log size
does not describe this GUI accumulation. Raw dumps remain local and are not
included in source or packages.

## Changes

- Retain the newest 1,000 GUI entries, evicting before appending. File logging is
  unchanged; temporary diagnostics remain GUI-only, as before.
- Replace producer-side per-message dispatcher scroll requests with one pending
  scroll on the dispatcher-owned item view, only while Log is visible. Resolve
  the current tail at dispatch time; cancel hidden/disposed work.
- Explicitly enable virtualization and container recycling for this ListView.
  Preserve normal WPF accessibility. No global theme or automation-peer override.
- Synchronize Clear and Export snapshots with producers and release locks even
  if collection subscribers throw. Handle WPF's synchronous reads during Clear.
- Open the selected view item on double-click instead of indexing a potentially
  newer source collection. Label the live view as the latest 1,000 entries;
  Export saves that retained view.

No controller input, feedback, audio, transport, profile-switching, or installer
code was changed for this fix.

## Regression evidence

Before the model change, tests reproduced both excess retention (1,250 instead of
1,000 rows) and a write lock left held after a collection subscriber threw.

All 17 targeted tests pass:

- Model: capacity/order, warnings/timestamps, stable snapshots, WPF Clear/refill,
  concurrent producers/snapshots/Clear, exception-safe Add/eviction/Clear.
- Bridge: 10,000 worker-thread messages while the STA is blocked, then WPF queue
  drain; pending changes followed by Clear/refill; source/view order agreement;
  no hidden scrolling; 100 visible requests coalesce to one current-tail request.
- Scroller: hidden bursts, coalescing, intervening Clear, hide cancellation, and
  disposal/unsubscription.
- Real Log XAML with Dark and Default themes, 1,000/2,000 entries: grid-row/cell
  accessibility, eight viewport jumps, tail realization, and recycling. Maximum
  realized containers were 39 (Dark) and 32 (Default).

Full Release x64 suite: **6,825 passed, 12 explicitly opt-in tests skipped,
zero failures**. The hardware and installation opt-in tests were not enabled.
Independent review found no blocking correctness issue in the patch.

The baseline fixed-viewport test was already virtualizing; it only lacked
recycling. It did not reproduce the entire long-lived live session's retained
container graph. The WPF test uses detached controls and the actual ScrollViewer
to drive offsets, not live-window tab/focus routing. WPF's asynchronous change
notification queue is not itself hard-capped by the source-row retention limit.
These tests do not claim an unlimited-rate logging guarantee or replace an
in-session smoke check after loading the candidate.
