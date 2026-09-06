# Mapping-owned stick precision migration

Status: the shared stick mapper now retains fractional coordinates through
profile transforms, directional remapping and final Xbox360/One and Switch2
encoding. This is a source/software-validation milestone, **not production or
hardware certification**. Remaining feature and physical validation gates are
listed below and in the controller-platform ledger.

## One authoritative mapped value

`DS4MappedStickAxis` stores a fractional coordinate in the existing profile
domain (0..255, center 128). `FromSigned` retains all 65,536 signed-16 source
values; the compatibility byte projection matches the existing Switch2
quantizer. The default value is neutral. NaN, infinity and out-of-range
coordinates are rejected. Pro and standalone/joined JoyCon projections seed
these axes from their logical, orientation-adjusted calibrated source values.

The four axis fields in `DS4State` own control state. Existing `LX/LY/RX/RY`
byte properties read a compatibility projection, but **every byte assignment
replaces precision**, including a write of the same byte. State constructor
and `CopyTo` copy the typed axes; `CopyExtrasTo` never does. Immutable raw
  source metadata cannot undo a remap, suppression, macro, gyro or OSC override.

This changes public byte fields to properties, removes unused public
filter-history implementation fields, and changes `DS4StateFieldMapping.axisdirs`
from a byte array to a readonly typed-store reference. That store retains byte
indexer/Length source compatibility, not the complete array API. Rebuild all consumers: assemblies
compiled against those old fields are not binary-compatible. No supported
repository consumer was found relying on their field/reflection ABI.

`PostMapStickData` also exposes synchronized compatibility properties in place
of fields. Its production submit/reset/consume APIs are authoritative;
`Mapping.gyroStickX/Y` are diagnostic compatibility mirrors, not an injection
API. External callers that previously wrote those public implementation arrays
must migrate instead of expecting their writes to drive production output.

## Migrated operations

- Rotation uses the same trigonometry and clamp, but retains fractional
  coordinates when either coupled axis is precise. Legacy-only input retains
  the old byte truncation.
- User drift offsets keep the existing integer profile settings and clamp.
  Precise input retains its fraction; legacy input keeps the old result.
- Sensitivity and square-stick wrappers use the same existing equations.
  Sensitivity retains per-axis precision; square-stick promotes both coupled
  outputs if either input is precise. Center checks use the actual coordinate,
  not the rounded byte. Every legacy write still truncates at its original
  stage, including when transforms are chained. The existing square-stick
  trigonometric core is unchanged.
- `DS4StickProfileTransform` contains the existing shared radial/axial deadzone,
  anti-deadzone, max-zone/output, vertical-scale, outer-binding and output-curve
  equations. Both sides call it in their original order; radial operations
  couple precision, while axial operations retain independent ownership.
  `LegacyStickProfileOracle` freezes the pre-migration equations and cast sites
  for exhaustive byte-grid comparisons. The final outer binding remains an
  intentionally byte-valued binding, computed from the full precise vector.
- Custom curves capture a compiled immutable continuous evaluator once per
  coupled pair, not an interpolation of the rounded byte LUT. See
  [continuous-bezier-evaluation.md](continuous-bezier-evaluation.md) for solver,
  fallback and near-flat numerical behavior. Legacy byte LUT behavior remains
  unchanged; that existing mutable LUT does not provide an atomic profile-edit
  snapshot across all legacy/precise axes.
- Field-map population and output own typed values in one backing store.
  Direction remapping scales precise signed magnitude about center 128 using
  the asymmetric 128/127 extents. Legacy remapping keeps `255-value`. Explicit
  suppression/None/macro/button byte writes retire the affected precise value.
- Angle calculation, mapped-stick activation/mouse reads and the three Switch2
  stick-scroll/direction/assist lanes use fractional profile coordinates.
  Neutral OSC input leaves the current axis untouched; explicit OSC input
  replaces it. Strongest-contributor selection uses the actual coordinate and
  retains the current owner on ties. Deferred gyro contribution does not own a
  copy of the prior physical winner, which could otherwise replay after release.
