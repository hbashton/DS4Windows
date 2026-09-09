# Release Candidate 4.5 — More Ways to Play

## The short version

Switch 2 controllers meet Xbox One / Series emulation, Joy-Cons become more
flexible, and DS4Windows gets a substantial round of everyday reliability and
usability improvements.

This is the cumulative change from **RC4.3**, including the work developed for
RC4.4. The highlights are Switch 2 Pro support over USB and Bluetooth, wireless
Joy-Con 2 support on their own or as a pair, more expressive feedback conversion,
and a dedicated Switch 2 settings page built around how you actually play.

It is a **tester release candidate**. There is real Windows and physical-controller
evidence behind important paths, alongside extensive automated coverage; that
does not mean every controller, game and connection combination is certified.
Bluetooth headset audio is not a supported feature of this candidate.

## Your controller, more choices

**Xbox One / Series is a virtual-output choice, not a Switch 2-only feature.**
The existing supported physical-controller families can select it through the
normal profile system. Switch 2 input also uses that same mapper and the existing
virtual-output choices, including Xbox 360, DualShock 4, DualSense and Switch 2
Pro. Existing button assignments and profile tools remain the place to customize
your controls.

The new Xbox path carries separate body-rumble and left/right impulse-trigger
feedback. Supported DualSense targets can use the existing opt-in trigger
controls; Switch 2 targets convert that extra detail into HD rumble. Controllers
with conventional motors receive a capability-appropriate approximation rather
than being assumed to have hardware they do not contain.

Windows testing has demonstrated changing USB Pro input, mapped GL/GR rear
buttons, and independently exercised all four Xbox feedback channels. Separate
Windows tests also demonstrated two distinct virtual Xbox controllers at once,
with removal of one leaving the other usable. That is meaningful progress beyond
an output selector or a successful connection message, but not a claim of
universal game compatibility or Xbox-console authentication.

## Joy-Cons that fit the way you play

- **One half is a controller.** A remembered Joy-Con can activate on its own;
  you do not have to wait for its partner or manually activate it each time.
  First-time Bluetooth association remains explicit.
- **Automatic or manual joining.** Automatic pairing combines compatible left
  and right halves. With it disabled, use the labeled **Link** buttons: select
  one half, then its opposite. Click again to cancel. **Unlink** separates a
  pair without forgetting its Bluetooth association.
- **Keep the pad you already had.** Joining retains the selected half's virtual
  pad and active profile instead of rebuilding both outputs. Unlinking retains
  the pair's output for one half. Physical reconnection still takes time; this
  is not advertised as a seamless or instantaneous transition.
- **Upright or sideways.** A single Joy-Con's artwork, mapping targets and
  shoulder-button presentation follow its selected holding style. The app does
  not pretend to infer that preference from how you happen to move it.
- **Mouse and motion options with context.** Desk-mouse controls are separate
  from motion aiming. Gyro output and activation settings sit beside their
  explanations, and **Aim with both Joy-Cons (DJG)** is an easy-to-find toggle.
  Bluetooth startup now enables the real optical and motion sensors; both
  halves have supplied changing sensor data through the profile mapper.

A newly diagnosed disconnect bug also gets a concrete fix: valid Joy-Con reports
could reset their internal counter and be mistaken for invalid input, tearing
down an otherwise connected half or pair. The corrected handling preserves
connection and timing checks while accepting those legitimate counter changes.

## Feedback with more of the detail left in

Switch 2 HD-rumble conversion now pays closer attention to **which side**, **which
frequency band**, and **when an effect peaks**.

DualSense audio-haptic conversion keeps left and right separate, measures short
attacks within each part of the received audio window, and reduces the chance
that a small ordinary-rumble contribution overrides a stronger authored effect.
Xbox body and impulse effects have more room to overlap without immediately
collapsing to the same maximum strength. Independent controls let you choose
whether to convert DualSense audio haptics, supported adaptive-trigger effects,
and Xbox impulse-trigger detail.

