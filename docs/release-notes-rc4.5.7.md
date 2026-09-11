# RC4.5.7 Hotfix — Sorry, broke Vibration/Triggers

4.5.6 introduced a Bluetooth DualSense feedback-delivery regression. Sorry about that. This hotfix addresses the command backlog and startup stalls behind delayed or incorrect vibration and trigger effects.

## What's fixed

- **Bluetooth DualSense game feedback:** queued game commands can now progress between audio packets while preserving their order, rather than falling behind the audio clock.
- **Feedback startup:** a partially filled audio buffer can no longer prevent the commands and audio needed to finish starting the stream.
- **Trigger Lab delivery:** live trigger changes now reach the Bluetooth output while speaker/audio passthrough is active. Unrelated profile edits do not clear the game's trigger effects.
- **Per-trigger control stays intact:** enabled Trigger Lab effects override the corresponding trigger; the other trigger continues to receive native game effects.

This is a targeted transport fix, not a gain boost. PCM encoding, audio cadence and input buffering are unchanged. It does not introduce a new controller backend or change Nintendo rumble tuning.

## Downloads

- **Installer:** `DS4Windows_5.0.5.7_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire ZIP, not just the EXE.

Both include the complete matching **VIIPER 0.1.4-rc4.5.6** package. No new VIIPER release is required for this fix. **DS4Updater 2.0.6 remains compatible.** Close DS4Windows and VIIPER before replacing their files, and keep your profiles.

## Validation

The fix passed the full automated x64 suite: **5,394 passed, zero failed**, with 11 hardware/environment-gated skips. Regression tests verify exact trigger command ordering, retry under a busy writer, startup progress, and unchanged PCM content through the output-writing code using simulated device I/O.

The private candidate was tested for haptics on a physical Bluetooth DualSense. Adaptive triggers still need confirmation in a trigger-supporting game; automated transport coverage is not a claim that every game/controller combination has been physically tested.

Published as an **unsigned release candidate** under the existing `VIIPERRC` policy. Matching source archives, notices, checksums and the GitHub Actions build record accompany the downloads.