- Xbox360 and Xbox One use the final mapping-owned signed-16 coordinate, and
  Switch2 uses the final unsigned-12 coordinate. Legacy byte sources keep the
  exact historical float/integer converters. Steering overrides retain their
  final authority. Sony targets still quantize to their actual 8-bit domain.
  No encoder reads raw source metadata to restore a discarded axis.
- Anti-snapback uses the existing segment/center-circle geometry and strict
  history expiry. Fuzz uses the existing radial movement threshold and exact
  endpoint exceptions. Both operate on typed coordinates and retain typed
  held values. Fuzz's deliberate suppression is not source quantization.
- Anti-snapback replaces the allocating LINQ closure and growing queue with
  a preallocated 8,192-point ring per stick. This accommodates the full 1-second
  UI window at 8,000 samples/s, including its boundary. Overflow increments a
  counter and bypasses only anti-snapback decisions while continuing to record
  fresh samples; decisions resume only after missing history expires. Another
  overflow extends that boundary. No input event queue is involved.
- Timeout is clamped to the UI range 0..1000 ms. Invalid/non-finite or out-of-UI
  delta disables anti-snapback; negative fuzz disables fuzz. This intentionally
  replaces unchecked malformed-profile behavior. Ordinary valid legacy
  settings retain the original geometry and threshold decisions.
- Timing comes from QPC converted to monotonic milliseconds, not wall clock
  or coarse `Environment.TickCount64`. No sleep or pacing loop is added.
- Input and preview pass their actual controller owner. Weak owner identity,
  profile revision/reset, physical/transport generations, pair/presence/layout
  and rotation changes fence history. Profile/UI reset requests are atomic;
  only the input/preview owner mutates its filter history.

Storage is bounded, but worst-case work is still linear in active history.
Maximum-window CPU-only observations and allocation tests are not Windows
presentation or physical-to-game latency results.

## Deferred gyro/touch ownership

The legacy joined Joy-Con path can have two input threads contributing to one
slot. An operation captures the accumulator epoch before its profile/activation
calculation and validates that same token when publishing. Reset, submit and
consume use a dedicated short gate around fixed scalar state. No profile math,
callback, controller I/O, logging or allocation is performed while holding it.
This synchronization is required by the demonstrated two-writer race, not a
pacing mechanism; contention/scheduler-tail latency still requires measurement.

Profile resets, Mouse owner construction/reset, exact slot retirement and
reports which skip custom mapping retire pending/current gyro contributions.
A delayed producer from the prior epoch cannot be relabeled by a new producer.
The immediate joined-gyro merge preserves a stronger physical value but retains
only the actual gyro candidate for the next primary report, never a snapshot
of that physical winner. Current gyro state is owned by the accumulator;
compatibility mirrors cannot bypass admission.

This does not serialize the entire pre-existing legacy joined-controller
shared mapped-state presentation path. That separate concurrency and hardware
validation gate remains open; Switch2 joined publication has its own existing
serialized runtime owner.

## Limits and remaining gates

- Triggers, outer bindings, and generated gyro/touch-stick/OSC byte outputs
  retain their existing value vocabulary. Expanding a trigger to Xbox ten-bit
  wire format cannot recover precision it never carried. This work does not
  claim full-precision analog stick-to-trigger remapping.
- Flick-stick current/previous source coordinates and right-stick mouse
  delta-acceleration still read legacy byte input. They do not overwrite the
  virtual stick coordinate, but full mouse-feature precision parity remains
  separate work.
- Per-unit raw travel calibration, controller-operated confirm/cancel profile
  selection, full source/target/profile integration inventory, BLE/USB device
  matrix, actual game feedback, latency/soak measurements and installer gates
  remain open. No sub-millisecond or non-inferiority claim follows from these
  tests, and no native-driver backend was added.
- Whole-profile concurrent-edit atomicity and the existing legacy joined
  controller shared mapped-state presentation path are not established by a
  typed coordinate alone. Accumulator retirement must be separately fenced;
  final validation and any remaining concurrency findings belong in the ledger.

The focused tests include all 65,536 signed positions into Xbox wire encoders,
all 4,096 calibrated 12-bit positions through identity profile/field mapping
into Switch2 wire encoding, frozen legacy byte grids, non-linear/chained
production profiles, explicit overrides and warmed allocation assertions.
These are not substitutes for the remaining complete feature/hardware matrix.
