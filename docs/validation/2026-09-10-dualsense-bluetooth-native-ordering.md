# DualSense Bluetooth native command ordering, 2026-09-10

Final source validation: **5,185 passed, 0 failed, 11 gated skips** (5,196 total).
The identified native-command coalescing defect is corrected in source. Complete
portable E has subsequently been built and launched; physical GTA V Enhanced
gameplay confirmation remains outstanding.

## Scope and original failure

This work addresses the physical DualSense Bluetooth helper's native-command
coalescing defect, not Nintendo rumble tuning or a new Bluetooth packet format.
The existing helper accepted adjacent native commands into a mutable latest-state
latch: pulse then stop became only stop, and trigger A then B became only B.
Spaced positive controls worked. The actual-helper, synthetic-I/O reproduction is
recorded in `2026-09-10-nintendo-rumble-ab.md` and the ignored diagnostic directory
`isolated_results/dualsense-native-bt-ordering`.

The immutable native command path must remain distinct from the intentionally
latest-value local-control path. SDL's PS5 outstanding-request replacement is
not a lossless native-edge reference (local pinned SDL commit
`c71abd08605b8bb7078372307a93274725c99fe0`, `SDL_hidapi_ps5.c`, lines 1128-1138).
Copying that replacement policy would not
solve this reproduced defect. Existing native Bluetooth framing, final transition
deduplication, media cadence, physical writer ownership, and microphone-mode
boundaries remain the transport authority for this change.

## Implementation

- Protocol 16 appends a command ID and generation to the existing atomic native
  state/template payload. The original field offsets are unchanged. An older
  helper is rejected by the existing versioned startup handshake.
- The parent reserves one of 32 fixed native credits before admission. A sent
  credit returns only on the exact helper presentation/cancellation receipt.
  Definite unsent cancellation rolls back its own credit; normal Clear/Reset
  cannot manufacture room by forgetting commands already sent.
- The helper owns 32 preallocated immutable command slots. One claimed slot
  remains owned throughout physical I/O, including a concurrent Clear. Busy
  retains the exact head; cancellation cannot recycle its storage early.
- Idle control reports and media piggyback both consume the same native FIFO.
  The shared command reader never waits for FIFO room. Media storage and clocks
  remain separate, with the existing microphone-mode boundary respected.
- Native presentation snapshots cannot import later commands' game state from
  a newer audio template. Accepted commands commit their quiescent game fields
  to both cached template generations without replacing speaker PCM, haptics
  expiry, or independently newer local audio controls.
- Local controls remain latest-value. A later native command retires only
  superseded fields of an older pending local update, so an old local pulse or
  LED claim cannot replay after a native stop or release. Other fields remain
  pending.
- Healthy capacity pressure retains the caller's exact pending transaction
  rather than scheduling transport recovery. Exact ACKs and genuine outbound
  room return wake the existing physical-output owner; they do not perform
  output on the pipe reader or require another input/game event.
- The caller's existing pending-native-before-new-speaker-publication rule is
  retained. Sustained overload can backpressure new speaker publication; this
  does not block the helper reader or its already queued media. The change
  preserves bounded ownership rather than promising unlimited throughput.
- Reset publishes a new generation only after its reset command is admitted.
  The original signed, nonzero generation sequence is preserved across integer
  wrap, matching the independent PCM ring's existing serial comparison.
  The acknowledgement receiver reuses fixed buffers instead of allocating a
  header and payload for every receipt.

## Verification status

The initial focused runs passed all 105 cases across actual-helper ordering (26), existing
idle retry (5), helper protocol (6), fixed credit accounting (12), caller and
parent-boundary behavior (25), and actual framed acknowledgement receiving (31).
They cover pulse/stop, trigger A/B/A, LED claim/release, repeated rumble, existing
duplicate-trigger filtering, full native capacity, Busy head preservation,
Clear/Stop, real sender/ACK wakes, distinct native/media receipts, preserved
speaker frames, final template commits, and local/native overlap.

