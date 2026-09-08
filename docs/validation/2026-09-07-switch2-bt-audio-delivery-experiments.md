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

## No-restart follow-up: downstream backlog established, 44 new compressed trials

The user reinforced that neither DS4Windows nor VIIPER may be restarted or
replaced. All work below is in **external utilities, tests and documentation**.
No application source or loaded DLL was changed. DS4Windows remains PID 32636
(19:30:06), VIIPER PID 30276 (18:27:56), controller generation 2. The existing
packet-plan bridge carried every new hypothesis without a runtime replacement.

### Correcting incomplete transport observation

An external `pcm-pair5` plan preserves the reviewed 48 kHz stereo PCM sample
sequence but groups two 480-byte pieces on each 5 ms deadline. Its fingerprint
is `3AEED9C9299EBBD2FABC2D184468F4B1E9D10025F6A92933E3D80719F5877F41`.
The first trace at 19:55 reported zero headphone writes and 337 ignored
fragments/shapes although the app reported 240 submissions. That was an
observer limitation, **not proof of absent delivery or valid wire pacing**.

The external observer now reassembles bounded ATT traffic on only its selected
link. Two independent 512-byte buffers separate the observed BIP directions.
It validates actual/declared lengths, CID, PB0/2 first fragments and PB1
continuations, rejects broadcast-shaped ACL and ambiguous peers, and scrubs
retained payload after completion/rejection/stop. Its self-test covers two-
and three-fragment 480/509-byte values, interleaving, timestamps, orphan and
oversized continuations, replacement starts, binding and zeroed buffers. It
still persists no raw HCI, keys, microphone data or audio recordings.

The 20:06 trace established actual fragmentation on this host: each 480-byte
ATT value became **251-byte PB0 + 236-byte PB1 ACL data**. The short observation
ended at 169 completed writes plus one pending fragment, with zero ETW losses.
WinRT had already reported 240 completed submissions. This exposed a downstream
queue that outlived the prior Line-In capture tail.

The client gained an explicit, bounded 2-second capture tail (default remains
350 ms), and the observer uses that tail for packet plans. At 20:08 it measured:

- **240/240** complete host ATT writes, zero pending fragments, zero ETW losses.
- **1,704.03 ms** between first observed write start and final fragment completion,
  versus approximately 600 ms for the app to submit 600 ms of PCM.
- 142,560 Line-In frames; peak 0.00087095, maximum 440/660 amplitudes below
  0.0000124, zero ADC clipping. No source-correlated AUX playback.
- Same connected generation; 152,818 input reports and 2.70 ms current report age.

This establishes that the tested raw-PCM stream overloads the current path.
Successful WinRT writes do not provide downstream backpressure. The trace is
at the **host HCI boundary**, not proof of over-air receipt, valid decoding or
DAC playback. Do not use the previous app-only timing as a real-time PCM claim,
and do not repeat this high-bandwidth format as a production strategy.

### Lower-bandwidth framing and separate-channel hypotheses

All sources retain peak 0.005, fades and a silent tail. The external framer
preserves the encoded source, generation and schedule, validates byte lengths,
and rejects overflow. A separate dual-mono factory reuses the canonical quiet
stereo PCM generator and encodes each side independently. Its tests decode both
streams and verify frequencies, channel isolation, duration, peaks and silence.

| New hypothesis family | Completed combinations | Writes | Highest Line-In peak | Correlated AUX signal |
| --- | ---: | ---: | ---: | --- |
| Adjacent Pro envelope, raw/length, zero/counter-bearing silent groups | 16 | 1,200 | 0.00086713 | Not detected |
| 16-bit encoded length, fixed 64/112/128-byte envelopes | 12 | 900 | 0.00094442 | Not detected |
| Independent 50-byte left/right Opus streams, four packings | 16 | 1,200 | 0.00091135 | Not detected |

The Pro-envelope tests include the documented leading zero before its two
16-byte groups (33 bytes, not the earlier 32-byte zero-prefix hypothesis).
Counter-bearing groups reuse the existing production rumble encoder with
**zero force**. That envelope is established on the adjacent vibration lane;
its use on the headphone lane is explicitly unproven. These were not writes
to the vibration or command characteristic.

The first two families tested stereo 5 ms/80 kbps and mono 20 ms/20 kbps in
ordinary-input and temporary-headset modes. Dual-mono used independent 50-byte
frames at 5 ms/80 kbps per side and 20 ms/20 kbps per side, with `L R`,
`50 L 50 R`, `50 50 L R`, and `0 50 L 50 R` packing in both notification modes.
No batch reported an error or signal candidate. Every headset restore succeeded;
normal reports advanced between probes. This does **not** resolve the known
temporary input pause while headset notifications are enabled.