Held profile and preview rumble now stays serviced instead of expiring after a
brief pulse. Sustained effects receive the refreshes their physical packets need;
finished audio slices are not looped into an unwanted buzz. Stop, profile changes
and replacement effects retain their ordering, including when a write is already
in progress. Shutdown handles a short overlap without falsely abandoning a
healthy controller, while a real timeout still blocks unsafe cleanup.

These are better-defined, tested conversions—not lossless waveform reproduction.
Switch 2 controllers cannot reproduce a DualSense trigger's mechanical resistance,
and tactile tuning remains something to assess with real games and controllers.

## Less guesswork in the interface

Switch 2 controls have their own illustrated, task-based section: desk mouse,
motion aiming, two-hand aiming, feedback, button layers, sticks and calibration.
**Give buttons a second job** includes a control picker and shortcuts into the
existing mapping editor instead of leaving you to locate the next setting.

Joy-Con artwork now has a fuller, more dimensional silhouette, with matching
hover/highlight areas and uncropped thumbnails. Switch 2 Pro images no longer
resolve as missing files when opening a profile. The C button has its missing
remapping target, and sideways Capture is labeled as Capture without silently
changing its default assignment.

Readability improvements include themed tooltips that remain legible, clearer
physical-button names, and explicit Link/Unlink labels. Controller Readings now
uses the selected Switch 2 device's live snapshot; Pro gyro-axis handling is
corrected without changing your saved inversion preferences. If you previously
inverted an axis to work around the old behavior, check that preference again.

Switch 2 controllers also receive distinct, persistent local IDs for display and
**Link Profile/ID**, replacing indistinguishable zero-address labels. A joined
pair has an identity based on its members. These are local installation/Windows
identities—not fabricated Nintendo serial numbers—and USB and Bluetooth remain
separate where a trustworthy cross-transport match is unavailable.

## The fixes that make the features easier to live with

- **Connecting, switching and disconnecting:** corrected slot collisions,
  stale controller rows after virtual-output replacement, and cross-controller
  blocking during another pad's setup. Unnecessary repeated startup-recovery
  waits and retired USB/IP reconnect attempts have been reduced or cleaned up.
  Real Windows enumeration still sets a floor on switch time.
- **Sony feedback and mute controls:** the cumulative update adds independent
  microphone/speaker mute targets, conventional mute-LED behavior, and mutual
  exclusion with mute-button profile switching. It also corrects a reproduced
  RC4.4 native-media fallback that could unintentionally select ordinary rumble,
  plus normal Bluetooth DS4 effect routing. The reported Hades II advanced-haptic
  regression still needs its specific game/transport acceptance check.
- **Profile effects after preview:** ending Trigger Lab preview restores the
  active profile's ordinary trigger settings when no Lab override is active.
- **Lightbar and audio housekeeping:** guarded profile-lightbar restoration
  after a verified game exit, quieter routine logs, more efficient DS4 Bluetooth
  audio ownership, and the targeted SteelSeries Sonar capture correction.
- **Safer startup and setup:** startup choices are preserved, renamed application
  setup is handled consistently, uninstall-only helpers are cached for future
  removal, and VIIPER's tray can recover when Explorer is not ready at login.
- **Updates that move forward:** a newer release candidate is no longer offered
  an older RC as an update. Release-candidate ordering is checked separately
  from the application's Windows version number.
- **Profiles and persistence:** corrected legacy color-channel loading, more
  reliable date/localization handling, calibration-save contention, original
  Switch Pro startup/calibration failures, and options-dialog failures involving
  multiple Switch 2 controllers. Steam/HidHide reclaim no longer uses the
  identified restart-loop path.

## USB headset output: verified, with clear limits

