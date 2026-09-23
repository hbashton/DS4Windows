# Getting started with DS4Windows 5

[Back to the overview](../README.md)

## What you need

- Windows 10 or Windows 11 **x64**. The VIIPER packages do not support x86 Windows.
- A supported controller and a USB connection or suitable Bluetooth adapter.
- The [Microsoft Visual C++ x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- Administrator permission for driver installation and repair.

The release packages include the .NET runtime, matching VIIPER backend,
USB/IP driver, and optional HidHide and FakerInput installers. You do not need
to find a separate VIIPER download or choose a driver version yourself.
The Visual C++ runtime remains a separate system prerequisite.

## Install

Download only from [this repository's Releases page](https://github.com/hbashton/DS4Windows/releases).
Current DS4Windows 5 builds are release candidates; check the notes for
limitations and signing status.

### Recommended: all-in-one installer

1. Close games, exit DS4Windows, and quit any running VIIPER from its tray icon.
2. Run the release's **x64 Setup EXE** and follow the prompts.
3. Include **HidHide** to prevent games from seeing both your real controller
   and its emulated counterpart. **FakerInput** is optional for keyboard/mouse output.
4. Restart Windows if setup requests it, then complete any requested repair.

The installer places DS4Windows and VIIPER under
`%ProgramFiles%\DS4Windows`. It includes the required driver payloads;
avoid independently upgrading or downgrading USB/IP for this installation.

### Portable ZIP

1. Extract **all** of `DS4Windows_VIIPER_x64.zip` into a permanent, writable folder.
   Do not launch from inside the ZIP or copy only `DS4Windows.exe`.
2. Run `DS4Windows.exe` from that folder. On a new PC, complete driver setup
   when prompted; portable does not mean driver-free.
3. DS4Windows starts its bundled VIIPER when needed. A verified matching
   running copy can be reused. If a conflicting copy is reported, quit that
   VIIPER instance before relaunching DS4Windows.

The marked portable package does not create or retarget the installed
DS4Windows/VIIPER startup tasks just by launching.
VIIPER stays beside DS4Windows. Install / Repair restores the matching version
in that folder, downloading it only if needed, without moving your files or
closing DS4Windows. Controller output pauses briefly during an active repair.

## Connect and choose a profile

Connect a supported controller and select it in DS4Windows. Controller-family
options are in **Settings > Device Options**; optional Moonlight/Sunshine input
must be enabled separately. Some devices, including DualShock 3, need their
own compatible driver setup.

For **Switch 2 Pro**, use USB or DS4Windows' Bluetooth discovery and
association controls in **Settings**. For **Joy-Con 2**, use those Bluetooth
controls. Put the controller into pairing mode and associate it there.

In **Controllers**, use Link/Unlink for Joy-Cons, or enable automatic pairing.
Pairs require an opposite-side controller from the same generation. Choose a
single Joy-Con's upright or sideways holding style manually.

Create or edit a profile and choose the emulated controller the game should
see. **Auto Profiles** can select profiles when a matching game or application
is active. Restart a game after changing emulation if it does not detect the
new controller.

For native DualSense advanced haptics, select **DualSense** output and enable
controller audio/speaker support. The game must support advanced haptics.
Disable overlapping Steam Input remapping for that game so it can see the
emulated DualSense. Trigger Lab overrides game trigger effects only on the
triggers where it is enabled.

Nintendo HD-rumble translation adapts the source feedback to Nintendo motors;
it is not identical to a physical DualSense and cannot add adaptive-trigger
resistance. Original Joy-Cons do not have Joy-Con 2 optical mouse hardware.

## Update without losing your profiles

Close games, DS4Windows, and VIIPER first. Back up profiles and settings before
updating, especially when moving between versions or installation types.

- **Installed:** run the new release's Setup EXE. Existing profiles are retained.
- **Portable:** extract the whole new ZIP into your existing portable folder,
  replacing the packaged app files. Keep your profiles, settings and
  `portable-data` folder. Do not copy old program or dependency files back over
  the updated ones.

Settings may be in `%APPDATA%\DS4Windows` **or beside the application**, depending
on your configuration. Check both before removing an old portable folder.
Follow the release's update instructions when a matching backend update is
required. In-app update checks use this fork's releases; stable builds do not
automatically install prereleases.

## If something isn't working

- **Double input:** check HidHide is installed and enabled for the physical
  controller, then disable overlapping remapping software for the game.
- **No virtual controller / backend not ready:** follow the displayed startup
  error. Use **Settings > Install / Repair VIIPER** when requested, and restart
  only if setup asks. Keep DS4Windows and its bundled VIIPER version together.
- **No native haptics:** confirm the game supports them, the profile emulates
  DualSense, controller audio/speaker support is enabled, and Steam Input is
  not replacing the virtual pad. Restart the game after changing these settings.
- **Game Bar navigation:** enable Game Bar compatibility and run DS4Windows
  elevated. This feature requires Xbox Game Bar to be installed.

Still stuck? [Open an issue](https://github.com/hbashton/DS4Windows/issues) with
your DS4Windows version, physical controller, USB/Bluetooth connection,
emulated controller, and steps to reproduce. Attach the relevant log from your
DS4Windows settings folder's `Logs` directory; check it for personal information
before sharing. If requested, enable verbose logging and reproduce the problem
once, then turn it off again.

For setup failures, use the installer's **Open log** or **Copy diagnostics**
buttons. Detailed setup logs are also under
`%ProgramData%\DS4Windows\Installer`.
