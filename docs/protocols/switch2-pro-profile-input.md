# Switch 2 Pro profile-input boundary

Status: profile boundary and runtime registration are implemented. This
document's tests verify the pure projection, not the live transport matrix;
consult the dated controller-platform validation ledger for hardware evidence.

This boundary connects the existing generation-fenced Switch 2 canonical input
frame to the existing DS4Windows profile state. It does not create a second
mapping engine and does not open a controller handle. The exact 12-bit source
observation remains attached to `DS4State` as metadata. Logical signed16 axes
now seed mapping-owned fractional coordinates with explicit byte compatibility
access. The [shared precision migration](mapped-stick-axis-precision.md) is
incomplete; raw metadata is never a shortcut around profile mapping.

## Admitted input

`Switch2ProProfileInputMapper` accepts only all of the following:

- canonical contract version 1;
- Pro Controller 2;
- either the exact native USB identity (`ProUsbCommon05Bcd0201`,
  `057E:2069`, `bcdDevice 0201`) or the exact source-pinned BLE identity
  (`BluetoothLeCommon05V1`, Nintendo service/Common05 characteristic,
  exactly `Read|Notify` properties with no additional property flags);
- common input report `0x05`;
- a calibration snapshot bound to the same nonzero device generation; and
- for BLE, a first, forward, or duplicate counter observation.

For the admitted Pro USB Common05 path, native read-claim order and host QPC
establish delivery order. Its raw counter is diagnostic: a discontinuity must
not suppress input or disconnect the runtime. The session and offline replay
both retain the raw modular delta/classification and compare the next valid
arrival against the new value. BLE counter admission is unchanged pending its
own hardware evidence. See [USB counter continuity](switch2-pro-usb-counter-continuity.md).
Unknown button bits are preserved but never promoted to controls.

## Axis contract

The profile pipeline carries these distinct representations:

| representation | range | purpose |
|---|---:|---|
| raw | `0..4095` | lossless Nintendo report value |
| normalized | `-32768..32767` | high-resolution profile-boundary value |
| mapped | fractional `0..255`, center `128` | authoritative mutable profile coordinate |
| legacy | `0..255`, center `128` | compatibility with the current `DS4State` mapper |

The generation-bound factory calibration is used when it passed the existing
adoption checks. Otherwise the calibration snapshot already names and owns the
symmetric fallback. Values outside an adopted endpoint clamp rather than wrap.
X is not inverted. Y is inverted so normalized negative means up and positive
means down. Center remains exactly zero/128.

The orientation is source-pinned to upstream SDL at commit
`c71abd08605b8bb7078372307a93274725c99fe0`, zlib license:

- `src/joystick/hidapi/SDL_hidapi_switch2.c:203-220`, `MapJoystickAxis`, establishes
  calibration/fallback normalization and its explicit inversion parameter;
- the same file at `:944-1028`, `HandleSwitchProState`, maps left/right X
  without inversion and left/right Y with inversion, and independently
  corroborates positional face buttons, shoulders, digital triggers, system
  buttons, D-pad, C, and paddles.

The BLE service, Common05 characteristic, exact `Read|Notify` property tuple,
and applicability to all controller models are pinned separately to
`ndeadly/switch2_controller_research@d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92`,
`bluetooth_interface.md` (GATT table and Common05 HID-report section). SDL does
not establish the BLE identity. The native USB tuple and observed report `0x05`
come from the project-owned passive evidence ledger in
`switch2-phase1-codec.md`; that capture is not evidence for BLE.

No PadForge code was used. PadForge remains behavioral evidence only under its
noncommercial/share-alike license boundary.

## Existing profile compatibility

Face buttons use the profile-selected Xbox physical-position or Nintendo
printed-label projection documented in `switch2-face-button-layout.md`.
Legacy profiles retain Xbox physical-position behavior. Minus/Plus map to
Share/Options. Home, Capture,
shoulders, stick clicks, digital ZL/ZR, D-pad, and the two back paddles use their
existing DS4Windows semantic slots.

The Switch 2 C button has its own `DS4Controls.Switch2C` source. It remains
explicit in `Switch2ProProfileInputFrame.CButton` and
`DS4State.Switch2RawInputStatus.CButton`. It is **not** aliased to
`DS4State.Mute`; doing so would incorrectly invoke DualSense microphone/mute-LED
policy. The profile field map, schema and UI expose that distinct C source;
there is no DualSense mute alias.

The Pro compatibility write also clears the separate Joy-Con 2 raw sidecar so
metadata cannot stick when a reused `DS4State` changes physical source. The Pro
sidecar retains the source transport and exact selected protocol revision, so
the newly admitted USB and BLE Common05 paths cannot become indistinguishable.

Every compatibility write clears represented button, trigger, touch, and paddle
controls absent from this source. The sidecar also clears any stale DualSense
raw-status metadata. `DS4State.PacketCounter` remains owned by the input lane;
the physical 32-bit counter is retained in the sidecar instead of replacing the
host packet sequence. `DS4State.Motion` likewise remains caller-owned and is
not mutated by this pure mapper. Motion publication and its source ownership
belong to `Switch2RuntimeInputDevice` and the runtime motion pipeline; these
projection tests alone do not establish its hardware scaling or fidelity.

## Verification

`Switch2ProProfileInputTests` covers:

- every evidenced Pro semantic exactly once;
- the C-versus-Mute non-alias rule;
- fallback and factory-calibrated centers/endpoints/clamping;
- Y orientation;
- released/unsupported-control clearing;
- raw metadata survival through constructor, `CopyTo`, and `CopyExtrasTo`;
- exact identity and stale-sequence rejection; and
- zero managed allocations across 20,000 warm-state map/write operations.

These pure projection tests do not establish hardware motion/output/haptics,
game visibility, full profile precision, or end-to-end latency. Runtime and UI
implementation exist separately and require their own relevant evidence.
