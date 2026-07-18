# Frontend modernization

This branch keeps the DS4Windows runtime and WPF binding layer intact while adopting the
navigation, spacing, cards, descriptions, and progressive-disclosure patterns used by the
DS5 Bridge companion app.

## Why the frontend remains WPF

DS5 Bridge's companion is an Electron/React application backed by a small vendor-HID
protocol. DS4Windows has hundreds of mature WPF bindings and event handlers connected
directly to controller, profile, output-slot, automation, and diagnostic services. Replacing
that layer with Electron would require a second public API for nearly the entire program and
would make regressions easy to miss.

The modernization therefore treats DS5 Bridge as the visual and information-architecture
reference. `BridgeShellStyles.xaml` provides the shared shell and component geometry, while
the existing view models remain the source of truth. Theme-specific colors stay in the
existing light and dark theme dictionaries, so runtime theme switching continues to work.

## Current navigation and feature coverage

Nothing in the existing UI has been removed. The main shell exposes:

- **Controllers**: connection type, access status, battery, selected profile, per-device
  profile linking, profile editing/creation, temporary lightbar color, and wireless
  disconnect behavior.
- **Profiles**: create, edit, rename, duplicate, delete, import, and export.
- **Auto Profiles**: application-driven profile and controller-slot switching.
- **Output Slots**: virtual controller slot inspection and control.
- **Settings**: ordinary startup, notification, charging, appearance, and update preferences.
- **Advanced settings**: VIIPER setup, OSC input/output, UDP server and smoothing, language,
  Steam/custom executable compatibility, process priority, absolute-mouse monitor, device
  registration, driver/update utilities, and diagnostics.
- **Log**: live status messages, export, clear, and detailed-message inspection.

The profile editor keeps the interactive controller mapping canvas and makes the dense
settings rail explicit:

- **Controls**: complete button, stick, trigger, touch, gyro, keyboard, mouse, macro, and
  unbound mapping support.
- **Special Actions**: create, edit, remove, enable, and export action definitions.
- **Controller Readings**: live input, dead-zone, and drift inspection.
- **Axis Config**: left/right stick radial and axial dead zones, anti-dead zones, max zones,
  output curves, rotations, outer bindings, delta acceleration, flick stick, L2/R2 tuning,
  and six-axis acceleration.
- **Lightbar**: normal color, battery color, flash behavior, empty color, and passthrough.
- **Touchpad**: mouse, controls, mouse joystick, absolute mouse, passthrough, tap/double-tap,
  scroll, trackball, smoothing, inversion, and click behavior.
- **Gyro**: controls, mouse, mouse joystick, directional swipe, passthrough, steering wheel,
  trigger conditions, toggles, smoothing, jitter compensation, and inversion.
- **Advanced**: virtual output type and disable switch, output hooks, debouncing, rumble and
  DualSense rumble translation, controller speaker and microphone passthrough, mute-button
  lighting, input readout, mouse acceleration, touchpad toggle, DS4 output data, Game Bar,
  launch-with-profile, idle disconnect, wireless polling, and absolute-mouse options.

## Rules for the next UI passes

1. Backend settings remain authoritative; views do not maintain shadow copies.
2. A setting may move under **Advanced**, but it is not removed or silently reset.
3. Common pages use one title, one short description, flat content, and bordered cards.
4. Visible helper text is preferred to unexplained acronyms or tooltip-only documentation.
5. Device-specific controls remain visible only when their existing availability binding
   says the device supports them.
6. New DS5 Bridge-derived features are separate follow-up work, not part of the visual
   migration.

## Follow-up feature seams

- **Audio Haptics** should be implemented behind a dedicated service and profile settings
  model. The existing NAudio dependency and DualSense speaker/microphone paths can be reused,
  but audio capture must not be coupled to a page's lifetime.
- **Adaptive-trigger profile library** should wrap the existing trigger-effect primitives and
  persist named presets independently of controller profiles before a preset UI is added.
- **Controller artwork** should use project-owned, device-specific assets for DualShock 4,
  DualSense, DualSense Edge, Switch, Joy-Con, and supported legacy devices. The shell must not
  assume that every connected controller is a DualSense.
