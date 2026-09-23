A smoother start, easier updates, and better DualSense Edge support. Thanks for helping make DS4Windows more dependable!

## What's improved

- **More reliable VIIPER startup and repair.** Slow startup checks are given time to finish, failed attempts clean up more safely, and retries keep DS4Windows open.
- **Clearer error messages.** If VIIPER cannot start, DS4Windows explains which startup check failed instead of showing only a generic error.
- **Safer app startup.** Opening DS4Windows twice at the same time no longer lets two installed instances compete for your controllers.
- **Fixed the `-autolaunch` update error.** Installed copies use the matching installer; portable copies stay in their own folder. Profiles and settings are preserved.
- **Better DualSense Edge compatibility.** Improved controller identification, motion calibration checks and virtual-controller reports.
- **Native DualSense feedback across both models.** Standard DualSense and DualSense Edge can use the shared advanced haptics and adaptive-trigger path with either emulated model.
- **Safer Edge settings handling.** Unsupported onboard-profile commands are rejected instead of appearing to work or applying incomplete settings. Virtual onboard-profile editing is not included.

The matching VIIPER is included. You do not need to update it separately.

## How to update

1. Close your games, DS4Windows and VIIPER.
2. **Installer:** download and run **DS4Windows_5.0.12.0_Setup_x64.exe**. Your saved profiles are kept.
3. **Portable:** back up your profiles and settings, then extract the complete **DS4Windows_VIIPER_x64.zip** into your portable folder, replacing the packaged app files. Keep your profiles, settings and `portable-data` folder.

If your older version shows the `-autolaunch` error when checking for updates, use the installer or complete portable ZIP once to get the corrected update path.

This is an unsigned Windows x64 release candidate, so Windows may show a publisher warning. Not every Edge feature has been verified on hardware over both USB and Bluetooth; please report any remaining issues with your controller model, connection type and log.

Enjoy, and thank you for your feedback!
