# Switch 2 Cemuhook yaw sensitivity

This behavior is pinned to the operative Cemuhook serializer in
`Switch2Connect` commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/cemuhook_udp.py`.

## Projection contract

Switch2Connect exposes five sensitivity levels and applies

`1 + (level - 1) / 12`

only to the yaw value serialized into its DSU/Cemuhook packet. The exact level
multipliers are 1, 13/12, 7/6, 5/4, and 4/3. Pitch and roll are unchanged.

DS4Windows applies the same multiplier after its existing optional DSU
One-Euro smoothing and immediately before `UdpServer.NewReportIncoming`.
Unlike the donor, it does not apply a separate Joy-Con or Pro raw-gyro scale:
the canonical `SixAxis` state already contains degrees per second. Copying the
donor's raw-family constants here would double-scale valid motion.

## Scope and ownership

The captured physical-report owner must be a `Switch2RuntimeInputDevice`.
Ordinary DS4, DualSense, and other DSU sources retain byte-for-byte prior yaw
behavior. The existing exact legacy-slot report lease remains authoritative,
and the captured device identity prevents a reused slot from changing the
source family observed by an older callback.

`Switch2CemuhookYawSensitivity` is stored in the existing profile and appears
as `Cemuhook yaw sensitivity` under `Switch 2 Controls`. Level 1 is the legacy
default and returns the original `double` without multiplication. Invalid
persisted levels normalize to 1. Non-finite input and finite overflow preserve
the original value rather than synthesizing a different invalid sample.

This is presentation policy only. It does not alter physical parsing,
calibration, canonical motion, gyro mouse/stick mapping, virtual-pad output,
report timing, or the Xbox One path. The warmed arithmetic path allocates no
managed memory.
