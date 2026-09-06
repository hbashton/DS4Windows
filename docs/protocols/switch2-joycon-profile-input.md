# Switch 2 Joy-Con profile-input boundary

Status: profile mapper, joined coordinator, BLE runtime sink, profile-default
and opaque per-controller orientation persistence/UI, and canonical controller
registration are implemented and software verified. Live radio/firmware
behavior remains a hardware gate.

This is one model-specific projection on top of
`Switch2CanonicalInputFrame` and `Switch2JoyConPairSnapshot`. It feeds the
ordinary `DS4State` mapping pipeline and does not create another mapping stack.
Logical calibrated axes now seed mapping-owned fractional coordinates; byte
access remains a compatibility projection. The [shared precision migration](mapped-stick-axis-precision.md)
still has unmigrated transforms and egress gates. Physical raw sidecars cannot
replace mapped output or undo standalone orientation/remapping.
The mapper itself performs no BLE association, transport I/O, output, or
haptics. Those capabilities stay in the separately owned production
coordinators and feedback lifetimes.

## Pinned source and admitted report

The button meanings and standalone rotations are pinned to clean upstream SDL
commit `c71abd08605b8bb7078372307a93274725c99fe0` (zlib license), file
`src/joystick/hidapi/SDL_hidapi_switch2.c`:

- `HandleCombinedControllerStateL` (`:773-824`);
- `HandleCombinedControllerStateR` (`:864-904`);
- `HandleMiniControllerStateL` (`:826-862`); and
- `HandleMiniControllerStateR` (`:906-942`).

Those functions establish the positions used here for 64-byte common report
`0x05`: SDL bytes 5..8 correspond to canonical Common05 button body bytes
`0x04..0x07`, and SDL bytes 11..16 correspond to canonical stick body bytes
`0x0A..0x0F`. The implementation is independent DS4Windows code; no PadForge
code was copied.

Vertical standalone decoding is additionally checked against
`hifihedgehog/SDL@d98c5804a9d20b0d96e993741797878c86b8f1e1`,
`src/joystick/windows/SDL_ble_switch2joystick.c`, whose
`BLE_DecodeJoyConLeft` and `BLE_DecodeJoyConRight` vertical branches explicitly
mirror the wired combined handlers. Motion-basis and live hold-mode behavior
are adapted from the GPL-3.0 Switch2Connect source at
`61ac6642ce12fe7217e38a860b14863b18ca7e28`. Its standalone controller card
changes `hold_mode` live, persists the choice by physical controller, reloads
it at controller construction, and forces joined/Pro presentation vertical.

The BLE service, Common05 characteristic, exact `Read|Notify` property tuple,
and applicability to all controller models are separately pinned to
`ndeadly/switch2_controller_research@d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92`,
`bluetooth_interface.md` (GATT table and Common05 HID-report section). SDL
establishes the mapping above, not this BLE identity.

Only canonical Common05 frames are admitted. Dedicated BLE reports `0x07` and
`0x08` are deliberately rejected because the pinned combined/mini functions do
not establish that those reports use identical byte positions. Their decoding
elsewhere in the Phase-1 codec is not permission to reuse Common05 semantics.

## Modes and rotation

Joined mode consumes one already-paired `Switch2JoyConPairSnapshot`. Left X and
right X are not inverted; both Y axes are inverted, matching SDL combined mode.

Standalone orientation has a profile default plus an optional per-controller
override. Vertical is the safe default and mirrors SDL combined mode: left
remains the logical left stick/D-pad/left shoulder and trigger, while right
remains the logical right stick/face buttons/right shoulder and trigger.
Horizontal exposes either physical half as one logical left stick, matching
SDL mini mode:

| physical half | logical X | logical Y |
|---|---|---|
| left | inverted physical Y | inverted physical X |
| right | physical Y | physical X |

Changing orientation takes effect on the next report and does not recreate the
Bluetooth session, runtime controller, output device, or feedback writer. One
report observes one complete enum snapshot, so axes and buttons can never mix
vertical and horizontal semantics. The motion projection clears its delta
history when the coordinate basis changes, preventing an artificial first-
sample transient.

The Controllers card exposes the live selector only for an exact standalone
left/right runtime. Its choice is stored in a fixed-size, versioned,
digest-protected record keyed by the existing install-local HMAC peer
pseudonym. No MAC address, Windows identity, device path, bond, or transport
credential is formatted or persisted. A missing/malformed record falls back
to the active profile; joined Joy-Cons and Pro controllers reject this
persistence binding. File I/O never runs under the input publication gate, and
the report hot path adds only one lock-free enum read.

