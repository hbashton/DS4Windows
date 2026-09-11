# Release Candidate 4.5.8 Hotfix — Bluetooth Haptics & Disconnect Recovery

This update fixes a native DualSense rumble regression traced to RC4.4 and a separate queued-disconnect shutdown deadlock. It keeps the subsequent feedback, Trigger Lab, Nintendo, audio, input, and installer improvements intact.

## What's fixed

- **Native Bluetooth rumble:** continuing audio packets no longer clear the game's legacy or improved rumble mode immediately after a valid command. The mode and motor values remain active until an explicit change or stop.
- **Feedback ownership:** local trigger/LED updates cannot accidentally cancel native rumble, and stale media templates cannot overwrite a newer accepted motor command. Adaptive-trigger and LED one-shot updates are still consumed once.
- **Disconnect recovery:** a disconnect queued on the controller worker now hands off to the lifecycle owner without a circular wait. Final output retirement and completion waits for external callers remain enforced.

This is not a gain boost or a rollback. PCM sample content, audio cadence, native command ordering and retry, per-trigger Trigger Lab priority, and Nintendo feedback tuning remain unchanged.

## Downloads

- **Installer:** `DS4Windows_5.0.5.8_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire ZIP, not just the EXE.

Both include the unchanged, complete **VIIPER 0.1.4-rc4.5.6** package. No broker code changed and no new VIIPER release is required. **DS4Updater 2.0.6 remains compatible.** Close DS4Windows and VIIPER before replacing their files, and keep your profiles.

## Validation and limits

The rebuilt x64 suite passed **5,413 tests with zero failures** and 11 hardware/environment-gated skips, including **2,646 Nintendo tests**. Allocation checks remained enabled and passed. Real-helper tests with simulated device I/O cover continuous rumble, explicit stops, command ordering, busy retry, microphone boundaries, unchanged PCM/gain, and disconnect completion.

The private candidate connected a physical Bluetooth DualSense, associated virtual DualSense output, and started speaker passthrough. These are startup observations, **not confirmation of in-game haptics or adaptive-trigger feel**. Physical game acceptance remains pending; no complete Hades II, Expedition 33, GTA, or all-game compatibility claim is made.

Published as an **unsigned release candidate** under the existing draft-first `VIIPERRC` policy. Matching sources, notices, checksums, and the verified GitHub Actions build record accompany the downloads.
