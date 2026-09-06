# Canonical controller feedback contract v1

This contract is the typed boundary between virtual-controller feedback and a
physical-target translator. It does not describe a USB, Bluetooth, Xbox GIP,
Sony HID, or Nintendo HD-rumble packet. Protocol-specific adapters scale and
encode only after claiming this complete value.

The current DS4Windows change defines and tests the contract and uses an exact
canonical decoder at the existing VIIPER Xbox 360 feedback boundary. That
decoder maps the two body-motor bytes into normalized 16-bit state and projects
them bijectively back into the unchanged legacy physical-rumble path. For a
Switch 2 physical target, the lease runtime and state-lane pump are the live
generation-bound physical-output owner across virtual-game feedback,
profile/macro rumble, test preview, source-preserved native Switch 2 groups, and
DualSense PCM-derived groups.

## Semantic values

The four actuator amplitudes are normalized unsigned 16-bit values:

- `BodyLow`: low-frequency/heavy body actuator;
- `BodyHigh`: high-frequency/light body actuator;
- `LeftTrigger`: left impulse-trigger actuator;
- `RightTrigger`: right impulse-trigger actuator.

The actuator-mask bits retain fixed wire values `BodyLow=0x01`,
`BodyHigh=0x02`, `LeftTrigger=0x04`, and `RightTrigger=0x08`. Every valid v1
frame must carry `All=0x0f`: v1 is a complete four-channel snapshot, never a
patch. A source writes zero for an inactive or unsupported channel. A target
translator then selects the channels it can express. This prevents an older
non-zero channel from surviving a later partial stop.

Source identifiers are fixed as follows:

| Value | Source |
|---:|---|
| 0 | invalid |
| 1 | Xbox One virtual device |
| 2 | Xbox Series virtual device |
| 3 | Xbox 360 virtual device |
| 4 | DualSense virtual device |
| 5 | DualSense Edge virtual device |
| 6 | DualShock 4 virtual device |
| 7 | Switch 2 virtual device |

Command values are `Apply=1`, `Neutral=2`, and `Stop=3`. `Apply` requires at
least one non-zero amplitude. `Neutral` explicitly zeros every actuator while
keeping the current ownership lease. `Stop` explicitly zeros every actuator
and retires the ownership lease. Both require zero in every amplitude.
`Stop` is terminal within an ownership epoch; another apply requires a newer
epoch.

## Fixed little-endian encoding

Version 1 is exactly 72 bytes:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII magic `CFBK` |
| 4 | 2 | version (`1`) |
| 6 | 2 | encoded length (`72`) |
| 8 | 1 | source |
| 9 | 1 | command |
| 10 | 1 | actuator mask |
| 11 | 1 | reserved, zero |
| 12 | 2 | body-low amplitude |
| 14 | 2 | body-high amplitude |
| 16 | 2 | left-trigger amplitude |
| 18 | 2 | right-trigger amplitude |
| 20 | 4 | reserved, zero |
| 24 | 8 | source-local sequence |
| 32 | 8 | controller-device lifecycle generation |
| 40 | 8 | feedback-transport generation |
| 48 | 8 | ownership epoch |
| 56 | 8 | monotonic timestamp, microseconds |
| 64 | 8 | time to live, microseconds |

Sequence and all three lifecycle fences are non-zero. TTL is in the inclusive
range 1..250,000 microseconds; producers must refresh an unchanged live effect
before that lease expires. This 250 ms protocol ceiling bounds actuator state
after a producer failure instead of trusting an effectively infinite lease.
Timestamp and TTL
use clock domain `windows-qpc-host-v1`: the system-wide Windows
`QueryPerformanceCounter` value converted as
`floor(counter * 1,000,000 / QueryPerformanceFrequency)`. The QPC origin and
frequency are common to processes on one host; a process-relative stopwatch
origin, wall clock, or raw unconverted QPC tick is not compatible. The
conversion must avoid intermediate integer overflow and truncate
sub-microsecond precision.

Expiry is inclusive at `timestamp + TTL`. A timestamp up to 5,000 microseconds
ahead of the consumer sample is tolerated as a producer/consumer sampling
race. A timestamp farther in the future is invalid for application and follows
the expiry-release path; this prevents a malformed maximum timestamp from
remaining fresh indefinitely. `Stop` is freshness-bounded too, so a delayed
command cannot cross a device, transport, or ownership replacement.

