# VIIPER backend architecture

VIIPER is DS4Windows' only virtual-controller backend. It exposes Xbox 360,
DualShock 4, DualSense, DualSense Edge, and Switch 2 Pro devices through
VIIPER's native UdeCx virtual USB bus as complete USB devices, including the
applicable Sony audio interfaces. Production packages do not carry or invoke
USB/IP.

## User setup

DS4Windows readiness requires an authenticated native ping with exact server,
ABI, capability, driver-package, and loaded-driver build identity. Install /
Repair uses the immutable signed bundle described in
`architecture/viiper-native-signed-bundle.md`; it never downloads or
substitutes a driver. VIIPER installs its canonical LocalSystem broker under
`%ProgramFiles%\VIIPER`, owns service/credential hardening and legacy startup
migration, and commits only after the native driver and authenticated broker
are healthy. Portable DS4Windows copies use the same machine-wide native
backend and the same protected transaction.

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

The native UdeCx adapter changes scheduling and transport ownership, not those
PlayStation algorithms. DualSense and DualShock report bytes, counters,
timestamps, audio packetization, resampling, haptics, microphone servo state,
and final-state replay remain the transport-neutral parity oracle. Native
reset, D0, endpoint, cancellation, and broker-reconnect barriers fence that
state so stale generations cannot leak across lifecycle boundaries.
