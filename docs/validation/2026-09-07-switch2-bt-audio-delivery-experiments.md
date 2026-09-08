# Switch 2 Pro Bluetooth headphone delivery experiments

Status: **not working/verified yet**. This is a research checkpoint, not a
production headphone-audio implementation or release candidate.

## Live b90 connection evidence

With DS4Windows left open, repeated requests used the same process, input lease
and transport generation. Input reports advanced from 18,542 to 18,580 during
inventory/header queries. The Pro exposed both dedicated headset UUIDs, with
ATT PDU 512 and maximum single ATT value 509 bytes. Direct headset reads
returned ProtocolError. The exact observed volatile `17/02` setup request
received `1701010210780000`; reports continued from 20,273 to 20,295. This
acknowledges setup only; it does not establish codec/framing or playback.

The non-elevated client was denied by the elevated app's local pipe. Running
the client with the same user's elevation worked; the pipe ACL was not relaxed.

## Finite b91 Line-In tests

The user-confirmed controller headphone-out to PC Realtek Line-In connection
was used. The exact active nondefault float/48k/stereo capture endpoint was
re-enumerated before testing; its volume was not changed. No microphone/default
capture, WAV file or persistent audio recording was used. Only numeric metrics
remain in local lab evidence.

Test source: 500 ms, 440 Hz left / 660 Hz right, linear peak 0.005, 10 ms fades,
100 ms trailing quiet PCM, encoded using the existing Concentus 2.2.2 library.
All packet formats below are **unconfirmed hypotheses**. They were sent only
to the dedicated headphone characteristic, not the rumble or command UUID.

| Hypothesis | Writes accepted | Stale frames omitted | Line-In tone delivered |
| --- | ---: | ---: | --- |
| 5 ms stereo Opus with 32 zero-prefix bytes | 66 | 54 | Not detected |
| 20 ms stereo raw Opus | 30 | 0 | Not detected |
| 20 ms stereo Opus with 32 zero-prefix bytes | 30 | 0 | Not detected |

The expected left/right tones stayed at background-level amplitudes, around
1e-5 or below, rather than a source-correlated signal. No ADC clipping was
observed. These negatives do not rule out Opus: setup, framing, jack gating and
cadence must be established independently. The 5 ms experiment was additionally
confounded by coarse timer scheduling. The sender discarded late frames instead
of growing a backlog or burst-replaying them; this is not a valid 200 Hz audio
delivery result. All three tests retained the same controller generation with
advancing input counters. They did not increase the preceding measured maximum
input-report gap (30.61 ms for the first two; 44.70 ms before/after the third).

b91's headset notification probe refused to change CCCDs because readback did
not establish its expected state. The initial error lacked individual status
details; b92 now returns those statuses and tracks the exact lease's successful
notification writes. A rejected read's default enum value is never treated as
proof of disabled notifications. Normal production launch behavior is unchanged.

## b92 corrections and verification

- Reuses `ViiperHighResolutionWaiter` for the finite lab sender; no global timer
  resolution or controller input scheduling change.
- Adds explicit, fixed one-byte length-prefix hypotheses based on adjacent
  input framing. No arbitrary payload/UUID/firmware command API.
- Records maximum report gap without allocating on the report path.
- Preserves same-generation setup gating, actual late-write lifetime, bounded
  PCM and frame counts, single-ATT limits, input/headset separation and CCCD
  cleanup. No codec success flag is inferred from diagnostic completion.
- Focused Bluetooth/waiter suite: **417 passed, zero failed**.
- Full Release x64 suite: **3,917 passed, zero failed, 11 opt-in skips**.
  b91's preceding suite passed 3,913 with all 146 allocation-named tests passing.
  The skips concern separate opt-in audio/Go-peer hardware scenarios, not
  waived allocation or lifetime failures.
- Tests decode each synthetic candidate offline to check quiet peaks, channel
  frequencies, duration and tail; reject oversized ATT frames; ensure pre-cancel
  never writes and cancellation retains the real admitted write; ensure failed
  setup cannot admit a tone and failed queries leave status available.

