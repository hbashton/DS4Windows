# Live Xbox feedback output policy on ordinary physical targets

The Xbox One virtual target is selected independently of physical controller
family in the profile selector and OutputSlotManager. Switch 2 feedback enters
its canonical HD-rumble session. Other physical targets use
`XboxOnePhysicalFeedbackSession`: conventional targets fold impulse into their
side's body motor using max; supported physical DualSense targets keep the four
channels separate and use the existing opt-in TriggerLab game-rumble controls.
Those are capability projections, not equivalent physical actuators or waveform
fidelity claims. Legacy Nintendo's existing fixed-carrier HD writer is unchanged.

Before the September 5 fix, the profile output switch woke only the Switch 2
Xbox feedback session. An ordinary target kept its current rumble state until
another game frame or its existing expiry timer arrived. The regression uses
the real profile setter, ViiperOutDevice callback, canonical session/pump and
ControlService rumble setter, with a recording physical target and manual clock.

The common `QueueXboxFeedbackPolicyRefresh` entry now wakes the existing Xbox
feedback worker for both kinds of target. The ordinary-target request captures
the exact physical session, slot, stream generation and publication sequence
without waiting behind a state setter. The worker enters existing feedback
callback admission and restricts only that current presentation through the
same canonical pump. It does not mint a frame, sequence, timestamp, TTL or ACK.
Failed neutral state acceptance remains retryable on that worker; expiry and
terminal retirement retain their existing behavior.

The current publication identity is exposed before a live output-policy read.
Thus a concurrent disable either affects that read or captures that publication
for the queued restriction. Re-enabling does not remove a current restriction;
a newly accepted game frame can resume, including an identical actuator state.
Pending requests retain the newer sequence, and stale slot/session/stream work
cannot overwrite or mutate a successor. Profile reloads call the common wake
outside the Switch 2-only conversion branch as well.

This does not add an input-rate branch, input polling, another mapper or another
feedback worker. The extra request allocation is on profile edits, not per input
report. Ordinary target success still proves **state-setter acceptance**, not a
completed HID write. Physical output-disable flags, disconnect behavior, persistent
TriggerLab effects, every real source/target route and tactile acceptance require
separate hardware evidence. The existing compatibility-matrix packet-builder tests
only serialize canonical states; their names do not prove those physical routes.

## Switch 2 publication-time follow-up

Switch 2 uses the same profile wake but retains its USB/BLE session and sole
physical writer. The production Xbox callback now supplies a cached, bounded
live-policy reader to that session. After authenticating the incoming frame,
the session exposes its new publication revision and then samples policy under
its existing gate. A concurrent disable is therefore either seen by that read
or queues a restriction for that publication. No new input lock or worker is
introduced; the delegate is allocated once per output device, not per report.

A late master-output disable converts Apply to canonical Neutral with unchanged
sequence, source/lifetime identity, timestamp and TTL, bypasses profile delay,
and clears older delayed effects/release envelopes. An impulse-only disable
selects body-only rendering. Re-enabling does not undo restrictions already
applied to the current presentation; fresh game feedback remains a new effect.
Terminal Stop bypasses the reader entirely. Malformed, foreign and replayed
frames are rejected before it. A failed policy read rejects the publication;
the caller's existing NACK/retirement path still owns cleanup.

Cold refresh requests preserve newer publication revisions within an exact
session/slot/stream. A CAS retry rechecks current identity before it can replace
pending work, so a delayed old-owner request cannot erase a successor's wake.
Both USB recording-writer and BLE production-callback tests cover the change;
they do not establish physical unplug, wireless or tactile acceptance.

Detailed red/green and full-suite results are in VIIPER's dated platform ledger.
This source is not part of the live b56 or staged b57 payloads.

## Legacy Nintendo writer follow-up

The original Switch Pro and Joy-Con writers (not Switch 2) consumed and cached
the new motor amplitudes before their interrupt-OUT call. They ignored its
boolean result. After a failed zero report, the next pass saw unchanged zero
amplitudes and skipped the write, potentially leaving a previous effect active.
This affects the ordinary physical rumble mailbox used by Xbox feedback as well
as other virtual targets; it is not the reported Switch 2 unplug defect.

Both writers now retain a writer-owned pending bit before preparation/write and
clear it only on a successful write return. A failure or exception leaves the
latest state eligible on the next existing pass. Each pass merges the current
mailbox again; no old failed payload is queued ahead of a newer game effect.
Success returns neutral output to its existing idle suppression, while active
rumble retains its existing refresh behavior. Packet layout, frequency table,
counter advancement, gain and side routing are unchanged.

