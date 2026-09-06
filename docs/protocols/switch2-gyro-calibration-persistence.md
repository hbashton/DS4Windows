# Switch 2 stationary gyro calibration persistence

Status: implemented and software verified for Bluetooth Pro Controller 2,
standalone Joy-Con 2, joined Joy-Con 2, and USB Pro Controller 2. Physical
controller validation remains a hardware gate.

## Source behavior and retained estimator

Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28` establishes the operative product
behavior in `src/controller.py`: **Calibrate Gyro** collects a stationary
interval, averages each gyro axis into a bias, stores a controller-specific
entry, reloads that entry when the controller is created, and subtracts the
bias from subsequent motion. Its README describes the result as permanent
sensor-bias calibration.

DS4Windows already had a stricter allocation-free estimator in
`Switch2StationaryGyroCalibration`; replacing it with an unqualified average
would reduce correctness. The retained estimator commits only after:

- five contiguous stationary seconds and at least 100 distinct samples;
- report deltas no greater than 100 ms;
- acceleration within 0.85--1.15 g;
- corrected gyro magnitude no greater than 1 degree/second; and
- a finite replacement bias no greater than 2 degrees/second.

Manual restart keeps the last accepted bias active until a full replacement
qualifies. Invalid frames reset only observation/qualification state; they do
not erase an accepted or loaded bias. This completes the donor's persistence
behavior while preserving the stronger existing admission policy.

## Ownership and persistent format

Each runtime binds persistence before activation using the existing
install-local `Switch2PersistentPeerId`. Bluetooth Pro and standalone Joy-Con
runtimes bind one physical peer, joined Joy-Cons bind both sides independently,
and USB Pro derives the same kind of opaque peer at its trusted container
boundary. No Bluetooth address, Windows identity, device path, container GUID,
serial, bond, or transport credential enters the record.

The accepted bias is normalized to degrees/second before storage, so Pro and
Joy-Con native LSB scales cannot be confused with persistent units. Each
49-byte record contains:

- four-byte magic and one-byte version;
- the 16-byte opaque peer pseudonym;
- three little-endian finite float bias values; and
- a truncated 16-byte SHA-256 integrity digest.

Loads require exact length, magic, version, peer, digest, finite components,
and the same 2-degree/second magnitude cap used by the estimator. Files are
written to a unique temporary name, flushed to disk, then atomically replaced.

## Report-path behavior

A newly committed bias advances a monotonic per-IMU revision. The serialized
runtime compares that revision after motion projection and enqueues only the
rare changed record. One bounded FIFO background drain preserves commit order
and performs all file I/O outside the publication gate; transient failures are
retried in place so an older record cannot overwrite a newer queued bias.
Steady-state reports perform fixed revision comparisons only, allocate no
managed memory, and never wait for disk.

Loading a record adopts the bias before controller activation and marks that
revision already persisted, preventing a reconnect from rewriting the same
record. It also leaves automatic calibration stopped until the user chooses
**Restart gyro calibration** in Switch 2 Controls.

## Verification

- `Switch2GyroCalibrationFileStoreTests` cover ordered newest-write wins,
  fixed record size, opaque naming/content, peer mismatch, digest corruption,
  non-finite values, and the absolute bias cap.
- `Switch2StationaryGyroCalibrationTests` cover adoption in degrees/second,
  observation reset that preserves bias/revision, stationary qualification,
  replacement behavior, and zero allocations.
- `Switch2RuntimeInputDeviceTests` prove a live Pro commit reaches persistence,
  reconnect adoption precedes activation, joined sides remain independent,
  right-only standalone binding is exact, and invalid shapes fail closed.
- `Switch2BluetoothRuntimeInputSinkTests` exercises 10,000 warm reports with a
  loaded persistent bias, zero rewrites, and zero managed allocations.

These are software and replay claims. They are not evidence of physical
sensor stability, filesystem durability under sudden power loss, radio
behavior, representative-game motion, or end-to-end hardware latency.
