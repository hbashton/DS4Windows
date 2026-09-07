# Switch 2 Pro USB audio: physical headphone output confirmed

## Scope and result

2026-09-06, user-connected Switch 2 Pro over USB, controller headphone jack
cabled to the PC's Realtek Line In. The user explicitly confirmed **L-in** and
authorized controller audio testing. Tests ran portably on the Desktop while
the existing b86 controller lab continued running. No installed DS4Windows,
VIIPER, drivers, sound defaults, endpoint volumes, microphone permissions or
Sonar settings were changed. No ambient microphone was opened or recorded.

**Confirmed:** standard Windows USB-audio playback physically arrives at the
controller's 3.5 mm jack, with stereo channel assignment intact and bounded
start/stop. There is no need for DS4Windows to invent an audio report format or
route this playback through its input mapper or VIIPER.

**Not confirmed:** headset microphone signal, Bluetooth audio, game-specific
audio selection, sample loss/quality across long sessions, or audio latency.
A normal AUX-to-Line-In cable tests headphone output, not the headset's mic
contact. A compatible headset microphone is needed for that acceptance test.

## Evidence

Windows reports the physical composite device `USB\VID_057E&PID_2069`, with
MEDIA interface `MI_02` handled by `usbaudio`, driver 10.0.26100.8972. HID and
WinUSB interfaces remain separate. WASAPI exposes active native endpoints:

- `Headphones (Switch 2 Pro Controller)` — render; mix float 48 kHz, stereo.
- `Microphone (Switch 2 Pro Controller)` — capture; mix float 48 kHz, stereo.

The Windows mix format is not a claim about the wire format. The audited USB
descriptors declare alternate streaming interfaces with stereo 16-bit PCM at
48 kHz, output endpoint `0x03`, input `0x83`, 192-byte isochronous packets and
`bInterval=1`. The microphone terminal and stream descriptors disagree about
channel count; do not invent microphone channel semantics from their names.

The portable probe (`utils/Switch2UsbAudioProbe`) played a 500 ms -34 dBFS
stereo tone, left 440 Hz / right 660 Hz, with 10 ms fades. It captured only the
user-selected Realtek Line In and kept samples in RAM. Representative measured
100 ms blocks from the final run (60,960 captured stereo frames):

| Capture window | Left RMS | Right RMS | Left 440 Hz | Left 660 Hz | Right 440 Hz | Right 660 Hz |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Before, 200 ms | 0.000593 | 0.000594 | 0.0000151 | 0.0000969 | 0.00000912 | 0.000101 |
| Tone, 500 ms | 0.031163 | 0.031106 | 0.043931 | 0.000137 | 0.000105 | 0.043695 |
| Tone, 700 ms | 0.031148 | 0.031125 | 0.043387 | 0.000238 | 0.000349 | 0.042564 |
| After, 1,000 ms | 0.000622 | 0.000622 | 0.0000154 | 0.0000687 | 0.0000233 | 0.0000767 |

This is a manual evidence-based analog-delivery verdict, not merely the probe's
successful WASAPI completion. The window timestamps are relative to capture,
not synchronized DAC/ADC latency measurements. No global output was redirected;
the PC's default remains SteelSeries Sonar - Gaming.

## DS4Windows presentation

The Switch 2 Controls page now includes **Use a headset over USB** for Pro
controllers and offline profiles (not Joy-Cons). It provides the native
Headphones/Microphone selection instructions and a Windows Sound settings
shortcut. It explicitly labels Bluetooth headset audio as unsupported. The
shortcut only opens `ms-settings:sound`; it changes no setting automatically.

The existing Sony audio-routing capability gate intentionally stays closed for
Switch 2. That gate controls DS4Windows-managed Sony transports; it is not a
denial of the independent native Windows USB audio device. Saved Sony speaker,
mic, and audio-haptics choices must not accidentally arm those routes on a
Switch 2 or make its input runtime report a false startup failure.

Native USB audio selection is independent of virtual-pad type. A user may pick
the physical endpoint in the game or Windows Sound settings; this work does
not silently change default output, add a duplicate loopback mixer, or claim
that a virtual Xbox headset has been implemented.

## References and remaining boundaries

- `ndeadly/switch2_controller_research`, audited commit
  `d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92`, `descriptors.md` and
  [Bluetooth interface](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md).
  Bluetooth exposes audio characteristics on newer firmware, but the documented
  50-byte audio field is **unknown format**, not established raw PCM.
- Local Switch2Connect README explicitly lists wireless Pro headphone/mic audio
  as unsupported. No working wireless decoder was found there to copy.
- [Nintendo headset microphone instructions](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68398/)
  distinguish headset-mic use through the controller jack.
- [Microsoft's Windows Settings URI documentation](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings#sound)
  documents the non-mutating Sound settings navigation used by the button.

No firmware flash, driver replacement, undocumented USB command, Bluetooth
audio enable command, or microphone capture was attempted for this result.

## Validation status

- Targeted audio applicability, UI policy/layout and artwork tests: 107 passed.
- Headset illustration rendered and visually inspected without clipping.
- Full Release/x64 suite: **3,892 passed, zero failed, three opt-in audio tests
  skipped** (3,895 total). Allocation assertions remained enabled and passed.
  `DS4WindowsTests/TestResults/full-b88-usb-headset-release.trx`.
- Portable probe builds with zero warnings/errors; its source is included in
  this checkpoint. Main app publishes successfully (existing warnings remain).
- Live profile-card navigation and headset microphone acceptance: pending.
- The separate b87 Link/Unlink build was prepared but not launched; the running
  b86 session and unsaved profile edits were left alone.

## Prepared portable b88

`5.0.4.88-USB-Headset-Link-Preview` includes the preceding `d2f1a89`
Link/Unlink/retained-pad changes. Desktop lab shortcut: `DS4Windows Portable
b88.lnk`; runtime folder: `DS4Windows-current-2026-09-06-b88-usb-headset-link`.
The launcher snapshots b86's saved settings only after the existing apps exit.
It validates all four hashes, uses the unchanged pinned broker, and does not
alter Program Files. **Not launched yet**, pending the user's save/exit.

SHA-256:

```text
DS4Windows.dll 3CF7A25036EE637D2BA70498B2D16121F9CF70B991F3169CEC24D8626D393CE3
DS4Windows.exe 5E3847A02125F9084B052E0D69F2045D1D7EE27C2EE20532725D34B61E447010
viiper.exe 7B6B00CF3AC205549AF80692E45BD7785D3B8BC558A92D4FF5A1A61060592B78
xbox-one-authorized-persona.json 2A85D3395529C7305F55338E4965A7FFD4E269DDE535FA41BD98BC54D67111C4
```