The user's explicitly authorized portable replacements stopped only the exact
old DS4Windows and VIIPER processes, copied saved lab settings into new runtime
folders and launched hash-pinned matched packages. Program Files, drivers,
bonds, startup tasks, Windows defaults and endpoint volume settings were not
changed. b91 reconnected successfully and supplied the results above. b92 was
launched at 18:17 local; at this checkpoint the controller has not reconnected.
A two-second targeted wake-only advertisement started/stopped without opening
a second GATT service but did not establish reconnection.

b92 package hashes:

| File | SHA-256 |
| --- | --- |
| DS4Windows.dll | `BB130F7F910C4717535C11924A4AF16FE729AF0B08A5C13670BAB601C30BA5F7` |
| DS4Windows.exe | `EA330080C05DFEFE53515A34FDC1D21E3A70E564763206287F506FA04F27EF4E` |
| viiper.exe (unchanged) | `7B6B00CF3AC205549AF80692E45BD7785D3B8BC558A92D4FF5A1A61060592B78` |

Next hardware step: with b92's existing session active, obtain actual headset
jack-state metadata, verify restore/input continuity, and remeasure precise
pacing and output candidates using Line In. Do not call any of this working
Bluetooth headphone support until the physical source/channel/stop tests pass.

## b92 hardware follow-up

After the user woke the Pro, b92 connected on the same remembered association.
The bounded headset observation succeeded: 19 notifications, all 112 bytes,
all with the expected 50-byte audio region. Jack states were 0x05 (9) and 0x0D
(10): documented headphone-present states. Ten frames matched the published
Opus-compatible idle pattern exactly. This is actual-controller idle evidence,
not live microphone codec identification or headphone-output proof.

The headset CCCD read returned ATT error **0x02 (Read Not Permitted)**; the
ordinary-input CCCD read succeeded as Notify, matching the lease's successful
write. b91's mandatory successful-read assumption had blocked the observation.
Both notification restore writes succeeded. Ordinary reports resumed in the
same generation; the measured maximum gap rose from 30.57 to 300.31 ms during
the deliberate 250 ms headset observation. This reproduces the donor's mode
switch on Windows: headset notifications replace common05 input, so a future
production audio mode must present the embedded input correctly instead of
leaving buttons stale. b93 omits the undefined CCCD enum on a failed read.

With high-resolution pacing, the 5 ms stereo Opus + zero-rumble + one-byte length
hypothesis sent **120/120 frames, zero omitted**, over about 596 ms. Measured
write starts were approximately 5 ms apart. Input generation remained active.
This proves the earlier sender pacing problem was correctable; the Line-In
result remains a separate acceptance gate. Next, test output while headset
notifications are active: the newly evidenced report-mode switch may also gate
playback. This is a hypothesis to measure, not an established root cause.

## b93: completed finite matrix, no-restart transport observation

The Pro reconnected at 18:29 local after the earlier authorized b93 replacement.
All subsequent tests below retained DS4Windows PID 23824, VIIPER PID 30276 and
transport generation 2. The user then requested **no more app restarts**; no
replacement, reconnect or bond/radio change was used for these experiments.

The exact `18/01` query returned `1801010110780000000040F000006000`, matching
the documented example. Its unknown payload was not treated as proven volume,
mute or codec state. Setup remained acknowledged from the earlier `17/02`.

All eight finite hypotheses (5/20 ms, with/without a 32-zero-byte prefix,
with/without a one-byte length) were measured with headset notifications
active. Every 5 ms candidate sent 120/120 packets; every 20 ms candidate sent
30/30; none dropped packets. Both notification restore writes succeeded.
All remained negative at Line In. The seven-candidate follow-up ran as a
single short batch, stopping on error, ambiguous restore or a signal candidate.

The remaining normal-input candidates were also batched, skipping already valid
negative measurements. These are the fresh b93 results, not extrapolated from
the active-headset mode:

| Normal-input hypothesis | Sent / dropped | Largest expected-channel tone amplitude | Peak Line In |
| --- | ---: | ---: | ---: |
| Raw Opus, 5 ms | 120 / 0 | 0.00001446 | 0.0008161 |
| Length + Opus, 5 ms | 120 / 0 | 0.00000844 | 0.0007145 |
| Length + Opus, 20 ms | 30 / 0 | 0.00001043 | 0.0007329 |
| Zero prefix + Opus, 5 ms | 120 / 0 | 0.00001376 | 0.0008267 |
| Zero prefix + length + Opus, 20 ms | 30 / 0 | 0.00001215 | 0.0007055 |

