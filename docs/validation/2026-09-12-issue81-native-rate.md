# Issue 81: separate native-feedback pacing from local settings

This follows `47cc945` (conservative pending-repeat suppression). It does not
replace ordered feedback with latest-value state and does not relax unknown
trigger-byte preservation.

## Source change

- Native Bluetooth feedback has a separate 500 Hz / 2 ms submission ceiling.
  Local profile/settings updates retain their 200 Hz / 5 ms policy.
- Native deadline waits use the existing high-resolution timer, with stop/new
  work interruption and a final readiness recheck. They add no spin window.
- Due media, startup/prime ordering, microphone barriers and retained-command
  retries still take priority where required. Only accepted writes consume a
  native command or advance its submission timestamp.
- Native-bearing physical writes are explicitly tracked, including native
  state piggybacked onto media. Exact completion, failure and lifecycle rules
  apply to those writes. The production pending allowance remains the existing
  32 slots; it is **not** reduced to one outstanding command.
- An optional smaller native-credit limit exists in the internal writer seam
  for adversarial tests. Delayed Windows IRP completion is not a Bluetooth or
  actuator acknowledgement; a one-credit production gate could worsen delay.
- No changes to input mapping, Nintendo translation, PCM gain/codec, persistent
  rumble mode, trigger ownership, descriptor-sized writes or VIIPER protocol.

## Automated evidence

`isolated_results/issue81-native-rate/` contains the local TRX evidence.

- `native-rate-first.trx`: exposed an old deadline assertion and an unintended
  early completion poll of unrelated media. The deadline regression now covers
  both old/new rates and credit exhaustion. The writer now skips completion
  polling when there is no pending native write; the original Busy/media
  fairness test passes unchanged.
- `native-rate-focused-green.trx`: 527 passed, zero failed/skipped.
- `native-rate-full-green.trx`: 5,538 passed, zero failed, 11 existing gated
  skips (5,549 total). Allocation assertions remained enabled.
- Fixed-deadline pressure tests retain every distinct rumble command, A/B/A
  trigger transition, final zero and all 128 finite PCM frames, including zero
  frames. Offered 250 Hz under a 500 Hz budget and offered 500 Hz under a 1,000
  Hz test budget pass. The old 200 Hz budget under offered 500 Hz demonstrates
  accumulated queue delay as a positive bottleneck control.
- Initial synthetic run: offered 250/budget 500 had 1.506 ms maximum effect age
  and queue high-water 1; offered 500/budget 1,000 had 0.623 ms maximum age and
  high-water 1. Old budget 200 accumulated 1,564 ms final-stop age. These are
  scheduler/test-hook observations, **not measured Bluetooth or game latency**.
- Writer tests cover credit exhaustion, media progress, 0x31 and native-bearing
  0x36, sync/async completions, failed/short completion, cancellation/disposal,
  unchanged default allowance and zero-allocation warmed readiness checks.

## Validation boundary

The 500 Hz value is a submission ceiling, not a guaranteed sustained service
rate or hardware capability claim. Hardware comparison and GTA reporter
confirmation remain pending at this checkpoint. No release or issue closure
is authorized by these automated results alone.

## Connected Bluetooth comparison

After the source checkpoint, complete portable RC4.5.8 and candidate packages
passed the independent 553-file / 297-dependency verifier. Both used the same
released VIIPER binary and a cloned profile selecting virtual DualSense. Only
the test copy's output type and idle timeout were changed; the Roaming profile
and installed files were preserved. The initial installed session reached its
existing 900-second idle timeout before the first replay; the user reconnected
the controller after the baseline portable was ready.

The probe opens only an explicitly inventoried virtual DualSense HID interface
whose parent chain has the USB/IP UDE service/hardware identity. It sends one
second of modest, distinct body-rumble commands at 250 Hz, then an explicit
zero. It never directly writes the physical controller. Physical submission
traces are captured from the existing helper's bounded diagnostic arrays using
non-suspending, read-only inspection. Both 0x31 and native-bearing 0x36 carriers
are included; media copies of an already accepted amplitude are not new effects.

An initial probe using Sleep(1) ran at only about 67 Hz on this host. Its 68
commands including stop were preserved, but it is **not** the pressure test.
The diagnostic scheduler was corrected to use a high-resolution waitable timer
without spin or global timer-resolution changes; its no-HID timing check
produced 250 wakes in 1,000.47 ms. Both comparison replays below have 250 body
commands plus the stop, and comparable roughly 4.1 ms source intervals.

| Host submission observation | RC4.5.8 baseline | Candidate 33d7e9f |
| --- | ---: | ---: |
| Ordered rumble amplitude transitions, including stop | 251/251 | 251/251 |
| Median source-write start to physical submission | 126.45 ms | 0.166 ms |
| 95th percentile | 239.91 ms | 0.237 ms |
| Maximum | 252.03 ms | 5.636 ms |
| Final neutral delay | 252.03 ms | 0.179 ms |
| Peak source commands awaiting submission | 51 | 2 |
| Remaining commands / later amplitude changes | 0 / 0 | 0 / 0 |

The baseline physical command interval was about 5 ms and accumulated delay;
the candidate followed the offered roughly 4 ms stream and drained. Media
submission intervals remained about 10 ms with occasional 20 ms gaps in both
replay windows. No short physical-write completions were observed. The actual
physical HID descriptor write length is 547 bytes; logical 78/398-byte packet
lengths must not be mistaken for the Windows write size or changed to force
higher throughput.

Evidence is under the Desktop `DS4Windows-Issue81-Lab-20260912` directory:
`baseline/samples/burst250-highres`, `candidate/samples/burst250-highres`, and
their inventories, replay requests, identity records and live metrics. Offline
analysis lives in `isolated_results/issue81-live-metrics/`. Candidate archive
SHA256: `3B335B43C741FFBCBEF0A0C4B3D51699402A7C95DE1E3A9F0FCFC24A72FFB8A0`.

This establishes the measured body-rumble transition sequence and a substantial
host-queue improvement on the connected Bluetooth controller. It does **not**
measure radio arrival, actuator response, trigger payload fidelity on hardware,
or GTA's perceived smoothness. Trigger/opaque-byte preservation is covered by
the source regression suite, not this rumble-only probe. The first input
histogram windows span unequal idle periods, so they cannot establish input
latency parity. GTA reporter confirmation remains necessary; no release or
issue closure follows automatically from this result.

Two additional candidate replays also preserved 251/251 measured amplitude
transitions each. Median source-start to submission was 0.1677/0.1658 ms,
maximum 0.4521/0.5298 ms, and final neutral 0.1511/0.1765 ms. Each peaked at one
awaiting command and drained to zero. The first repeat's observation window
ends immediately before the deliberate second replay, avoiding mislabeling
that later authorized rumble as a resurrected effect.

Candidate-only idle/load input checks retained identical process, device and
histogram identities across six snapshots, with exact bucket/count deltas.
Intervals above 5 ms were 3.59% idle / 3.95% load in the first pair and 4.96%
idle / 3.87% load in the second. No new interval above 50 ms was observed;
binned p50/p95/p99 upper bounds remained 2/5/20 ms. This is a bounded gross-stall
check with no consistent load-correlated worsening, **not** baseline parity.
Heap scans took 544–648 ms and approximate midpoint windows were 1.85–2.04
seconds, so neither precise one-second isolation nor improved input throughput
can be claimed. Evidence: `candidate/samples/input-windows` and `samples/final`.