`LegacyNintendoRumbleDeliveryTests` runs the real constructors, state mailbox,
merge, packet encoder and writer decision, replacing only OS initialization and
the final HID write. Thirty cases cover Pro plus left/right Joy-Con with USB/BT
report sizes, rejection/exception, uncertain failed nonzero output followed by
neutral, newer-state replacement, active refresh, idle suppression and publication
during a write. Fifteen failure cases reproduce the old lost-stop behavior.
These are simulated write results, not tactile or physical transport evidence.

There is no new worker, timer, loop, allocation or sleep in the writer path.
However, these **legacy** writers still execute synchronously on their input
loops using the pre-existing 100ms HID timeout and cancellation-completion wait.
Retrying a failed neutral can therefore stall that legacy input loop on another
pass. This remains an explicit latency/lifecycle acceptance gap; this fix is not
evidence of a nonblocking output path, bounded kernel completion, or terminal
flush after the reader has stopped. Switch 2 uses its separate owned writer.
Exceptions still propagate as before; the exception test verifies that a caller
which continues does not lose the pending state, not that the production input
thread recovers from an unhandled HID exception.

## DS4 profile-output stop follow-up

The DS4 base writer's separate `NoOutputData` issue is corrected in source:
`CheckProfileOptions` now configures the physical DS4 profile-output policy without
rewriting hardware feature bits. Its former ordering disabled the writer before
the queued zero could reach it. Immutable input-only capability and a subsequently
detected hardware `NoOutputData` flag both remain authoritative. Profiles cannot
clear either limitation. Switch 2's no-HID foundation remains unchanged; other
physical device classes retain their existing custom-pipeline configuration.

The DS4 policy uses a revision and the existing short rumble-mailbox lock, not a
transport lock. A real toggle clears pending game/preview rumble, and disabled
publication cannot be resurrected by re-enabling. The existing DS4 compositor
also neutralizes direct haptic-state writes while disabled. A possible nonzero
effect submission (including Bluetooth audio-mode controls) requires a final
stop through the same sole writer. With no possibly active motors, disabling
does not invent a new output probe; in particular, a disabled-at-startup profile
does not cause another write to an unproven interface.

The stop bypasses ordinary effect coalescing, sets both motors to zero and omits
lightbar/flash validity. USB/Sony-adapter and Bluetooth `0x05`/`0x11` layouts keep
their existing encoding. The audio owner receives the corresponding `0x11`
control with `F1` validity, preserving audio mode and volume fields and recomputing
the Bluetooth CRC. Mode changes while profile output is disabled also clamp
motors and omit lightbar validity. No new HID handle, writer, timer, queue or
mapping path is introduced.

A rejected/throwing stop is not acknowledged. Direct-lane failures keep their
existing disconnect/error policy; if that lifetime continues, the next writer
pass remains eligible. Audio-mailbox admission transfers native completion/retry
ownership to that mailbox: it is not physical-flush evidence. A late result names
only its captured policy revision, and a later enabled nonzero effect always
requires another stop on disable. The logical controller's normal input path
is not retired merely because profile effects were disabled.

`DualShock4ProfileOutputStopTests` uses real DS4 construction, mailbox, merge,
packet encoding, CRC and audio state/mailbox, substituting only the final writer
admission. Its 34 cases cover six output configurations, lost-stop reproduction,
failure/throw, preview and stale-frame suppression, rapid toggles, publication
during I/O, startup refusal, immutable/dynamic hardware limitations, audio-mode
effects, audio-owned retry and the no-HID foundation. Three warmed checks measure
zero managed allocation through 20,000 enable/effect/disable/stop cycles, with
capture disabled in the fake writer. This is allocation evidence, not a latency
measurement. No physical DS4 stop/feedback acceptance is claimed.

The non-audio DS4 writer still uses its existing synchronous HID path and
`READ_STREAM_TIMEOUT` (3,000ms), plus native cancellation retirement. A required
final stop can encounter that existing wait or a transport failure. Removing
those input-loop stalls and proving terminal physical flush remain separate
production gates; this repair does not establish low-latency parity. It does not
claim to repair all master-output policy in DualSense/native or original Nintendo
custom writers, or the Switch 2 unplug, wireless, game or haptic-delivery gates.
