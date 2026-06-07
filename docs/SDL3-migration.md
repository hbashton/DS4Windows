# SDL3 Migration Investigation

This branch was opened to investigate replacing or augmenting DS4Windows gamepad input and emulation with SDL3.

## Current DS4Windows Architecture

DS4Windows currently has separate input and output stacks.

Input is handled through direct HID access:

- `DS4Windows/DS4Library/DS4Devices.cs` enumerates supported HID devices from `knownDevices`, opens HID handles, reads serials, filters virtual devices, and creates typed controller objects.
- `DS4Windows/DS4Library/InputDevices/InputDeviceFactory.cs` creates `DS4Device`, `DualSenseDevice`, `SwitchProDevice`, `JoyConDevice`, and `DS3Device`.
- Each physical controller class owns its input report parser, output report writer, CRC/timeout behavior, battery handling, gyro/touch parsing, LED/player light handling, and special device features.

Output emulation is handled through ViGEm:

- `DS4Windows/DS4Control/Xbox360OutDevice.cs` creates and feeds virtual Xbox 360 controllers through `Nefarius.ViGEm.Client`.
- `DS4Windows/DS4Control/DS4OutDevice.cs` creates virtual DS4 controllers through `Nefarius.ViGEm.Client`.
- `DS4Windows/DS4Control/ControlService.cs` associates physical input controllers with virtual output devices in `PluginOutDev`, `UnplugOutDev`, and feedback setup.

## SDL3 Capabilities Relevant Here

SDL3 can replace or augment parts of the physical input side:

- `SDL_Init(SDL_INIT_GAMEPAD)` initializes gamepad support and automatically initializes joystick support.
- The SDL gamepad API normalizes controllers into standard locations rather than raw axes/buttons.
- SDL gamepads support hotplugging, device metadata, mappings, rumble, trigger rumble, LEDs, sensors, and touchpads where the platform/backend exposes those features.
- SDL joystick APIs can provide lower-level access, including virtual joystick objects that are visible to SDL applications.

SDL3 does not replace ViGEm for DS4Windows' primary emulation use case:

- SDL virtual joysticks are program-supplied devices visible to SDL's own joystick/gamepad layer.
- They do not create system-wide Windows XInput or DualShock devices that normal games can see.
- DS4Windows still needs ViGEm, another Windows virtual device driver, or a future HID-class driver for real output emulation.

## Practical Recommendation

Do not rip out the existing HID classes first. Start with an optional SDL3 input backend and keep ViGEm as the output backend.

Recommended sequence:

1. Add an `IInputDeviceBackend` abstraction around discovery, open, close, report polling, and device metadata.
2. Keep the current HID backend as the default implementation.
3. Add an SDL3 backend behind an experimental setting.
4. Make the SDL3 backend produce the existing `DS4State` shape so mapping, auto profiles, output slots, and special actions remain unchanged.
5. Only enable SDL3 devices for controller types where it can supply all required DS4Windows data for that type.
6. Keep native HID paths for DualSense adaptive triggers, Bluetooth audio experiments, controller speaker/mic plumbing, DS4/DualSense report passthrough, and any feature needing Sony/Nintendo-specific reports.

## Candidate SDL3 Backend Shape

The first SDL3 implementation should be narrow:

- Initialize SDL with gamepad, joystick, events, haptic, and sensor support.
- Set the background-events hint before initialization so DS4Windows can keep reading controllers while another app is foreground.
- Enumerate SDL joystick instance IDs and open those for which `SDL_IsGamepad` returns true.
- Track hotplug by SDL joystick/gamepad instance ID, but map devices into DS4Windows by serial/path metadata where available.
- Read standard controls using SDL gamepad axes and buttons.
- Read optional sensors and touchpad data only when SDL reports support.
- Convert SDL axis ranges to DS4Windows' existing `DS4State` byte ranges before entering the mapping pipeline.

## Risk Areas

These need proof before SDL3 can become default:

- Exclusive mode and HidHide behavior: SDL may see hidden or virtual devices differently than the direct HID path.
- Device identity: DS4Windows relies on serial/MAC-like IDs for profiles, auto profiles, slots, and duplicate prevention.
- Touchpad fidelity: DS4Windows uses DS4/DualSense-specific touch fields and counters that SDL may not expose in the same form.
- Gyro calibration: current device classes own calibration and coordinate transforms.
- Output data to physical controllers: SDL can do some LED/rumble/sensor handling, but DS4Windows has custom report logic for Sony and Nintendo devices.
- DualSense advanced features: adaptive triggers, speaker/mic/audio work, mute button, player LEDs, and Bluetooth report quirks should remain HID-native until SDL3 parity is demonstrated.
- Virtual output: SDL virtual joystick is not a Windows virtual controller replacement.

## Dependency Options

The project is .NET 8, so viable C# paths are:

- Use a maintained SDL3 C# binding package and ship native SDL3 binaries with the app.
- Write a tiny local P/Invoke layer for only the SDL3 calls DS4Windows needs, then expand it carefully.

For this project, a small local interop layer is safer for investigation because the API surface is narrow and it avoids committing the app to a large binding package before proof-of-concept testing.

## First Code Milestone

The first useful code milestone should not change user behavior:

- Add `Sdl3InputBackend` skeleton.
- Add a diagnostic command or debug log path that lists SDL-visible gamepads with name, path, vendor, product, type, serial, sensors, touchpad count, and mapping.
- Do not feed SDL input into profiles yet.
- Compare the SDL diagnostic output against current HID detection for DS4, DualSense, DS3, Switch Pro, and Joy-Cons.

After that, pick one low-risk controller type for real input, probably generic DS4-compatible or Xbox-like gamepads, and test state conversion into `DS4State`.

## References

- SDL3 gamepad category: https://wiki.libsdl.org/SDL3/CategoryGamepad
- SDL3 joystick category: https://wiki.libsdl.org/SDL3/CategoryJoystick
- SDL3 initialization: https://wiki.libsdl.org/SDL3/SDL_Init
- SDL3 virtual joystick: https://wiki.libsdl.org/SDL3/SDL_AttachVirtualJoystick
- SDL3 C# bindings list: https://wiki.libsdl.org/SDL3/LanguageBindings
