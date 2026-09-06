# Switch 2 body-rumble profile gain

## Source audit

The comparison source is Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`.

- `src/config.py` persists `vibration_strength` per emulation/rumble-mode
  category.
- `src/gui.py` exposes the working value as a 0..10 Strength slider.
- `src/controller.py::_compute_vibration_config` consumes it in
  `set_vibration`; the control is not merely advertised UI. It applies
  controller- and mode-specific multipliers before the BLE/USB HD-rumble
  report is encoded.
- The same source persists and displays `rumble_delay_ms`, but repository-wide
  source inspection finds no feedback or controller consumer at this commit.
  It is dead UI, not an operative delay feature, so DS4Windows does not add a
  misleading latency control.

## DS4Windows adaptation

DS4Windows already persists one profile-level `RumbleBoost` value in the
range 0..200 percent. Before this change, the VIIPER-to-Switch-2 canonical
feedback route bypassed `ControlService.SetDevRumble`, so that existing
profile value did not affect a physical Switch 2 controller.

`Switch2HdRumbleBodyTuning` now carries that existing value through the
shared virtual-feedback session and the one transport-owned delivery sink:

- 100% is the byte-compatible calibrated default.
- 0% mutes body/native haptics but does not silently disable independently
  configured Xbox impulse-trigger conversion.
- 200% is saturating; no packed 10-bit field can wrap above 1023.
- Canonical body-low/body-high values are scaled after the licensed SDL
  amplitude basis conversion and before they are combined with an impulse
  lane.
- Native Switch 2 and DualSense-derived rich groups retain every control code,
  physical side, and chronological subframe. Only the two packed amplitude
  fields in each subframe are scaled.
- A non-default profile gain is allocation-free and does not add a queue,
  timer, worker, input-path branch, or physical writer.

The Profile Editor exposes the same `RumbleBoost` binding inside **Switch 2
Controls** as **Body rumble strength**. This is intentionally another view of
the existing value, not a duplicate setting.

## Optional Xbox-style body carriers

The same pinned Switch2Connect source exposes an operative 1-through-10
ordinary-rumble Frequency control. Its default `VibrationData` carriers are
low `0x0E1` (225) and high `0x1E1` (481). For the ordinary Xbox path it keeps
the low carrier at 225 and computes the high carrier with:

```text
factor = (level - 1) * 4 / 81
high = floor(241 + 240 * factor)
```

This yields the exact level table `241, 252, 264, 276, 288, 300, 312, 323,
335, 347`.

DS4Windows adapts that behavior as an explicitly opt-in profile policy rather
than changing the established native path:

- **Use Xbox-style body rumble carriers** defaults off. Existing profiles and
  native Switch 2/DualSense rich feedback therefore preserve every supplied
  carrier code exactly.
- **Xbox body frequency** is persisted in the inclusive range 1 through 10,
  defaults to 10, and falls back to 10 if legacy or malformed XML is outside
  the domain.
- When enabled, canonical coarse body synthesis and source-preserved body
  groups use the table's high carrier and the fixed 225 low carrier. Side,
  amplitudes, and all three chronological subframes remain unchanged.
- Xbox impulse-trigger conversion remains an independent overlay. An active
  side-local impulse retains its configured 300--481 high carrier; the body
  policy cannot replace it. The low body carrier remains 225.

Together, native/default versus Xbox-style carrier selection supplies the
operative part of the reference's Switch/Xbox rumble-mode choice without
duplicating its mode-specific strength stores or exposing dead delay UI.

The carrier selection is a pair of scalar fields in the existing immutable
body-tuning snapshot. It adds no timer, queue, worker, allocation, or input
hot-path work.

## Lifecycle rules

The delivery sink snapshots body gain and carrier selection with the feedback
policy and impulse tuning when a delivery is admitted. An outcome-uncertain
write retains that exact body tuning for its byte-identical retry. A later
profile change can refresh only a new presentation; it cannot mutate a
possibly applied report.

Stop remains transport-owned, immediate, neutral, and independent of profile
gain. USB and Bluetooth use the same synthesis and retry rules while retaining
their separate framing/counter implementations.

## Evidence boundary

Automated tests prove scaling arithmetic, saturation, the exact 1-through-10
carrier law, default/native compatibility, impulse independence,
source-preserved side/amplitude/subframe identity, live refresh, uncertain
retry stability, allocation behavior, and traversal through both USB and
Bluetooth lifetimes. They do not establish perceived intensity, actuator
safety margins, frequency response, or acoustic equivalence on physical
hardware; those remain portable hardware validation items.
