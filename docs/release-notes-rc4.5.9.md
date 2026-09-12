# Release Candidate 4.5.9 Hotfix — Faster Feedback, Same Detail

Rapid feedback should stay responsive, not build up a delay during sustained action. This hotfix targets the DualSense Bluetooth feedback backlog reported in #81 while retaining the RC4.5.8 haptics and disconnect fixes.

## What's improved

- **Faster native feedback dispatch:** game feedback now has a separate 500 Hz / 2 ms software pacing ceiling instead of sharing the 200 Hz / 5 ms local-settings cadence. Distinct effects and stops remain ordered; audio and microphone scheduling retain their existing boundaries.
- **No unnecessary repeat backlog:** consecutive byte-identical, known state-setting commands may share an unclaimed pending queue entry. Changed effects, A-to-B-to-A transitions, stops, unknown commands, extra trigger bytes, and ownership changes are preserved.
- **Adaptive-trigger commands use the faster path too.** Automated tests verify trigger changes, complete trigger payloads, and release commands remain intact. Live validation covered body rumble, not in-game adaptive triggers; this addresses the shared queue-delay cause, not every possible cause of trigger skipping.

This is not a gain boost or a rollback. PCM samples, codec settings, audio cadence, native rumble-mode ownership, per-trigger Trigger Lab priority, and Nintendo HD-rumble tuning are unchanged. Existing Switch 2 and Joy-Con functionality remains included.

## Measured on a Bluetooth DualSense

In a controlled one-second 250 Hz body-rumble burst, all **251 changes, including the final stop**, reached the physical Bluetooth writer in order. Against RC4.5.8, the final-stop submission delay fell from **252 ms to about 0.18 ms**. Two additional candidate runs preserved all 251 changes, with final-stop submission delays of **0.15–0.18 ms**.

These are **host-side source-write-to-physical-writer measurements**, not radio acknowledgments, actuator latency, guaranteed 500 Hz hardware throughput, or proof of every game's behavior. GTA rapid-fire gameplay and adaptive-trigger feel still need in-game confirmation. Short input checks showed no consistent report-interval worsening during these bounded bursts.

## Downloads

- **Installer:** `DS4Windows_5.0.5.9_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire ZIP, not just the EXE.

Both include the complete, unchanged **VIIPER 0.1.4-rc4.5.6** package and Xbox output identity. No new broker release is required. **DS4Updater 2.0.6 remains compatible**; the portable bootstrap obtains the published updater rather than bundling an outdated copy. Close DS4Windows and VIIPER before replacing files, and keep your profiles.

## Validation

The rebuilt x64 suite passed **5,539 tests with zero failures** and 11 existing hardware/environment-gated skips. Allocation assertions remained enabled, including a new intentional-allocation control. Coverage includes ordered native feedback, trigger payload preservation, media/PCM continuity, transport retry and completion handling, and existing Nintendo behavior.

Published as an **unsigned release candidate** under the existing draft-first `VIIPERRC` policy. Matching sources, notices, checksums, and the GitHub Actions build record accompany the downloads. Detailed evidence is recorded in [the feedback cadence validation](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.5.9/docs/validation/2026-09-12-issue81-native-rate.md).
