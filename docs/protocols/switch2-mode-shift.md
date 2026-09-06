# Switch 2 Mode Shift mapping layer

## Source and ownership

The behavior is adapted from the GPL-3.0 Switch2Connect project at commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`, specifically
`src/controller.py`, `src/virtual_controller.py`, and `src/config.py`.

DS4Windows does not introduce a second mapper. `Switch 2 Mode Shift` is
append-only shifted-action trigger ID 37 in the existing DS4Windows mapper.
Each `DS4ControlSettings` entry owns three small alternative-action lanes,
matching the donor's independent Mouse, R Joystick, and Steering mapping
stores. Button, axis, key, mouse, macro, and extras execution still travels
through the same `MapCustom` path used by other controllers and shift triggers.

## Profile behavior

The **Switch 2 Controls** section exposes:

- Hold-to-shift activation inputs;
- press-to-toggle activation inputs; and
- independent auto-apply controls for Gyro Mouse, Gyro Mouse Joystick, and
  motion steering.

Choose the Mouse, Gyro Mouse Joystick, or Motion steering layer to edit. Then
edit an ordinary control binding, choose **Shift Modifier**, select **Switch 2
Mode Shift**, and assign the alternative action. Changing the layer selector
refreshes mapping-list labels immediately. Existing numeric trigger IDs 0
through 36 are unchanged.

The activation surface includes the common semantic buttons plus Switch 2 C,
Joy-Con 2 rail/paddle inputs, and each profile-thresholded IR sensor. A button
cannot be both Hold and Toggle. Activation inputs are consumed as in-app
commands. Both the source and already-populated destination fields are
neutralized while normal release bookkeeping continues, so the press cannot
leak a controller action or leave a prior key, macro, extras, or lightbar-macro
action active.

Profiles written by the initial shared trigger-37 implementation migrate the
shared action and extras once into all three lanes. The legacy representation
is then cleared, so subsequent saves contain only the authoritative scoped
stores. Profiles containing only a scoped action or only scoped extras still
activate the canonical custom-mapping gate.

## State machine

Tap and Hold use the donor's shared XOR behavior:

```text
button_active = tap_toggle XOR any_hold_pressed
layer_active  = gyro_auto_apply XOR button_active
```

A Hold therefore temporarily inverts a Tap-entered layer. Multiple Tap inputs
that acquire a press edge in one report collapse to one toggle edge, matching
the donor's boolean pre-pass. A joined Joy-Con report already contains both
physical halves under one pair epoch, so either half controls the same state.

Leaving a gyro-auto-applied scope clears the Tap latch. Profile revision,
device generation, transport generation, Joy-Con pair epoch, binding changes,
and timestamp regression establish a fresh held-button baseline and clear
state. A button already held across such a boundary cannot manufacture a Tap
edge.

## Hot-path and lifecycle constraints

Mode Shift is evaluated lazily only when at least one mapping selects trigger
37. The warmed policy uses fixed per-slot state, performs no allocation, starts
no timer or worker, and publishes no separate input report. Gyro auto-apply
observes the current report's established Mouse/Mouse-Joystick presentation
decision without consuming it; motion steering observes the existing profile
axis selection.

Invalid or stale Switch 2 sidecars, wrong contract versions, invalid QPC data,
and unknown persisted button masks fail closed. The policy never changes the
physical transport, canonical input frame, VIIPER output scheduler, or feedback
lifetime.
