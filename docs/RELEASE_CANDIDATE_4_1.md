# Release Candidate 4.1 — The Parity Patch Update

RC4.1 focuses on the two things users feel immediately: faster controller
feedback and an installer that explains and recovers from failures.

## Lower-latency controller feedback

- DualSense game haptics, Audio Haptics, speaker audio, and controller state
  now share a preallocated, lossless real-time path instead of crossing
  multiple independent queues.
- Redundant handoffs were removed so feedback reaches the physical controller
  sooner, while transient CPU load no longer causes deadline-based frame loss.
- Native haptics, lightbar state, player LEDs, adaptive triggers, rumble, and
  DS4Windows overrides are merged atomically without replaying stale feedback.
- Per-app audio capture now recovers after source changes, process restarts,
  headset/speaker switches, and stalled loopback sessions. Audio source lists
  can also be refreshed manually.
- The automatic controller source is now labeled `Game Audio` and includes the
  exact virtual Windows playback endpoint currently serving that controller.
- Closed dropdowns no longer change profile, audio, or haptics settings when
  the user scrolls the surrounding page.
- Rapid profile changes are latest-wins: stale virtual-device transitions and
  stale UI callbacks are discarded instead of replaying in sequence. This
  keeps Quick Settings, the emulated controller, and PlayStation audio routing
  synchronized with the final selected profile.
- Two-finger profile swipes now track the center of both contacts, tolerate
  natural vertical movement, and issue exactly one change per gesture.
- Auto Profiles recover from transient evaluation errors and safely skip
  disconnected controller slots without stopping future detection.
- VIIPER microphone status monitoring now owns its connection timeout for the
  full request lifetime, preventing a rapid profile transition from crashing
  DS4Windows by disposing a shared wait handle.
- Fresh light/default-theme installs can construct the Overview screen without
  relying on a dark-theme-only converter, fixing the silent first-run launch
  failure reported by RC4 users.
- Unsupported USB-IP versions, mixed driver files, failed ABI readiness, and
  the unsafe Citrix USB filter bypass any saved prompt suppression and open the
  guided offline Repair flow. A detected 0.9.7.8 install is removed before a
  required reboot; bundled 0.9.7.7 is installed only in the next boot session.

## A safer all-in-one installer

- `DS4Windows_5.0.1.0_Setup_x64.exe` contains DS4Windows, VIIPER 0.0.8, and
  usbip-win2 0.9.7.7, with optional HidHide and FakerInput checkboxes.
- VIIPER's tray, API, file metadata, installer health check, and bundled payload
  all identify as 0.0.8; older backend binaries trigger a guided update.
- Bundle-wide and VIIPER setup mutexes prevent overlapping install, repair,
  update, and uninstall transactions.
- Setup validates the interactive Windows user, preserves the correct profile
  across elevation and restart, and avoids creating startup tasks for the wrong
  account.
- Verified transaction markers reject partial success. Required restarts can
  resume safely, including standard-user installs elevated with a separate
  administrator account.
- Failures now identify the exact infrastructure step and expose persistent
  helper diagnostics instead of ending with a generic rollback message.
- File and process cleanup is restricted to installer-owned paths. Profiles,
  settings, logs, and user-created files are preserved during updates.
- RC4.1 advances the application and MSI package to 5.0.1.0, providing a
  deterministic upgrade from RC4 rather than an ambiguous same-version repair.

## Downloads

- **Recommended:** `DS4Windows_5.0.1.0_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip`

The installer is completely offline. A restart is requested only when the
bundled USB-IP driver actually needs to be installed or replaced.
