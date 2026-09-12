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
