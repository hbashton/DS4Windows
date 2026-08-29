# Canonical controller feedback contract v1

This contract is the typed boundary between virtual-controller feedback and a
physical-target translator. It does not describe a USB, Bluetooth, Xbox GIP,
Sony HID, or Nintendo HD-rumble packet. Protocol-specific adapters scale and
encode only after claiming this complete value.

The current DS4Windows change defines and tests the contract but deliberately
does not wire it into an existing output path. Xbox 360 feedback, VIIPER
DualSense native output, physical DualSense ownership, and existing rumble
handlers therefore retain their current behavior.

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

Sequence, all three lifecycle fences, and TTL are non-zero. Timestamp and TTL
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
