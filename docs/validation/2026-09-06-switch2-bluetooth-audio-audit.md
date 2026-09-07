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

1. Obtain timestamped, known-working console-to-controller headphone traffic,
   including stream setup/stop and negotiated write size. A console plus a
   suitable BLE capture setup, or an already-authorized equivalent capture,
   is needed; neither is established as available on this machine. Do not
   alter the user's bond or flash firmware just to obtain it.
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
