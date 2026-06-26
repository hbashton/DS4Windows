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

## Microphone input contract

DualSense Bluetooth microphone packets share HID report ID `0x31` with normal controller input. Direct L2CAP references see `A1 31 flags ...`; Windows HidBth strips the `A1`, so DS4Windows sees `31 flags ...`. Bit 1 in the flags byte identifies a 71-byte Opus microphone payload. Mic packets must be handled as audio only and must never be parsed as buttons, sticks, touchpad, or gyro.

DS4Windows arms the physical Bluetooth mic stream through the verified combined Bluetooth audio report `0x36`, not the earlier standalone `0x31` state-report attempt. The top-level combined audio-control byte is `0xFE` without mic input and `0xFF` with mic input; bit 0 is the mic enable. A control-only `0x36` packet is sent when mic-in starts, and every later combined speaker/haptics packet keeps that byte asserted while mic-in is requested.

`extras\dualsense-bt-mic-probe.ps1` is the isolated diagnostic path. It opens the controller HID path outside DS4Windows, can compare the DS5Dongle-style combined `0x36` enable bit against the earlier standard `0x31` state attempt, and logs raw report prefixes/counts without feeding packets into the mapper.

Reference behavior from DS5Dongle/DS5_Bridge indicates the mature path enables mic streaming with bit 0 of the outbound combined Bluetooth audio report's control byte, then diverts mic-tagged `0x31` input frames before normal input parsing. DS4Windows follows that isolation rule: mic-tagged frames are dropped or decoded as audio and never parsed as controller buttons, sticks, touchpad, gyro, or desktop mouse actions.

The stream can be sticky on controller firmware. Turning the DS4Windows checkbox off stops routing/decoding mic packets and stops asserting the enable bit, but a full controller reconnect may be required to stop the controller from sending mic-tagged frames during that Bluetooth session.

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

For microphone work, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\extras\dualsense-bt-mic-probe.ps1 -Seconds 10 -Mode Combined36
```

Use `-Mode Both` to compare combined `0x36` and standard `0x31` arming attempts, and `-RawHex` only when raw packet bytes are needed for protocol analysis.
