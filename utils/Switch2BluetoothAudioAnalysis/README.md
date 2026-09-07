# Switch 2 Bluetooth audio: offline hypothesis check

Run `dotnet run --project utils/Switch2BluetoothAudioAnalysis -c Release -- --reference-idle`.
It performs no hardware or audio I/O, changes no device settings, and writes
only numeric results to standard output. It uses the existing Concentus 2.2.2
dependency; nothing is added to the application runtime.

The three-byte control is the documented Opus silence packet `F8 FF FE`.
The 50-byte fixture reconstructs the published Switch 2 headset-input idle
region (`F8 FF FE` followed by 47 zero bytes). It is **not a capture from the
user's controller**. A successful decode means this idle pattern is compatible
with Opus, not that Nintendo's live input codec or output format is proven.
No decoder result is used as permission to write packets to hardware.

References:

- [Published input layout](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md).
- [Capture-region analysis](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/capture-format-analysis.md): the codec rejection in section 4 concerns **telemetry at offset 66**, not non-silent microphone frames at offset 15.
- [Opus silence control](https://docs.discord.com/developers/topics/voice-connections#voice-data-interpolation).
- [Opus packet framing](https://www.rfc-editor.org/rfc/rfc6716.html#section-3).

Do not assume the nominal 5 ms output configuration implies a 5 ms input
packet. Compare decoded duration against a timestamped non-idle capture.
Do not strip trailing zero bytes from real packets or treat the motion region
as audio. Headphone-output framing/codec must be established independently.

## Existing public capture inventory

`python summarize_att.py <explicit.pcapng> [another.pcapng]` uses Scapy 2.7.0
to summarize decoded ATT operations and a capture hash. Install Scapy into a
separate portable tools environment, not the DS4Windows package. This reads
files only; it does not require a capture driver. It prints neither packet
payloads nor Bluetooth addresses/keys and does not decrypt or reassemble
fragmented traffic. Zero decoded ATT records is inconclusive, not evidence
that a controller lacks audio. Use a full protocol analyzer for fragments.
