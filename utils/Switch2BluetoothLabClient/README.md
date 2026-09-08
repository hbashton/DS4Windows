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

Audio trials additionally require `CaptureValid: true`. The client waits for
at least 16,800 actual Line-In frames before submitting a plan, then measures
the requested 350 ms or 2,000 ms of samples after the app response. Wall-clock
waiting alone is not capture readiness. Missing baseline/tail, early stop,
capture errors, nonfinite/unaligned samples or the bounded eight-second buffer
overflow invalidate the trial. Read-only mute/master/channel levels must be
usable and unchanged before/after. The final partial measurement window is
included. Only this explicit Realtek Line In is used; no endpoint or volume
settings are changed and no PCM is saved. Invalid capture returns exit 1 and
must not be counted as a playback-negative result.

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
An optional fifth argument applies an external framing hypothesis: `id0`,
`seq8`, `id0-seq8`, `len16le`, `id0-len8`, or `seq8-len8`. These prepend an
explicit zero report-ID candidate, frame counter and/or encoded-byte length;
they are **not confirmed Nintendo envelopes**. Lengths and MTU are checked,
and the already-reviewed audio payload and schedule remain byte-for-byte
unchanged. `PlanFramer.cs` is compiled into this tool and its tests, not the
DS4Windows app. Adding these hypotheses therefore required no live app update.

Additional offline hypotheses:

- `len16le-pad64`, `len16le-pad112`, etc.: fixed-size zero-padded envelopes,
  preserving an explicit encoded length. Supported sizes are 64, 112, 128,
  256, 480 and 509; too-small envelopes fail. Padding also supports `id0-len8`,
  `seq8-len8`, `pro-len8` and `proseq-len8`. Larger sizes are not assumed to
  be sustainable simply because ATT accepts them.
- `pro-raw` / `pro-len8`: the adjacent Pro report's leading zero plus two
  zero 16-byte groups before raw/length-prefixed audio. This corrects the
  32-vs-33-byte distinction in that *hypothesis*, not a proven output defect.
- `proseq-raw` / `proseq-len8`: use the existing production rumble encoder
  for counter-bearing, zero-force groups. Reusing that envelope on the
  headphone characteristic is still unproven. No nonzero rumble is generated.
- `pcm-pair5`: groups two 480-byte stereo PCM pieces at each 5 ms deadline,
  preserving every sample. Restricted to the reviewed ordinary-input
  `t:pcm:2:2.5:0:raw` source. Hardware tracing showed this overloaded the
  current connection; do not repeat it as a production transport strategy.

For a specifically traced trial, append `--capture-tail-ms 2000` to a tone or
`run-plan` invocation. The default is 350 ms; those are the only admitted tail
lengths. This extends RAM-only Line-In observation after the app's response,
not the audio duration or gain, and does not change the live application.

```text
Switch2BluetoothLabClient.exe --create-plan t:opus:1:20:20:raw 2 new-framed-plan.json id0-seq8
```

The separately tested dual-mono factory reuses the same reviewed stereo PCM
source, deinterleaves it, then runs independent mono encoders. Both streams
have 50-byte CBR frames: 5 ms / 80 kbps per side, or 20 ms / 20 kbps per side.
The observed mono-compatible input region motivates this hypothesis; it is
not evidence that headphone output uses dual-mono Opus.

```text
Switch2BluetoothLabClient.exe --create-dual-mono-plan t 5 2 new-dual-plan.json len8
```

