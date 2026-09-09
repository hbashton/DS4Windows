# Controller diagram remapping repairs, 2026-09-08

## User-visible defects and fixes

- The Pro's C button had artwork but no interactive target/highlight. It now
  has a shared vector hit/highlight shape at the existing artwork's C position,
  a canonical `Switch2C` mapping-row lookup, and an accessible physical name.
  No artwork raster was replaced; the root independently inspected the
  existing Pro image and the normalized target position.
- Joy-Con hover labels now name the physical control in the selected
  orientation. Sideways left Capture is displayed as `Capture: Guide` for
  the default Xbox mapping, not `PS: Guide`. The output remains Guide; the
  established standalone mini-controller PS routing was deliberately preserved.
- Press-to-remap recognizes Capture and the validated Pro/Joy-Con C-button
  source field. Existing higher-priority button detection is unchanged.
- Live press-to-remap uses the canonical control-to-row dictionary instead of
  stale numeric offsets, which could select the wrong touch/stick/extra row.
- Offline profile readings use the chosen physical controller context, not
  hardcoded controller slot zero. Invalid context no longer silently borrows
  another controller.

## Evidence

The original C/physical-label regressions recorded **8 failed / 0 passed** in
`controller-diagram-before.trx`. After implementation and additional canonical
readout tests: **21/21 focused** and **133/133 broader mapping schema, artwork,
atlas, and capability tests passed** (`controller-diagram-*.trx`). Root source
review found no mapper/output-routing change or material blocker.

Tests combine behavioral geometry/label/canonical-source checks with explicitly
source-bound WPF target wiring assertions. They are not a live hover/click
acceptance test. No app was restarted or replaced and no user profile changed.