No candidate produced a source-correlated tone, broad signal increase or ADC
clipping. Reports advanced through 68,132 in the same controller generation.
This rejects only these tested combinations, not Opus as a codec family and
not Bluetooth headphone capability. It does not verify other codec, routing,
gain, setup, packing, channel or packet-duration combinations.

### New evidence from real-time ETW (no raw trace recording)

A separate bounded observer used the installed BTHPORT event-402 manifest and
Microsoft's real-time ETW mechanism without opening a second Bluetooth service.
It bound to the exact state query sent through the existing app, discarded raw
event buffers, and retained only counts, timing and headset-header metadata.
All diagnostic sessions stopped; no ETL/PCM recording or persistent trace was
created. Diagnostic helper builds did not replace either running app.

- Corrected a diagnostic handle assumption: observed ATT value handles are
  **20 command, 26 response, 44 headphone output, 46 headset input**. WinRT's
  **19/25/43/45** are declaration handles. UUID-based application writes already
  selected the right characteristic; there was no discovered wrong-target fix.
- Raw 5 ms: **120** actual 50-byte ATT Write Commands to value handle 44,
  average HCI-observed spacing **5.0087 ms**, range **4.1799–6.0599 ms**.
  This is host/transport submission, not controller DAC or game latency.
- During that test, all **121** headset notifications were 112 bytes:
  35 had jack 05/audio length 50; 34 had jack 0D/length 50; **52 had jack
  05/audio length 0**. Motion length was zero in these observations.
- Raw 20 ms: **30** actual 200-byte writes; 44 headset notifications, split
  22/22 between jack 05/0D, all audio length 50. Neither test lost ETW events.
- The application's strict `ValidHeaders` counter only recognizes audio
  length 50. The extra reports under 5 ms writes are zero-audio-length reports,
  **not proven malformed reports or explicit playback rejection codes**.
- Enabling headset notifications still deliberately interrupts ordinary input
  during the finite test. Restore resumes input; the historical maximum gap
  of about 765 ms includes these deliberate lab intervals. A production audio
  mode still needs correct embedded-input handling before gameplay use.

The trace decoder's first assumptions were rejected by live evidence before
interpreting headset data: this Windows build's BIP type numbers are not direct
H4 numbers, and declaration/value handles differ. The corrected parser has a
truncation sweep, binding/filter/fragment/ambiguity self-tests and a clean build.

### Software checkpoint and remaining work

b93 focused suite: **422 passed, zero failed**. Full Release x64 suite:
**3,922 passed, zero failed, 11 opt-in skips**, including **all 146
allocation-named tests passing**. No allocation/lifetime failure was waived.

b93 package hashes:

| File | SHA-256 |
| --- | --- |
| DS4Windows.dll | `628D11CE636A05D30E09CA704031A921CC1A004C30B45D31F585CF64B8E95DC7` |
| DS4Windows.exe | `0C1E2349C549B03EE51D9399EEBB802E0686C9F329FE1D8812095F09630EFBD4` |
| viiper.exe (unchanged) | `7B6B00CF3AC205549AF80692E45BD7785D3B8BC558A92D4FF5A1A61060592B78` |

Bluetooth headphone playback is **still unimplemented/unverified**. The current
live app exposes a finite whitelist, not arbitrary framing/codec/command writes.
The measured candidates are exhausted; repeating them without a new variable
is not progress. Preserve the connection while auditing new setup/format
evidence. Do not silently restart the app to expand its command vocabulary,
open a competing Bluetooth owner, or turn these negatives into a claim that
the hardware cannot support Bluetooth audio.

## Packet-plan bridge checkpoint: external formats, unchanged live session