The Switch 2 Pro's native Windows USB headphone output was physically verified
through its 3.5 mm jack, including left/right channel assignment and start/stop.
The Switch 2 page explains how to select that endpoint and provides a Windows
Sound settings shortcut. It does not silently replace your default audio device.

This is the physical controller's USB audio endpoint, not an emulated Xbox
headset. **Headset microphone capture has not been verified. Bluetooth headphone
and microphone audio remain unsupported:** successful Bluetooth command replies
and experimental packet writes did not establish audible output.

## What the testing does—and does not—say

The final RC4.5 source passed **4,463 tests twice**, with zero failures and
11 existing opt-in skips each time. A separate run passed all **152
allocation-named tests**. Existing CI exclusions remained unchanged; failed
intermediate runs led to investigated fixes, not relaxed assertions. The
[qualification record](validation/2026-09-08-rc45-qualification.md) distinguishes
these runs from the earlier 4,426-test source checkpoint and records packaging
results separately.

All eight opt-in Xbox client/broker process-integration cases also passed
separately against the current broker source. Those use isolated loopback
connections and simulated native attachment; they do not count as additional
physical-controller tests.

Physical evidence includes USB Pro input and feedback, Bluetooth controller and
sensor activity, native USB headphone output, and Windows virtual-pad lifecycle
checks. Newest UI, disconnect, sustained-rumble and combined-package changes
still need fresh tester acceptance. Test results are not a measurement of
controller-to-game latency.

The shipping path remains **VIIPER plus USB/IP**. This candidate does not claim
a completed native-driver backend, 0.125 ms input latency, a measured
multi-controller latency cure, or zero-time virtual-pad switching. Joy-Con 2 USB
support and the complete source/target/game matrix are not certified here.

For the most useful feedback, include the physical controller, USB or Bluetooth,
selected virtual pad, profile, game, and what happened immediately before the
problem. Try single and joined Joy-Cons, profile/output switching, held rumble
followed by Stop, and power-off/reconnect. Those everyday transitions are where
this candidate most needs to earn its release status.

## Evidence behind this assessment

This assessment covers `VIIPERRC4.3` through the RC4.5 source preparation,
including the [RC4.4 feature set](RELEASE_CANDIDATE_4_4.md). Detailed records:

- [Current controller fixes, repeated full-suite results and remaining acceptance](validation/2026-09-08-controller-followup-rollup.md)
- [Reported-bug fixes](validation/2026-09-08-github-reported-bugs.md) and [startup/setup/tray follow-up](validation/2026-09-08-reported-issues-follow-up.md)
- [Installer registration regression and existing-machine cleanup](validation/2026-09-08-installer-duplicate-arp-entries.md)
- [Joy-Con standalone activation](protocols/switch2-joycon-automatic-activation.md), [Link/Unlink handoff](validation/2026-09-06-joycon-link-handoff.md), and [Windows multi-pad evidence](protocols/switch2-cross-slot-and-pad-switch-recovery.md)
- [Switch 2 playstyle interface](validation/2026-09-06-switch2-playstyle.md) and [physical Bluetooth sensor observations](validation/2026-09-06-joycon-bluetooth-sensors.md)
- [Haptic detail conversion](protocols/switch2-haptic-detail-rendering.md), [held-rumble and shutdown validation](validation/2026-09-08-switch2-held-rumble-maintenance.md), and [Xbox feedback capabilities](protocols/xbox-one-physical-output-policy.md)
- [Physical USB headphone evidence](validation/2026-09-06-switch2-pro-usb-audio.md), [Bluetooth audio limits](validation/2026-09-08-switch2-bt-receiver-plans.md), and [DualSense regression scope](dualsense-rc43-rc44-native-haptics-regression.md)

Reference implementations and protocol research—including Switch2Connect, SDL,
PadForge/HIDMaestro, and Nintendo/Microsoft protocol work—were used where
relevant. Their demonstrated behavior informed the implementation; a feature
working upstream was not counted as a DS4Windows hardware test.