A final independently traced 20 ms dual-mono/length trial at 20:18 observed all
**30 writes over 580.62 ms**, zero pending fragments/lost events, and 40 headset
reports split equally between jack 05/0D, all with 50 audio bytes. Its extended
Line-In capture was still negative (peak 0.00092854, tone amplitudes below
0.0000143). This particular compressed trial did not have the raw-PCM backlog;
queueing alone therefore cannot explain all playback failures. Input resumed
at 193,370 reports, current age 13.30 ms, same process/generation.

The observer now requires a packet plan's app-reported write count to match
complete observed host writes, with no pending fragments, before returning
success. Even that result remains explicitly **not playback**.

### Evidence and regression checkpoint

Desktop numeric evidence:

- `tools/b94-traced-plan-20260907-200610-254.jsonl`: first reassembled PCM trace.
- `tools/b94-traced-plan-20260907-200822-561.jsonl`: full PCM queue drain.
- `tools/b94-batch-20260907-201145-080.jsonl`: 16 Pro-envelope combinations.
- `tools/b94-batch-20260907-201336-400.jsonl`: 12 padded combinations.
- `tools/b94-batch-20260907-201704-902.jsonl`: 16 dual-mono combinations.
- `tools/b94-traced-plan-20260907-201831-527.jsonl`: compressed delivery/tail trace.

Focused tests: `b94-pro-envelope.trx` **46 passed**; `b94-dualmono.trx`
**28 passed**. Full Release x64 `b94-no-restart-probe-full.trx`:
**3,972 passed, zero failed, 11 opt-in skips**. All 147 allocation-named
tests passed. Observer self-tests pass. All owned ETW sessions stopped.