## Ownership and hot-path rules

`ControllerFeedbackMailbox` owns one replaceable complete snapshot. It admits
new state lexicographically by device generation, transport generation,
ownership epoch, then source-local sequence. A source change requires a new
ownership epoch. An expired value remains the ordering watermark, preventing
an older frame from becoming valid merely because its newer successor aged
out.

Publication, latest-state read, state-transition claim, and span serialization
copy only value types under one short monitor and allocate no managed memory
after warmup. A claim cursor tracks applied and released revisions separately:

- each new live revision yields `Frame` once;
- when that same revision expires, it yields `Release` once even if it was
  already applied;
- a revision first observed expired or more than 5,000 microseconds in the
  future yields `Release` once without exposing its amplitudes;
- subsequent claims of that state yield `None`.

`Release` obligates the translator to issue a local all-actuator zero/stop. It
is not merely “no frame available.” `TryReadFresh` is diagnostic and cannot
drive a physical output lease because it has no one-shot expiry transition.
Translators, callbacks, waits, logging, socket writes, release writes, and
physical HID I/O must remain outside the mailbox monitor.

## Arbitration runtime and state-lane pump

`ControllerFeedbackRuntime` adds four fixed publisher slots with deterministic
priority: test/preview, native game, audio-derived state, then profile effects.
It selects only the newest device/transport generation, fences ownership epoch
and source, admits one sole writer generation, and turns replacement or TTL
expiry into one logical `Stop` delivery epoch. A failed physical stop is
retryable with the same delivery value and epoch; the per-attempt claim token
changes. A successful stop advances to the next eligible owner only once.

Sequence wrap cannot revive an ownership epoch. An epoch must advance before a
source can restart at sequence one. A source-only renewal advances sequence,
timestamp, and TTL without scheduling another physical write when command and
all four actuator values are unchanged. This makes bounded leases practical
without periodic motor-output churn.

`ControllerFeedbackStateLanePump` is the scheduler-independent, per-physical-
device lifetime owner for the latest-wins state lane only. It owns exactly one
`ControllerFeedbackRuntime`, one generation-bound writer lease, and one
interlocked callback gate. Origins do not construct pumps. Instead, the owner
creates at most one nested state lane for each fixed origin; every such lane
publishes into the owner's shared four-slot runtime. A lane owns only its
source, ordering watermark, state, TTL, and renewal policy. It has no claim,
admission, sink, writer, or retirement method. This prevents two origin-local
pumps from bypassing global priority and invoking one physical sink as
independent "sole" writers.

Ordered stop/ownership transitions remain runtime events. Source-preserved PCM,
its bounded side-local adaptive-trigger approximation, and native HD-rumble
groups are staged against the same claimed canonical delivery epoch rather
than flattened into four legacy amplitudes. Each successfully staged rich
revision explicitly refreshes the current presentation after publication, so
two different oscillator groups cannot be mistaken for an unchanged lease
merely because their canonical nonzero marker matches. Ordinary canonical
lease renewals remain deduplicated. The owner and its lanes:

- accept complete four-actuator `Apply` or lease-retaining `Neutral` state;
- arbitrate every registered origin before the one owner invokes a sink;
- renew each source only at its configured interval strictly below TTL;
- request stop instead of resurrecting state when a renewal misses expiry;
- invoke a transport sink after all internal monitors have been released;
- reject concurrent/reentrant owner pumps with one interlocked writer gate;
- retry one logical neutral idempotently; and
- withdraw a reusable origin with its exact Stop before advancing that lane's
  ownership epoch and exposing a lower-priority successor; and
- terminalize every registered lane before bounded owner retirement.

The owner deliberately owns no thread, timer, wait handle, physical device, or
protocol encoder. The live Switch 2 integration places exactly one owner on the
authenticated Bluetooth or owned-USB physical lifetime. Xbox One broker wire,
locally decoded Xbox 360/DS4/Sony motor state, DS4Windows profile/macro and
preview effects, native Switch 2 oscillator groups, and DualSense PCM-derived
three-slice dual-band groups (including supported trigger-program overlays)
enter that same owner and sole physical writer.
Session retirement or reusable-lane withdrawal publishes Stop and completes
`TryStopAndRetire` before the device or transport generation is replaced.
Creating one owner per virtual source remains invalid.

