# DualSense Bluetooth feedback regression after RC4.5.6

Status: source fixes reviewed and full test suite green; physical game acceptance pending.
This is not a release or a claim that subjective haptics have been verified.

## Report and differential

The user reports substantially quieter/incorrect native game haptics and missing
adaptive triggers on a physical Bluetooth DualSense in multiple games after
upgrading to RC4.5.6. Trigger Lab also failed. RC4.5.5 is the comparison baseline;
the work does not change gain, PCM encoding, the audio clock, or Nintendo rumble.

RC4.5.6 replaced mutable native command coalescing with a bounded, ordered FIFO,
but its steady V5 media path could consume only one command per media packet.
The actual production media scheduler supplies 150 packets in 1.6 seconds
(93.75 Hz). Even a 100 Hz command source therefore accumulates ten commands over
that interval. A full native lane can also backpressure new media at the device
boundary. Returning to lossy coalescing would reintroduce the rapid pulse/stop and
trigger A/B/A loss that the FIFO was intended to fix.

A second reproducible circular wait requires only one to seven queued startup
media packets: native commands cannot piggyback until eight packets prime the
stream, while exhausted native credits prevent the parent admitting those
missing packets. A pending microphone-disable boundary adds a related case
because its two FE media packets must precede the 0x32 mode command.

The separate local Trigger Lab omission is **not new to RC4.5.6**: source in both
4.5.5 and 4.5.6 stops merging local triggers into the native-owned cache, and the
active speaker template path publishes only local rumble/audio updates. It
explains the observed local Weapon request not reaching the helper, but is not
independent proof of the release-wide game regression.

## Live evidence, with limits

Read-only, non-suspending process snapshots were taken from the installed
RC4.5.6 mapper and its matching Bluetooth helper. These are best-effort live heap
observations, not atomic dumps; source and synthetic-I/O tests corroborate them.
Private raw snapshots remain outside source control.

- The mapper's claimed physical snapshot and mailbox both requested a left
  Weapon effect (mode 0x25, start 68, force 6); the helper still held Off (0x05).
- Cumulative counters recorded a full four-command broker dispatch queue with
  a maximum queue age of approximately 1.01 seconds and a full 64-frame realtime
  haptics queue with a maximum age of approximately 2.59 seconds. These are
  historical maxima, not measurements of current game latency or proof of a
  specific lost waveform.
- Native credits and queues were empty at inspection, media writes continued,
  and no current transport fault was present. The reproduced startup deadlock
  was therefore not active in that snapshot.

## Changes

- Native commands may use the existing 0x31 control lane between steady media
  deadlines, retaining FIFO order and the aggregate 5 ms command spacing.
  Startup/due audio has priority. Control wakeups do not advance the media clock
  or consume a PCM frame. The media wait can wake for newly arrived commands,
  microphone transitions and lifecycle changes.
- A partial, unprimed media queue no longer reserves an impossible control
  fairness credit. Idle/local/native commands can progress while it is partial.
- During healthy native-capacity pressure, independent speaker packets may
  complete the prime using only the last successfully admitted native state's
  quiescent fields. Exact pacer identity and generation are checked. All native
  validity strobes are consumed; a retained future command cannot leak through
  these media packets. Independent local audio controls are retained.
- The real microphone-disable order (two FE media packets, then 0x32, then native
  commands) is preserved; the eight-packet media prime is not weakened.
  The final locked media dequeue also rechecks a newly ready microphone
  transition, so a change arriving during the wait cannot be overtaken by audio
  using its new mode.
- The physical owner tracks local trigger changes per side and sends only those
  changed sides through the explicit helper command lane. Unrelated volume/LED
  edits and unchanged default Off do not replay stale triggers. Rejected local
  admissions remain pending for retry.

## Validation ledger

- Original partial-prime reproduction: failing actual parent/device/helper
  tests with positive controls, `isolated_results/rc456-native-prime-liveness/partial-prime-red.trx`.
- Local Trigger Lab: two observed-path cases failed before the change and passed
  afterward, followed by 125 related passing tests.
- Combined focused lane: **232 passed, zero failed, zero skipped**,
  `isolated_results/rc456-native-prime-liveness/combined-focused-green.trx`.
  Includes actual writer-boundary byte/order tests, finite PCM including silence,
  full native capacity, Busy retry, Clear/reset, microphone order, future-state
  exclusion, newer local triggers and replacement-pacer rejection.
- Four intermediate focused failures exposed fixtures counting all writes as
  media. Their waits now count media and command completion separately; exact
  media payload, native command and ordering assertions remain required.
- Independent ownership/lock/lifecycle review found no blocker in the combined
  admitted-native media and explicit local-trigger changes.
- An isolated original-release build at `08cbbc7b32a7c87d0867f619af61017f83b8c0d8`
  ran the exact new 32-command burst test. Media/PCM/trigger integrity checks
  passed, then the independent-control-lane assertion failed as expected. This
  is an actual helper differential, not a missing-member or fixture failure.
- First unfiltered Release x64 run: **5,385 passed, one failed, 11 gated skips**.
  The new sustained 100 Hz case timed out waiting for native credit; the 200 Hz
  case passed. The failure is retained at
  `isolated_results/rc456-bt-regression-full/rc456-bt-regression-full.trx` and is
  not excluded. Allocation assertions passed.
- Investigation reproduced a draft-scheduler livelock: both media and an older
  control deadline were overdue. Due-media priority rejected standalone control,
  but wake selection kept returning to the older control deadline. Both worker
  threads were alive, native storage was full, and the ACK queue was empty.
  Wake selection now chooses overdue media before considering a control slot.
  Three deterministic tests cover future, equal and overdue media deadlines.
- The corrected combined cadence/prime/ordering lane passed **86/0**, including
  19 new cadence/boundary cases. The paced 100/200 Hz cases then passed two more
  independent repetitions (four passes). Every trigger transition and finite
  PCM generation, including zero boundaries, is checked. These are synthetic
  physical-I/O tests, not Bluetooth link or game-to-controller latency measures.
- Final rebuilt unfiltered Release x64 suite: **5,394 passed, zero failed,
  11 gated skips**, 5,405 total. Strict allocation checks remained enabled.
  `isolated_results/rc456-bt-regression-final-full/rc456-bt-regression-final-full.trx`,
  SHA-256 `CD3D78D3EB046F44A3232986F8044FD10FCC3FDB6576C40EB20FE803F43302AC`.
- No additional input pacing or audio buffering is introduced; existing
  transport/media pacing remains. This does not promise zero end-to-end latency
  or unbounded native command throughput.

No installed application, driver, saved profile or public release has been
replaced during this source audit. Game feel and actual Bluetooth trigger
behavior must still be checked on the completed private candidate.
