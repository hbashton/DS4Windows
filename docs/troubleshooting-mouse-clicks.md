# Troubleshooting mapped mouse clicks

A missed mapped click can have more than one cause. RC4.6.3 fixes overlapping mouse toggles, mouse-macro ownership and FakerInput side-button translation; it does not identify the cause of every click problem.

## Check the binding and where it fails

Confirm that the intended controller input is assigned to **Left Mouse** in the profile's regular action. Mouse Left is cursor movement, and a shifted action requires its configured modifier. Check for macros or a different profile being activated at the same time.

Compare a simple click outside the game with a click while holding the mapped movement keys. If the problem occurs only in one game, consult that game's input settings or documentation about simultaneous controller and mouse/keyboard input. A valid Windows mouse event does not guarantee that a game accepts it alongside virtual-controller input. Do not assume this is the cause without comparing the behavior.

## On a laptop or PC with a touchpad

Windows can suppress touchpad-generated mouse input briefly after keyboard activity. Microsoft calls this **Accidental Activation Prevention**; the **Most sensitive** setting disables that suppression. This documented mechanism is a troubleshooting possibility, not proof that a controller's mapped mouse events are being filtered. [Microsoft's touchpad guidance](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/touchpad-tuning-guidelines).

If clicks fail specifically while typing or holding mapped keyboard keys, you can try this reversible check:

1. Open Windows **Settings → Bluetooth & devices → Touchpad** (Windows 11) or **Settings → Devices → Touchpad** (Windows 10).
2. Expand **Taps**, note your existing **Touchpad sensitivity**, and select **Most sensitive** if available.
3. Repeat the same click-and-key combination. Restore the previous sensitivity if it does not help or causes accidental touchpad input while typing.

These controls may be absent on a PC without touchpad hardware or on some older vendor-managed touchpads. DS4Windows does **not** change this Windows setting automatically. There is no need to edit the registry or reinstall input drivers for this check.

The documented Settings shortcut is `ms-settings:devices-touchpad`, available when touchpad hardware is present. [Microsoft's Settings URI reference](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings).