### Terminal broker commands bypass effect timing

For Switch 2 USB and Bluetooth, a valid exact-lifetime broker `Stop` enters the
existing canonical owner immediately, even when the profile selects a positive
rumble delay or contains invalid effect tuning. It retains the broker's sequence,
timestamp and TTL; it is never rewritten as a newly timed game effect. Once
canonical admission records terminal Stop, queued effects and impulse-release
presentation are canceled even if the physical neutral still requires a retry.
The existing owner remains responsible for transport delivery and retirement;
admission alone is not physical neutralization evidence.

Stale, foreign and post-terminal broker frames are rejected before changing a
delay queue or current presentation settings. This precheck only observes the
last canonical admission; it does not mint or consume broker sequence numbers.
The canonical runtime revalidates ordering at actual publication. Ordinary
`Apply`/`Neutral` presentation timing and release envelopes are unchanged.

### Live Xbox output and impulse policy (September 5 source follow-up)

Changing the profile output master or Xbox impulse-to-HD checkbox now wakes the
existing Xbox feedback-delivery worker, including when the game sends no new
CFBK packet. The Sony-only control worker is not used. Requests carry the exact
session, stream, slot and publication revision. Rapid off/on requests intersect
only for that same publication; stale requests cannot overtake a newer packet,
stream or session. The input/profile callback captures this revision without
taking a lock held across physical output. No input pacing or new worker was added.

Under the existing session and physical-owner fences, the worker discards stale
delayed effects and release presentation, then stages a restriction for the
accepted NativeGame frame in the existing HD-rumble sink. Disabling impulse
conversion preserves the body channels. Disabling all output scales both sides
to zero. This is a frame-bound rendering change, not a canonical publication:
source sequence, timestamp, TTL, ownership and arbitration remain unchanged.
Zero-amplitude output retains legal compatibility carrier codes and is not a
terminal all-zero Stop report. Re-enabling does not resurrect suppressed
components of that frame; a fresh broker packet can start a new effect.

The sink never changes an unresolved physical submission. A refresh may need
two bounded pump passes: complete/retry the retained exact bytes, then present
the restriction. Its receipt distinguishes those results. Failed refreshes stay
on the same worker with a 100 ms idle retry wait; successful idle work returns
to an indefinite wait. No synthetic broker payload, correlation or ACK is sent.
Expired effects use the existing runtime Stop rather than a renewed frame.

These changes apply to Switch 2 Xbox output/impulse enablement. Frequency,
strength and delay tuning still update on subsequent game feedback. Other
physical-controller families retain their existing paths. The current portable
b56 payload does not include this source follow-up; physical/UI acceptance of
this newer build remains pending.

The pure runtime still has no controller registry or physical identity; those
invariants are enforced by `Switch2BluetoothFeedbackLifetime`,
`Switch2ProUsbOwnedFeedbackActivationLifetime`, and
`Switch2RuntimeInputDevice`. Other physical-controller families retain their
existing device-specific feedback paths and do not instantiate a competing
canonical owner.

## Xbox 360 canonical adapter and provenance

The local VIIPER wire authority is
`device/xbox360/inputstate.go::XRumbleState` at commit
`241d3294f94cd64df09aa254cc32b6275ea8f567`: server-to-client feedback is
exactly two bytes, left/low/heavy then right/high/light. DS4Windows now rejects
any other length at this decode boundary. Each byte is normalized with
`value * 257`; projection uses the inverse rounded division, which is exact for
all 256 inputs. Trigger channels are explicitly zero. No PadForge, HIDMaestro,
Switch2Connect, Linux, or other external source code was copied for this
adapter; it is an independent implementation of the versioned in-workspace
wire contract.

## Adversarial verification status

Focused tests cover stale device/transport generations and ownership epochs,
fixed priority independent of source sequence, sequence wrap, TTL expiry,
lease-only renewal, neutral-versus-stop behavior, one logical stop with
idempotent retry, bounded retirement failure, multi-origin arbitration under
one runtime/writer, rejection of duplicate origin lanes, owner-wide stop before
retirement, callback reentrancy, concurrent sole-writer exclusion, exact Xbox
360 round trips, and zero managed allocations after warm-up for publish/claim/
admit/deliver/complete cycles. These are replay and concurrency tests, not
hardware haptic-onset verification.
