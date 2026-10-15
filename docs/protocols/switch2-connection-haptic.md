# Switch 2 connection confirmation haptic

This contract is pinned to `Switch2Connect` commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`, specifically
`Controller.trigger_connection_haptics`, `Controller.set_vibration`, and the
USB override in `UsbHidController.trigger_connection_haptics`.

## Exact signature

The cue is a two-part native HD-rumble signature:

1. bass for 200 ms: source LF control `0x060`, LF amplitude `0x350`, HF
   control `0x0c0`, and HF amplitude `0x250`;
2. neutral for 10 ms; and
3. a sharp click for 1,000 ms: the neutral LF control `0x0e1`, LF amplitude
   `0x030`, HF control `0x1e2`, and HF amplitude `0x300`, followed by neutral.

`ignore_freq_scaling=True` bypasses only frequency scaling in the donor. Its
shared renderer still applies a 1.3x LF multiplier, a Pro 1.0x/Joy-Con 0.6x HF
multiplier, 10-bit clamping, and the Joy-Con combined-amplitude limiter. The
actual Pro bass amplitudes are therefore 1,023/592 and its click amplitudes are
62/768. The actual Joy-Con values are 759/264 and 62/460 respectively.

Each effective active value occupies the first of three five-byte subframes.
The second and third subframes retain the donor's neutral controls (`0x0e1`,
`0x1e1`) with zero amplitude. A Pro Controller receives the Pro group on both
physical actuators. A standalone Joy-Con writer selects its own Joy-Con group,
while a joined pair receives that Joy-Con group on both halves. USB waits 1,200
ms after activation commit before starting, matching the donor's first-
connection readiness accommodation.

## Ownership and arbitration

The cue does not write HID, WinRT, or USB directly. Its exact oscillator group
is staged against the exact canonical frame returned by the existing fixed
`ProfileEffect` lane. That origin is the lowest feedback priority, so audio,
native game output, and an explicit test preview can supersede it. The staged
native group is consumed only if that exact frame wins the normal feedback
runtime and sole physical-writer pump.

The transport owner starts the asynchronous schedule only after its input
activation commit succeeds. Prepare and abort-before-commit remain physically
silent. The scheduler performs no work in the controller input report path.
Non-neutral profile or preview rumble cancels the cue; terminal neutralization,
disconnect, and unpublished abort close admission before the task can publish
another phase. Each logical runtime generation can start at most one cue.

## Profile compatibility

`Switch2ConnectionHapticEnabled` is persisted in the existing profile and is
shown as `Play connection confirmation haptic` under `Switch 2 Controls`. It
defaults on for legacy profiles to match Switch2Connect behavior. Turning it
off prevents the task from being created after the next controller activation.

Unit tests establish source constants, subframe placement, profile migration,
BLE and USB sole-writer delivery, the four-stage schedule, and terminal
retirement. Audible/tactile quality and hardware onset remain physical
validation gates.
