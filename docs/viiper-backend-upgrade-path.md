# VIIPER backend architecture

VIIPER is DS4Windows' only virtual-controller backend. It exposes Xbox 360,
DualShock 4, DualSense, DualSense Edge, and Switch 2 Pro devices through
usbip-win2 as complete USB devices, including the applicable Sony audio
interfaces.

## User setup

DS4Windows checks VIIPER and usbip-win2 at startup. When either component is
missing, the app offers its bundled self-elevating setup. Setup installs both
components and registers exact, enabled, highest-privilege `RunVIIPER` and
`RunDS4Windows` logon tasks before any driver step can require a restart. Once
the USBIP ABI is ready, setup starts the server and verifies its local API.
Settings also provides Install / Repair and Refresh actions.

The elevated tasks are rooted explicitly at `\RunVIIPER` and
`\RunDS4Windows`. The VIIPER executable and managed DS4Windows recovery copy
live in the dedicated, protected `%ProgramFiles%\DS4Windows` tree; the
installer never rewrites access controls on an arbitrary ZIP extraction
directory.

Install / Repair copies a managed recovery build into Program Files, but its
`RunDS4Windows` task points to the exact executable that launched setup. If the
user later opens a different portable copy, DS4Windows asks once for
administrator confirmation and retargets that task to the newly chosen
portable executable. Installing VIIPER remains independent of where that
portable package was extracted.

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
