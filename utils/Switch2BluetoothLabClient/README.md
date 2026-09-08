# Persistent, in-process Switch 2 Pro Bluetooth probe

This client talks only to a local diagnostic pipe in an explicitly enabled
portable DS4Windows. **Leave DS4Windows open.** It owns the Nintendo connection
procedure, input notifications, LED/rumble traffic, and controller lifetime.
This client never opens a Bluetooth device or takes controller ownership.

## Enable the probing build

Launch the hash-pinned portable lab normally, with the child environment
`DS4WINDOWS_SWITCH2_AUDIO_PROBE=1`. Both this switch and a successfully validated
`--portable-lab` context are required. No pipe is created in production mode,
for USB devices, or for Joy-Cons. Windows API reads use the **existing** service
owned by each connected Bluetooth Pro's input lease.

Each connection produces a small descriptor under
`lab-data/Switch2AudioProbe/ds4w-s2audio-*.json`. Its pipe name and transport
generation identify that specific controller lifetime; it contains no address,
association key, input buttons, or recorded audio. A completed descriptor says
`stopped`; do not reuse it. The pipe admits the current Windows user only and
uses a bounded, fixed command vocabulary with one operation at a time.

```powershell
Switch2BluetoothLabClient.exe <session-descriptor.json> status
Switch2BluetoothLabClient.exe <session-descriptor.json> inventory
Switch2BluetoothLabClient.exe <session-descriptor.json> headset-header
```

- `status`: reports connection/lifetime and advancing input counters without
  radio I/O. Repeated queries must preserve the same transport generation.
- `inventory`: explicitly queries characteristic UUIDs/properties/handles and
  negotiated ATT PDU size on the already-owned service. There is no periodic
  discovery polling and no second GATT service opener.
- `headset-header`: performs one read of the dedicated headset-input UUID and
  returns only length/jack-state framing metadata. It does not subscribe to
  microphone notifications, store audio payloads, or change normal input CCCD.
- `configure-audio`: **writes** the single documented volatile `0x17/0x02`
  setup request and records its correlated reply. It uses the existing
  serialized command/response owner, queues intervening LED changes through
  the normal latest-state mechanism, and does not write firmware, association
  state, or audio-output payloads. An acknowledgement is not headphone playback.
- `stop-probe`: closes only this diagnostic pipe, not the controller or mapper.

Automatic profile idle/absolute disconnect is suppressed for the Bluetooth Pro
for this opt-in lab process lifetime, including between individual probes.
Manual Stop, disconnect and application exit still work. Saved profile settings
are not changed; normal launches retain their existing timeout behavior.

All pipe/file/radio work runs on a separate worker. The report-path hook updates
report timing counters without allocation. An idle probe generates no extra controller
traffic. A three-second probe deadline does not discard actual Windows operation
ownership: late operations drain before any successor probe or controller-service
disposal. No timed-out request is automatically replayed. A noncooperative radio
operation may leave the probe unavailable while the normal bounded teardown
quarantines its resources; it must not cause use-after-dispose.

Exit 0 means a valid non-error diagnostic reply, **not audio support**; 1 means
an error/failure and 2 invalid usage. Oversized/unrecognized raw pipe requests
are rejected without radio operations and may have their connection closed.

## Validation

Unit tests use fake GATT access and real local pipes, not physical controllers.
They cover repeated probes while reports advance, serialized setup/LED commands,
unrelated replies, exact setup bytes, bounded requests, cancellation/late-write
drain, and zero-allocation report observation. Physical acceptance additionally
requires the same controller generation and advancing reports before/after
repeated live queries. This feature is probing infrastructure; Bluetooth headphone
playback remains unimplemented pending output-framing/codec/delivery evidence.

## b92 finite delivery experiments (not production audio)

`headset-observe` temporarily subscribes to the dedicated headset report for
250 ms. It returns jack-state/length histograms and a count of exact published
idle-pattern matches, never buttons, motion, microphone payloads or recordings.
It disables that subscription and reasserts the already-owned common05 Notify
state afterwards. This may briefly interrupt ordinary input: do not use while
playing. Failed cleanup fences further radio probes until a reconnect, while
the owning lease retains all outstanding operations. Nintendo CCCD reads may
fail even when writes work; the exact lease's successful Notify writes are
tracked for this opt-in probe and read failures are reported, not interpreted
as disabled notifications. A successful but conflicting CCCD read is rejected.

The following fixed commands each require an acknowledged `configure-audio`
in the same controller generation and an explicit third argument identifying
the user-confirmed Realtek Line In:

```text
Switch2BluetoothLabClient.exe <session.json> tone-opus5 <Realtek Line In ID>
```

Tone choices are `tone-opus5`, `tone-opus20`, `tone-rumble-opus5`,
`tone-rumble-opus20`, and their `tone-length-opus5/20` and
`tone-rumble-length-opus5/20` variants. They are **hypotheses**, not established
Nintendo output formats. Opus is motivated by the published input idle pattern;
the optional zero rumble prefix and single length byte are candidate framing
based on adjacent documented report structures, not confirmed output captures.
The legacy b93 server accepts only these fixed tones, with no arbitrary payload,
gain, frequency, file or UUID. The new packet-plan bridge is described below;
its external generator owns waveform review and encoding.

Each experiment contains 500 ms of synthetic 440 Hz left / 660 Hz right,
source peak 0.005 (-46 dBFS), 10 ms fades and 100 ms of trailing silence. CBR
Opus packets are pre-encoded, stay within one negotiated ATT value and are
never split. One real Windows write is outstanding at a time; stale packets
are discarded, not burst-replayed. b92 reuses the existing high-resolution
waitable timer after b91 exposed coarse `Task.Delay` pacing on this machine.
Cancellation drains an admitted write before releasing its service.

