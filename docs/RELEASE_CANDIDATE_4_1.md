# Release Candidate 4.1 — Bugfixes and Lower-Latency Audio/Vibration

RC4.1 is a reliability-focused update for DS4Windows 5. It tightens the
real-time controller path, fixes several first-run and profile-switching
regressions, and makes the new offline installer safer and easier to recover.

## Faster, more consistent controller feedback

- DualSense game haptics, Audio Haptics, speaker audio, and controller state
  now share a preallocated real-time path with fewer queue handoffs. Feedback
  is no longer discarded merely because a scheduling deadline was missed.
- The virtual DualSense input ceiling is now 1,000 Hz. DS4Windows submits only
  fresh mapped input, allowing each physical controller to run at its observed
  rate without manufacturing duplicate reports or forcing a 200/250 Hz cap.
- Native game haptics, lightbar/player LEDs, adaptive triggers, rumble, and
  DS4Windows overrides are merged atomically so state is not replayed, lost,
  or applied to the wrong media frame.
- Game ownership now begins only after meaningful output, survives brief idle
  periods, and ends with the owning game process. Closing a game immediately
  restores the profile lightbar, player LEDs, triggers, and rumble.
- Rumble preview and game-rumble delivery are synchronized across the new
  worker threads, fixing missing beats, stuck vibration, and stop/start races.
- Per-app audio and Audio Haptics capture recover after source changes, app
  restarts, speaker/headset changes, and stalled loopback sessions. The source
  list also has an explicit Refresh action.

## User-facing bug fixes

- Rapid profile changes are latest-wins. Stale virtual-device transitions and
  UI callbacks are discarded instead of replaying slowly, keeping Quick
  Settings, audio routing, and the emulated controller synchronized.
- Two-finger profile swipes track both contacts and issue one switch per
  gesture, while Auto Profiles recover from transient evaluation failures and
  safely ignore disconnected slots.
- Fresh light/default-theme installations no longer crash on launch because
  of a missing `InverseBoolConverter` resource.
- VIIPER microphone status monitoring owns its timeout for the entire request,
  preventing profile transitions from disposing a wait handle still used by a
  background callback.
- Closed dropdowns no longer change profile, audio, or haptics settings when
  the mouse wheel is used to scroll the page.
- The automatic audio source is labeled **Game Audio** and identifies the
  exact virtual playback endpoint currently assigned to the controller.

## Safer all-in-one installation and updates

- `DS4Windows_5.0.1.0_Setup_x64.exe` is a self-contained offline installer for
  DS4Windows, .NET, VIIPER 0.0.8, and usbip-win2 0.9.7.7. HidHide and
  FakerInput remain optional checkboxes. The portable ZIP remains separate.
- Unsupported USB-IP versions, mismatched driver files, ABI failures, unsafe
  Citrix USB filters, and an incorrect VIIPER binary always open mandatory
  guided repair; a saved “do not show again” choice cannot suppress a broken
  backend.
- The 0.9.7.8 to 0.9.7.7 USB-IP correction is split safely across a required
  reboot. Startup tasks remain disabled until the next boot verifies driver
  removal, exact hashes, ABI compatibility, and the VIIPER API.
- Install, update, repair, uninstall, and in-app infrastructure repair are
  serialized. Duplicate button clicks and duplicate Burn Plan requests are
  ignored, while a competing transaction returns Windows Installer busy
  instead of mutating the same files or processes.
- Process preflight now owns the same infrastructure lock as VIIPER repair, so
  one setup cannot terminate DS4Windows or VIIPER while another setup is
  replacing or validating them.
- Helper logs include a unique start record and matching completion/exit-code
  record. Concurrent append attempts are retried, and failure diagnostics stay
  available under `%ProgramData%\DS4Windows\Installer`.
- Package composition rejects a stale or mixed `DS4Windows.release` identity
  before running WiX. The verified manifest is published before the installer
  EXE, making the EXE the final atomic release commit point.
- Updates replace only manifest-owned files. Profiles, settings, logs, and
  unrecognized user files are preserved, while partial success never receives
  the Ready marker or enabled startup tasks.

## Validation completed

- 738 automated regression tests passed; two live audio-capture tests were
  skipped because they require an interactive application session.
- The clean self-contained x64 publish, setup helper, custom bootstrapper,
  WiX MSI ICE checks, and Burn bundle all built successfully.
- Payload hashes, package ownership, unsafe path checks, optional dependency
  conditions, offline layout, clean/update/repair/uninstall ordering,
  cancellation, downgrade blocking, collision handling, failure rollback, and
  reboot/resume were validated.
- The 0.9.7.8 to 0.9.7.7 reboot-boundary simulation passed, and deliberate
  overlapping installer/build attempts failed closed without changing the
  completed package manifest.

## Downloads

- **Recommended:** `DS4Windows_5.0.1.0_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip`

The installer requests a restart only when USB-IP must be safely installed or
replaced.
