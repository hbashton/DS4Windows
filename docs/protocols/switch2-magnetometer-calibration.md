# Switch 2 magnetometer calibration

Status: implemented in the canonical Switch 2 motion runtime and covered by
deterministic software tests. Live-controller magnetic-environment validation
is still required.

## Source basis and fit quality

The calibration model is adapted from Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/controller.py::_fit_full_soft_iron_calibration`. An explicit calibration
session retains at most 20,000 finite raw magnetic samples. A full ellipsoid
fit is adopted only when it has at least 500 samples, more than 25 raw units of
range on every axis, full algebraic rank, a positive ellipsoid, correction
condition no greater than 3, at least seven of eight orientation octants, RMS
relative residual no greater than 0.08, and P95 residual no greater than 0.15.

When the full fit fails but a sufficiently covered min/max capture still
produces a finite, invertible, well-conditioned matrix, the donor-compatible
diagonal min/max correction is adopted. Otherwise the attempt is rejected and
the previous calibration remains active.

## Runtime ownership

Each physical IMU owns its calibration. A Pro Controller has one owner; a
standalone Joy-Con has one side; a joined Joy-Con runtime retains independent
left and right owners and adopts a two-side calibration attempt atomically.
The raw sample is transformed as `matrix * (raw - bias)` before the existing
quality-gated magnetic yaw assist. Completing or loading a fit clears the
magnetometer cache, increments the magnetic calibration epoch, and resets yaw
learning so raw and calibrated coordinate histories can never mix.

The ordinary report path adds only the finite check, bias subtraction, and
fixed 3x3 multiply. It allocates nothing and creates no mapper, scheduler,
thread, or queue. Ellipsoid fitting and sample-buffer allocation occur only on
the user-invoked calibration control path.

## UI and lifecycle

The **Switch 2 Controls** profile section exposes Start, Complete, and Cancel
controls for the currently selected physical Switch 2 controller. While a
session is collecting, transport input continues to drain normally, but the
logical DS4Windows controller publishes neutral input. This prevents the
figure-eight motion from driving a game and matches Switch2Connect's output
suppression. Cancel and rejected completion immediately restore ordinary
publication without replacing the prior fit.

The UI reports sample count, adopted model, octant coverage, RMS residual, and
P95 residual. Calibration is physical-controller data rather than profile
policy; the adjacent 9-axis assist checkbox remains the per-profile opt-in.

## Persistent identity

Successful fits use the existing DPAPI-protected install-key boundary to
derive an install-local HMAC pseudonym from the stable Bluetooth association
identity or USB container identity. Neither the Bluetooth address, Windows
DeviceId, USB path, container GUID, serial, bond, nor key material reaches the
runtime, UI, logs, filenames, or records.

Records are fixed-size and versioned, repeat the expected 16-byte pseudonym,
contain only the validated bias/matrix/reference/model, carry a truncated
SHA-256 corruption digest, and are replaced atomically. Loading occurs before
runtime activation. Malformed, corrupt, mismatched, or ill-conditioned records
are ignored and cannot enter motion projection.

## Verification

`Switch2MagnetometerCalibrationTests` covers full off-diagonal ellipsoid
recovery, diagonal fallback, narrow-axis rejection, cancel/non-finite input,
and zero allocations over 20,000 warm transforms.

`Switch2RuntimeInputDeviceTests` covers calibration-time neutral publication,
completion/cancel recovery, full-fit adoption, persistent write, and reconnect
load. `Switch2MagnetometerCalibrationFileStoreTests` covers byte-exact record
size, opaque naming, round trip, digest corruption, different-peer rejection,
single-bind ownership, and pre-activation adoption. The broader Switch 2 suite
also covers production Bluetooth and USB composition with the optional
persistence boundary.
