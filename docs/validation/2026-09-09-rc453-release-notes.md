# Release Candidate 4.5.3 Hotfix — Reliable Stop & Reconnect

This hotfix addresses a shutdown deadlock that could leave **Stop** disabled until DS4Windows restarted, sometimes alongside a `read failure: 995` message. It also protects a newly reconnected controller from delayed cleanup belonging to its previous connection.

## What's fixed

- Controller-removal callbacks no longer block a worker that Stop is waiting to finish. Cleanup remains serialized and tied to the exact connection.
- DS4 and DualSense recognize error 995 caused by their own deliberate Stop cancellation. Unexpected 995 errors and other read failures still retain their diagnostics and removal handling.
- A worker calling Stop cannot try to join or interrupt itself.
- Delayed removal cannot delete a replacement controller that reused the same device path or address. Hotplug also rejects controllers already being removed.

## Before you update

Close DS4Windows before installing or replacing files, and back up your profiles. Extract the complete portable ZIP; do not replace only `DS4Windows.exe`. Portable users on RC4.5.1 or older should use the ZIP for the initial upgrade—the safe updater entry point arrived in RC4.5.2.

The portable package remains complete, with the unchanged **VIIPER 0.1.3-rc4.5**, matching `viiper.exe`, Xbox emulation identity, required runtime, and offline dependency installers. No new broker installation is required solely for this hotfix. Portable updating to RC4.5.3 uses **DS4Updater 2.0.6**.

## Validation and limits

The versioned build passed **4,918 automated tests**, with **zero failures** and **11 existing opt-in skips**. This includes **29 new shutdown/reconnect regression cases** and three release-ordering checks. Tests reproduce the stop/removal race and verify cancellation and reconnect ownership; they do not establish physical confirmation of the reporter's session.

This is an **unsigned prerelease**, not a signed stable release. Release tag: **VIIPERRC4.5.3**. Windows file version: **5.0.5.3**.
