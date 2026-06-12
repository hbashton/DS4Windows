# VIIPER Backend Upgrade Path

This branch starts the DS4Windows VIIPER backend as an opt-in virtual output path. The goal is to keep the current ViGEm/custom output paths stable while VIIPER and usbip-win2 support grows into a user-friendly, install-and-forget backend.

## First usable milestone

The first DS4Windows-side milestone should feel like a normal profile option:

- Profiles can select `Xbox 360 (VIIPER)`, `DualShock 4 (VIIPER)`, `DualSense (VIIPER)`, `DualSense Edge (VIIPER)`, or `Switch 2 Pro (VIIPER)`.
- DS4Windows opens a VIIPER device stream and sends mapped controller state every frame.
- Buttons, sticks, triggers, D-pad, touch coordinates, gyro, accel, mute, capture, function buttons, and Edge paddles are mapped from the existing DS4Windows `DS4State`.
- Basic feedback is accepted from VIIPER and routed back to the real controller where DS4Windows can support it: rumble, lightbar RGB, flash, and player LED data.
- The Settings tab shows a VIIPER support panel that reports `VIIPER helper`, `usbip-win2`, and `server` state.
- Selecting a VIIPER output prompts the user to install or repair VIIPER support if either VIIPER or usbip-win2 is missing.
- A bundled setup script installs/repairs usbip-win2, downloads the latest VIIPER Windows helper, starts the VIIPER API, and verifies localhost connectivity.
- Switch 2 output labels use Nintendo naming, including `B/A/Y/X`, `Minus`, `Plus`, `Home`, `Capture`, `L/R`, and `ZL/ZR`.
- The profile editor presents compact DualSense/DualSense Edge and Switch 2 legends beside the emulated controller picker.
- Existing ViGEm Xbox 360, ViGEm DS4, and custom virtual DualSense output choices stay available.

## Completed in this branch

1. **One-click VIIPER setup**

   DS4Windows has a Settings tab section for VIIPER support with status and an install/repair action. The install step runs elevated only when needed.

2. **Health/status panel**

   The Settings tab reports VIIPER helper, usbip-win2, and VIIPER server state. Virtual output creation also refuses to start with a clear error when prerequisites are missing.

3. **First-run prompt**

   When a user selects a VIIPER output for the first time and the backend is not ready, DS4Windows prompts them to install/repair VIIPER and usbip-win2.

4. **usbip-win2 usage**

   DS4Windows drives VIIPER through its localhost TCP API. VIIPER uses usbip-win2 underneath on Windows and auto-attaches localhost-created USB/IP devices, so DS4Windows does not shell out to usbip directly in the normal path.

5. **Switch 2 UI polish**

   The mapping names and profile editor hint use Nintendo-style labels so a Switch 2 profile does not read like an Xbox or PlayStation controller.

## Features still needed

These are the user-facing features that should be added before VIIPER becomes the default recommendation.

1. **Bundled service mode**

   Ship VIIPER as a managed helper process or Windows service that DS4Windows can launch and monitor. The best user experience is: DS4Windows starts, VIIPER is already ready, virtual devices appear when profiles need them, and everything is cleaned up on exit.

2. **Explicit attach recovery**

   VIIPER auto-attaches localhost USB/IP devices, but DS4Windows should still surface attach failures, recover if the auto-attach path fails, and detach stale devices after crashes.

3. **Virtual-device cascade prevention**

   VIIPER DS4, DualSense, and Switch outputs must be tagged or filtered so DS4Windows never re-ingests its own virtual controllers as physical inputs. The current DS4-specific guard should become a generic "own virtual output" registry.

4. **DualSense raw output report support**

   Current VIIPER feedback exposes basic rumble, lightbar RGB, and player LEDs. Full DualSense behavior needs raw output report forwarding or a richer VIIPER feedback schema so adaptive triggers, mic LED, mute LED, speaker volume, and advanced haptics can be passed back to a physical DualSense.

5. **Adaptive trigger bridge**

   After raw output support exists, map DualSense adaptive-trigger commands from games into DS4Windows' existing adaptive-trigger implementation when the real input controller is a DualSense.

6. **Switch 2 feedback polish**

   Improve Switch 2 rumble decoding beyond "best effort" motor intensity and add any VIIPER-specific output report handling as it becomes available.

7. **Configurable VIIPER endpoint**

    Add host, port, and password fields for advanced users who run VIIPER on another machine, while keeping localhost defaults hidden unless advanced settings are expanded.

8. **Output backend migration tools**

    Add a profile migration command that can duplicate existing ViGEm profiles into VIIPER profiles. Users should be able to test VIIPER without risking their working setup.

9. **Diagnostics that users can send**

    Add a compact "Copy VIIPER diagnostics" button that includes DS4Windows version, VIIPER server version, usbip-win2 state, selected output type, last API error, and attach status.

## Recommended user upgrade flow

The best user journey is:

1. User updates DS4Windows normally.
2. User opens a profile and changes `Emulated Controller` to a VIIPER output.
3. DS4Windows detects missing prerequisites and shows one clear prompt: `Install VIIPER virtual controller support`.
4. Installer runs elevated, installs usbip-win2 if needed, places the VIIPER helper in `%LOCALAPPDATA%\VIIPER`, and starts the VIIPER server.
5. If Windows requires a reboot, DS4Windows says so plainly and keeps the original ViGEm profile active until reboot is complete.
6. After reboot, DS4Windows starts the VIIPER helper automatically.
7. The profile editor shows `VIIPER Ready`.
8. User clicks Apply and DS4Windows creates the selected virtual controller.
9. If anything fails, DS4Windows falls back to the previous output type and offers a `Copy diagnostics` button.

## Safe rollout plan

1. Keep VIIPER outputs opt-in for one release.
2. Collect installer/status logs from real machines.
3. Enable automatic fallback to ViGEm/custom output if VIIPER fails.
4. Add raw DualSense feedback support.
5. Promote VIIPER DualSense and DualSense Edge as recommended for users who need native controller identity.
6. Consider making VIIPER the default only after install, attach, cleanup, and cascade prevention are boringly reliable.
