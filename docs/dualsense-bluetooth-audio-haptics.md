# DualSense Bluetooth Audio and Haptics

## Scope

This branch keeps three DualSense feedback paths separate:

| Path | Source | Physical controller transport |
| --- | --- | --- |
| Rumble, adaptive triggers, lightbar, LEDs | DualSense HID output report `0x02` | normal DualSense HID output (`0x31` on Bluetooth) |
| Advanced haptics | VIIPER virtual UAC channels 3/4 | Bluetooth HID `0x32`, packet `0x12`, 3 kHz signed stereo PCM |
| Controller speaker audio | selected Windows render endpoint, including VIIPER's virtual `Wireless Controller` endpoint | Bluetooth HID `0x35`, packet `0x13`, 48 kHz stereo Opus |

The channels are intentionally not mixed. In particular, the advanced-haptics PCM stream is never converted to generic rumble or routed to the controller speaker.

## In-game setup

1. Install a VIIPER build containing the DualSense UAC interface.
2. Select **DualSense (VIIPER)** in the DS4Windows profile.
3. Connect a physical DualSense or DualSense Edge over Bluetooth.
4. In the profile's **DualSense Audio** section, enable **Mirror audio to controller speaker**.
5. Select the virtual `Wireless Controller` / DualSense render endpoint as **Audio source**.
6. In the game, select that same endpoint when the game provides a controller-audio output choice.

The VIIPER virtual audio endpoint is created by VIIPER's USB Audio Class function. DS4Windows does not create a fake Windows audio endpoint in user mode. Windows audio endpoints require a driver-backed device interface; creating one separately would require an installed, signed virtual audio driver.

## Microphone input safety contract

DualSense Bluetooth microphone packets share HID report ID `0x31` with normal controller input. Direct L2CAP references see `A1 31 flags ...`; Windows HidBth strips the `A1`, so DS4Windows sees `31 flags ...`. Bit 1 in the flags byte identifies a 71-byte Opus microphone payload. Mic packets must be handled as audio only and must never be parsed as buttons, sticks, touchpad, or gyro.

The microphone enable bit lives in the `0x36` combined Bluetooth audio-control sub-report byte. DS4Windows toggles only bit 0 and preserves all other bits from the latest native combined report. It deliberately does not build a sparse synthetic `0x36` packet, because those packets also carry state, haptics, route, volume, and sequence fields that can affect normal input and speaker behavior.

If microphone packets flood the input path and normal controller frames stop arriving, DS4Windows disables mic-in locally and continues dropping mic-tagged frames. Controller input has priority over microphone capture.

## Implementation references

This is an independent implementation based on publicly documented packet behavior, not a copy of PadForge source code.

- [SAxense](https://apps.sdore.me/SAxense) documents the Bluetooth `0x32` haptics transport. Its source is MPL-2.0.
- [dualsense-bt-haptics](https://github.com/awalol/dualsense-bt-haptics) documents the Bluetooth controller speaker packet grammar and Opus framing. It is MIT licensed.
- [PadForge](https://github.com/hifihedgehog/PadForge) was used as a behavioral reference for per-controller sequencing, separate haptics and speaker lanes, and WASAPI loopback architecture. PadForge is CC BY-NC-SA 4.0, so its source code is not included here.
- DS5Dongle and DS5_Bridge were used as behavioral references for microphone packet identification, Opus frame sizing, and the rule that host mic ownership must not stomp mute LED or unrelated audio-control bits.

## Diagnostics

When the virtual audio interface is active, a VIIPER traffic capture should contain:

- `audio-haptics-out` for host audio written to the virtual UAC OUT endpoint.
- `saxense-hid-0x32` for the generated Bluetooth haptics report.

If the Windows `Wireless Controller` audio endpoint has an error state, remove stale VIIPER DualSense devices, restart VIIPER, then recreate the output. The endpoint descriptor changed after the initial experimental build, so Windows can retain an old failed device instance until the virtual device is recreated.
