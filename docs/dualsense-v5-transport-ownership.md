# DualSense V5 transport ownership and lock order

This document describes the runtime ownership introduced for the DualSense
and DualSense Edge VIIPER V5 path. It is intentionally local to the code that
enforces the rules; it is not a general promise about legacy output devices.

## Scheduling planes

| Work | Semantics | Owner | Producer behavior |
| --- | --- | --- | --- |
| Final mapped control edge | Ordered, capacity 64 | `ViiperInputScheduler` and the V5 writer | Copy one complete `ViiperMappedInputState`, classify, signal |
| Stick, motion, touch coordinates, in-epoch trigger movement | One replaceable latest snapshot | `ViiperInputScheduler` | Replace the unclaimed continuous slot, signal |
| Failed ordered input | One recovery item ahead of later edges | `ViiperInputScheduler` | Writer returns the claimed logical item; producer never retries I/O |
| Microphone PCM/Opus | Bounded frame rings, complete frame boundaries | V5 writer | Copy into a preallocated slot, record deadline, signal |
| Physical rumble, triggers, lightbar, LEDs and microphone control | Latest validity-aware state | Physical DualSense output worker | Mark dirty and signal |
| Native game output | Bounded ordered command ring | Physical DualSense output worker | Copy the complete command and signal |
| Controller clock/media-buffer observations | Replaceable latest mailbox | Bluetooth observation worker | Store value, QPC timestamp and generation, signal |
| Physical Bluetooth microphone | Bounded 16-frame ordered ring | Bluetooth microphone worker | Validate, fixed-size copy, sequence/QPC/generation, signal |
| Report-boundary device configuration commands | Existing ordered action queue | Device-command worker | Input callback signals after completing the report; owner claims under the collection lock and invokes after releasing it |
| Battery and charging notifications | Coalesced atomic status bits | Device-command worker | Update mapped battery fields, then signal only after virtual publication |
| OSC monitoring | One replaceable snapshot per controller | OSC monitoring worker | Fixed-state copy after virtual publication, signal |
| UI/report diagnostics on the DualSense-to-VIIPER path | One coalescing snapshot per controller | Report diagnostics worker | Store primitives/references after virtual publication, signal |

Input transition entries always contain the complete final state. The same
state drives classification and 33-byte packet serialization, including the
final mapped L2/R2 analog values and coupled digital bits. A trigger epoch
remembers its highest received and highest successfully transported value.
An uncommitted writer claim is not considered presented; if its write fails,
an epoch peak is retried in ordered position before a queued release.
Each peak records the physical receive ordinal of its complete snapshot. A
shared unclaimed initial press can absorb upgrades for both L2 and R2 only when
both maxima came from that same received snapshot. Independently timed maxima
are emitted as chronological complete states, preventing a synthetic
cross-time `(L2 peak, R2 peak)` combination that never physically existed.

Ring overflow is counted and is not expected in normal or loaded operation.
If it occurs, the scheduler retains the newest rejected complete state as a
replaceable recovery snapshot so the virtual device converges after the
ordered ring drains rather than remaining stuck in an old pressed state.
The ring preallocates fixed value-owned state envelopes rather than 64 separate
managed `byte[33]` objects. Claim copies one immutable envelope into the sole
writer's preallocated 33-byte serializer slot. This is the same slot-ownership
contract as prebuilt packet slots, while avoiding both per-slot object headers
and any producer mutation of claimed bytes; warmed build/classify/publish/claim
and serialization remain allocation-free.

## V5 framed stream

`ViiperOutDevice` has one framed-stream writer owner. Only that owner assigns
the shared V5 sequence, updates the table-driven IEEE CRC, builds the complete
frame in its reusable buffer, and calls the socket write operation. Input and
microphone producers never call `WriteFrame`. The production owner uses an
owner-only build/send entry point and therefore takes no compatibility frame
ownership monitor. It completes framing and CRC before taking only the narrow
stream send lock for one contiguous socket write. The locked allocating API is
retained solely for non-hot compatibility callers and tests.

The arbitration order is:

1. oldest ordered input (including retry);
2. latest continuous input;
3. due microphone work;
4. remaining media work.

An already-due microphone frame is serviced after the bounded input burst.
An explicit lower input-rate cap uses an absolute monotonic cursor. Its wait is
interruptible by due media work; immediate, off and zero configurations do not
create a transport clock. VIIPER's interrupt endpoint remains the default
one-millisecond presentation clock.

The microphone-interface event extension is opt-in by device alias. Current
clients first request `dualsensecombinedaudioduplexv5events`,
`dualsenseaudioonlyduplexv5events`, or
`dualsenseedgecombinedaudioduplexv5events`. A precisely typed unknown-device
HTTP 400 response retries the legacy alias and enables the narrow
`bus/{busId}/{devId}/microphone-interface` compatibility query. Legacy aliases
never receive the otherwise-unknown frame type `0x85`. Its fixed payload is an
active byte followed by a little-endian 64-bit stream generation.

## Physical controller ownership

The physical input thread may parse, map, build final fixed state, classify,
publish into bounded storage and signal workers. It does not perform physical
HID writes, V5 socket writes, audio-pacer calls, microphone subscriber calls,
OSC monitoring writes, broad status queries, filesystem profile diagnostics,
or tray/logger callbacks on the DualSense-to-VIIPER path.