The user correctly rejected rebuilding/restarting DS4Windows for each new
codec or packet hypothesis. The new lab bridge accepts a bounded, versioned
packet plan from an external generator. Encoding, framing, counters and packet
offsets are data supplied by that generator, not new cases in DS4Windows.
Legacy fixed commands remain compatible. A new format within these transport
bounds does not require an application rebuild after the bridge is loaded.

This is **not a new controller GATT descriptor**. The existing input lease
writes only the same dedicated headphone-output characteristic. The local pipe
descriptor advertises `PacketPlanProtocol: 1`; the external client rejects b93
before radio/capture access because that running app lacks the bridge. No
arbitrary target UUID, setup command, firmware operation, competing service
owner or injected code is introduced. Different controller setup requirements
would still need separate source review; this is not an unrestricted command
executor or a claim that the protocol has been solved.

Plans are same-generation/setup gated, at most 1 MiB JSON, 1024 packets,
256 KiB payload and a schedule below one second. Writes respect both 509 bytes
and negotiated MTU. At most four explicit fragments share a deadline. The
sender preserves opaque bytes, aborts a missed next-frame deadline rather
than burst-replaying, and retains each real Windows write through completion.
Cancellation is reported even when it arrives during the final write. Invalid
envelopes cannot reach GATT, and a later status query still works.

Offline checks demonstrate two distinct opaque formats using one setup and
probe lifetime. The standalone generator also created a 30-packet mono Opus
20 ms / 20 kbps plan without hardware access. Its 50-byte packets and `F8` TOC
match the observed idle packet's shape only: they are **not evidence of the
headphone decoder or audible playback**. Other generator tests decode mono,
stereo and PCM candidates to verify quiet peaks, channels, fades and silence
tail. Raw external plans must undergo equivalent waveform review; arbitrary
encoded bytes cannot prove quietness at the transport layer.

Headset-header parsing now admits the observed audio lengths 0 and 50. A
separate allocation-free parser decodes documented headset buttons/sticks;
the lab observation explicitly reports `ControlsForwarded: false`. The
production common05 admission, queue and mapper are unchanged. Headset input
forwarding and packed-motion handling remain unfinished, so notification-mode
experiments are still lab-only and not appropriate during gameplay.

Final checks for this source checkpoint:

- `b94-bridge-hardening.trx`: **43 passed, zero failed**.
- `b94-bridge-final-full.trx`: **3,944 passed, zero failed, 11 opt-in skips**.
- All **147 allocation-named tests passed**; no allocation or lifetime failure
  was waived. Skips remain three opt-in process-loopback tests and eight
  Go-peer interop cases, not Bluetooth playback verification.
- External client: **zero build warnings, zero errors**.
- Packet-plan tests retain the admitted operation during stop, reject stale
  generations and malformed requests, and preserve exact payloads on the same
  service. They use fakes and do not establish hardware behavior.

DS4Windows PID 23824 (18:27:57 local) and VIIPER PID 30276 (18:27:56 local)
were still running unchanged at this checkpoint. No app replacement, radio
probe, Line-In recording, driver/bond/profile change or Program Files write
was performed in this bridge work. No new portable runtime package is staged
yet. Loading the bridge will require an initial user-approved app replacement;
the current no-restart instruction remains in force. Bluetooth AUX playback,
stereo separation, stopping and simultaneous low-latency input are still open
physical acceptance gates, not inferred successes from the software tests.

## b94 bridge loaded with approval; 28 further measured combinations

The user explicitly approved the restart. At 19:30 local, only portable
DS4Windows was closed and its managed DLL replaced. The old DLL and launcher
were backed up under Desktop lab `tools/b93-before-packet-bridge`. The existing
runtime folder was reused to retain its exact, pinned broker path. VIIPER
**PID 30276, started 18:27:56**, was not stopped or replaced. Installed software,
drivers, bonds, Windows sound settings and saved profiles were not changed.

The new app is **PID 32636**, started **19:30:06**, managed version **5.0.4.94**,
DLL SHA-256 `A9A373A61A3FE6E7C0E0B06E8466C79170698CF668D4E3D94AF748AC4442AF72`.
The existing native apphost/dependency files were retained; the non-app NuGet
dependency sets matched. The release marker and launcher hash were updated.
The early startup line still printed the old marker before that update; the
live pipe's `PacketPlanProtocol: 1` independently proves the new bridge loaded.
`Start-Portable.ps1 -VerifyOnly` passes without launching anything.