Arguments are mode `t`/`a`, frame milliseconds `5`/`20`, generation, new path,
and packing: `raw` (`L R`), `len8` (`50 L 50 R`), `lengths` (`50 50 L R`),
`id0-len8` (`0 50 L 50 R`), or `self-delimited`. The latter uses the real
[RFC 6716 Appendix B](https://www.rfc-editor.org/rfc/rfc6716.html#appendix-B)
multistream syntax: `TOC_L 49 L[1..49] R[0..49]` for these mono code-0 frames,
not an outer packet-length prefix. Tests feed the complete 101-byte packet to
an actual two-stream Opus multistream decoder and compare both channels with
independent decoding. Standard Opus framing does not establish its adoption by
Nintendo. Tests independently decode both sides, verify
440/660 Hz separation, quiet peaks, exact duration and trailing silence. This
also creates files offline without overwriting or touching either running app.

One additional, fixed adjacent-protocol hypothesis is available offline:

```text
Switch2BluetoothLabClient.exe --create-hwopus-plan t 2 new-hwopus-plan.json
```

Modes `t`/`a` wrap the unchanged 20 ms stereo 80 kbps source in the
[libnx hwopus IPC header](https://github.com/switchbrew/libnx/blob/master/nx/include/switch/services/hwopus.h):
big-endian 32-bit encoded length, big-endian 32-bit actual final range, then
the 200-byte Opus packet. This is also an
[upstream Opus test-stream envelope](https://github.com/xiph/opus/blob/main/src/opus_demo.c),
not a discovered Bluetooth controller format. Tests cross-check the range
against the real source encoder and decoder and preserve the same quiet source.
No additional app build is needed for either external framing hypothesis.

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

## Receiver-plan bridge: bounded setup and routing research

This separate capability requires `ReceiverPlanProtocol: 1` in the live session
descriptor. A packet-plan-only build is not sufficient. The client rejects
missing/unsupported capabilities and stale generations before sending a pipe
request; it never starts, replaces, or reconnects DS4Windows itself.

```text
Switch2BluetoothLabClient.exe session.json run-receiver-plan "<explicit Line In ID>" receiver-plan.json --capture-tail-ms 2000
```

Author and review the JSON offline. Fields are `Id`, `Generation`,
`HeadsetNotifications`, `Commands` (an array of base64-encoded full request
byte strings), optional `Audio` (an existing packet-plan object), and optional
`AllowExperimentalParameters` (defaults to `false`). There is no matrix runner
or automatic retry. Pick one justified intervention at a time.

The shared parser enforces a 1 MiB JSON limit, 1–8 commands, a bounded ID and
the exact active generation. The command vocabulary is closed:

- `0C/02` and `0C/04`: only the exact four-byte `2F 00 00 00` feature mask;
  selection must precede enable within the same plan.
- `17/02`: by default only the observed `80 BB 00 00 02 F0 00` payload.
  With the explicit experimental gate, the bounded alternatives are channel
  bytes 1/2 and final 16-bit values 120/240/480/960. Their interpretation as
  48 kHz, channels and frame samples is a hypothesis, not established semantics.
- `18/01`: only the exact header-only query. Its response bytes are not assigned
  invented volume/mute meanings.
- `18/03`: by default only the observed one-byte `07` payload. Values 0–7 are
  admitted only with `AllowExperimentalParameters: true`; they are research
  hypotheses, not documented valid routing flags. State persistence is unknown.

`Audio`, when present, must use the same generation and set its own
`HeadsetNotifications` to `false`: the enclosing receiver plan owns that window.
The app additionally requires an acknowledged `17/02` in this plan or an earlier
operation in the same generation. Failed commands prevent streaming. Audio
retains the existing packet-plan size/MTU/schedule bounds and fixed headphone
UUID. The envelope is `run-receiver-plan\n`, four-byte little-endian JSON length,
then the JSON. The existing DS4Windows command/input lease remains the only
GATT owner; this does not expose arbitrary commands or targets.

**Every receiver plan requires the explicit Line In, including command-only
plans:** a routing command might activate previously primed output. Baseline,
level checks, RAM-only capture and actual post-response sample requirements are
unchanged. The tail is 350 ms by default, or exactly 2,000 ms when requested.
The bounded tail is also collected after a submitted request fails or times out.
Do not treat a timeout as proof that the controller operation has drained.

Exit 1 includes root/nested errors, missing or false command acknowledgement,
failed reported GATT/restore status, a fenced audio lease, incomplete optional
audio packet count, or missing/false `CaptureValid`. `SetupAcknowledged: false`
alone is legitimate for a query-only plan. Exit 0 means only successful bounded
diagnostic completion with valid capture—not audible output or production audio
support. Stop on a signal candidate, invalid capture, or cleanup/continuity error;
do not automatically sweep the experimental parameter space.

The revised headset parser recognizes both observed audio lengths (0 and 50)
and separately decodes documented buttons/sticks without reading audio bytes
as motion. Its dedicated controls-only value maps all 21 documented button bits
to canonical semantics while retaining the distinct raw 24-bit layout, 8-bit
counter and 12-bit sticks. It no longer labels a 112-byte headset observation
as Pro09 or carries an unusable offset-66 motion region into a 63-byte body.
This source correction does not itself enable controller publication.
`ControlsForwarded: false` explicitly records that these alternate
reports are **not yet connected to the runtime mapper**. The characteristic
admission and 63-byte common05 queue are not weakened or bypassed; input/audio
coexistence remains unfinished and must be implemented before production use.
