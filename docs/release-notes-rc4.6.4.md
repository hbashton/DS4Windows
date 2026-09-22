# Release Candidate 4.6.4 — Smoother DualSense Haptics

Enjoy more detailed, consistent feedback from your DualSense over Bluetooth. This update focuses on keeping both advanced haptics and traditional rumble smooth through busy action scenes.

## Improvements and fixes

- **More faithful advanced haptics:** improved Bluetooth haptic conversion helps preserve the detail and character of supported games' effects.
- **Fewer interrupted effects:** fixes brief gaps that could make closely spaced haptic effects feel incomplete or skippy.
- **Rumble that lasts as intended:** fixes a settings update that could cut an active rumble effect short. Genuine stop commands still stop the effect normally.
- **Adaptive triggers stay responsive:** trigger and lighting updates continue alongside rumble without unnecessarily interrupting it.
- **Matching VIIPER included:** the installer and portable ZIP include the updated backend required for these improvements.

These improvements apply to DualSense and DualSense Edge over Bluetooth when emulating a DualSense. Advanced haptics require a game that supports them.

## Install or update

### Recommended: all-in-one installer

1. Close your games, exit DS4Windows, and quit VIIPER from its tray icon.
2. Download and run **DS4Windows_5.0.10.0_Setup_x64.exe** from the assets below.
3. Follow setup, then reopen DS4Windows and reconnect your controller. Existing profiles are retained.

### Portable version

1. Close your games, exit DS4Windows, and quit VIIPER.
2. Download **DS4Windows_VIIPER_x64.zip** and extract the **entire** ZIP into a new folder. Do not replace just the EXE.
3. Keep a backup of your existing profiles and settings. If you store them beside DS4Windows, copy them into the new folder before launching.
4. Start DS4Windows from the extracted folder. It will start the bundled VIIPER when no other copy is running. On a new PC, complete the driver setup when prompted.

**For the best experience:** select **DualSense** as your emulated controller, enable controller audio/speaker support for advanced haptics, and restart the game after changing controller emulation. Game support and individual effects vary.

This is an **unsigned Windows x64 release candidate**. Download only from this repository's release page. No new USB/IP version is required; the supported version remains **0.9.7.7**.

Thanks for your feedback and patience. Enjoy the update!
