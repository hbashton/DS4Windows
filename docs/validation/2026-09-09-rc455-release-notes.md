# Release Candidate 4.5.5 Hotfix — Reliable Setup & Startup

This hotfix repairs a setup/startup conflict that could stop an upgrade with **VIIPER/USB-IP setup failed (0x80070001)**, even though the USB/IP driver was healthy.

## What's fixed

- Setup can recover the verified older VIIPER startup registration left behind by the affected build, including a recognized portable-broker target. It saves the original task before changing it.
- DS4Windows no longer redirects the installed startup task to a portable runtime preference or removes the task's ownership marker.
- The app and installer now agree on the broker's startup priority, preventing repeated unnecessary task replacement.
- Owned tasks are updated in place instead of being deleted before replacement. Unrelated startup tasks are preserved.
- When setup cannot safely recover a task, the error identifies the task conflict instead of hiding it behind a generic dependency failure. Retry diagnostics refer to the current attempt.

Includes the earlier 4.5.3/4.5.4 Stop and read-failure recovery fixes. This hotfix does not change controller input, rumble or USB/IP transport behavior.

Validation: **4,952 automated tests passed**, zero failures, with 11 existing opt-in skips. Startup ownership/recovery, durable backup and setup-error regression checks also passed. Allocation assertions remain enabled.

## Updating

Close DS4Windows before updating and back up your profiles. The installer and complete portable ZIP include the runtime, VIIPER **0.1.3-rc4.5**, Xbox emulation identity and offline dependency installers. No separate broker update is required.

Portable RC4.5.2 and newer can use the already-published [DS4Updater 2.0.6](https://github.com/hbashton/DS4Updater/releases/tag/v2.0.6). For an initial upgrade from RC4.5.1 or older, extract the complete ZIP into a new folder and copy your settings/profiles.

Release tag: **VIIPERRC4.5.5**. Windows file version: **5.0.5.5**. This is an **unsigned prerelease**.
