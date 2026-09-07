# Switch 2 Pro Bluetooth headset audio: evidence and remaining work

Status: **not implemented or hardware-verified**. Bluetooth controller input,
HD rumble and LEDs are separate, already-existing paths. USB headphone output
is physically verified in `2026-09-06-switch2-pro-usb-audio.md`. Neither fact
establishes Bluetooth headset audio.

## Source audit

- Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`, README known
  limitations: wireless Pro headphone/microphone audio is unsupported. Its
  DualSense audio-haptics implementation is not a headphone codec.
- PadForge current upstream `55af8c9e82929fe8210ce86786e4f8b8b5ec0447`:
  `PadForge.App/Common/Input/AudioPassthroughService.cs` describes Sony
  speaker-capable devices; its Bluetooth routes are DualSense Opus and DS4
  SBC. No Switch 2 headset encoder was found in the examined paths. The local
  checkout was left unchanged; the upstream commit was fetched for inspection.
- HIDMaestro local `9df50410230c11b410f43909ede0e5fc8b23d15b`: its generic
  USB audio engine/virtual descriptors do not establish a physical Switch 2
  BLE headset codec. Current upstream was identified as
  `00b7303f8533c3fe10687a765c84e929b34a5e9c`; this note does not claim a full
  audit of that newer tree.
- [ndeadly reference](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md):
  firmware 2.0+ Pro controllers expose audio output
  `cc483f51-9258-427d-a939-630c31f72b06` and audio input
  `7492866c-ec3e-4619-8258-32755ffcc0f9`. The rumble UUID ends in `b05`;
  ordinary Pro input ends in `0f8`. These are not interchangeable.
- [switch2mac protocol](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/PROTOCOL.md)
  and its [audio investigation](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/audio-investigation.md)
  describe an acknowledged `0x17/0x02` setup command and unresolved playback
  experiments, not a proven complete encoder/transport. They report ordinary
  input starvation when subscribing to audio notifications. This is a
  third-party hardware observation, not a reproduced Windows result.
- The other exact output-UUID code-search match, the ESP32 controller emulator
  at `0ea0c62aeab2440c7f9c77f29cf87ffa3b4c192f`, declares characteristics but
  its examined GATT handler does not implement headphone playback.

## Important correction: Opus is not ruled out for real audio

The summary in switch2mac's protocol notes claims multiple codecs are ruled
out. Its underlying [capture analysis](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/capture-format-analysis.md)
actually tests the **40-byte telemetry region at offset 66**. The recording
had no headset microphone, and the **50-byte audio region at offset 15** was
idle. Rejecting a codec on motion bytes cannot reject it for live audio.

The published idle region begins `F8 FF FE` and then has 47 zero bytes. Those
first three bytes match a [documented Opus silence packet](https://docs.discord.com/developers/topics/voice-connections#voice-data-interpolation).
Using the existing Concentus 2.2.2 library, the new offline utility reproduced:

| Synthetic fixture | Decoded samples/channel at 48 kHz | Duration | Channels | RMS / peak |
| --- | ---: | ---: | ---: | ---: |
| `F8 FF FE` | 960 | 20 ms | 1 | 0 / 0 |
| `F8 FF FE` + 47 zeros | 960 | 20 ms | 1 | 0 / 0 |

This is an **Opus-compatible idle-pattern result**, not identification of
Nintendo's live codec. It used synthetic reconstructions of published bytes,
not packets from the user's controller. No radio or audio device was opened.
Successful decoding alone is insufficient: non-idle frames must correlate
with a known stimulus and their timestamps. Do not trim zeros from captured
packets. The nominal 5 ms setup parameter cannot be assumed to describe the
microphone packet duration; the Opus interpretation above is 20 ms.

Reproduce with:

```text
dotnet run --project utils/Switch2BluetoothAudioAnalysis -c Release -- --reference-idle
```

## Public captures examined offline

Scapy 2.7.0 decoded these existing public captures. No nonempty link-layer
continuations were counted. No decoded writes to `0x002c` or notifications
from `0x002e` appeared; these are pairing/reconnect/wake samples, not a
known-good headphone-output recording.

| Capture under `captures/nrf52840` | Packets | SHA-256 |
| --- | ---: | --- |
| `btle_procon2_pairing_decrypted.pcapng` | 1504 | `f0fdb0964795b00bd04652541a018d659949dd6d7205baba28fd23f5969ba47f` |
| `btle_procon2_reconnect_decrypted.pcapng` | 324 | `35232c557f2610cf0f6d7ef9a6e82cc1ba1f200ce15fc5792bfddefabf3f0732` |
| `btle_procon2_wake_console_decrypted.pcapng` | 3769 | `5ec5558e4de4f4ac1ff266900cc71248676f2d0f8bb1a02f826a6a72c8e4f53f` |

`utils/Switch2BluetoothAudioAnalysis/summarize_att.py` reproduces the inventory
without displaying payloads, addresses or keys. It does not decrypt or
reassemble fragments. The two motion capture files produced no decoded ATT
in the initial check and are inconclusive; they are not counted as negative
audio evidence. Absence in a sample is not proof of an unsupported device.

## Implementation and acceptance gates

1. Establish headphone-output setup/stop, framing, codec, and negotiated write
   size. Timestamped known-working console traffic is the strongest reference,
   but is not the only permitted research path. Bounded, source-supported
   hypotheses on the dedicated audio characteristic can also be evaluated
   against the user's physical Line-In loopback. A write completing, a codec
   accepting synthetic silence, or a haptic actuator buzzing cannot establish
   headphone delivery. Do not alter the user's bond or flash firmware merely
   to obtain evidence. No known-working headphone capture is currently available.
2. Separately validate the microphone candidate with a real headset mic and
   known stimulus if input audio is required. The current controller AUX-out
   to PC Line-In cable does not provide microphone-contact input. Do not
   feed PC line-level output directly into a headset mic contact.
3. Extract only the audio region, compare decoded waveform/frequencies and
   duration, and establish output framing independently. Do not assume mic
   encoding is identical to headphone encoding. A GATT write completing is
   not an audible-delivery verdict.
4. Reuse the existing audio-source plumbing and exact controller-owned BLE
   lifetime. Audio encoding/writes must not block the input publication path.
   Bound queued audio, honor negotiated write capacity, cancel/drain on
   disconnect, and verify coexistence with normal input/rumble/LEDs. Do not
   copy another platform's arbitrary chunk splitting as a proven protocol.
5. Verify a quiet, bounded left/right signal physically through the same
   Line-In cable, then check levels, frequency/channel assignment, stop,
   disconnect/reconnect and controller report intervals while audio runs.

Production currently keeps headset UUIDs out of input and rumble. A new
regression test locks down that separation. No audio CCCD was enabled, no
audio setup/output packet was sent, and no firmware, bond, installed driver,
Windows default or endpoint volume was changed during this audit. Bluetooth
headset UI remains unavailable until actual delivery is verified.

Verification: the protocol test group passes 26 tests. The complete Release
suite with the new separation regression passes **3,902 tests, zero failures,
three opt-in audio skips** (`full-b89-audio-with-bluetooth-isolation.trx`),
including allocation checks. The offline utility also rejects unknown modes
with exit code 2. These are software checks, not Bluetooth delivery acceptance.

## 2026-09-07: Bluetooth-only capability probe

The user left the Pro on Bluetooth with its headphone jack cabled to the PC's
Realtek Line In. USB controller audio endpoints were absent. No microphone,
Line-In recording, or audio playback was opened in this session.

1. Windows' connected **association-endpoint** inventory identified the Pro.
   The default device-interface enumeration did not; it is not equivalent.
2. With portable b88 running, service discovery succeeded but characteristic
   discovery returned AccessDenied, including from an elevated helper.
   Explicit `OpenAsync(SharedReadAndWrite)` exposed **SharingViolation**.
   That establishes competing service ownership, not a codec failure.
3. After checking that no profile editor was open, Windows Restart Manager
   was used to close only the exact portable b88 process gracefully, with its
   force flag **off**. The scope was verified as one process, no services or
   file-resource dependents. Shutdown returned 0; DS4Windows logged normal
   controller/USB-IP cleanup and Stopped. VIIPER remained running and was not
   altered. No reboot, process kill, or portable IPC-policy bypass was used.
4. The controller became disconnected. Opening its previously observed Windows
   identity still returned the correct Pro name, but uncached service discovery
   returned **Unreachable**. This is not evidence of a broken association.
5. A two-second targeted wake manufacturer advertisement, using the documented
   layout and the selected controller's exact address, reached Started and
   then Stopped. Windows reserves GAP Flags; this was not a byte-identical
   console advertisement. The subsequent service query remained Unreachable.
   A separate 30-second scan saw no validated advertisement from this Pro.
   These observations do not prove that console-style wake is unsupported.

`utils/Switch2BluetoothAudioProbe` retains this bounded inventory/reconnection
probe. It has no GATT command/output/CCCD writes and never reads a microphone
payload. Its explicitly selected wake mode is the only transmitter. It cannot
install drivers, change the controller association, render or record audio, or
change endpoint defaults/levels. Exact identities are printed only by its local
inventory mode and are omitted from this public evidence note.

Review corrected an unshipped probe assumption: byte 13 of ordinary Pro input
must not be labeled as the headset report's jack state. The probe now performs
no input-value read at all. No such decoded observation had been obtained from
the hardware before this correction.

Verification: Release x64 **30 protocol tests passed, zero failures** (four new
probe checks plus 26 existing phase-one tests), and the standalone utility
builds successfully. The first test attempt omitted the required x64 platform
and failed to resolve existing architecture-specific references; a new
synthetic advertisement fixture also initially had an extra zero byte, which
was corrected before the passing run. No failed test is waived.

**Remaining:** wake the controller, inventory the released audio service, then
establish the headphone-output protocol and measure actual delivery via Line
In. No audio setup packet or audio-output payload has yet been sent. DS4Windows'
Bluetooth input/rumble implementation and the installed Program Files builds
were not modified. The task remains incomplete; this is diagnostic groundwork,
not Bluetooth headphone support.