The unfiltered Release x64 suite passed **5,169**, failed **0**, and skipped the
same **11** gated cases (5,180 total). Strict zero-allocation assertions and their
allocation-positive controls remain enabled and passed. Durable first-run TRX:
`isolated_results/native-bt-command-fifo/full/native-bluetooth-full.trx`;
SHA-256 `B15495D92E01DB67ABE509A0B18995D99A3BD31AC996C4683DA0273CB4316866`.
Independent code reviews found no concrete blocker in physical claim ownership,
template/state ordering, lock boundaries, framed receipts, or caller recovery.

The first full repeat also passed 5,169/0/11 (5,180 total), with TRX
`isolated_results/native-bt-command-fifo/full-repeat/native-bluetooth-full-repeat.trx`;
SHA-256 `2D22160F32A02AB4855D491644B3AD67ED663B2F4D57609A6E082CE153625FFD`.

Final review added two actual-helper in-flight Clear cases (idle 0x31 and media
0x36). A synthetic native submission calls Clear and admits a new generation
before returning accepted. The tests observe the claimed slot/credit still held,
the old unclaimed tail cancelled, and the late old claim unable to overwrite the
new template. Both pass, with all 32 native slots restored afterward.

Counter-boundary review caught and corrected a draft positive-only generation
rule: wrapping `int.MaxValue` to 1 would make a future PCM frame look stale to
the existing signed-delta comparison. Fourteen additional boundary cases now
preserve the original `int.MaxValue` to `int.MinValue` progression, skip only
zero, retain new PCM until its reset is consumed, and verify negative native
identities/receipts. The production PCM ring itself is unchanged. The targeted
RED run failed 9 cases with 7 positive controls passing; the corrected expanded
group passed all 137 cases. Evidence is under
`isolated_results/dualsense-native-bt-ordering/generation-wrap-red` and
`generation-wrap-final-green`.

The final unfiltered Release x64 suite, including those sixteen additional
cases, passed **5,185/0/11**, total 5,196. Final durable TRX:
`isolated_results/native-bt-command-fifo/final-full/native-bluetooth-final-full.trx`;
SHA-256 `BDE80AA504E560B14EF019B61BC0F1346ECA40F6874E00187DC91C57A3E4253E`.

No physical controller, installed application, driver, profile, or public release
was changed or launched for this validation. Synthetic I/O demonstrates software
ordering and lifetime behavior, not actuator feel or successful GTA V Enhanced
rapid-fire feedback. Hardware gameplay confirmation remains outstanding.
The earlier private D portable and public downloads have not been replaced and
do not contain this new command-ordering fix. The later E launch is recorded below.

## Complete portable E launch

At the user's request, the complete private E candidate was built from clean
source `3d45895593de6608769982a469bd25ad8c636dd6` and launched on September 10,
2026 at 22:28 UTC. Both composition checks and an independent inventory audit
verified all 551 immutable package files, including the self-contained runtime,
Xbox identity, and pinned compatible VIIPER. This is not a public release.

- Directory: `Desktop/DS4Windows-Haptics-Lab-20260910-E/portable/DS4Windows`.
- App SHA-256: `9EDE7163392006ADC14FA5E473FCCA5C726E61EC05B32D6FCC7D2415DB927FB8`.
- Broker SHA-256: `DC2D47B49F94FA827903FD24F18AE0289EBE682F09D6EF67A09EE7A6A8005EB7`.
- Manifest SHA-256: `557DECC7777882DB44CEFD32DBED18A44AB78881A6618C49630D881AA69F78BE`.

The isolated `ds` profile selects DualSense emulation. Existing profiles,
Program Files installations, drivers, and HidHide policy were not changed.
The application log reports the VIIPER backend ready; its visible UI is running
and waiting for a controller. No controller or game-feedback success is claimed
from this launch. The base ZIP remains an intermediate with the released broker;
only the verified final portable directory is the private testing candidate.

## Remaining hardware acceptance

Use a complete portable candidate containing this checkpoint, not the existing
D directory or a loose publish output. With a physical DualSense on Bluetooth
emulating DualSense, compare repeated taps and sustained machine-gun bursts in
GTA V Enhanced, including transitions back to silence and concurrent game audio.
Confirm effect timing/feel and no sticky motor, trigger, or LED state after
release, game exit, Stop, and reconnect. Preserve the existing finite-PCM and
transport-cadence measurements; this software fix does not establish a new safe
Bluetooth report rate or guarantee arbitrary burst throughput.
