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

## A safer all-in-one installer

- `DS4Windows_5.0.1.0_Setup_x64.exe` contains DS4Windows, VIIPER 0.0.7, and
  usbip-win2 0.9.7.7, with optional HidHide and FakerInput checkboxes.
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
