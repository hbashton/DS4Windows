# Release Candidate 4.5.4 Hotfix — Read Failure Recovery

This follow-up makes DS4 Bluetooth recovery safer after an unexpected read failure, including failures that happen **before pressing Stop**.

## What's fixed

- After a terminal Bluetooth read failure, DS4Windows no longer forces another physical output write before removing the failed controller. A stalled output request must not block that recovery path.
- Failed-read cleanup also avoids publishing a fresh effect into the Bluetooth audio lane.
- Normal rumble, lightbar, CopyCat settings and the working control-pipe behavior from issue #84 are unchanged.
- Includes 4.5.3's Stop/removal deadlock and reconnect-ownership fixes. A new regression test verifies the corrected sequence: unexpected read error first, then Stop.

This fixes application recovery behavior. It does **not** suppress unexpected errors or claim to prevent every Windows read failure. The initiating cause of the reporter's particular 995 remains unconfirmed.

Validation: **4,927 automated tests passed**, zero failures, with 11 existing opt-in tests skipped. The new recovery regressions failed against their previous behavior before passing with the fixes.

## Updating

Close DS4Windows before updating and back up your profiles. The installer and complete portable ZIP include the required runtime, VIIPER **0.1.3-rc4.5**, Xbox emulation identity and offline dependency installers.

Portable RC4.5.2 and newer use the already-published [DS4Updater 2.0.6](https://github.com/hbashton/DS4Updater/releases/tag/v2.0.6). Its verified-metadata version handling supports this release without another updater code change. For an initial upgrade from RC4.5.1 or older, extract the complete ZIP into a new folder and copy your settings/profiles.

Release tag: **VIIPERRC4.5.4**. Windows file version: **5.0.5.4**. This is an **unsigned prerelease**.
