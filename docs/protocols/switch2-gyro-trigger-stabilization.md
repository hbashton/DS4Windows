# Switch 2 in-app gyro trigger stabilization

This behavior is pinned to the operative trigger-modifier policy in
`Switch2Connect` commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/controller.py`. DS4Windows reuses that policy inside its canonical gyro
mouse paths; it does not introduce another mapper or presentation thread.

## Profile contract

`Switch2GyroTriggerTunings` is a sparse per-profile table and appears as
**In-app gyro trigger stabilization** under **Switch 2 Controls**. The user
first selects **Gyro Mouse** or **Gyro Mouse Joystick**, then the exact existing
DS4Windows activation-source token, including Always On. Each source has
independent tuning and may select any Switch 2 semantic button for either of two
policies:

- deadzone buttons freeze output on press and release edges, apply a
  directionally projected subtractive soft deadzone while held, and retain the
  deadzone for the configured release window;
- dampening buttons multiply output while held and retain that multiplier for
  the configured release window.

The donor defaults are retained: 15 deadzone units, 100 ms press and release
freezes, 200 ms deadzone and dampening release latches, and 90 percent
dampening. Durations are bounded to 0 through 60,000 ms, deadzone to 0 through
100, and dampening to 0 through 100 percent. Invalid persisted values normalize
to those defaults. A legacy profile without this element has no selected
buttons and therefore preserves prior behavior. Mouse and Mouse Joystick are
separate scopes, matching the donor's separate in-app mapping layers.

## Runtime ownership and lifecycle

The modifier is eligible only when the exact input device is a
`Switch2RuntimeInputDevice` and the active output mode is **Gyro Mouse** or
**Gyro Mouse Joystick**. It observes the already-mapped semantic button state,
including C and source-specific Joy-Con 2 or Pro paddles. A joined pair uses one
identity containing the pair epoch plus both device and transport generations.
A Pro controller uses its exact device and transport generations.

The trigger evaluator retains the exact activation source that caused the
inactive-to-active edge. For an AND chord it uses the newly pressed member that
completed the chord; for a toggle it retains the source across the resulting
toggle interval. Reverse-ratchet and Always On configurations have deterministic
configured-source entries as DS4Windows extensions.

The state machine resets its edge baseline and all time windows when the source
identity, activation-source entry, tuning, profile revision, or timestamp
ordering changes. Deactivation also clears the windows. Reactivation establishes
a baseline from the currently held buttons, so reconnecting, switching
profiles, changing tuning, changing source, or engaging gyro while holding a
button cannot manufacture a press edge.

Report completion QPC time is authoritative for all windows. Invalid time or
source metadata clears modifier state. The warmed report-time state machine and
both arithmetic transforms allocate no managed memory.

## Presentation boundary

Freeze resets the existing mouse accumulator or mouse-joystick smoothing path.
For high-rate Switch 2 mouse presentation it also withdraws the gyro source, so
the 1 kHz presenter cannot repeat motion during a freeze. Deadzone and
dampening are applied after DS4Windows' established gyro deadzone and before
jitter compensation, smoothing, inversion, and output presentation.

The modifier never changes physical parsing, calibration, canonical SixAxis
motion, Cemuhook/DSU output, virtual-pad motion, ordinary controller mapping, or
Xbox One encoding. Optical-sensor gyro aiming keeps its independent per-side
tuning because the selected left or right sensor is itself the activation
source.
