# Switch 2 held-rumble maintenance, 2026-09-08

## Scope and evidence

The reported symptom was premature cutoff of Switch 2 Test Heavy/Test Light and held game rumble. This change is source/test validated, not a claim that the running portable build or physical controller has been updated or perceptually accepted.

Two source-level problems were reproduced:

1. The profile editor publishes a held preview once. The Switch 2 local lane has a 250 ms lease, but production previously had no `Lane.ServiceLease` caller to renew a genuinely held local value.
2. Canonical identical-state deduplication and the physical sink's completed-delivery deduplication suppress subsequent writes. Renewing a game's canonical lease alone therefore does not sustain finite physical HD-rumble packets.

Existing primary implementations corroborate repeated nonzero output, not increased intensity:

- [SDL, pinned c71abd08605b8bb7078372307a93274725c99fe0](https://github.com/libsdl-org/SDL/blob/c71abd08605b8bb7078372307a93274725c99fe0/src/joystick/hidapi/SDL_hidapi_switch2.c#L1040): `UpdateRumble` continues while amplitudes are nonzero; `RUMBLE_INTERVAL` is 12 ms. SDL also doubles its interval for parented Joy-Cons in that implementation.
- [Switch2Connect, pinned 61ac6642ce12fe7217e38a860b14863b18ca7e28](https://github.com/TommyWabg/Switch2Connect/blob/61ac6642ce12fe7217e38a860b14863b18ca7e28/src/controller.py#L2850): System Bluetooth pacing uses a 15 ms minimum to avoid starving input notifications. Its scheduler repeats active output.

The new Pro USB worker uses 12 ms; Bluetooth uses 15 ms, including joined Joy-Cons. These are requested send-start maintenance intervals, not Windows hard-real-time guarantees or measured hardware latency.

## Implementation and preserved boundaries

- One coalescing output-only worker is attached only to an active Switch 2 runtime with its existing physical feedback lifetime. No second writer, mapping stack, controller handle, or input-thread timer wait is introduced. It parks when there is no pending output or actual sustained nonzero output. Pending local intent cannot keep it scheduled after the physical feedback lifetime closes or leaves its committed state; transient Busy remains retryable and is not treated as completion.
- Only genuinely held local profile/preview values renew their existing 250 ms lease. Finite connection/identification cues are explicitly distinguished even when an immediate write fails. A missed lease deadline still requests Stop rather than resurrecting old state. External game leases are never fabricated or extended by maintenance.
- An explicit apply-only maintenance sink repeats current held output without changing sequence identity, ownership, TTL, routing, tuning, or independent subframe groups. Physical successful resubmission advances the existing transport counter. Uncertain writes retain their exact pending packet/counter until resolved.
- Untuned rich source groups are retained for an exact current frame, so repeating native designs or adaptive-trigger approximations does not flatten them into the canonical body marker or compound tuning. Newly staged source data wins. Completed PCM and native streamed slices are not looped into a sustained buzz.
- Zero-strength/silent rendering does not generate continuous silent Apply traffic. Completed Neutral and Stop remain one-shot. Profile changes can restore actual output and restart maintenance.
- A physical-lifetime transaction gate protects publish, rich staging, policy refresh, and draining as one operation. Maintenance and local/input-path drain entries do not wait for that gate. Ordinary virtual-session retirement retains its serialized completion behavior. Explicit immediate physical-retirement entries remain nonblocking; production cold teardown now absorbs transient admission contention within its existing deadline, as detailed below.
- A local Stop requested during an already-admitted physical claim is retained as a per-lane intent for the live worker. It disables renewal immediately. The generic rule forbidding withdrawal of an in-flight claim is unchanged. A following held body value is deferred behind the required Stop; finite cue stages use at most 16 cancellable retries spaced 15 ms apart. Neither queued intent nor canonical enqueue acceptance is reported as physical-write evidence.
- Existing cue tests now compare exact group/neutral transitions rather than assuming one physical packet per stage. Fake runtime workers are stopped in test cleanup even when an assertion fails.

## Regression evidence

- `switch2-rumble-maintenance-before.trx`: **4 failed, 2 passed**. The held Heavy/Light and native profile/preview repeats failed against the old deduplication behavior; streamed PCM and stale-after-Stop protections passed.
- `switch2-rumble-uncertain-before.trx`: **1 failed** against an intermediate implementation. An uncertain rich keepalive retried through the ordinary pump lost its retained groups. Exact retry handling was corrected; the broader targeted runs below include this regression.
- `switch2-rumble-maintenance-final-targeted.trx`: **175 passed, 1 failed**, before pending local Stop intent was implemented. The newly added held-writer test proved that `TryWithdraw` correctly refuses an in-flight claim; that refusal must be handled at the local scheduling layer, not by weakening the generic runtime.
- `switch2-rumble-closed-lifetime-before.trx`: **1 failed** before the physical-admission guard. A retained local Stop intent kept a worker scheduled even after its feedback lifetime had retired; the guard now parks it without claiming that intent completed.
- `switch2-rumble-maintenance-complete-final.trx`: **191 passed, 0 failed, 0 skipped** in the initial consolidated targeted rebuild, before the full-suite cold-retirement finding below. This includes 25 dedicated rumble-maintenance cases, 4 generic maintenance-safety cases, 14 state-lane pump cases, 25 sink cases, 40 Bluetooth lifetime cases, 49 owned USB lifetime cases, 22 identity cases, and 12 input-failure diagnostic cases. This result does not substitute for acceptance of the later cold-retirement changes.

The focused coverage includes actual worker scheduling against fake physical leases, preview survival beyond 250 ms, counter progression, exact rich groups and uncertain retry, source expiry, zero-strength parking, streamed no-replay, held-writer contention, Stop-before-successor ordering, neutral/retirement, USB operation fencing, session replacement after retirement, and allocation assertions. Full-suite and all-allocation results belong to the parent validation ledger after the final integrated source build.

No live apps were restarted, no controllers were accessed, and no profiles were changed during this source/test work. Physical feel, actual radio scheduling under multiple-controller load, and real-game acceptance remain portable-build hardware validation rather than inferred test results.

## Full-suite cold-retirement follow-up

`controller-followup-final.trx` subsequently reported **4,412 passed, 1 failed, 11 existing skips**. The failing delayed-feedback fixture directly retired its physical lifetime while its runtime worker remained active. Inspection found the same ordering in the real standalone and joined Bluetooth runtime owners: physical feedback retires before input stop and runtime terminal publication. A transient transaction-admission failure is permanently quarantined by those owners, so changing only test cleanup would hide a production problem.

Two distinct retirement defects were isolated with held fake physical writes:

1. The supposedly immediate physical-session retirement cleared impulse-release state before reaching its nonblocking owner-retirement guard. That clear still unconditionally acquired the output transaction gate. `switch2-rumble-cold-retirement-dedicated-before.trx` reproduced deadline overruns (**2 failed, 4 passed**) using dedicated stop threads, ruling out ThreadPool scheduling as the explanation. The explicit physical-retirement clear now uses nonblocking admission; ordinary session clear remains serialized. Its immediate with/without-session cases pass in `switch2-rumble-session-clear-after.trx` (**2 passed**).
2. After that hidden wait was removed, the actual owner tests exposed the transient-admission quarantine directly. `switch2-rumble-cold-admission-before.trx` reports **4 failed, 2 passed**: healthy Pro/left/right/joined teardown returned failure while an admitted maintenance write could still complete within the caller's deadline. True-timeout cases remained fail-closed.

The correction uses an explicit cold retirement entry: seal new feedback work, acquire the exact session gate followed by the transaction gate against one existing absolute deadline, and invoke the same immediate retirement logic reentrantly. It does not loop on arbitrary false results or retry genuine physical failures beyond the existing attempt budget. Runtime-owner call sites use that bounded entry; immediate input/safety APIs remain nonblocking. Active-runtime fixture cleanup follows the actual cold entry rather than weakening its terminal assertions. USB composite teardown had the same one-shot Busy-to-quarantine problem; both ordinary removal and split-commit compensation now pass their existing absolute Stopwatch deadline to the concrete feedback lifetime. Generic interface fallbacks retain their prior remaining-time and authenticated-result checks.

Bluetooth terminal-attempt admission reserves the existing Windows cancellation budget of 100 ms per physical target (200 ms for an ordinary joined write). A physical-loss cleanup reserves only for still-live survivors established by exact native release proofs: one survivor needs 100 ms, while two proven released targets need no fabricated output write. These are admission and cooperative transport-cancellation bounds, not hard preemption of uncooperative Windows/native/injected I/O. A genuine deadline expiry still quarantines; late write completion does not authorize teardown retroactively.

Cold-path regression evidence:

- `switch2-rumble-cold-bt-green-usb-before.trx`: **9 passed, 1 failed**. All six standalone/joined cold-owner rows and two immediate Bluetooth rows passed. The actual USB composite/core release-before-deadline row still failed with premature quarantine; its true-timeout row passed. The USB participant correction followed this evidence.
- `switch2-rumble-cold-retirement-complete.trx`: **328 passed, 0 failed, 0 skipped** after the integrated Bluetooth, joined, and USB fixes. The USB rows now both pass and assert real participant/core ordering, exact neutral delivery, retirement, and disposal using an adopted fake physical output and dedicated held-write/stop threads. This proves bounded admission and authenticated cleanup behavior, not native cancellation timing or hardware acceptance.
- `switch2-rumble-survivor-budget-accepted.trx`: **3 passed, 0 failed, 0 skipped** after the final survivor-count correction. Both exact released targets retire inside a 50 ms budget without writes; a live survivor receives no write when only 50 ms remains; a 180 ms budget admits exactly one real survivor-neutral write. The initial boundary fixture run was **2 passed, 1 failed** because its survivor assertion incorrectly invoked the Pro decoder on a Joy-Con packet; only that assertion was corrected to the existing Joy-Con-neutral decoder before this rerun.

Source and tests were then frozen for the parent's two complete-suite runs and all-allocation acceptance. Those final full-suite counts are recorded in the consolidated follow-up ledger; the earlier targeted results above are not substitutes for them.