After the user woke the Pro, it reconnected at 19:30:30 on transport generation
2, with Xbox 360 virtual output. The fresh exact `17/02` setup was acknowledged
at 19:31:50. All experiments below retained this process, generation and broker.

### First new codec/cadence trials

| Candidate | Completed writes | Line-In result |
| --- | ---: | --- |
| Mono Opus, 20 ms / 20 kbps, raw, common input | 30 | No source-correlated tone |
| Same mono Opus, headset notifications | 30 | No source-correlated tone |
| Mono Opus + one-byte length, headset notifications | 30 | No source-correlated tone |
| Stereo PCM16LE, 2.5 ms, 480 bytes, common input | 240 | No source-correlated tone |
| Same high-rate PCM with headset notifications | Inconclusive | Probe deadline; not counted as a delivered negative |

The first four capture peaks were 0.000664–0.000764; expected-channel tone
amplitudes stayed near background, below 0.000015. No ADC clipping occurred.
The fifth test hit the three-second probe deadline and stopped the batch. The
bridge retained the underlying operation; it was not replayed or abandoned.
A subsequent actual status query succeeded in the same generation with 12,927
reports and a 12.48 ms current report age. An explicit inventory query also
succeeded, showing negotiated MTU 512 / 509-byte single-write capacity. This
proves the lab worker drained and its cleanup fence was not set; it does not
identify which underlying operation consumed the deadline. Historical maximum
input gap rose to **2,505.34 ms** during that deliberate lab-only headset trial.
Do not describe this test as simultaneous low-latency input/audio success.

### External framing trials without rebuilding or restarting DS4Windows

The external generator was extended after b94 was already running. Six
additional envelope hypotheses were tested for **stereo 5 ms / 80 kbps Opus**
and **mono 20 ms / 20 kbps Opus**, each with common input and headset
notifications: 24 distinct combinations in two short batches.

- Leading zero byte (`id0`).
- Incrementing byte counter (`seq8`).
- Zero byte then counter (`id0-seq8`).
- Little-endian 16-bit encoded length (`len16le`).
- Zero byte then 8-bit encoded length (`id0-len8`).
- Counter then 8-bit encoded length (`seq8-len8`).

These are explicitly hypotheses motivated by adjacent report-ID/counter/length
conventions, not captured headphone framing. Offline tests verify the exact
prefixes, unchanged payloads/schedules, no source-buffer aliasing, and length
overflow rejection. The quiet waveform and silence-tail checks still apply.
Only the external client changed; **the app DLL and live connection did not**.

All 24 completed: **1,800 writes total**, 120 per stereo trial or 30 per mono
trial. No source-correlated Line-In signal was detected; largest ADC peak was
below **0.000855**, with no clipping. All headset cleanup results were Success,
and ordinary input resumed before the next trial. Final recorded status had
47,215 reports and 1.10 ms current report age in the same generation. Active
headset reports still have `ControlsForwarded: false`: normal input pauses
during those finite trials. This remains a production coexistence defect.

Desktop numeric evidence (no raw audio recordings):

- `tools/b94-batch-20260907-193305-273.jsonl`: first codec/cadence batch.
- `tools/b94-batch-20260907-193850-723.jsonl`: first framing batch.
- `tools/b94-batch-20260907-194153-234.jsonl`: remaining framing batch.
- Corresponding `b94-audio-*.jsonl` files contain plan fingerprints, write-start
  timing, exact setup response, cleanup metadata and Line-In block metrics.

The external framer/codec focused suite passed **34 tests, zero failures**.
The full Release x64 run `b94-external-framer-full.trx` passed **3,951 tests,
zero failures, 11 opt-in skips**, including all **147 allocation-named tests**.
Bluetooth headphone playback is **not fixed**. These negatives reject the
measured combinations only. The proper output setup/enable/stop semantics,
framing and codec remain unresolved; the `18/01` bytes are not proven volume
or mute values, and the documented-but-unknown `18/03` was not sent. No new
restart is authorized by these completed experiments.
