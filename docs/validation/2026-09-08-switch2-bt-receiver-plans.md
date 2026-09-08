# Switch 2 Pro Bluetooth receiver setup experiments

Physical Bluetooth headphone playback remains **unconfirmed and silent in the
measurements below**. Command acknowledgements, accepted GATT writes and passing
software tests are not playback evidence. This ledger continues
[the preceding delivery experiments](2026-09-07-switch2-bt-audio-delivery-experiments.md).

## Authorization and exact runtime

The user approved the portable workflow and further setup/reverse-engineering
work. One app-only reload occurred at 23:51 local on September 7. The exact old
DS4Windows PID 32636 and DLL A9A373... were backed up to the Desktop lab's
`tools/b94-before-receiver-bridge` before replacement. The old process received
CloseMainWindow and was stopped only after the bounded exit wait. No installed
Program Files copy, driver, Bluetooth bond, adapter identity or audio level was
changed. Original VIIPER PID **30276** remained running.

- Portable DS4Windows PID **34488**, app DLL file version **5.0.4.95**.
- DLL SHA-256 **6B1333921B85420086C9AB461751C00E2BEE8A253F30F00F3B7B41BE6B040375**.
- Apphost retained **0C1E2349C549B03EE51D9399EEBB802E0686C9F329FE1D8812095F09630EFBD4**.
- VIIPER retained **7B6B00CF3AC205549AF80692E45BD7785D3B8BC558A92D4FF5A1A61060592B78**.
- External b95 lab-client executable **4E14D373EB84F42D11F6CB5265080B092A743165552BD0E265A24634F0CDE301**.
- Runtime folder remains the existing Desktop lab's
  `runtime/DS4Windows-current-2026-09-07-b93-bt-audio-lab`; folder and apphost
  labels are historical, not the loaded DLL's identity.
- The hash-pinned portable launcher was updated and VerifyOnly passed.

The user pressed A after the reload. At 23:52:50 the real Pro accepted the strict
documented `0C/02 -> 0C/04`, both mask `2F000000`, and resumed controller input.
All following experiments used **the same generation 2 and same connection**.

## Reusable, constrained receiver-plan capability

`ReceiverPlanProtocol: 1` adds finite setup plans to the existing local pipe;
new setup order/parameters no longer require rebuilding DS4Windows. The existing
input lease and command owner remain authoritative. No second GATT owner or
arbitrary target UUID is exposed.

The closed vocabulary is documented `0C/02`, `0C/04`, `17/02`, `18/01` and
`18/03`. Normal plans accept exact observed bytes only. Explicit experimental
opt-in permits a bounded subset of unproven `17/02` parameter alternatives and
`18/03` values; this is not a declaration of valid routing semantics. All commands,
payload budgets, generation and actual audio MTU are checked before radio writes.
Requests are copied before execution. Replies require exact headers, lengths,
subcommand and applicable echo; failures fence the command lane without replay.

The command owner stays reserved through optional headphone packets and headset
notification compensation. Cancelled waits retain actual Windows writes until
completion. Restoration now attempts common05 after headset-disable completes or
faults, without overlapping the writes. Shared server/client result validation
rejects missing cleanup, nested audio failures and partial packet counts; a failed
receiver plan invalidates prior audio-setup admission. Every receiver trial,
including command-only plans, requires explicit Realtek Line In measurement.

Release x64 verification of this source: **4,114 passed, zero failed, 11 opt-in
skips**, including all **148 allocation-related tests**. Final focused set:
**145 passed**. TRX: `b95-receiver-final-full.trx` and
`b95-receiver-final-focused.trx`. The final full run built file version 5.0.4.95;
the deployed app DLL hash matches that tested build. Client publish passed with
the two pre-existing shared rumble-code nullability warnings only.

## Physical sequence and outcomes

Only the user-confirmed Pro AUX-out to Realtek Line In is captured, float/stereo
48 kHz, unchanged unmuted scalar 1. Samples remain bounded in RAM and are discarded.
Each trial uses at least 16,800 real baseline frames plus at least 96,000 real
post-response frames. All eight captures below were valid, with zero clipping,
overflow, nonfinite samples or dropped frames. No test frequency emerged above
the baseline/background floor; post-submission peaks remained below 0.001.

