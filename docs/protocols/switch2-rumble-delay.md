# Switch 2 game-rumble delay

## Source audit

The compatibility target is Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`:

- `README.md` documents an optional millisecond delay for synchronizing
  vibration with game audio;
- `src/config.py` persists `rumble_delay_ms` with a legacy default of zero;
- `src/gui.py` exposes the value in its vibration controls; and
- `src/virtual_controller.py` applies the delay to traditional, Xbox, and
  DualSense feedback callbacks.

The donor starts an independent `threading.Timer` for each delayed callback.
Its Xbox callback has an additional generation check, but the traditional and
DualSense callbacks do not. DS4Windows implements the same user-visible delay
at its canonical Switch 2 feedback-session boundary so every supported virtual
pad follows one lifecycle and ordering law.

## Profile contract

`Switch2RumbleDelayMilliseconds` is an integer in the inclusive range 0 through
9,999 ms. Missing or invalid values normalize to zero. Zero is the default and
uses the existing synchronous publication path: no timer, queue, worker, or
added input-path work is created.

The setting delays game-owned rumble only. Player LEDs, startup commands,
connection confirmation, interactive identification, and other local profile
effects are not delayed.

## Nonzero-delay law

The first nonzero publication lazily creates one bounded FIFO and one timer for
the exact `Switch2VirtualFeedbackSession`. Xbox CFBK, legacy actuator state,
native Switch 2 groups, and rich DualSense groups enter that same FIFO. Source,
side, oscillator, and three-subframe identity remain intact.

Wire CFBK is authenticated when accepted. At its due time it is reconstructed
with the same command, actuator mask, values, source sequence, device and
transport generations, ownership epoch, and TTL, but with a fresh canonical
timestamp. This prevents an intentionally configured delay from making a
previously valid frame fail the ordinary 250 ms freshness fence while retaining
all ownership and sequence checks at presentation.

The queue is capped at 8,192 items. Reaching the cap discards the stale backlog
before accepting the newest state, avoiding unbounded memory growth and making
the newest stop/state authoritative.

## Lifecycle fences

A delay-value change, profile-revision change, or return to zero clears pending
work before the next publication. Session retirement clears and disposes the
timer before retiring the sole physical feedback owner. A timer callback must
re-authenticate the still-active session under the session gate before it can
present anything. Retired or superseded sessions therefore cannot resurrect
rumble.

This delay is explicitly selected presentation latency. It is not part of the
default low-latency input or feedback path.
