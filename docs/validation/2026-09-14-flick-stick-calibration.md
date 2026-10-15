# Bindable 360-degree flick-stick calibration

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


This records the initial implementation in `62c6a6e`. The subsequent
[Axis Config revision](2026-09-14-axis-calibration-and-trigger-ui.md) replaces
the remapper UI described below; the runtime and binding compatibility tests
remain relevant.

Implements [issue #104](https://github.com/hbashton/DS4Windows/issues/104), using
the existing profile's left/right Real World Calibration rather than a second
mapping system or separately stored sensitivity. Usage and primary references
are documented in [the calibration guide](../flick-stick-calibration.md).

## Behavior and containment

- New output IDs 45/46 append without renumbering existing bindings. Both normal
  and shifted bindings round-trip through the actual ProfileDTO XML serializer.
- Each tap requests one approximately one-second turn, with total mouse counts
  rounded once from `360 * RWC`. The normal per-controller mapper handles the
  action, consumes its original game button, and keeps physical observations
  unchanged. Reports produce cumulative integer differences, not lossy
  per-tick fractional mouse values.
- Held inputs do not repeat; presses while turning are ignored, not queued.
  RWC is captured at the start. The two calibration choices never combine into
  an accidental double turn on simultaneous activation.
- Connection/mouse-owner, output-handler, profile revision, cancellation
  generation, invalid availability, clock rollback and over-250-ms report-gap
  boundaries cancel old motion. Cold resets use atomic generations rather than
  racing the mapper's per-slot state. Terminal Nintendo neutral publication
  cancels calibration; an ordinary report without motion does not.
- Macro-recording/unmapped frames cancel instead of catching up on resume.
- Calibration-only backend flushing preserves pending gyro/touch motion and
  uses an atomic FakerInput operation. A bounded pair of movements can require
  two signed-16-bit reports: counts survive, wheel deltas appear only once, and
  held buttons remain held. Existing normal/immediate mouse methods, input
  transports, virtual-pad presentation, audio and haptics are unchanged.

## Evidence

The final focused Release run passed **91 tests, no failures or skips**:

- Exact counts at 125/250/500/1,000 Hz and irregular intervals; fractional RWC,
  valid maximum RWC, malformed values, one-shot latch and cancellation cases.
- Zero managed allocations in 5,000 measured active/idle calibration frames.
- Actual MapCustom execution with an in-memory controller and recording output
  backend: no leaked game button, preserved input snapshots, shifted bindings,
  terminal/nonterminal behavior and reconnect without a profile reload.
- Bounded output splitting, buffered movement preservation, default immediate
  dispatch, and the bundled managed mouse-report wrapper's wheel/button reset
  semantics. No native driver connection is made by these tests.
- Actual production tab markup rendered offscreen in Default/Dark themes at
  630/812 DIP widths and 96/144 DPI. Six layout tests pass; the main agent also
  inspected the rendered default and 150%-scale dark images for clipping.

An intermediate test build caught a missing direct reference to the existing
FakerInput managed wrapper. The test project now references the same bundled,
platform-matched assembly as the application; no package/version was changed.
The rerun compiled and passed all focused checks.

Artifacts are under `isolated_results/flick-calibration-20260914/`, including
`calibration-focused-complete.trx` and six `flick-calibration-*.png` renders.
The complete Release suite passed **5,913 tests, zero failures**, with 11 opt-in
tests skipped (three live audio capture and eight Go/DS4 process interop cases),
5,924 total. Evidence is `calibration-full.trx`. No failing assertion was disabled.

## Validation boundary

These tests do not run a game, inject Windows input, connect a driver or replace
the installed application. Actual in-game degrees depend on the user's mouse
look/sensitivity/acceleration; that is precisely what this manual calibration
action measures. No claim of universal camera-angle detection is made. This
source work does not alter already-published RC4.6 release assets.