The source is 500 ms of quiet 440 Hz left / 660 Hz right, peak 0.005, 10 ms fades,
and 100 ms trailing silence. Raw 5 ms stereo Opus at 80 kbps has 120 packets of
50 bytes; raw 20 ms stereo at the same bitrate has 30 packets of 200 bytes. Frame
format and numerical setup interpretations remain hypotheses.

| Local time | Intervention | Result |
| --- | --- | --- |
| 23:53:52 | Normal reports; `18/01`, exact `17/02`, raw 5 ms stereo | Both ACKs; 120 sends; quiet negative |
| 23:54:42 | Add observed `18/03=07`, query; identical payload, no repeated `17` | Exact echo; state query unchanged; quiet negative |
| 23:55:01 | Enable headset notifications, query; identical payload | 120 sends; both restores successful; quiet negative |
| 23:55:24 | Headset notify before exact `17/02`, `18/03=07`, query | All ACKs; both restores successful; quiet negative |
| 23:57:52 | Keep `17` value 240 and `18` value 07; switch payload to raw 20 ms stereo | 30 sends; quiet negative |
| 23:58:14 | Change only proposed frame field from 240 to 960 (`C003` little-endian) | ACK; identical 20 ms payload; quiet negative |
| 23:58:38 | Exact `17` value 240; experimental `18/03=05`; raw 5 ms stereo | Exact 05 echo; query unchanged; quiet negative |
| 00:00:03 | Explicit documented `17` and `18/03=07`; adjacent-report 33-zero-byte + length envelope around same 5 ms source | 120 sends; quiet negative |

These are sequential interventions, not independent fresh-state baselines.
`18/03` persistence and inverse are unknown; 00 was not used as invented cleanup.
The final trial explicitly used observed 07 rather than assuming a reset.
The 05 test was motivated by the documented input states for headphones (05/0D)
versus a headset (07/0F); applying that distinction to command 18 is an inference,
not established semantics.

Every `18/01` reply was `1801010110780000000040F000006000`.
Exact `17/02` ACK was `1701010210780000`; `18/03` echoed 07 or 05 as sent.
Unchanged query bytes do not prove unchanged routing.

The new feature initialization has a separate verified effect: headset reports
now contain motion lengths **30/40/4**, instead of the previously observed
`MotionLength=0`. Headset notifications temporarily replace common05 input in this lab
mode. All restores succeeded and reports advanced afterward; the largest
accumulated report gap was **765.2614 ms** during a deliberate headset window.
At 00:00:06 the same Pro had 28,889 reports and current report age 3.3277 ms.
This is **not production input/audio coexistence**: `ControlsForwarded=false`.

