# Switch 2 Joy-Con IR mouse profile lane

Status: implemented in the ordinary DS4Windows profile/mouse path and covered
by deterministic unit tests. No live-controller claim is made by these tests.

## Ownership

`Switch2JoyConRawInputStatus` retains the optical X/Y, roughness, and distance
observations decoded from each Joy-Con 2 common report. The profile lane reads
that fixed-size sidecar from `Mapping.MapCustom` and adds its relative velocity
to the same `tempMouseDeltaX` / `tempMouseDeltaY` accumulator used by existing
DS4Windows mouse mappings. The existing final rounding and
`outputKBMHandler.MoveRelativeMouse` call remain the only OS presentation
boundary. There is no second mapper, thread, queue, or direct `SendInput` path.
The lane also forwards the active sensor's physical stick through the same
profile-selected `outputKBMHandler` as vertical and, when selected, horizontal
wheel events; it does not create a side-channel injector.

The threshold bands, unsigned-16-bit coordinate wrapping, velocity scale, and
600 ms activation/verification policy are source-pinned to the GPL-3.0
Switch2Connect project at commit
`4487322a306f04efa27682e3f3a508635a84fd98`. The implementation is adapted to
DS4Windows' canonical input and profile ownership rather than copied as an
independent runtime.

The generic mapped-stick **Hold/Tap** scroll policy is source-pinned separately
to Switch2Connect commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`Controller._apply_joystick_scroll_wheel`. It is implemented as a fixed-size
edge gate in front of DS4Windows' existing four wheel actions, not as a second
mouse emitter or binding table.

The per-direction mapped-action **Hold/Tap** policy is pinned to the same
commit's `Controller._apply_joystick_tokens`. It is a separate fixed-size gate
because the donor intentionally gives custom Tap mappings an 80 ms pulse and
diagonal transition suppression, while scroll Tap emits the complete new
sector. The gate changes only the active state supplied to the existing
DS4Windows action owner; it never interprets a key, macro, mouse button, or
virtual-pad binding itself.

## Profile policy

The **Switch 2 Controls** section contains an opt-in Joy-Con 2 optical-mouse
setting with:

- sensor selection: Auto, Left, Right, or Both;
- stick scrolling: Up/Down or Four-way;
- independent left/right mapped-stick wheel activation: Hold or Tap;
- independent Up/Down/Left/Right mapped-action activation for each stick:
  Hold or Tap;
- independent left/right activation thresholds: Strict, Balanced, or Relaxed;
  and
- independent left/right sensitivity from 1.0 through 10.0, defaulting to 4.0.

Auto selects the right sensor for a joined pair and the only present sensor
for a standalone Joy-Con 2. Both independently projects every present side and
sums their velocities into the one existing mouse accumulator, matching the
reference's merged-producer ownership without introducing another output
worker. Existing profiles remain disabled by default.
Invalid enum or numeric values fail to Auto, Strict, and 4.0 during profile
loading.

While a sensor's verified optical-mouse mode is active, its physical stick
produces scrolling with the source-pinned 0.2 deadzone and 60-unit full-scale
law. **Up/Down** preserves the original vertical-only behavior for existing
profiles. **Four-way** also publishes the physical X axis through the horizontal
wheel lane supported by both SendInput and FakerInput. Horizontal Joy-Con
presentation is transformed back to the physical X/Y basis before this
calculation, so changing V/H layout does not rotate the scroll controls. Auto
and explicit single-side modes use only the selected sensor's stick; Both sums
the two independently active sides. No wheel event is emitted while merely
threshold-armed, after threshold loss, or across a lifecycle/profile fence.
Unlike the reference runtime, DS4Windows does not consume the stick from the
virtual-pad mapping while optical mouse is active; the same physical input
remains available to a game, preventing the mouse feature from silently
disabling controller input.