The high-resolution mapper retains raw 12-bit values and normalized signed
16-bit values. `TryWriteLegacyState` seeds mapping-owned fractional axes;
reading the legacy byte surface explicitly quantizes to `0..255`, without
replacing that precision until an actual legacy-axis write. Stick calibration
and quantization share the Pro Controller 2 implementation. The registered
[input matrix](switch2-production-input-matrix.md) checks the current default
curve/custom-mapping pipeline and final target quantization; it does not prove
every profile transform or physical route.

## Lifetime and ordering

`Switch2JoyConProfileMapperState` is an immutable reducer state owned by one
profile lane. Joined construction binds both exact Common05 BLE session
descriptors and a nonzero pair epoch. Standalone construction binds one exact
descriptor and physical side. Re-selecting vertical/horizontal copies the same
descriptor, accepted counter, and timestamp baselines; cross-side selection
fails closed. Each accepted observation must retain those
device/transport generations and QPC frequency. The Common05 GATT descriptor
must carry exactly `Read|Notify`; property supersets are not silently promoted
into this source-pinned profile tuple.

The mapper rejects:

- a changed pair epoch;
- a mismatched model, protocol, descriptor, generation, or clock;
- a canonical backward/out-of-order counter classification;
- a counter that is backward relative to the mapper's last accepted baseline;
  and
- a completion timestamp older than the last accepted observation.

Duplicate counters and a reused unchanged half of a joined snapshot are valid.
Counter wrap uses the canonical unsigned 32-bit half-range rule. A rejection
returns the original reducer state unchanged.

### Coordinator transaction boundary

`Switch2JoyConJoinedCoordinatorState` now owns the pair-reducer and joined
profile-mapper values as one serialized transaction state. The generic pair
reducer intentionally admits a broader set of BLE canonical frames than this
exact Common05 profile boundary, so the coordinator first calls the mapper's
shared internal per-half admission seam. A dedicated report, wrong side,
descriptor/lifetime change, invalid calibration, timestamp regression, or
backward counter therefore cannot become latent pair state while waiting for
the other half.

After admission, the coordinator stages `Switch2JoyConPairReducer` output. A
`WaitingForOtherHalf` or `StaleHalf` result may retain that admitted pair value,
but leaves both mapper acceptance baselines unchanged. A `JoinedSnapshot` is
mapped against the current mapper value and the pair and mapper candidates are
published together only on total success. Any admission, reducer, or mapper
failure returns the original combined state. This final mapper gate also makes
state corruption/restoration fail closed rather than publishing a partially
updated pair.

`HalfLost` and `Split` clear pair presence as defined by the reducer and return
an explicit `ClearsProfileOutput` signal; mapper baselines remain historical
fences. The fixed-lifetime coordinator does not adopt a changed descriptor or
generation. Recovery after loss or a lifetime change requires deliberate
coordinator recreation, normally with a new pair epoch.

## Legacy compatibility and sidecar

Conventional face, D-pad, shoulder, digital-trigger, stick-click, system, and
Capture controls project to their existing `DS4State` meanings. Face controls
use the profile-selected Xbox physical-position or Nintendo printed-label
policy in `switch2-face-button-layout.md`; raw per-side bits remain unchanged.
A horizontal
standalone half is a mini controller with one logical left stick and a centered
right stick. An upright right half preserves its physical right stick while
the left stick is centered. Every unsupported or released legacy control is
cleared on every write to prevent reused-state stickiness.

Switch 2 C, four legacy mini-controller paddle slots, and four physical rail
controls have no complete,
lossless legacy identity. They remain explicit in
`Switch2JoyConProfileInputFrame` and the copied
`DS4State.Switch2JoyConRawInputStatus` sidecar. The joined frame also retains
the semantic button mask for each physical half separately; the union still
feeds the ordinary profile mapper. In particular:

- C is never mapped to `DS4State.Mute`;
- paddle/rail controls are not collapsed into the two ambiguous legacy paddle
  fields; and
- joined halves retain separate generation, counter, raw-button, unknown-bit,
  and physical-stick observations.

