# Axis Config calibration and legacy trigger UI cleanup

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Requested changes

- Move the 360-degree calibration feature to Axis Config, not button remapping
  or Special Actions. Each LS/RS Flick Stick panel now has a typed **360° test
  button** selector, using that stick's existing Real World Calibration.
- Remove Axis Config's legacy Trigger Effect, Trigger Start, and Trigger
  Strength controls. Keep normal trigger input tuning and Trigger Lab.

## Compatibility and runtime boundaries

- Both profile serializers persist `CalibrationTrigger` as a canonical source
  button name under each stick's `FlickStickSettings`. Missing/invalid fields
  become unassigned; defaults remain off. Universal choices include supported
  extended buttons without controller-specific hiding.
- Experimental saved remapper actions 45/46 still load and execute. They are
  no longer offered by the remapper UI. No existing binding IDs are renumbered.
- The canonical mapper observes both configured buttons before consuming
  their source fields and initial default output. Same-button assignments
  produce one right-stick turn. Another button mapped into that destination
  is preserved. Stored bindings and physical snapshots are not overwritten.
- Assigning an analog trigger reserves both its soft and full-pull normal
  outputs. Independent physical features such as gyro activation, Game Bar,
  and mute shortcuts are unchanged; the guide recommends a spare button.
- Mode or source changes cancel unfinished turns and baseline held buttons.
  Existing owner/profile/reset/gap cancellation and exact count conservation
  remain intact. No timer, worker, input queue, or sleep was introduced.
- Review found the existing Flick Stick branches could dereference a missing
  controller before the calibration cancellation check. Both now skip removed,
  removing, unsynced, or missing controllers; owner-loss tests cover Axis Config
  and legacy action entry points.
- Only the legacy trigger UI and its unused view-model wrappers/catalog were
  removed. Saved effect data, serialization, Trigger Lab restoration, native
  game feedback, impulse conversion, and haptic transports remain unchanged.

## Validation

The first focused Release run passed **170 tests**, zero failures or skips,
covering calibration, mouse output, new settings and UI, and Trigger Lab.
Afterward, four additional mapper cases were added for active two-stage trigger
modes; the complete suite includes these.

The complete Release suite passed **5,972 tests, zero failures**, with 11
opt-in tests skipped, 5,983 total, in 1 minute 43 seconds. This includes the
existing Trigger Lab restoration, profile migration, native DualSense trigger,
Nintendo input/output, and haptics coverage. No assertions were disabled.

Production calibration panel markup was rendered offscreen in Default/Dark
themes at 360/640 DIPs and 96/144 DPI. Eight render cases plus binding/mode,
placement, and legacy-control absence checks passed. The main agent inspected
the compact dark 150% and wider light renders: text and selectors are not
clipped. Axis Config's existing scroll viewer contains the additional rows.

Evidence is under `isolated_results/axis-calibration-20260914/`:
`axis-focused.trx`, `axis-full.trx`, and eight
`flick-calibration-axis-*.png` renders.

No live game, Windows mouse injection, controller I/O, application restart,
installer, or release asset replacement was performed. In-game rotation still
depends on the game's mouse sensitivity; this is a manual calibration aid.