The ordinary Button mappings editor also exposes append-only **Mouse Wheel
Left** and **Mouse Wheel Right** actions beside the established vertical wheel
actions. This lets either stick on a Pro Controller 2, joined pair, or
standalone Joy-Con 2 reproduce the reference's generic four-direction Scroll
Wheel mapping without enabling optical mouse. Digital directions use the
existing press/release/repeat ownership, while analog sources retain magnitude
accumulation and use an independent horizontal remainder/timing lane so a
diagonal can deliver both axes. These actions are also recognized by the
existing FakerInput promotion policy for stick-mouse profiles; SendInput and
FakerInput still share the same `PerformMouseWheelEvent(vertical, horizontal)`
boundary.

Each physical stick also has a **Mapped stick-wheel activation** selector.
**Hold**, the compatibility default, leaves the established analog
magnitude/repeat path unchanged. **Tap** emits one wheel event when the stick
enters a new cardinal or diagonal sector. A diagonal emits both authored wheel
actions, and returning through the center rearms the same direction. Tap uses
the reference's 3% fallback center deadzone, magnitude-normalized 30-through-150
wheel step, and independent 30 ms per-stick throttle. A profile change,
controller/transport generation change, pair change, QPC timebase change, or
timestamp regression establishes a held-input baseline without emitting a
synthetic wheel event. Binding resolution, mode shifts, horizontal signs,
backend choice, synchronization, and controller input all remain owned by the
canonical DS4Windows mapper.

The same section exposes independent **Mapped stick-mouse sensitivity** values
for the logical left and right sticks. They apply only when the corresponding
canonical stick directions are authored as Mouse Up, Down, Left, or Right.
Five is an identity transform over DS4Windows' normal mapped-mouse speed, zero
suppresses movement, and ten doubles it. This preserves the existing global
mouse-speed, vertical-scale, acceleration, delta-acceleration, binding,
Mode-Shift, rounding, and SendInput/FakerInput owners while adding the donor's
missing per-stick adjustment. The scalar is accepted only for one validated
Switch 2 Pro or Joy-Con lifecycle source; non-Switch-2 and ambiguous sidecars
remain at identity. A standalone Joy-Con uses the logical left control because
the existing orientation projection places its only physical stick there.
When High-rate mouse presentation is enabled, the resulting already-mapped
continuous stick velocity joins the same one-owner 1 ms presenter used by gyro,
IR, and Stick Assist. The exact admitted stick delta is removed from the normal
report accumulator only after admission, so no mapping logic is duplicated and
every invalid, disabled, stale-interval, or terminal case falls back to the
ordinary report path without losing motion.

The **Mapped stick-action activation** table applies to each logical stick
direction authored in the ordinary Button mappings editor. **Hold**, the
compatibility default, preserves the established action level. **Tap** emits an
80 ms pulse for keyboard keys, macros, mouse buttons, touchpad click, and
virtual-pad buttons, triggers, or axis directions. The pulse enters the
existing synthetic-key/mouse reference counters or `ControlToXInput` queue, so
release ownership and the selected X360/DualShock/DualSense/Xbox One backend
remain canonical. Default analog passthrough, Mouse Up/Down/Left/Right,
absolute mouse directions, and all four wheel actions are intentionally
excluded because those continuous or scroll-specific modes have their own
source-pinned behavior.

Custom Tap uses the donor's eight-sector and 3% fallback center-deadzone law.
Center rearms the direction. A cardinal-to-diagonal transition pulses only the
newly introduced direction; the already-held cardinal is suppressed. The
inverse diagonal-to-cardinal transition suppresses a cardinal that was the
direction newly fired by that diagonal. This prevents a small diagonal wobble
from duplicating an authored action while retaining deliberate transitions.
Left/right sticks, all eight direction settings, and all pulse expiries are
independent. Profile, pair, device/transport generation, QPC timebase/order,
or source changes clear pending pulses and establish a held-input baseline.

The same left/right threshold settings also expose **Switch 2 Joy-Con Left IR
Sensor** and **Switch 2 Joy-Con Right IR Sensor** as append-only controls in
the ordinary Button mappings list. Their active state is the source-pinned
distance/roughness threshold—not the pointer-motion verification latch—so each
side can drive existing DS4Windows mouse buttons, wheel actions, keyboard
keys, macros, profile actions, or virtual-pad controls even when direct IR
pointer movement is disabled. They default to unbound and never consume or
rewrite a physical controller button.

