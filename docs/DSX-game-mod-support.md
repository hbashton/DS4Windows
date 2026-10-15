# DSX game mod support

This optional compatibility listener lets supported DSX game mods on **this PC**
control a physical DualSense or DualSense Edge's adaptive triggers and lights.
It is not a replacement for DSX and does not receive audio/PCM haptics.

## Setup

1. In DS4Windows **Settings → Game mod support (DSX)**, enable
   **Let game mods control triggers and lights**.
2. Press **Start** in DS4Windows and connect your DualSense or DualSense Edge.
3. Set your mod to address `127.0.0.1`, port `6969`, unless you selected another
   local endpoint under **Connection details**.
4. Check the status below the checkbox. **Enabled** is a saved preference;
   **Listening** means the listener actually started.

Only one app can listen on an address/port. If the port is busy, close the other
listener (for example DSX), or choose the same new port in DS4Windows and the mod,
then choose **Apply / Retry**. Only loopback addresses are supported; this is not
a network/LAN server and does not require opening firewall ports to other PCs.

Controller indices are zero-based DS4Windows slots. A controller in slot 2 uses
index 1, even if it is the only DualSense connected. Other controller types are
not listed as supported devices and never silently redirected to slot 1.

## What takes priority?

- Trigger Lab wins on each trigger where it is enabled.
- A mod can temporarily own one trigger without blocking the other.
- Mod light/trigger settings are temporary; they do not rewrite your profile.
- ResetToUserSettings, disabling this option, stopping DS4Windows, or retiring
  the physical controller releases that session's overrides.
- Reset restores the current underlying game/profile state. It must not require
  a new game packet to stop an old mod effect.

Closing a mod does not itself send a UDP disconnect event. Mods should send
ResetToUserSettings when leaving their effect mode or exiting. Disable the
checkbox to release an override from a mod that did not reset itself.

## Protocol boundary

The wire contract is checked against
[Paliverse's DSX v3.1+ example](https://github.com/Paliverse/DSX/tree/1614f003d7f00fa501e789c16eacb219993c1c16/Mod%20System%20%28DSX%20v3%29),
and effect packing against
[Nielk1's documented DualSense effect factories](https://gist.github.com/Nielk1/6d54cc2c00d2201ccb8c2720ad7538db).
Verified legacy preset values also use numeric protocol evidence from
[DualSenseY](https://github.com/WujekFoliarz/DualSenseY/blob/6787d099f752b43c24a74cda2e8a881f40311c8a/DualsenseY/MainWindow.xaml.cs).

The server validates a complete bounded packet before dispatch. It also accepts
the official legacy v2 RGB form without brightness (full brightness) and empty
status instructions with null parameters. Invalid packets
do not fall back to controller 0 or an invented effect. Recognized but unsupported
instructions return an explanatory status and do not partially apply a batch.
Nonzero emulation trigger thresholds are not supported: input mapping remains
owned by the DS4Windows profile. A zero threshold is accepted as no added input
restriction. This compatibility boundary must not be described as full DSX parity.

Supported trigger modes are Normal/Off (`0`, `20`), the verified GameCube preset
(`1`), parameterized modes `13`–`18` and `21`–`26`, and CustomTriggerValue (`12`)
with verified submodes `0`–`8` and seven byte parameters. Legacy presets `2`–`11`
and `19`, and custom submodes `9`–`16`, are explicitly unsupported until their
output semantics are verified. Calibration commands are never forwarded. Mods
that require these unsupported modes or DSX-specific app discovery need further
compatibility work; they should not be advertised as already supported.

[utils/test_dsx_mod.ps1](../test_dsx_mod.ps1) queries status by default. Its explicit `-TestEffects` switch
briefly tests low-force right-trigger resistance and light output, then requests
reset. Do not run effect tests while gaming. Unit tests use fake devices and
ephemeral loopback ports, not physical controllers.
