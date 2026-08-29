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

The actuator mask has fixed values `BodyLow=0x01`, `BodyHigh=0x02`,
`LeftTrigger=0x04`, and `RightTrigger=0x08`. It declares which source channels
are meaningful. Values outside that mask must be zero. Translators intersect
the mask with physical-target capabilities rather than inferring capabilities
from a report length.

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
least one non-zero in-mask amplitude. `Neutral` explicitly zeros the selected
actuators while keeping the current ownership lease. `Stop` explicitly zeros
them and retires the ownership lease. Both require zero in every amplitude.
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
use monotonic microseconds rather than a process-specific stopwatch frequency.
Expiry is inclusive at `timestamp + TTL`; consumers must not apply an expired
frame. `Stop` is also freshness-bounded so a delayed command cannot cross a
device, transport, or ownership replacement.

## Ownership and hot-path rules

`ControllerFeedbackMailbox` owns one replaceable complete snapshot. It admits
new state lexicographically by device generation, transport generation,
ownership epoch, then source-local sequence. A source change requires a new
ownership epoch. An expired value remains the ordering watermark, preventing
an older frame from becoming valid merely because its newer successor aged
out.

Publication, latest-state read, fresh-state claim, and span serialization copy
only value types under one short monitor and allocate no managed memory after
warmup. Translators, callbacks, waits, logging, socket writes, and physical HID
I/O must remain outside that monitor.