The same sources are append-only **Shift Modifier** triggers 35 (left) and 36
(right). A profile can therefore reproduce the reference's per-side IR-only
mouse buttons through the ordinary mapper: edit a physical button, choose its
Shift Modifier action, select the matching IR Sensor trigger, then select a
mouse button or any other DS4Windows action. The physical button retains its
ordinary mapping while the sensor is inactive and changes only to the authored
shift action while active. Existing numeric trigger IDs 0 through 34 retain
their established meanings. This reuses profile XML, macros, key ownership,
mouse-button reference counting, and release handling instead of adding a
second conditional-click state machine.

## Lifetime fences and hot path

Projection state is per DS4Windows controller slot. It is cleared when any of
the following changes or becomes invalid:

- profile-switch revision;
- selected side, scroll mode, or activation threshold;
- pair epoch;
- physical-device or transport generation;
- QPC timestamp ordering or frequency;
- source contract/version, side presence, or common-motion provenance; or
- the profile feature's enabled state.

The first observation after a fence establishes the current coordinate and
emits no movement, so a reconnect or profile change cannot turn a large raw
coordinate difference into a cursor jump. QPC is converted to the existing
host-wide monotonic microsecond domain with overflow checks. The warm report
path is fixed-size and allocation-free.

## Verification

`Switch2IrMouseProjectionTests`, `Switch2IrMouseActivationGateTests`, and
`Switch2IrMouseProfileLaneTests` cover source-pinned thresholds, coordinate
wrap, activation, fail-closed configuration, Auto/explicit side selection,
profile/pair/device/transport/timestamp fences, disabled and unavailable
sources, conditional physical-stick scrolling in joined and horizontal modes,
vertical/four-way scroll axes, horizontal-presentation transforms, scroll
deadzone/release behavior, and zero managed allocations over 20,000 warm
calls.

`Switch2StickScrollTapLaneTests` cover center-to-sector arming, held-sector
suppression, full diagonal re-emission, center rearming, the 3% center deadzone,
the independent 30 ms left/right throttles, Hold bypass, profile/lifecycle/QPC
fences, signed vertical/horizontal canonical wheel resolution, and zero managed
allocations over 20,000 warm calls.

`Switch2MappedStickMouseSensitivityTests` cover independent left/right gain,
zero sensitivity, Pro and Joy-Con source validation, ambiguous/stale-source
identity fallback, invalid-value normalization, non-stick non-interference,
and zero managed allocations over 20,000 warm resolutions.

`Switch2MappedStickMousePresentationTests` cover signed per-axis velocity
recovery from the canonical report delta, centered-source withdrawal,
non-stick/invalid/stale-interval rejection, exact removal of only an admitted
mapped contribution, unchanged per-report fallback after refused admission,
and zero managed allocations over 20,000 warm captures. The high-rate mixer
tests include the mapped-stick source in freshness and profile-reset coverage,
plus all-or-nothing admission of the three Mapping-owned sources with one
shared timestamp.

`Switch2StickDirectionTapLaneTests` cover the exact 80 ms boundary, cardinal
to diagonal and diagonal to cardinal duplicate suppression, center rearming,
independent per-stick/per-direction Hold and Tap policy, profile/lifecycle/QPC
fences, fail-closed source and enum validation, and zero managed allocations
over 20,000 warm calls. `Switch2StickDirectionTapMappingTests` additionally
prove that an active/inactive pulse becomes the canonical virtual-pad
button/trigger/axis values and that ineligible or Hold directions preserve the
existing mapper result.

`Switch2ProfileMappingSchemaTests` covers opt-in defaults, round-trip profile
persistence, invalid-value normalization, append-only IR source-control
serialization, append-only IR shift-trigger IDs and mouse-action round trips,
independent left/right live trigger evaluation, stale-state release, and
mapping-table cardinality. These checks validate software
ownership and deterministic behavior; portable live-controller validation is
still required before claiming hardware-level IR behavior or measured latency.
