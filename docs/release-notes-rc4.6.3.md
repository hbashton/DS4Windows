# Release Candidate 4.6.3 — Smooth Profile Switching & Mouse Clicks

RC4.6.3 fixes temporary-profile switching regressions and overlapping mouse-button bindings and macros. It is built from published main, without the earlier unshipped haptics experiments.

## What changed

- Fix repeated profile loads while holding a temporary-profile switch. Releasing before activation finishes now cancels that activation instead of applying it late.
- Keep the current mapping reporting during profile preparation, and publish the prepared mapping at a guarded boundary. Backend and audio refresh work no longer runs on the physical input queue.
- Preserve held keys through automatic profile press/release transitions. A switch no longer directly releases a key that another held binding still owns.
- Preserve the existing virtual pad for same-output profile changes. There is no fixed activation delay; changing the virtual controller type still requires a real device transition.
- Fence delayed switches and refreshes against disconnects, replacement connections and newer profile selections.
- Keep mouse-toggle state separate for each binding, so unrelated toggled actions cannot block mouse-button presses or releases.
- Keep a mouse button held while any mapped binding or macro still owns it. Ending one macro no longer releases another owner's click.
- Correct FakerInput's X1/X2 side-button event translation.
- Preserve held mouse buttons when switching output backends, and retire old macro input safely on disconnect or Stop.

## Validation and limitations

Targeted regression checks cover temporary-profile press/release with held movement and clicks, early release, rapid re-press, nested temporary profiles, stale connection work, overlapping mouse holds, independent toggles, macro completion and side-button translation. Ordinary **R2 → Left Mouse** combined with cursor movement or keyboard input already passes the baseline tests; it is not newly enabled by this release.

Local validation: **6,681 tests passed, 0 failed**, with 12 opt-in hardware cases skipped. The profile-switch simulations use the real mapper, serialized profile worker and registered report admission with recording output sinks. They do not claim hardware/game acceptance. See the [profile-switch validation ledger](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.3/docs/validation/2026-09-21-temporary-profile-input-continuity.md).

The reporter's machine and game have **not** been validated. These fixes do not establish the cause of that report or guarantee that every game accepts simultaneous controller and mouse/keyboard input. See [Troubleshooting mapped mouse clicks](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.3/docs/troubleshooting-mouse-clicks.md) for conditional Windows touchpad guidance and game-specific checks. DS4Windows does not automatically change Windows touchpad settings.

## Packages

- Tag: **VIIPERRC4.6.3**. Windows application/MSI/installer version: **5.0.9.0**, advanced for correct upgrade ordering.
- Complete offline x64 installer and portable ZIP, with unchanged **VIIPER 0.1.5-rc4.6**, **USB/IP 0.9.7.7** and Xbox output identity. No separate broker update is required.
- **DS4Updater 2.0.7** remains compatible; its minimum-version requirement is unchanged. Extract the complete ZIP for manual portable updates.
- This is an **unsigned release candidate**. Publication is gated on exact-source CI, installer lifecycle and downloaded-package checks; automated validation does not replace acceptance on the reporter's hardware and game.
