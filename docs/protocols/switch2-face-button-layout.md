# Switch 2 face-button layout

Status: implemented for Switch 2 Pro Controller and every standalone/joined
Joy-Con 2 presentation mode. The profile schema, editor, canonical runtime
publication, and deterministic software tests are complete.

## Source and policy

Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28` exposes an `abxy_mode` setting
with `Xbox` and `Switch` values (`src/config.py`, `src/gui.py`,
`src/controller.py`, and `src/virtual_controller.py`). Its Xbox/PlayStation
outputs treat Xbox as the physical-position layout and Switch as the
printed-label layout. DS4Windows names the second option **Nintendo** to avoid
confusing a face-button policy with its separate Switch virtual-pad type.

The persisted profile field is `Switch2FaceButtonLayout`. Existing and legacy
profiles default to `Xbox`, preserving the previously shipped behavior.
Unknown serialized values normalize to `Xbox`.

## Canonical projection

The switch is applied once when a validated Switch 2 profile frame writes into
the existing `DS4State` mapping boundary:

| physical position / printed label | Xbox layout | Nintendo layout |
|---|---|---|
| west / Y | X (`Square`) | Y (`Triangle`) |
| north / X | Y (`Triangle`) | X (`Square`) |
| south / B | A (`Cross`) | B (`Circle`) |
| east / A | B (`Circle`) | A (`Cross`) |

This projection is shared by USB/Bluetooth Pro Controller 2, joined Joy-Con 2,
and vertical or horizontal standalone Joy-Con 2 frames. It is independent of
the selected virtual output type, so Xbox One, Xbox 360, DualShock 4,
DualSense, DualSense Edge, and Switch 2 output all consume the same authored
profile semantics.

The operation is four boolean assignments on the report hot path. It creates
no object, worker, queue, timer, or transport transition. Raw button bits and
the Pro/Joy-Con sidecars retain the exact physical observation, so C, rail,
paddle, IR, gyro, custom mapping, lifecycle, and freshness behavior are
unchanged. A live selection takes effect on the next report and never
recreates the controller or its feedback owner.

## Verification

`Switch2ProProfileInputTests` and `Switch2JoyConProfileInputTests` verify each
physical face position under both layouts, invalid-value rejection, and raw
sidecar preservation. `Switch2RuntimeInputDeviceTests` changes the active
profile value on one live runtime and verifies that the next publication uses
the new layout. `Switch2ProfileMappingSchemaTests` verifies the legacy Xbox
default, profile-store/XML round trip, and invalid-value normalization.

These are deterministic software checks. They do not claim physical hardware
validation or game-specific prompt behavior.
