# DualSense Bluetooth Audio and Haptics

## Scope

This branch keeps three DualSense feedback paths separate:

| Path | Source | Physical controller transport |
| --- | --- | --- |
| Rumble, adaptive triggers, lightbar, LEDs | DualSense HID output report `0x02` | normal DualSense HID output (`0x31` on Bluetooth) |
| Advanced haptics | VIIPER virtual UAC channels 3/4 | Bluetooth HID `0x32`, packet `0x12`, 3 kHz signed stereo PCM |
| Controller speaker audio | selected Windows render endpoint, including VIIPER's virtual `Wireless Controller` endpoint | Bluetooth HID `0x35`, packet `0x13`, 48 kHz stereo Opus |
| Controller microphone | physical DualSense Opus or DualShock 4 SBC microphone frames | VIIPER virtual DualSense/Edge 48 kHz stereo or DualShock 4 16 kHz mono UAC capture endpoint |

The channels are intentionally not mixed. In particular, the advanced-haptics PCM stream is never converted to generic rumble or routed to the controller speaker.

Speaker and microphone routing follows the emulated controller selected by the
profile, not the physical model. A physical DualSense can therefore feed a
virtual DualShock 4 audio endpoint, and a physical DualShock 4 can feed a
virtual DualSense endpoint. DS4Windows selects the matching virtual render
endpoint automatically and converts microphone PCM to the virtual endpoint's
native sample rate and channel layout.

## DualShock 4 Bluetooth speaker ownership

Enabling the physical DualShock 4 speaker creates one bounded audio owner for
the controller slot. That owner contains the Windows capture session, encoder
thread, reusable deadline timer, and (except for the measured shared-handle
diagnostic mode) one dedicated write-only HID session. The 8 ms production
report clock blocks on the reusable high-resolution timer; it does not busy
spin, raise the process-wide timer resolution, or run at `Highest` thread
priority.

Rumble and lightbar changes observed by the physical input loop are copied into
a fixed latest-value mailbox. The input loop never drains the HID audio queue or
waits for an overlapped completion. The audio owner submits that value ahead of
its next speaker report and acknowledges it only after successful completion;
failed writes remain pending, while an older completion cannot erase a newer
effect. The final disable closes mailbox admission and drains any already
accepted effect before the audio-mode-off barrier.

Disabling speaker streaming is a terminal owner transition. DS4Windows first
detaches and closes the Windows capture session, wakes and joins the encoder,
cancels any fixed overlapped write that delays retirement, and sends the final
ordered audio-mode report. It then unregisters and closes the HID lane and
disposes every wait handle. If the controller vanishes before the final report,
the host speaker state is still retired so ordinary rumble/lightbar output can
never target a dead audio owner. Microphone state is preserved independently,
and its ordered publisher prevents an old speaker-disable report from
overwriting a newer microphone mode. Stop and immediate restart commands share
one per-slot FIFO lifecycle executor, so the old capture and HID owner always
retires before a replacement can register or enable its lane.
An unexpected capture or native-write exit withdraws only its matching slot
generation and queues that same FIFO retirement. A delayed old callback cannot
clear or disable a newer owner, and status never reports an ended worker as
ready.

### Software render endpoints

The DualShock 4 Bluetooth speaker keeps the standard Windows loopback path for
ordinary render devices. A separate polling loopback with a 4 ms requested
WASAPI buffer is selected only when the chosen endpoint has both SteelSeries
Sonar product identity and the `ROOT\MEDIA` controller/interface projection
exposed by the Sonar virtual audio adapter. `SWD\MMDEVAPI` alone is deliberately
insufficient because Windows uses it for ordinary audio endpoints too. USB,
HDAUDIO, or Bluetooth identity evidence forces the standard path. This keeps
physical sound devices and unrelated virtual mixers on their established
capture contract while avoiding the long packet bursts caused by NAudio's
default roughly 100 ms polling buffer on the matched Sonar route. Four
milliseconds is the requested buffer size; Windows may negotiate a different
size, and NAudio polls at half the resulting buffer duration.

The polling route is still shared-mode WASAPI loopback with Windows PCM
conversion enabled. Its exposed format is NAudio's normalized capture format;
in particular, Sonar's extensible 32-bit float format remains IEEE float and is
not decoded as signed Int32. Capture binds to the endpoint resolved at start.
This narrow policy does not yet follow a later default-device change or rebuild
a running session when an audio router recreates its endpoint.

## In-game setup

1. Install a VIIPER build containing the DualSense UAC interface.
2. Select **DualSense** in the DS4Windows profile.
3. Connect a physical DualSense or DualSense Edge over Bluetooth.
4. In the profile's **Controller audio** section, enable **Stream audio to controller**.
5. Select the virtual `Wireless Controller` / DualSense render endpoint as **Audio source**.
6. In the game, select that same endpoint when the game provides a controller-audio output choice.

The VIIPER virtual audio endpoint is created by VIIPER's USB Audio Class function. DS4Windows does not create a fake Windows audio endpoint in user mode. Windows audio endpoints require a driver-backed device interface; creating one separately would require an installed, signed virtual audio driver.

## Bluetooth speaker processing

The profile can optionally process the physical Bluetooth controller-speaker stream before Opus encoding:

- **Dynamic range: Balanced** raises quieter detail while restraining loud effects.
- **Dynamic range: Strong** applies a narrower range for larger volume differences.
- **Bass/body boost** adds 0-6 dB around 200 Hz and filters unusable sub-bass below 70 Hz.

Selecting a DualSense or DualSense Edge as the emulated controller initializes the profile to **Balanced** and **3 dB** of bass/body boost. The user can tune or disable those values afterward. The processor is stereo-linked and bufferless, so it adds no look-ahead frame or transport latency. **Off** and **0 dB** preserve the original PCM path. These controls affect speaker audio only; advanced-haptics channels remain untouched.

## Protocol basis

This is an independent implementation built from public HID descriptors,
documented Sony report formats, and hardware traces captured during development.
The speaker, microphone, state, lightbar, trigger, and haptics lanes are owned by
one DS4Windows transport so their ordering remains deterministic.

## Diagnostics

When the virtual audio interface is active, a VIIPER traffic capture should contain:

- `audio-haptics-out` for host audio written to the virtual UAC OUT endpoint.
- `saxense-hid-0x32` for the generated Bluetooth haptics report.

If the Windows `Wireless Controller` audio endpoint has an error state, remove stale VIIPER DualSense devices, restart VIIPER, then recreate the output. The endpoint descriptor changed after the initial experimental build, so Windows can retain an old failed device instance until the virtual device is recreated.