Bluetooth AUX playback remains **unsolved**. These negatives do not rule out
Opus, other codecs, or headphone capability. The output enable/routing/stop
sequence and accepted packet format remain unresolved. No semantics were
invented for `18/01`, no unknown `18/03` command was sent, and no alternate
Bluetooth owner, DLL injection, restart or installed-software change was used.
The current [primary audio investigation](https://github.com/Peterksharma/switch2mac/blob/main/research/audio-investigation.md)
still does not provide verified Bluetooth headphone playback; its motion-blob
codec exclusions must not be misapplied to the separate 50-byte audio region.
Do not rerun these completed matrices without a concrete new variable or a
measurement defect. Prefer evidence for the enable sequence/packet structure
over another arbitrary prefix expansion.

## No-restart follow-up: exact payload matching, valid capture, six new negatives

At 20:43–20:46, six additional source-motivated framing trials used the existing
packet-plan bridge. Only external utilities/tests/docs changed; neither app was
replaced or restarted. DS4Windows remained PID 32636, VIIPER PID 30276, and the
controller remained in transport generation 2. The live DS4Windows DLL remained
SHA-256 `A9A373A61A3FE6E7C0E0B06E8466C79170698CF668D4E3D94AF748AC4442AF72`.

### Stronger measurement admission

The host observer now compares every complete headphone ATT payload, in order,
against SHA-256 digests of the validated external plan. It exports counts and a
first-mismatch index, not digests or raw payloads. Exact matches, count agreement,
one bound link, no lost events and no pending fragments are all required.
Self-tests exercise fragmented exact matches, corruption/extra packets and RAM
scrubbing. This still observes the host boundary, not over-air receipt or DAC
acceptance.

The Line-In client now waits for at least 16,800 actual baseline frames before
submission and the requested actual tail frames after the response. Missing
samples, early capture termination, errors, overflow, nonfinite/unaligned data,
or changed/unusable read-only endpoint levels invalidate the trial and return
nonzero. Final partial windows are included. All six trials were valid, with
139,680–145,920 captured frames, at least 96,000 measured tail frames, unchanged
endpoint levels and no clipped samples. No audio recordings or settings changes
were made.

### Two concrete framing hypotheses

The first four trials use genuine
[Opus self-delimited multistream framing](https://www.rfc-editor.org/rfc/rfc6716.html#appendix-B),
not the already tested outer packet-length prefixes. A real two-stream decoder
verifies the complete 101-byte packet against independent left/right decoders.
The final two use the
[libnx hwopus header](https://github.com/switchbrew/libnx/blob/master/nx/include/switch/services/hwopus.h)
with actual encoder/decoder-verified final range and the unchanged quiet 20 ms
stereo source. That header is also an
[upstream Opus test-stream format](https://github.com/xiph/opus/blob/main/src/opus_demo.c).
Neither source establishes Nintendo Bluetooth adoption; these are hypotheses,
not implementations copied from a working headphone transport.

| Trial | Matched packets | Host span, ms | Highest Line-In peak | Source tone detected |
| --- | ---: | ---: | ---: | --- |
| Self-delimited dual mono, ordinary input, 20 ms | 30 | 580.36 | 0.00088591 | No |
| Self-delimited dual mono, headset mode, 20 ms | 30 | 580.04 | 0.00082631 | No |
| Self-delimited dual mono, ordinary input, 5 ms | 120 | 595.02 | 0.00081627 | No |
| Self-delimited dual mono, headset mode, 5 ms | 120 | 596.48 | 0.00085171 | No |
| Hwopus wrapper, ordinary input, 20 ms | 30 | 580.38 | 0.00079148 | No |
| Hwopus wrapper, headset mode, 20 ms | 30 | 580.41 | 0.00077865 | No |

All 360 payloads matched exactly; no trace losses or incomplete packets were
reported. All temporary headset restores succeeded. Normal input advanced after
each trial. The known `ControlsForwarded: false` limitation during headset mode
is unchanged, not a production coexistence success. These compressed trials did
not exhibit the earlier PCM backlog. They do not establish a correct codec or
enable/routing sequence and must not be repeated without a new concrete variable.

Desktop evidence:

- `tools/b94-self-delimited-plans-20260907-204329-608/results.jsonl`, with four
  corresponding `b94-traced-plan-*` numeric captures starting at 20:43:30.
- `tools/b94-hwopus-plans-20260907-204641-320/results.jsonl`, with traces
  `b94-traced-plan-20260907-204641-880.jsonl` and
  `b94-traced-plan-20260907-204646-189.jsonl`.
- `tools/b94-audio-status-20260907-205011-803.jsonl`: still connected in generation
  2, 319,781 reports, 4.92 ms current report age. The 2,505.34 ms lifetime maximum
  is unchanged historical evidence from the earlier high-rate active-headset
  trial, not a new gap in this tranche. All owned ETW sessions stopped.

Focused `b94-source-framing-and-capture.trx`: **30 passed**. Full Release x64
`b94-source-framing-capture-full.trx`: **3,993 passed, zero failed, 11 opt-in
skips**, including all **147 allocation-related tests**. The full run's logger
filename argument was initially split by PowerShell after testing completed;
the generated TRX was renamed without altering it. Counts are verified from that
TRX, not inferred from the shell's overall exit code. Observer self-tests and an
independent code review also passed.

Bluetooth AUX playback remains **unresolved**. The source audit found no new
verified enable/mute bytes. The original
[17/02 and 18/03 documentation patch](https://github.com/ndeadly/switch2_controller_research/commit/85a8b54eaf5d0c1ccb24b6feacba938edff7b9b0)
does not define their semantics or a complete headphone sequence. No unknown
18/03 command, alternate GATT owner, DLL injection or new app image was used.
The next useful evidence is the controller's accepted audio enable/routing and
packet structure, not another repetition of these negative formats. Preserve
the running apps and connection; do not expand command access by replacing them.

## Source-only follow-up: headset control identity and remaining evidence gaps

The preceding turn was progress: six new hypotheses were measured and the
external probe/capture validity changes were checkpointed as `f428c73`. This
follow-up made no radio writes and did not replay negative formats. Revalidated
live PIDs/start times and the A9A373... app hash remained unchanged. Source test
builds stayed in the repository; no files were copied into the live runtime.

### Correcting a controls representation hazard

The existing diagnostic decoder returned `Switch2BasicInputReport` labelled
Pro09, although its source was the dedicated 112-byte headset report. It also
carried a motion region at offset 66 that cannot fit the canonical 63-byte body.
No current mapper consumed it, so this was not an established live input bug;
it was an unsafe starting point for implementing audio/input coexistence.

The source now uses `Switch2ProHeadsetControls`, owning decoded scalars only:
the native 8-bit counter/power byte, raw 24-bit buttons, explicit canonical
button semantics, unknown bits and exact 12-bit sticks. The 21 documented bits
are mapped individually, including GL/GR and physical B/A/Y/X positions. There
is no report-kind alias, borrowed buffer, retained microphone/motion data or
implicit lifetime authorization. Existing stick decoding and profile axis math
are reused. The diagnostic call uses this type; production admission, queues,
the mapper and the running process are unchanged.

New tests cover all 21 single-bit mappings, unknown combinations, every legal
audio/motion-length pair, rejected lengths/truncations/oversize, all counter and
power byte values, stick endpoints, buffer independence and zero allocation.
Focused `b94-headset-typed-controls.trx`: **88 passed, zero failed**, including
35 new typed-control cases plus the existing tone/protocol tests.
Full Release x64 `b94-headset-controls-full.trx`: **4,028 passed, zero failed,
11 opt-in skips**, including all **148 allocation-related tests**. No test
runtime was deployed over either running application.

### Why forwarding is not a one-line fix

If headphone output ultimately requires headset notifications, its controls
must enter the existing serialized publication path through an explicitly
admitted auxiliary source of the same lease. Common05 has a 32-bit counter,
different button positions, a fixed 63-byte body and a pinned descriptor. The
headset's counter requires a separate baseline. Do not fake common05 packets,
discard ordered button transitions, bypass generation/teardown gates, or call
the mapper under the lab callback's statistics lock.

`Switch2RuntimeInputDevice.TryPublishProDetailed` currently resets motion
projection whenever a frame lacks common motion. A controls-only update needs
an explicit freshness contract: no repeated integration of cached gyro, no
invented motion sample, and no repeated clearing of calibration/filter state.
Full runtime wiring has **not** been implemented or represented as complete.

The last measured headset trace (`b94-traced-plan-20260907-204646-189.jsonl`)
contains 41 reports with **MotionLength=0**, not a captured 30/40-byte motion
stream. The renewed donor audit found no full packed-motion decoder:
Switch2Connect/PadForge use common05 raw IMU; switch2mac's packed-field analysis
is partial. A newer
[hid-nintendo2-dkms parser](https://github.com/XenuIsWatching/hid-nintendo2-dkms/blob/32a981ea7f916f1792a7e35aa0ecf79063ec4001/hid-nintendo2.c#L871)
handles accelerometer-only USB Pro09 in one 30-byte configuration; it does not
establish Bluetooth headset gyro or timestamp units. Its offsets must not be
applied to absent motion data or the 40-byte variant. Preserving software state
cannot recreate motion reports that were not delivered.

### Output setup boundary rechecked

The fixed `17/02` bytes exactly match the pinned command reference. The command
channel awaits the full controller notification separately from the WinRT
write, so setup acknowledgement is more than host submission. Nevertheless,
the reference still labels its semantics unknown; no verified route, codec,
volume or stream-start operation emerged.

Working USB audio uses native `WasapiOut`/`usbaudio`. Its descriptors expose
USB-class mute/volume and an output streaming alternate setting, but neither
the physical ledger nor available audio-era captures establish corresponding
Bluetooth commands. Do not transplant USB control IDs or label `18/01` volume
without that missing evidence.

Bluetooth playback, channel assignment, physical stopping/clipping and input
coexistence remain unproven. A known-working Bluetooth audio sequence is still
needed to distinguish an unknown enable/routing gate from an invalid packet
format. The user confirmed that a Nintendo Switch 2 console is available. A
separate capture-method feasibility audit is now warranted; console ownership
alone does not make encrypted over-air traffic readable. No console pairing,
advertisement, controller handoff, radio change or restart is authorized by that
answer. The only currently present USB Bluetooth adapter observed on the PC is
the RZ616, so a second independent radio must not be assumed.

The [console-capture feasibility plan](2026-09-07-switch2-console-audio-capture-plan.md)
records the evidenced separate-sniffer route, custom pairing/key handling,
Windows capture/mock-peripheral limitations and explicit handoff gates. It is
not executed. The user explicitly confirmed that no BLE sniffer is available.
The proposed separate-sniffer route is therefore unavailable with the established
equipment; console ownership does not remove that prerequisite. No acquisition,
radio repurposing, controller handoff or restart was performed.

The additional `german77/JoyconDriver` Switch 2 dissector audit at commit
`6238c941078b224df7130dc0ba78a4d09a7cac68` also supplied no usable audio setup:
`command_handler.lua` has no specialized `17` decoder and labels `18` unknown;
the input decoder leaves AudioStatus undecoded and does not decode the dedicated
headphone/headset stream. Its existence is not evidence of implemented
Bluetooth headphone playback.

No new radio probes were run in this follow-up. The last source-test result
remains **4,028 passed, zero failed, 11 opt-in skips**; these documentation
updates do not add hardware validation. Bluetooth AUX playback remains silent
in the completed measurements. A verified implementation or suitable existing
capture could still supply the missing setup/framing without new hardware;
the unavailable sniffer is not proof that Bluetooth audio cannot be implemented.

## Source-only Pro report-feature startup correction

The preceding no-sniffer follow-up recorded a constraint but did not advance
playback. This audit identified an actual source omission relevant to input
coexistence: Bluetooth Pro preparation skipped the feature-mask/enable sequence
that the Joy-Con path already performs. The documented IMU feature bit gates
both common05 motion and the dedicated headset report's motion region. Thus the
previously observed `MotionLength=0` is consistent with missing initialization;
it must not be used to conclude that the controller cannot supply motion there.
It does not prove that this omission caused the silent headphone output.

The pinned
[Pro Bluetooth initialization sequence](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md#pro-controller-2)
sets and then enables `0x2F` with `0C/02` and `0C/04`. This is distinct from the
existing USB `0x27` mask and Joy-Con `0x94` mask. Feature selection does not
change the selected report UUID or its parser. The source correction keeps
common05, its 63-byte body, the canonical mapper and the input hot path intact.

Seven feature responses in the existing Pro donor PCAPs contain the documented
12-byte command replies (`0C0101021078000000000000` for mask and
`0C0101041078000000000000` for enable). The raw captured ATT values are 26 bytes
on the combined lane, with a 14-zero-byte prefix; they are not direct 12-byte
captures of our command-only response UUID. The closed codec uses the documented
command-only format without opportunistic prefix stripping. The mask replies
take about 105 ms in pairing/reconnect/wake captures; no artificial delay or
20 ms deadline is justified. The existing 2,000 ms startup deadline is retained.

Both exchanges share command ownership. Pro acceptance validates the complete
expected-step response, not the legacy Joy-Con success-byte predicate. An exact
duplicate mask acknowledgement cannot satisfy the enable step. Failure prevents
input subscription/publication. Cancellation must return without waiting forever
for a noncooperative Windows write, while retaining ownership and deferring
resource disposal until that actual operation drains. Joy-Con behavior remains
unchanged. The new diagnostic describes feature readiness, not audio readiness.

Source verification: Release x64 build passed (existing warnings only).
`b94-pro-feature-startup-focused.trx`: **116 passed, zero failed**, including
24 new cases for exact requests/replies, subscription order, malformed/future
and duplicate replies, ownership exclusion, both-step cancellation with a held
write, and bounded adapter failure without premature disposal.
`b94-pro-feature-startup-full.trx`: **4,052 passed, zero failed, 11 opt-in
skips**, including all **148 allocation-related tests**. Shared fakes now
acknowledge the exact Pro startup before exercising calibration/output behavior;
the legacy Joy-Con predicate and startup remain covered and unchanged.

No runtime deployment or new radio probe is authorized by this source change.
The loaded bridge still cannot issue these feature commands, and no second
Bluetooth owner was opened. Direct command-only Pro acknowledgement and actual
motion availability after startup remain hardware acceptance gates; source
tests and normalized donor captures are not substitutes for them.

The separate new donor check of
[Pico-NS2-Waker](https://github.com/david419kr/Pico-NS2-Waker/tree/c4b45b6ff55b69c8a819abde7a4c91f20ae44972)
does not supply Pro Bluetooth audio: its implemented audio route is console USB
Audio Class to Sony Bluetooth reports for a DualSense. Its physical Pro BLE
path is HID-only. Its `18/01` reply reproduces the same already-known bytes,
without establishing volume/routing semantics. No new headphone format was
justified for a live trial.

The unchanged live process was queried at 21:28:42 local: PID 32636, generation
2, connected, 473,266 reports and 1.1593 ms current report age. The lifetime
2,505.3441 ms maximum remains historical. The status-only helper exited zero;
it performed no radio I/O. Headphone playback, correct channels, clean stopping,
clipping checks and full input/audio coexistence remain unproven.

After the test suite, status at 21:47:02 local still reports PID 32636,
generation 2, connected, 545,908 reports and 10.4155 ms current report age.
The historical maximum is unchanged. Both app PIDs/start times and the loaded
A9A373... DLL hash remain unchanged. The status-only helper exited zero and no
test/trace session remains running. This turn made source progress on an input
prerequisite, not physical headphone playback.
