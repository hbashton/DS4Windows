# Switch 2 Pro motion basis correction, 2026-09-08

The reported symptom was reversed left/right movement when using the Pro controller as a gyro mouse. A source-level basis mismatch was confirmed: the Pro projection treated the donor's already-semantic Cemuhook values as native DS report axes and then passed them through `SixAxis.populate`, which applies semantic signs again.

## Independent reference evidence

- [Switch2Connect native DS report mapping, pinned 61ac6642](https://github.com/TommyWabg/Switch2Connect/blob/61ac6642ce12fe7217e38a860b14863b18ca7e28/src/virtual_controller.py#L3749): upright Pro gyro and acceleration both become `(x,z,-y)`. Its subsequent WinUHid/DS4 assignments preserve those values. This is distinct from the [Cemuhook branch](https://github.com/TommyWabg/Switch2Connect/blob/61ac6642ce12fe7217e38a860b14863b18ca7e28/src/virtual_controller.py#L3014), which was the previous projection's source.
- [SDL physical Switch 2 mapping, pinned hifihedgehog d98c5804](https://github.com/hifihedgehog/SDL/blob/d98c5804a9d20b0d96e993741797878c86b8f1e1/src/joystick/hidapi/SDL_hidapi_switch2.c#L1266): both sensors likewise use `(x,z,-y)`. SDL's [DS4 sensor path](https://github.com/libsdl-org/SDL/blob/c71abd08605b8bb7078372307a93274725c99fe0/src/joystick/hidapi/SDL_hidapi_ps4.c#L1190) publishes the calibrated DS report axes without another orientation transform.
- [Switch2Connect native gyro mouse](https://github.com/TommyWabg/Switch2Connect/blob/61ac6642ce12fe7217e38a860b14863b18ca7e28/src/controller.py#L6431) takes negative world-Z yaw for horizontal movement. In a level/upright local-yaw comparison, this agrees with the corrected Pro canonical yaw, not the old positive-Z result. This does not claim identical horizon algorithms.

PadForge's local `SdlDeviceWrapper` reads SDL sensor values directly. These comparisons establish the reference basis; they are not new physical direction measurements on this machine.

## Bounded implementation

Only `Switch2ProMotionProjection` changes production behavior. Both USB Common05 and Bluetooth Common05 enter this same projection. The corrected native report tuple is `(x,z,-y)` for gyro **and** acceleration. Existing `SixAxis.populate` then produces:

| Canonical field | Raw Pro input, before existing scale |
| --- | --- |
| Gyro pitch | `+X` |
| Gyro yaw | `-Z` |
| Gyro roll | `+Y` |
| Acceleration X | `-X` |
| Acceleration Y | `-Z` |
| Acceleration Z | `-Y` |

The acceleration correction is the same directly evidenced report-versus-semantic defect, not an unrelated tuning change. No common SixAxis, mouse, Joy-Con orientation, or virtual-pad encoder is changed. Existing Pro scales, elapsed timing, gyro bias persistence, calibration, magnetometer/horizon processing, and deadzone placement remain unchanged.

Existing profile horizontal-axis selection and X/Y inversion are preserved. A user who previously enabled inversion specifically to compensate for this defect may need to turn that workaround off; the application does not silently rewrite their preference. The canonical correction also reaches DSU and virtual devices that carry motion. Xbox output itself has no native gyro sensor lane; gyro-to-stick/mouse mappings continue through the canonical mapper.

## Offline regression evidence

`pro-sensor-basis-before.trx` recorded **12 failed, 0 passed** with the old production projection: USB and Bluetooth, each raw axis, both signs. The expected values were derived from native report references rather than copying the implementation.

Additional tests exercise the production `MouseCursor` using captured fake output, covering USB/Bluetooth, yaw/roll horizontal selection, and all four existing X/Y inversion combinations. They cannot send OS input or open a controller. Global test settings and the previous output handler are restored in `finally`.

`pro-motion-readings-after.trx` recorded **113 passed, 0 failed, 0 skipped**, Release/x64 with `--no-restore --no-build` against the current shared test build:

- `Switch2ProProfileInputTests`: **33 passed**, including the 12 formerly failing basis cases and 4 direct mouse cases (each checking all four inversion settings).
- `Switch2RuntimeInputDeviceTests`: **43 passed**.
- `Switch2ProductionGyroMappingIntegrationTests`: **29 passed**.
- `ControllerReadingsSnapshotTests`: **8 passed** (the separate concurrent readings fix).

The filter retained existing Pro allocation, scale, calibration, deadzone, horizon and registered canonical gyro-mapping tests. No application was restarted or deployed, no live profile was edited, and no new physical direction acceptance test was performed for this change.