One physical output worker owns all ordinary USB/Bluetooth HID output. The
producer-facing `DualSensePhysicalOutputStateMailbox` publishes one complete
value snapshot containing rumble and preview generation, profile lightbar,
adaptive triggers, volume/routing, haptic validity, microphone/mute state,
player LEDs, and native-game ownership state. Compound `SetHapticState`
publishes lightbar and both motors in one version, and individual motor setters
perform their read-modify-write under the same mailbox monitor. Only the
physical owner claims that value and mutates its private `currentHap`
compositor copy. Native VIIPER output is built into a fixed dispatch-owner
scratch buffer, including the combined-carrier path.

The dedicated lifecycle worker first prevents new-generation work, then retires
the output, observation and microphone workers with definitive join barriers.
Only after every old-generation worker is gone does it write the final neutral
and microphone-release state. Disconnect/removal notification follows that
final write on the lifecycle owner; the read/CRC failure branch only posts the
fixed failure kind, OS error and QPC timestamp. Failure formatting/logging and
idle Bluetooth disconnect also run on the lifecycle owner. A replacement
generation cannot reuse worker buffers while an old
subscriber, device command or audio-pacer call remains active.

## Lock order and I/O boundaries

Hot paths avoid nesting except for the explicitly documented, one-way atomic
Bluetooth combined-report admission order below. Every other producer takes at
most one short subsystem lock, copies or claims its item, releases that lock,
and only then signals or invokes the owner.

- `ViiperInputScheduler.stateLock` protects scheduler state and packet-slot
  ownership only. It is never held during rate waits, framing, CRC or network
  I/O.
- Microphone queue locks protect ring indices and fixed copies only. Decode,
  callback and socket work occur after claim.
- The sole V5 production writer mutates its reusable frame buffer and sequence
  without a frame-ownership monitor. The stream send lock is entered only
  after the complete frame exists; no input, microphone or scheduler lock is
  held. A compatibility frame-ownership monitor exists only for non-production
  callers and is never mixed with the owner-only path in one stream generation.
- Stream recovery uses an atomic single-owner election and one reusable
  completion event. Concurrent failed consumers observe the elected owner's
  result for that generation instead of reopening twice. Backoff, logging,
  API/controller creation, transport close/open and worker startup happen with
  no recovery or generation lock held. A short callback-admission monitor
  contains only replacement-stream/generation publication. It then closes
  admission for the retired generation and waits on a callback refcount event
  after releasing the monitor. Physical output, subscriber callbacks and
  logging therefore never execute under a generation lease. The feedback
  buffer is cleared afterward under its own independent lock before the new
  reader starts. Disconnect waits for the elected recovery owner to retire
  before a replacement lifecycle resets the reusable gate.
- Physical output, observation, microphone and lifecycle locks are independent.
  HID I/O occurs only on the output/lifecycle owner after queue locks have been
  released. Combined-control sequence reservation and fixed-ring admission use
  one explicit one-way order:
  `bluetoothCombinedTransportWriteLock` -> `bluetoothAudioPacerLock` (reference
  claim only) -> pacer `stateLock` (fixed copy and bounded FIFO enqueue only).
  There is no reverse edge: the pacer has no device callback/reference, its
  sender releases `stateLock` before pipe I/O, and lifecycle retirement releases
  `bluetoothAudioPacerLock` before waits, stop or disposal. The exact control
  report is therefore enqueued before a later speaker report can reserve the
  next physical sequence. The completion consumer retains only its fixed,
  report-ID/epoch-bound token and pacer active-operation claim after releasing
  the combined admission lock. Timeout, HID completion and lifecycle clearing
  occur outside every state/generation lock; accepted admission is never rolled
  back because a following report may already own the next sequence. Ordinary
  latest-state template publication is likewise admitted under the same short
  combined boundary but never waits there. Starting/replacing
  physical workers uses an atomic lifecycle election; joins, final output and
  thread creation occur outside every lifecycle monitor. Before a replacement
  lifecycle thread is published, the elected starter drains any signal left by
  a stop-before-first-start request; a later request is distinguished by its
  monotonic external-request version and is re-signaled after publication. The
  Bluetooth transport-recovery retry captures the physical-output generation,
  uses one reusable interruptible wait, and owns an idle barrier. Stop closes
  admission and waits for that owner outside its short admission monitor before
  a replacement generation can clear the transport-stopping flag.
- Physical microphone attach/detach/retry records an intent epoch, snapshots
  source identity under its one short lock, and performs the physical call
  afterward. A completion made stale by a concurrent attach/detach repairs the
  same physical source to the newest desired state. Compressed frame slots are
  tagged with source generation so a callback already admitted at detach
  cannot feed a replacement source.
- Native-output trace locks protect only report/process-reference snapshots.
  Hex formatting, UI logging, process liveness queries and `Process.Dispose`
  happen after the trace lock is released.
- OSC and report-diagnostic locks protect one pending snapshot per controller.
  UDP, JSON/filesystem, logging, tray and callback work occurs after claim.
- Shutdown publishes a generation/stop boundary first, signals every owner,
  joins the old generation, and only then performs final physical output.

Callbacks, logs, JSON/filesystem work, waits and all network/physical HID I/O
therefore execute with no scheduler, queue, state, collection or generation
lock held.

## Diagnostics

Set `DS4WINDOWS_VIIPER_LATENCY_DIAGNOSTICS=1` to enable fixed-bucket aggregate
histograms. The writer reports no more often than its existing 30-second health
interval and formats snapshots after locks are released. Snapshots contain
count, p50, p95, p99, p99.9 and observed maximum for:

- mapped state ready to scheduler publication;
- publication to writer claim;
- claim to socket-write start;
- socket-write duration;
- physical HID read completion to report callback;
- physical output queue age and HID-write duration;
- physical microphone extraction to subscriber dispatch.

Samples above the final finite bucket use the observed maximum for a selected
tail quantile instead of being reported as the finite bucket boundary.