The client captures only the explicit active, unmuted `Line In (Realtek(R)
Audio)` endpoint in float/48k/stereo, with a bounded RAM buffer. It reports
100 ms RMS/peak, clipping and 440/660 Hz amplitudes, then discards samples.
No default endpoint, volume setting or audio file is used. Client exit 0 still
means only diagnostic completion. A successful GATT write, silence-pattern
match, or offline codec round-trip **does not confirm physical playback**.

If the app is elevated, launch the local client with the same user/elevation;
do not weaken the current-user-only pipe to bypass an access-denied response.

## b93: reuse the connection and compare notification modes

`audio-state` sends the exact documented `18/01` query through the same command
owner and returns its correlated reply. A successful query does not grant the
setup gate and the eight response data bytes have no invented volume/mute meaning.

Each `tone-*` command also has an `active-*` form, for example `active-opus5`
and `active-rumble-length-opus20`. These stream the same finite hypothesis while
the headset notification is active, then restore both notification states.
They require the same setup acknowledgement and explicit Line-In argument.
Ordinary input can pause for the duration of this lab-only mode: it is not
ready for use while gaming. Report generation/counters and cleanup status must
be checked before continuing with the next candidate.

For quick experiments, leave DS4Windows and VIIPER running and send consecutive
commands to the existing descriptor. Do not reconfigure/reconnect for each
candidate, replay a failed write, increase gain after a silent result, or rerun
already measured combinations without a specific new question. The companion
[`Switch2BluetoothTraceProbe`](../Switch2BluetoothTraceProbe/README.md) can
observe transport/header metadata without replacing this app build. Neither
utility provides arbitrary commands or a production Bluetooth audio stream.

## Packet-plan bridge: formats live outside DS4Windows

The next bridge revision advertises `PacketPlanProtocol: 1`. b93 does not have
it. Do not send plans to an older app or replace/restart that app without the
user's approval. Changing a packet format is **not** a reason to rebuild
DS4Windows once this bridge is loaded.

The external client generates and encodes test signals, or accepts a reviewed
JSON packet plan generated by another offline tool. DS4Windows knows nothing
about the plan's codec or framing: its `run-plan` request carries a four-byte
little-endian JSON length followed by the JSON. The existing input lease owns
all writes to the **same dedicated headphone UUID**. There is no new GATT
descriptor, second connection owner, arbitrary command/UUID selection, DLL
injection, installed-driver change or production audio endpoint.

```text
Switch2BluetoothLabClient.exe --create-plan t:opus:1:20:20:raw 2 new-plan.json
Switch2BluetoothLabClient.exe session.json run-plan "<explicit Line In ID>" new-plan.json
```

`--create-plan` is offline, creates a new file without overwriting, and never
opens a controller or capture endpoint. The session generation must match.
For a built-in generator choice, the client can generate and submit directly:

```text
Switch2BluetoothLabClient.exe session.json a:opus:1:20:20:raw "<explicit Line In ID>"
```

Generator syntax is `t|a:opus|pcm:channels:milliseconds:kbps:framing`:

- `t` retains ordinary input; `a` temporarily enables headset notifications.
- Channels: 1 or 2. Frame durations: 2.5, 5, 10 or 20 ms. PCM is limited to
  2.5/5 ms by the generator's single-write budget.
- Opus rates: 20, 40, 80 or 160 kbps, forced fullband and explicit channel
  count. PCM uses `0`. Framing: `raw`, `len`, `rum` or `rumlen`.
- Impossible sizes are rejected, including a one-byte length for >255 bytes.
  Signals remain 500 ms with 100 ms quiet tail, 48 kHz, peak 0.005,
  440 Hz left/mono and 660 Hz right, with fades. These are hypotheses only.

This menu is just an external generator convenience. **It does not constrain
the bridge to those codecs, prefixes or rates.** Any reviewed external encoder
can create plans containing `Id`, `Generation`, `HeadsetNotifications` and
`Packets` (`OffsetMicroseconds`, base64 `Payload`). New packing, counters,
framing and codecs require no DS4Windows format decoder changes. Explicit
fragment plans are preserved; the bridge does not invent fragmentation itself.

Limits are transport/lifetime limits: <=1 MiB request, <=1024 packets, <=256 KiB
total bytes, <=509 bytes per write and the actual negotiated MTU, schedule from
zero through <1 second, monotonic offsets, <=4 fragments per deadline. Invalid
plans, stale generations and missing setup fail before radio writes. A missed
next-frame deadline aborts rather than replaying stale frames. An admitted
Windows write drains before any successor or headset restore. A SHA-256 plan
fingerprint identifies exactly what was sent.

Raw encoded bytes cannot prove a waveform's loudness or silence tail. Review
and, where a decoder exists, decode each external candidate offline before
hardware use; use only the user-confirmed controller-to-Line-In test wiring.
Line-In measurements and actual controller continuity remain the acceptance
tests. Plan completion, fingerprints and accepted writes are not playback.

The revised headset parser recognizes both observed audio lengths (0 and 50)
and separately decodes documented buttons/sticks without reading audio bytes
as motion. `ControlsForwarded: false` explicitly records that these alternate
reports are **not yet connected to the runtime mapper**. The characteristic
admission and 63-byte common05 queue are not weakened or bypassed; input/audio
coexistence remains unfinished and must be implemented before production use.
