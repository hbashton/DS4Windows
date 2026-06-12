# VIIPER Backend Upgrade Path

This branch starts the DS4Windows VIIPER backend as an opt-in virtual output path. The goal is to keep the current ViGEm/custom output paths stable while VIIPER and usbip-win2 support grows into a user-friendly, install-and-forget backend.

## First usable milestone

The first DS4Windows-side milestone should feel like a normal profile option:

- Profiles can select `Xbox 360 (VIIPER)`, `DualShock 4 (VIIPER)`, `DualSense (VIIPER)`, `DualSense Edge (VIIPER)`, or `Switch 2 Pro (VIIPER)`.
- DS4Windows opens a VIIPER device stream and sends mapped controller state every frame.
- Buttons, sticks, triggers, D-pad, touch coordinates, gyro, accel, mute, capture, function buttons, and Edge paddles are mapped from the existing DS4Windows `DS4State`.
- Basic feedback is accepted from VIIPER and routed back to the real controller where DS4Windows can support it: rumble, lightbar RGB, flash, and player LED data.
- Existing ViGEm Xbox 360, ViGEm DS4, and custom virtual DualSense output choices stay available.

## Features still needed

These are the user-facing features that should be added before VIIPER becomes the default recommendation.

1. **One-click VIIPER setup**

   Add a Settings page section that can install, repair, update, start, and stop the bundled VIIPER server and usbip-win2 driver. Users should not need to manually run PowerShell scripts, know what port `3242` means, or open a separate VIIPER terminal.

2. **Health/status panel**

   Show a simple status line beside VIIPER outputs: `Ready`, `Server not running`, `usbip-win2 missing`, `Needs reboot`, or `Attach failed`. Include a `Fix` button when DS4Windows can repair it automatically.

3. **First-run wizard**

   When a user selects a VIIPER output for the first time, DS4Windows should offer to install prerequisites, elevate only for the install step, request reboot only when required, then return the user to the profile editor.

4. **Bundled service mode**

   Ship VIIPER as a managed helper process or Windows service that DS4Windows can launch and monitor. The best user experience is: DS4Windows starts, VIIPER is already ready, virtual devices appear when profiles need them, and everything is cleaned up on exit.

5. **usbip-win2 attach automation**

   DS4Windows should automatically attach newly created VIIPER USB devices locally, recover if attachment fails, and detach stale devices after crashes. The UI should surface attachment failures without making the user diagnose usbip manually.

6. **Virtual-device cascade prevention**

   VIIPER DS4, DualSense, and Switch outputs must be tagged or filtered so DS4Windows never re-ingests its own virtual controllers as physical inputs. The current DS4-specific guard should become a generic "own virtual output" registry.

7. **DualSense raw output report support**

   Current VIIPER feedback exposes basic rumble, lightbar RGB, and player LEDs. Full DualSense behavior needs raw output report forwarding or a richer VIIPER feedback schema so adaptive triggers, mic LED, mute LED, speaker volume, and advanced haptics can be passed back to a physical DualSense.

8. **Adaptive trigger bridge**

   After raw output support exists, map DualSense adaptive-trigger commands from games into DS4Windows' existing adaptive-trigger implementation when the real input controller is a DualSense.

9. **Switch 2 polish**

   Improve Switch 2 rumble decoding beyond "best effort" motor intensity, expose Nintendo-specific buttons cleanly in the UI, and add per-profile naming so users understand how PS-style buttons map to Nintendo-style buttons.

10. **Configurable VIIPER endpoint**

    Add host, port, and password fields for advanced users who run VIIPER on another machine, while keeping localhost defaults hidden unless advanced settings are expanded.

11. **Output backend migration tools**

    Add a profile migration command that can duplicate existing ViGEm profiles into VIIPER profiles. Users should be able to test VIIPER without risking their working setup.

12. **Diagnostics that users can send**

    Add a compact "Copy VIIPER diagnostics" button that includes DS4Windows version, VIIPER server version, usbip-win2 state, selected output type, last API error, and attach status.

## Recommended user upgrade flow

The best user journey is:

1. User updates DS4Windows normally.
2. User opens a profile and changes `Emulated Controller` to a VIIPER output.
3. DS4Windows detects missing prerequisites and shows one clear prompt: `Install VIIPER virtual controller support`.
4. Installer runs elevated, installs usbip-win2 if needed, places the VIIPER helper beside DS4Windows, and registers the helper service.
5. If Windows requires a reboot, DS4Windows says so plainly and keeps the original ViGEm profile active until reboot is complete.
6. After reboot, DS4Windows starts the VIIPER helper automatically.
7. The profile editor shows `VIIPER Ready`.
8. User clicks Apply and DS4Windows creates the selected virtual controller.
9. If anything fails, DS4Windows falls back to the previous output type and offers a `Copy diagnostics` button.

## Safe rollout plan

1. Keep VIIPER outputs opt-in for one release.
2. Add the installer/status UX and collect logs from real machines.
3. Enable automatic fallback to ViGEm/custom output if VIIPER fails.
4. Add raw DualSense feedback support.
5. Promote VIIPER DualSense and DualSense Edge as recommended for users who need native controller identity.
6. Consider making VIIPER the default only after install, attach, cleanup, and cascade prevention are boringly reliable.
