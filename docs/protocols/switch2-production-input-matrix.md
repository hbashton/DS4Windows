# Registered Switch 2 input-to-broker matrix

This is software composition evidence, not physical, Windows API, game or
latency acceptance. The old `ViiperCompatibilityMatrixTests` selected numerical
seeds using a physical-family enum and built packets from hand-filled DS4State.
Its names now say that explicitly. It never proved physical-family integration.

`Switch2ProductionInputMatrixTests` reuses the existing registered gyro-mapping
fixture. The path is Common05 bytes -> real decoder/calibration/profile
projection -> runtime device with a production registration transaction and
slot host -> SetCurveAndDeadzone/MapCustom -> final broker packet encoder.
Registration supplies the logical report handler; tests do not install another
mapper. Fake OS discovery/transport pumps and profile persistence prevent any
controller, keyboard, mouse, installed configuration or driver access.

## Coverage

Seven source modes cross six target types:

| Sources | Target broker payloads |
| --- | --- |
| Pro USB and Pro BLE | Xbox 360, Xbox One, DualShock 4, DualSense, DualSense Edge, Switch 2 Pro |
| Joined Joy-Con BLE | Same six |
| Left/right standalone BLE, vertical and horizontal | Same six |

- 42 routes check applicable ordinary buttons, both digital triggers at full
  scale, repeated held state, releases, represses, exact field masks and
  terminal neutral before profile teardown. Capture has no native representation
  on Xbox 360/Sony here; Xbox One Share and Switch Capture are asserted explicitly.
- 42 routes check each raw stick axis at `0, 1, 2047, 2048, 2049, 4094, 4095`.
  Independent expected arithmetic verifies center/endpoints, near-center detail,
  unused-half neutrality, vertical/horizontal rotation, Xbox signed-16 and
  Switch unsigned-12 precision, and Sony's final signed-byte quantization.
- 21 explicit extra-control binding cases each check all six outputs: Pro
  GL/GR, Capture, C, horizontal mini-controller paddle roles and four joined
  SL/SR identities. Mapping to A must not also leak the old native button;
  release and terminal removal must release the binding.
- Five Joy-Con-mode cases reject manufacture of controls from Pro-only rear
  bits. The existing raw/unknown metadata policy is not broadened by SDL's
  inclusion of those bits in a combined handler.

The bit/orientation facts are rechecked against the existing pinned references:
upstream SDL `c71abd08605b8bb7078372307a93274725c99fe0`,
`src/joystick/hidapi/SDL_hidapi_switch2.c` combined/mini handlers; and
Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/controller.py` btn_states (GL/GR gated by is_pro; physical rails separate).
See [Joy-Con protocol/provenance](switch2-joycon-profile-input.md). No reference
repository was modified and no PadForge code was copied. Golden output fields
are the existing DS4Windows/VIIPER broker contracts, not raw USB/GIP reports;
Xbox One's [versioned contract](xbox-one-semantic-egress-v1.md) is asserted with
version, size and reserved tail checks as well.

## Limits

Transport pumps, persisted-profile loading and broker sockets are substitutes
or absent. New decoder sessions seed the fixtures; this matrix does not prove
counter continuity, arrival ordering, skew arbitration, BLE initialization,
association or physical disconnect. It does not exercise Windows virtual-device
selection/creation, scheduler admission, USB/IP terminal completion, game polling
or haptic output. Existing lifecycle/interop tests cover separate boundaries.

The axis cases use fallback calibration and the fixture's default/reset stick
configuration. Real calibration, all custom transforms, all source firmware,
live orientation edits, optical/gyro modes, unknown layouts, legacy physical
families, every source-target feedback route, hardware and game acceptance still
need their own evidence. Passing this matrix is not full controller support.

This tranche changes tests and documentation only. Live b56 and staged b58
payloads are unchanged. Exact TRX results are recorded in VIIPER's dated ledger.