### Physical rails and append-only profile vocabulary (2026-09-02)

Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/controller.py:4265-4273` (GPL-3.0), identifies left SL/SR at raw bits
21/20 and right SL/SR at 5/4. Its GL/GR bits 25/24 apply only to Pro. The
combined/vertical Joy-Con decoder now treats those Pro bits as unknown rather
than manufacturing paddle input. Physical SL/SR identity is retained in joined,
vertical and horizontal sources. No PadForge source was copied.

The in-process Joy-Con source contract is version 3. Profile flag bits 25..28
append `LeftRailSL`, `LeftRailSR`, `RightRailSL`, `RightRailSR`; these semantic
flag numbers are not the raw packet bit positions. `DS4Controls` IDs 63..66
append the corresponding named sources. All earlier numbers and XML names
remain unchanged. Rail readers require the current, exclusive Joy-Con source
and the matching present half; old-version or conflicting sidecars cannot
activate a rail.

Horizontal SDL mini-controller defaults remain intact: physical SL/SR also
produce logical L1/R1, and L/ZL or R/ZR keep their existing legacy paddle slots.
A custom physical-rail mapping therefore coexists with L1/R1 unless that
legacy mapping is unbound. The profile editor explains this. Selecting either
alias as an explicit Mode Shift command instead consumes both aliases through
normal release bookkeeping. That alias relationship requires a current,
exclusive standalone-horizontal source, pair epoch zero, matching presence and
nonzero generation identities. It never applies to joined, vertical or Pro
sources. Hold takes precedence over Toggle across the same physical alias
family without rewriting the saved configuration or raw source data.

Mode Shift observes IR activation directly from the immutable source sidecar
using each side's configured threshold, never from the field map where command
consumption clears buttons. Early and late bindings therefore observe the same
IR Hold/Toggle state within one report.

Orientation is part of the in-process gyro-modifier source identity. Changing
vertical/horizontal layout baselines Mode Shift and gyro-lock Toggle edges and
clears old tuning freeze/release latches; Hold remains immediately effective.
This does not change transport generations, wire contracts or freshness fences.
Ordinary Controls/Mouse/Mouse Joystick gyro activation preserves its existing
on/off toggle latch across a proven same-lifetime standalone orientation change,
but baselines the newly projected buttons and refreshes tuning selection. Each
mode has its own fixed-size observer. First observation, Always On, generic
controllers and unrelated source transitions retain their existing behavior;
directional swipes have no toggle latch and remain immediate Hold/ratchet paths.

Ordinary Mouse, Mouse Joystick, Directional Swipe and Controls gyro menus add
C activation token 30 and the four rail tokens 31..34. Always On remains
activation token -1 and tuning ID 29. Press/edge masks are now 64-bit. Menus
persist explicit tokens rather than display indices, clear stale checks on
reload and display unsupported saved tokens honestly without rewriting them.
Direct Shift Modifier entries append rails at IDs 38..41 and C at 42; synthetic
Mode Shift stays 37. The same sources are available to existing button, macro,
gyro-lock, IR-tuning and Mode Shift settings. DJG lists only each half's own
rails.

The sidecar is copied by the `DS4State` copy constructor, `CopyTo`, and
`CopyExtrasTo`. A Pro or Joy-Con compatibility write clears the other Switch 2
sidecar, preventing source metadata from sticking across device types. The
input lane retains ownership of `DS4State.PacketCounter`; the independent raw
counter for each half remains in the sidecar and is never collapsed into a
potentially regressing host sequence.

## Dual Joy-Con gyro modes

The production joined runtime projects the admitted left/right IMU samples into
the existing lane-owned `DS4State.Motion` object. Per-profile **Enable Dual
Joy-Con Gyro (DJG)** settings reproduce the three source-audited
Switch2Connect modes without creating a second mapper, polling loop, or output
owner:

- **Switch dominant side** changes which side provides the fused dominant
  orientation;
- **Switch gyro side** enables exactly one physical IMU at a time; and
- **Single-side toggle** toggles only the IMU on the physical half whose
  activation button changed. With no dominant side selected, direct merge
  remains the compatible initial mode and both IMUs begin active.

Hold reacts to an admitted press and its release; Toggle reacts only to a
press edge. Each physical half has an independently persisted activation
**mask**, edited as checkboxes under Switch 2 Controls. Any selected button or
that half's IR sensor holds one per-side OR gate; these are alternatives, not
a chord. Releasing one input while another remains held, or handing off
between them in one report, does not create another edge. The halves retain
independent edges, including simultaneous presses. Unchecking all inputs
leaves no activation binding. Existing XML field names and enum bit values
are unchanged, so old single-button profiles retain their bindings.

IR activation uses the matching left/right optical activation threshold, even
when optical pointer movement is disabled. It requires a present, correct-side
Common05 source and nonzero distance; it does not require the mouse-motion
verification latch. Supplied IR button bits are stripped before threshold
observation, so a foreign or synthetic raw IR bit cannot activate DJG.
Activation is observational: buttons and sensor samples remain present in the
ordinary profile/game input frame and are never consumed by the gyro policy.

This OR/IR behavior follows Switch2Connect GPL-3.0 source at
`61ac6642ce12fe7217e38a860b14863b18ca7e28`, `src/controller.py`:
`mapping_pairs` includes physical and active IR controls (4369-4405), selected
DJG actions OR into `trigger_djg` (4934), and only the aggregate transition
calls `handle_djg_trigger` (5356-5360). No PadForge code or opaque binary is
used. Lifecycle baselining below is DS4Windows' explicit safety policy.

Profile configuration, relevant IR threshold, profile-switch revision and
pair-epoch changes synchronize their held-input baseline before accepting an
edge. An inherited Hold release cannot undo a press that was never admitted;
the next fresh press resumes normal behavior. Identical-settings profile
switches reset the old toggle state. Invalid enum/unknown-button combinations
fail closed and clear the baseline. A legacy profile that stored direct
merge through `Dominant=None` before the explicit mode field existed migrates
to Single-side toggle; other modes normalize a missing dominant side to Right.
The dedicated notifying DJG editor refreshes the dominant-side selection when
a mode change normalizes Direct merge to Right. The resolver and fused
projection are allocation-free after warmup.

Saved profiles and presets reuse the existing editor instance. The shared
late-profile refresh now rebuilds DJG, gyro-lock, mode-shift, gyro-trigger tuning
and direct IR tuning checkbox selections before rebinding. This refresh reads
the loaded profile; it does not replay old checkbox setters or create missing
tuning tables.

## Verification and explicit omissions

`Switch2JoyConProfileInputTests` verifies every pinned raw button bit exactly
once for all four SDL handlers, vertical and horizontal rotations and signed
endpoints, orientation changes that retain freshness fences, vertical motion
axes, per-side semantic-button provenance, exact gyro-side selection, factory
calibration, epoch/generation/clock/timestamp/counter fences,
Common05-only
admission, released-state clearing, C/paddle non-aliasing, all mapping copy
paths, and zero managed allocations over 20,000 warm joined map/write calls.
`Switch2JoyConJoinedCoordinatorTests` additionally verifies invalid halves are
never retained, atomic waiting/join and stale-half recovery, fixed-lifetime
rejection, loss/split clear semantics, rollback at the final mapper gate, and
zero managed allocations over 20,000 warm coordinator transactions.

`Switch2BluetoothRuntimeInputSinkTests` and
`Switch2RuntimeInputDeviceTests` additionally prove that an active left/right
runtime accepts both orientations without changing the authenticated physical
binding, terminal state, runtime generation, or mapper replay baselines.
`Switch2JoyConHoldModeFileStoreTests` cover exact fixed-record round trips,
digest and peer rejection, standalone-only binding, live replacement, and
reconnect adoption. The BLE sink test proves a controller override wins a
contradictory profile default on the next physical report.
`Switch2DualJoyConGyroModeTests` additionally cover every Hold/Toggle edge,
all three mode transitions, profile/pair held-button synchronization, invalid
configuration rejection, overlapping buttons/IR and handoffs for every mode,
Hold/Toggle and side, independent simultaneous edges, and zero managed
allocations over 20,000 warm resolutions. Runtime tests feed decoded Common05
reports through the joined motion path to prove stationary IR activation,
physical-button overlap, opposite-side isolation, threshold changes, identical
profile-switch resets, unchanged source sidecars and another zero-allocation
20,000-call IR-observation/resolution test. XML tests cover empty, legacy
single-bit and combined physical/IR masks. An STA WPF binding test covers
dependent dominant-side normalization, checkbox mask preservation, independent
halves and reopening the editor. This does not substitute for visual or live
controller validation of the full profile editor.

This is not evidence of live Joy-Con radio behavior, USB support for Joy-Con,
report `0x07`/`0x08` profile semantics, physical output cadence, game
visibility, or end-to-end latency.

The rail correction above has raw Common05 byte decoding, sidecar copy/release,
profile-schema and real `Mapping.MapCustom` coverage. Its target test checks
exact button bits for Xbox 360, Xbox One, DS4, DualSense, Edge and Switch 2 Pro,
across four rails and three orientations, with baseline/press/hold/release/
repress, ordinary bindings, both command aliases, and Hold/Toggle conflicts.
The gyro tests exercise actual Mouse/Mouse Joystick Hold/Toggle activation and
tuning selection for IDs above 31, XML tuning IDs 29..34 in both scopes, and
all four menu reload/save paths. Warm activation evaluation and Mode Shift
alias normalization allocate zero bytes across 20,000 iterations. These are
software assertions, not hardware packet captures or latency measurements.
