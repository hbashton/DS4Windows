# Release Candidate 4.2 — Controller and Audio Reliability

RC4.2 tightens the parts of DS4Windows that must remain invisible when they
work: controller input, Game Bar navigation, Bluetooth ownership, and clean
application-audio routing.

## Highlights

- **Stable Xbox 360 and Game Bar input.** VIIPER 0.0.9 retains adaptive,
  thread-safe latest-state delivery. Profile changes now retire temporary Game
  Bar companions before a native Xbox pad appears, so the overlay cannot bind
  a stale controller that is about to be removed.
- **Lower-latency input without stale queues.** Mapped controller state uses a
  bounded latest-wins writer instead of replaying obsolete reports under load.
- **Reliable DualShock 4 Bluetooth ownership.** A temporary HID read drought
  no longer tears down a controller that Windows still reports as present.
- **Correct per-app DS4 audio.** Process loopback now consumes exactly one
  8 ms source quantum per encoder tick; PCM is no longer silently discarded,
  malformed, or replayed at the wrong effective rate.
- **Game-safe app speaker overrides.** A selected application remains the
  controller speaker source when a native PS5 game takes feedback ownership.
  Game triggers, LEDs, lightbar, rumble, and haptics are merged atomically
  without replacing the app's PCM generation. Audio Haptics Replace remains
  authoritative; Mix combines the captured and native game haptics.
- **Stronger capture recovery.** Selected-app speaker audio and Audio Haptics
  recover cleanly after source changes, process restarts, and headset/speaker
  transitions.
- **Prerelease updates that follow the installed channel.** Release-candidate
  users automatically receive newer prereleases; stable users remain on stable
  builds, and a newer stable release still takes priority.
- **Cleaner layouts.** Scrollable Audio Haptics, Trigger Lab, Auto Profiles,
  and log views no longer place their scrollbar over controls or text.

## Included software

- DS4Windows **5.0.2.0** (self-contained x64)
- VIIPER **0.0.9**
- usbip-win2 **0.9.7.7**
- Optional offline HidHide and FakerInput installers

The standard installer preserves profiles and settings while upgrading only
package-owned files. Portable users can continue using the release ZIP.
