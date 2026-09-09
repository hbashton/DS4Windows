# Nintendo UI and original Joy-Con integration — 2026-09-09

This records the current source changes, not a hardware or release acceptance.
The running apps and published RC4.5.1 assets have not been replaced.

## Universal profiles and current linking UI

- The profile section is named **Nintendo Options**. Settings remain visible
  and editable for every controller. Unsupported hardware features do not
  produce input or discard saved values.
- Original Joy-Cons no longer use the old Joined/global gyro-provider UI.
  Each half starts as a standalone controller. The existing magnet checkbox
  and two-click Link / Unlink interface dispatch to each generation's backend.
- Original linking retains the first-selected virtual pad and profile. Only
  the secondary virtual pad is removed; splitting restores the secondary pad.
  Readers continue running during the cold topology transition.
- Logical publication pauses during secondary output work; retaining the pad
  is not a measured promise of interruption-free linking. Unlink and secondary
  removal release held virtual/keyboard/mouse input immediately even without
  a later physical report. Deliberately configured toggle/macro semantics remain.
- Original manual pairs are saved by stable physical profile-link identity.
  Saved pairs are reconciled before automatic oldest-compatible matching.
  Mixed original / Joy-Con 2 pairs are not supported.
- Original card holding-style changes apply to its active profile; controllers
  sharing that profile share the setting. Joy-Con 2 retains its existing
  controller-specific override. Artwork and mapping follow the effective mode.

## Input and motion

One exact connection generation per physical half feeds an owned snapshot into
a serialized logical projection. A stale half is neutralized without blocking
the fresh half. Mapping, mouse, and logical UDP observation use the composed
state, including the opposite hand's activation buttons. Completion timestamps
use the common host clock, not alternating connection-relative counters.

Applicable shared profile behavior includes layout, upright/sideways mode,
per-hand gyro/DJG, activation dampening and lock, mode shift, stick assist,
stick scrolling/tapping, mouse sensitivity, horizon stabilization, soft
deadzone, emulator yaw scaling, and automatic disconnect policy. High-rate
mouse publication is fenced across pairing changes, removal, and Stop/Start.
Gyro calibration queues work on the exact physical input readers.
Original diagnostics keep the logical owner's registered source. Shared idle
activity accepts either half; the legacy charging exemption remains intact.
Upright right-hand stick assist now reads the producer's RX/RY, while sideways
mode reads its logical LX/LY; both preserve sub-byte precision.

Original Joy-Cons have neither Joy-Con 2 optical mouse input nor its
magnetometer data. The new raw-stick wizard also requires raw pre-clamp input
not currently exposed by the original driver; the UI explains that limitation.
These unsupported tools do not repurpose or fabricate transport reports.

## Feedback qualifications

Original Joy-Con feedback uses its existing sole `0x10` output writer. Shared
Xbox impulse/body and DualSense synthesis preserves side and frequency-band
information where representable. Three-subframe synthesis is reduced to each
band's strongest slice and its carrier because the original packet describes
one oscillator pair per actuator. This is an explicit temporal approximation,
not PCM passthrough or demonstrated hardware equivalence.

The feedback path includes profile tuning, preview priority, finite connection
cues, stream expiry, retry neutralization, delayed-output authority and
Stop/retirement fences. Xbox delay remains bounded by the authenticated live
feedback lease: an effect cannot replay after that lease expires.

No original Joy-Con physical haptics acceptance has been performed in this
tranche. Automated packet/lifecycle tests cannot establish subjective feel or
radio behavior. Final automated results are recorded in the active request
ledger after the full suite finishes.

## Special Actions

Both activation and unload checklists now use one canonical trigger catalog.
The UI exposes C, GL/GR, Capture, Mute, Edge function and back paddles, individual
Joy-Con rails, and other supported sources. C is distinct from Mute. Existing
Actions.xml tokens are preserved. Capture and side-button selections no longer
disappear on reopening. Unknown saved tokens remain visible and fail closed,
instead of silently making a chord less restrictive.