The motion-length-4 reports are rejected by the current controls-only diagnostic
parser, exactly explaining e.g. 14 missing controls decodes out of 127 otherwise
valid headers. This length is consistent with the variant in independent donor
research: [pinned switch2mac capture analysis](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/capture-format-analysis.md#L36).
The donor interprets it as a no-new-record marker; our length-only observations
do not independently verify its payload, counters or flag semantics.
A narrowly scoped future parser correction can admit 4 while leaving the packed
motion opaque. It is not necessary to reload this probe or evidence of playback.

Local raw numeric evidence remains in the Desktop lab's `tools/b95-receiver-*.jsonl`;
reviewed JSON plans are in `tools/b95-receiver-plans-g2`. No microphone payload,
raw ETL/HCI, pairing key, or PCM recording was persisted.

## Remaining acceptance gates

Actual identifiable stereo output, repeatable start/stop, no clipping and continuous
ordinary input/motion alongside audio remain open. The new trials did not isolate
the remaining codec/envelope/setup requirement. Previous format negatives were
measured before the new Pro feature startup and `18/03` intervention, so they
cannot conclusively eliminate those formats in this newly established state.
No gain increase, arbitrary command sweep or additional app restart followed
these negative results.

## Two additional host-traced, current-setup trials

The input idle prefix `F8 FF FE` describes a standard Opus fullband mono 20 ms
packet, not 5 ms stereo; see [RFC 6716 section 3.1](https://www.rfc-editor.org/rfc/rfc6716.html#section-3.1).
The existing generator test checks that its mono 20 ms / 20 kbps source has a
50-byte packet with TOC F8, retains a quiet decoded 440 Hz signal and ends in
silence. This motivates two candidates but does not prove that the controller's
headphone output shares its input codec or envelope.

With the same active generation, `17/02` returned to its documented 2/240 bytes,
`18/03` explicitly at 07, and temporary headset notifications:

| Local time | Payload | Exact host payload matches | Host output span | Line In |
| --- | --- | --- | --- | --- |
| 00:04:39 | Mono 20 ms / 20 kbps Opus, raw 50 bytes | 30/30 | 580.6489 ms | Valid quiet negative; peak 0.00084618 |
| 00:05:12 | Identical source with 33-zero-byte + length prefix, 84 bytes | 30/30 | 580.4294 ms | Valid quiet negative; peak 0.00083387 |

Both traces bound only controller link 512 through its exact `18/01` request on
command attribute 20. No ETW loss, mismatched payload, or pending reassembly was
reported. Both complete host-delivery flags were true; **these are host HCI
observations, not proof of radio reception, codec acceptance or DAC playback**.
Both headset/common05 restores succeeded. The capture tail contained 96,000 and
96,480 actual frames respectively, with zero clipping or invalid capture reasons.
The two trace sessions stopped normally; no raw trace or PCM file was recorded.

Evidence: Desktop lab tools `b95-trace-mono20-raw-20260908-000439-893.jsonl` and
`b95-trace-mono20-prolen-20260908-000512-747.jsonl`. The first audio-plan fingerprint
is `38C4258D08120B528FB8F2C949353F6F00C2EB005A6A6D81BDA8233BAFCD6262`; the second is
`8258C41BD7DE5CA417CC5BE17B14D2E4D01DE99CFA6C6E246210E9AF0D9F087E`.

At 00:07:36, the same app and Pro generation remained connected: **58,786 reports**,
11.5949 ms current report age, unchanged historical 765.2614 ms maximum from the
deliberate lab headset windows. VIIPER remained untouched. The source checkpoint
for the reusable receiver bridge is `a34df03`; there was no further app rebuild
or reload during the ten hardware trials.

The remaining discriminator is a working controller-origin/output protocol
reference or a measurement that identifies the missing receiver/codec/envelope
requirement. Ten valid negative trials under corrected feature startup do not
establish impossibility, and they do not justify calling this production audio.

## New reference-capture option, not executed

Authenticated GitHub code search of the exact headphone UUID found four files
in three repositories; the headset UUID found sixteen files in eight repositories.
The exact spaced `17/02` request and payload produced no hits. No working physical
Pro Bluetooth headphone decoder emerged. A newly found
[ESP32 controller emulator](https://github.com/zhantss/ESP32-BLE5-NSController-Emulator/tree/0ea0c62aeab2440c7f9c77f29cf87ffa3b4c192f)
offers a plausible alternative **capture endpoint**, not a ready audio solution.

Its source implements Nintendo's address/key exchange and NimBLE security-store
integration. A successful emulator endpoint could therefore observe decrypted
console-origin application packets without a passive Bluetooth sniffer. This
requires a separate compatible development board and the user's Switch 2 console;
it cannot run on this Windows PC alone. The author reports C61 fully tested, C6
basic-tested and patched S3 basic-tested, using ESP-IDF 5.5.2+ (tested 5.5.3).
Those are upstream claims, not our verified hardware results; see its
[hardware requirements](https://github.com/zhantss/ESP32-BLE5-NSController-Emulator/blob/0ea0c62aeab2440c7f9c77f29cf87ffa3b4c192f/README.en.md#hardware-support).

As shipped it reports no headset, produces no dedicated headset notifications,
rejects headphone handle 002c writes, and returns an empty `18/03` reply instead
of the documented echo. `17/02` receives a generic empty success ACK. A capture
prototype would need documented headset prerequisites plus bounded headphone/
17/18 request logging and USB/UART export. Existing key-debug logging must be
disabled; pairing keys, unrelated traffic and voice payloads must be excluded.
Instrumenting its current write callback alone is not enough to establish that
the console will start or sustain audio.

No emulator board is known to be available, and no matching connected Espressif/
common USB-serial development-board device was found in the limited read-only
inventory. No emulator was built/flashed, console pairing changed, hardware
purchased, or current controller connection handed off. Hardware availability is
the next user-dependent input for this reference-capture path.
