# Joy-Con 2 Bluetooth mouse and motion startup — 2026-09-06

## Failure and evidence

The user reported both desk mouse and gyro mouse failing in portable b83.
The runtime log shows a connected joined Joy-Con pair using `1PASSTHRU`.
The saved profile enables optical mouse, both sources, sensitivity 4. Its
later gyro configuration is Mouse output, hold ZL (`SATriggers=5`, turns on,
not toggle), with DJG enabled and L/R selecting the leading side. Therefore
the latest gyro report is not explained by the output mode being Controls.
Earlier b83 observations of Controls are historical, not current evidence.

The production Bluetooth lease prepared command responses, calibration,
LEDs, and Common05 input, but never selected/enabled the mouse/motion
sensors. This is a confirmed implementation omission and a candidate
hardware cause, not yet an end-to-end hardware success claim.

## Reference and implementation

Reference: GPL-3.0 [Switch2Connect controller.py at
61ac6642ce12fe7217e38a860b14863b18ca7e28](https://github.com/TommyWabg/Switch2Connect/blob/61ac6642ce12fe7217e38a860b14863b18ca7e28/src/controller.py),
SW2 startup (approximately 2351–2377), `write_command`, and `enableFeatures`
(approximately 2666–2709). The local pinned source and license were inspected.

The new closed codec emits only these volatile Bluetooth requests:

| Operation | Bytes |
| --- | --- |
| Select motion, optical mouse, magnetometer | `0C 91 01 02 00 04 00 00 94 00 00 00` |
| Enable selected sensors | `0C 91 01 04 00 04 00 00 94 00 00 00` |

This follows the donor's feature helper, not its unrelated association,
firmware, or device-specific startup commands. The donor explicitly warns
against replacing 0x94 with 0xFF because extra fields can cause phantom
trigger input. No USB mask or Pro-controller startup was changed.

Both exchanges use the existing sole persistent command/response owner,
before Common05 input publication, only for duplex Bluetooth Joy-Con 2
leases. The sequence retains ownership between steps. Responses require
the evidenced command ID, minimum eight-byte length and success status 1;
no undocumented subcommand echo is assumed. Rejection/cancellation fences
the channel and prevents a later enable/LED/calibration command. Retirement
drains admitted writes before endpoint disposal. Production startup is
bounded by the existing open deadline and a two-second sensor-operation
deadline. Failures identify sensor initialization separately from notification
subscription failure, including the command failure category.

## Bounded diagnostics

Portable-only `DS4WINDOWS_SWITCH2_MOUSE_TRACE=1` enables fixed-size summaries
at most once per two seconds, up to 30 windows per controller registration.
They show optical coordinate changes and latest surface readings per side,
raw gyro peaks, current projected motion, aiming output, held ZL/L/R, and
whether the custom mapper ran on the sampled report. These are observations,
not proof that the OS/game consumed cursor output.

The existing generation-owned diagnostics mailbox carries these summaries;
formatting and logging remain on its background worker. No new input queue,
polling worker, per-report allocation, mouse injection or permanent tracing
is introduced. The default launch leaves tracing disabled.

## Validation and delivery boundary

The targeted Bluetooth suite passed 83 tests after its old LED-only test
responder was extended to acknowledge and validate the new sensor requests.
The initial six transition-test failures were traced to that outdated fake,
not ignored or removed. The full suite then passed 3,846 tests with zero
failures and three opt-in audio skips, including allocation assertions.
The final full run including motion tracing passed 3,847 tests, zero failures,
and three opt-in audio skips (`full-b84-mouse-motion.trx`).

New coverage includes exact request bytes, both physical sides, subscription
ordering, failed selection/enable, unrelated responses, serialization against
LED/calibration, cancellation, retirement while a write is in flight, bounded
trace windows, reconnect baselines, allocation-free sampling, gyro peak
overflow safety, and diagnostics coalescing without replay.

Portable b84 was launched separately on the Desktop at 17:36:39 local, after
the user closed both previous apps. VIIPER is unchanged. All copied profile
file hashes matched the saved b83 originals. Program Files, installed drivers,
bonds in the original runtime, and startup tasks were not modified.

## Physical observations (17:37–17:38 local)

Both real Bluetooth Joy-Con 2 controllers acknowledged the sensor sequence:
left at 17:37:29 and right at 17:37:36. The left initially activated standalone,
then both formed the joined controller. The trace recorded optical coordinate
changes from both sides, including a joined two-second window with 49 left
and 119 right changes and surface distances of 144/143 with roughness
3120/3171 (inside the profile's Strict thresholds).

Raw gyro values from both sides and nonzero projected yaw/pitch reached the
report mapper. Later samples recorded ZL and L held; e.g. 17:38:01 showed
Mouse mode, mapper run, ZL+L held, and projected yaw -104.06/pitch -7.81.
This establishes real sensor responses and delivery into the canonical
profile pipeline. It does not by itself prove OS cursor/game consumption,
button-release stopping behavior, or complete DJG parity. User confirmation
of optical and gyro cursor movement is still pending. The trace is bounded
and does not continue producing per-report logs.
