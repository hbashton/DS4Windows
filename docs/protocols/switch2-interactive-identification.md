# Switch 2 interactive controller identification

This contract is pinned to `Switch2Connect` commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`, specifically
`ControllerFrame._on_vibrate_clicked` and the shared
`Controller.set_vibration` renderer.

## Exact Ping signature

The Controllers tab exposes `Ping` on every logical Switch 2 controller card.
One click sends two 100 ms native HD-rumble pulses separated by 100 ms of
neutral output, then returns to neutral. Re-clicking cancels and neutralizes the
old sequence before starting a fresh one.

The donor source subframe uses neutral controls `0x0e1`/`0x1e1` and amplitude
800 on both oscillator fields. It passes that same subframe in all three
temporal positions. The donor's `ignore_freq_scaling=True` render still applies
model-specific amplitude law:

- Pro Controller: LF/HF amplitudes 1,023/800 in all three subframes;
- Joy-Con: LF/HF amplitudes 696/327 after the 1.3x/0.6x multipliers and the
  combined 10-bit Joy-Con limiter.

A Pro receives the Pro group on both actuators. A joined pair receives the
Joy-Con group on both halves, while a standalone Joy-Con sends it only through
its authenticated physical writer.

## Ownership and lifecycle

Ping uses the existing fixed `TestPreview` lane. Exact native oscillator groups
are keyed to the exact canonical frame admitted by that lane and are consumed
only if that frame wins the normal feedback runtime. There is no direct HID,
WinRT, USB, timer-loop, or second-writer path.

Because identification is explicit user preview, it cancels and withdraws the
lower-priority connection cue. An ordinary preview, terminal request, removal,
or generation retirement cancels it and neutralizes any stage it owns. The
asynchronous 100 ms scheduler never runs on the controller input thread.

Unit tests pin the donor source and model-specific post-render bytes, BLE/USB
sole-writer delivery, Pro and standalone-Joy-Con routing, two-pulse timing, and
terminal neutralization. Physical tactile strength remains a hardware gate.
