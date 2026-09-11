# RC4.5.6 — Steadier Feedback & Safer Startup

This stabilization update brings together the feedback-delivery work since 4.5.5 with fixes for initialization, recovery and setup ownership.

## What's improved

- **More dependable startup.** Windows 10 can use the encrypted VIIPER connection without requiring Windows 11's native encryption provider. Authentication stays enabled. Portable startup now reports the failed connection stage and releases its own unsuccessful broker before showing the error.
- **Safer installation and recovery.** Installer cleanup and repair share the same setup lock. VIIPER releases listeners it opened if initialization fails partway through, so an unsuccessful attempt does not leave its ports occupied.
- **Better feedback delivery.** Native DualSense commands retain their order through busy USB and Bluetooth writers, with bounded retries instead of prematurely acknowledging undelivered state. Sustained Switch 2 rumble uses the referenced first-active-subframe framing.
- **Audio Haptics for Switch 2 Pro and Joy-Con 2.** Turn application or system audio into stereo HD rumble, using Mix or Replace alongside game feedback. Both application pickers find audio sessions across active outputs, including separate Sonar routes. System audio follows the Windows default output.
- **Clean audio recovery and stopping.** Capture failures roll back fully so they can recover. Source changes discard stale audio. Writer failures stop the affected runtime, retire its resources and clear its owned feedback without overwriting untouched native game feedback.
- **Complete matching downloads.** Both packages include VIIPER 0.1.4-rc4.5.6, the Windows 10 encryption dependency, Xbox emulation identity, runtime files and notices. DS4Updater 2.0.6 remains compatible; no separate updater replacement is needed.

## Downloads and updating

Use **DS4Windows_5.0.5.6_Setup_x64.exe** for the standard installation, or extract the **entire portable ZIP** into its own folder. Do not copy only DS4Windows.exe. Close DS4Windows and VIIPER before replacing an existing installation's files; keep your profiles.

This is an **unsigned release candidate**, published under the existing VIIPERRC policy. Source archives, license notices, SHA-256 checksums and the CI build record accompany the downloads.

## Validation and remaining limits

Release checks cover the full automated x64 suite, startup/Stop ownership, installer failure simulations, complete offline packaging, and hosted MSI install/repair/uninstall plus an upgrade from the published 4.5.5 package with profile-preservation checks. Matching broker tests run on Windows and Linux; the updater suite is also checked.

These checks do not guarantee every controller/game combination. Final subjective Joy-Con smoothness and GTA V Enhanced rapid-fire feedback still need tester confirmation on USB and Bluetooth. Bluetooth Switch 2 headset audio remains unsupported. No native-driver backend or new end-to-end latency guarantee is claimed.
