# Switch 2 gyro-mouse Stick Assist profile lane

Status: implemented in the ordinary DS4Windows profile/mouse path and covered
by deterministic software tests. No live-controller latency claim is made by
those tests.

## Source audit

Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28` advertises **Stick Assist** and
selects the intended stick in `src/controller.py`: Pro and joined Joy-Con pairs
use the right stick, while a standalone Joy-Con uses its own stick with its
horizontal/vertical presentation transform. At that pinned revision, however,
the selected `sx` and `sy` values are never added to `target_vx` or
`target_vy`. The advertised control is therefore dead in the audited source.

DS4Windows implements the advertised behavior rather than copying that dead
control flow. The profile setting is `Gyro mouse stick assist`, from 0.0
through 10.0. Zero is the existing-profile default and disables the feature.

## Ownership and projection

The established `Mouse` gyro-ratchet/toggle evaluator publishes its decision
to `Mapping.MapCustom` exactly once for the same serialized physical report.
The mapper does not re-evaluate a toggle edge. A later report without a fresh
SixAxis callback cannot inherit the preceding report's activation state.

While Gyro Mouse is actively presenting, Stick Assist selects:

- the logical right stick for Switch 2 Pro;
- the logical right stick for a joined Joy-Con 2 pair; or
- the logical left stick for a standalone Joy-Con 2, because the existing
  mini-controller projection places either physical side's orientation-
  corrected stick on that logical axis.

The already-deadzoned profile state is normalized and integrated using the
source report's QPC interval. One sensitivity level is 48 pixels/second at
full deflection, matching DS4Windows' established stick-mouse velocity unit.
The result is added beside other profile mouse producers before the existing
fractional-remainder rounding and `outputKBMHandler` boundary. The physical
stick is not consumed or rewritten, so it remains available to the selected
virtual pad and ordinary mappings.

There is no interpolation loop, synthetic controller report, additional
thread, queue, timer, or mouse injector.

## Lifetime and latency fences

State is per DS4Windows controller slot and is invalidated by:

- gyro-mouse deactivation or zero/invalid sensitivity;
- profile revision;
- Pro/Joy-Con source kind;
- device, transport, or joined-pair generation;
- QPC frequency changes or out-of-order timestamps; and
- an interval greater than 50 ms.

The first report after a fence establishes a baseline without moving the
pointer. Duplicate timestamps cannot emit duplicate movement. A long host or
transport stall is discarded rather than converted into a large cursor jump;
the next fresh interval resumes normally. The warm path is fixed-size and
allocation-free.

## Verification

`Switch2StickAssistProfileLaneTests` covers Pro, joined, standalone-left, and
standalone-right selection, axis signs, QPC integration, gyro deactivation,
profile changes, long gaps, duplicate timestamps, invalid sensitivity, and
zero managed allocations over 20,000 warm calls.

`Switch2ProfileMappingSchemaTests` covers the opt-in default, profile round
trip, and invalid-value normalization. Full Switch 2 and DS4Windows regression
suites remain required after changes to the canonical mapper.
