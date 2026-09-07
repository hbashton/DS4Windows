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
