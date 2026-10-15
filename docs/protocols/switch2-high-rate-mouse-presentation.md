# Switch 2 high-rate mouse presentation

This contract is pinned to the one-owner, latest-state interpolation design in
`Switch2Connect` commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/controller.py::_collect_interpolation_sources` and
`Controller._interpolation_thread_loop`.

## Scope

DS4Windows' existing mapper remains authoritative. The high-rate presenter is
not a second mapping stack and does not read physical HID reports. It receives
only the latest already-mapped continuous velocity for four Switch 2 sources:

- the established Gyro Mouse output after sensitivity, deadzone, smoothing,
  threshold, ratchet/toggle, inversion, and profile policy;
- verified Joy-Con 2 IR mouse movement;
- orientation-corrected Gyro Mouse Stick Assist; and
- an authored logical-stick direction mapped to Mouse Up, Down, Left, or Right,
  after the ordinary DS4Windows deadzone, sensitivity, vertical scale,
  delta-acceleration, mapped-stick sensitivity, and mouse-acceleration policy.

Wheel events, flick-stick deltas, mouse buttons, keys, macros, profile actions,
absolute mouse actions, and non-stick relative-mouse mappings stay on the
canonical per-report path. They are never held or repeated by the presenter.

## Ownership and timing

One `Switch2RuntimeInputDevice` owns one lazy presenter. A joined Joy-Con pair
is already one logical runtime, so it cannot create competing left/right mouse
workers. Input reports replace four fixed-size source records and wake the
owner; they create no task, timer, queue entry, or warm-path allocation.

Stick Assist, IR, and mapped-stick state are committed as one transactional
batch with one QPC timestamp, presenter lock, and wake per physical report.
If any value is invalid, none of those three source records changes and all
three producers retain their exact per-report fallback for that report. Gyro
remains independently owned by the existing motion callback and mixes into the
same worker.

For mapped-stick mouse, `Mapping` first computes the exact signed canonical
per-report delta. A fixed-size ephemeral frame divides that delta by the same
validated report interval used by the canonical calculation, recovering the
post-policy velocity without reimplementing the mapping law. Only after the
runtime accepts the velocity does Mapping remove that exact contribution from
the report accumulator; every other mouse producer remains there. Invalid or
over-100 ms report intervals, invalid/ambiguous Switch 2 sidecars, disabled
high-rate policy, or refused runtime admission preserve the original
per-report delta exactly, so the optimization cannot swallow or double motion.

While at least one source is active and fresh, the worker targets a one
millisecond cadence and integrates velocity using `Stopwatch` elapsed time.
Fractional counts belong only to that worker. Windows' one-millisecond timer
resolution is acquired only for the active interval and released while idle or
terminal. A scheduler interval above 50 ms uses the source-compatible 15 ms
fallback rather than presenting a large catch-up jump.

Every source expires after 100 ms without replacement. This covers current
20 Hz BLE intervals without allowing a dead producer to leave the cursor
moving. A profile revision change atomically clears all earlier sources before
accepting the new revision.

## Output and lifecycle

`VirtualKBMBase.MoveRelativeMouseImmediate` is the only output seam. The
SendInput implementation uses a by-reference native call and allocates no
managed array. FakerInput combines a pending canonical relative delta with the
scheduler delta and updates the virtual HID report under one write lock, so a
worker sample cannot overwrite ordinary mouse movement or a pending button
state.

Terminal retirement stops source admission, waits for an already-admitted
output call, stops the worker, and only then continues terminal-neutral
publication. A stopped presenter cannot be restarted. Abort-before-publication
uses the same stop boundary. Output-handler replacement exceptions are
contained by the presenter and cannot terminate controller input.

## Profile compatibility

`Switch2HighRateMousePresentation` is stored in the existing profile and shown
as `High-rate mouse presentation (1 kHz)` under `Switch 2 Controls`. It defaults
on for legacy profiles because it changes presentation cadence, not mapping
semantics. Turning it off returns gyro, IR, Stick Assist, and mapped-stick
movement to their prior physical-report-cadence paths on the next input report.

The 1 kHz value is a scheduler target. Hardware-perfect cadence and perceived
latency remain a physical validation gate; unit tests establish arithmetic,
ownership, persistence, allocation, and stop behavior only.
