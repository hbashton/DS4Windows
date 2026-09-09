# Release Candidate 4.5.2 Hotfix — Joy-Con 1 & Nintendo Options

Original Joy-Cons join the new Nintendo experience, with easier linking, clearer settings, and live feedback while you remap. This hotfix also fixes the persistent pulsing rumble reported in Hades II with a Switch 2 Pro emulating a DualSense.

## Original Joy-Cons, familiar controls

- Use one original Joy-Con on its own, or link a left and right through the same Controllers-page Link/Unlink buttons used by Joy-Con 2.
- The magnet checkbox now controls automatic pairing for both generations. The old separate Joy-Con global options have been removed.
- Linking keeps the first-selected controller's profile and virtual pad. Saved pairs are remembered; unlinking or losing a half releases held input safely.
- Choose Horizontal or Vertical manually from the controller card. Stick direction and controller artwork follow your choice.
- Use applicable Nintendo profile features with original Joy-Cons: per-hand gyro aiming, aim activation, mode shifting, stick-to-mouse tools, and translated Xbox/DualSense feedback.

## Nintendo Options and easier remapping

- **Switch 2 Controls is now Nintendo Options.** Profiles remain universal: settings stay visible and saved even on controllers that cannot use a particular feature.
- A **Show live preview** button adds controller readings while remapping, without permanently filling the screen.
- Clearer dual-Joy-Con aiming labels explain side swapping, pausing, and the separate aim-activation setting.
- Special Actions now expose the missing C, GL/GR, Capture, Mute, Edge function/paddle, and individual Joy-Con rail buttons as selectable triggers.
- Capture and side-button trigger selections survive reopening. Unrecognized saved trigger names are preserved instead of silently weakening an action's trigger combination.

## Rumble and reliability fixes

- Fixed stale DualSense compatibility rumble being restarted by later non-rumble control reports on Nintendo controllers. This caused the hands-off pulsing reported in Hades II.
- Fresh control commands select the appropriate rumble source; cached audio snapshots cannot bring an old compatibility effect back.
- A fresh zero-motor stop remains stopped even when later audio packets carry an older motor snapshot. Mixed audio/adaptive effects no longer replay old audio samples as sustained vibration.
- **Translate Xbox impulse-trigger vibration** is a separate Advanced option for every controller, independent of Trigger Lab. The Nintendo Options checkbox controls the same saved setting.
- DualSense and DualSense Edge can send Xbox impulses to their adaptive triggers or blend them into body rumble. Turning impulse translation off leaves ordinary body rumble enabled.
- Fixed original Joy-Con paired disconnect behavior and unlinking a session-only pair when its saved-pair file is damaged.
- Fixed upright right-Joy-Con stick-assist input selection while preserving fine axis precision.

## Portable updates

- Safe portable updating uses **DS4Updater 2.0.5**: checked downloads, staged replacement, preserved profiles and pairing data, and rollback on supported failures.
- The updater waits for the relevant apps to close rather than force-killing unrelated copies. Long portable-folder paths are supported.
- The portable ZIP remains complete, including the matching `viiper.exe`, Xbox emulation identity, required runtime, and offline dependency installers.

## Before you update

Close DS4Windows before installing or replacing files, and keep a backup of your profiles. Portable users coming from RC4.5.1 or older should use this ZIP for the initial upgrade; the new safe updater entry point is delivered in 4.5.2.

Original Joy-Cons do not gain Joy-Con 2's optical mouse or extra hardware. Pairing is within the same generation; mixed original/Joy-Con 2 pairs are not supported. Original holding-style changes save to the current profile; Joy-Con 2 retains its per-controller choice. Original HD rumble approximates translated effects within its packet format, and the new raw-stick calibration wizard is not supported for original Joy-Cons.

This is an **unsigned release candidate**, not a signed stable release. It uses Windows file version **5.0.5.2**. VIIPER remains the matching **0.1.3-rc4.5** build; a new broker installation is not required solely for this rumble correction.
