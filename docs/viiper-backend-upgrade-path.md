# VIIPER backend architecture

VIIPER is DS4Windows' only virtual-controller backend. It exposes Xbox 360,
DualShock 4, DualSense, DualSense Edge, and Switch 2 Pro devices through
usbip-win2 as complete USB devices, including the applicable Sony audio
interfaces.

## User setup

DS4Windows checks VIIPER and usbip-win2 at startup. When either component is
missing, the app offers its bundled self-elevating setup. Setup installs both
components, registers a hidden `RunVIIPER` logon task, starts the server, and
verifies its local API. Settings also provides Install / Repair and Refresh
actions.

## Profile migration

The retired serialized values `X360` and `DS4` remain readable solely for
backward compatibility. They normalize immediately to `ViiperX360` and
`ViiperDS4`; new saves never write the retired values.

## Runtime containment

DS4Windows records locally created VIIPER Sony interfaces before normal HID
enumeration and rejects them as physical inputs. Moonlight/Sunshine virtual
controllers use a separate opt-in admission policy, so accepting streamed
controllers cannot make DS4Windows recursively ingest its own output.

## Feedback and audio

VIIPER feedback is read by `ViiperOutDevice` and routed to the currently bound
physical controller. Xbox/standard rumble, Sony lightbar output, adaptive
triggers, advanced haptics, speaker playback, and microphone capture are
translated according to the physical controller's capabilities.
