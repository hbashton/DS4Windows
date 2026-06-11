# Virtual DualSense Driver Contract

This directory defines the kernel/user boundary for the experimental virtual
DualSense output path.

DS4Windows cannot create a real DualSense HID device through ViGEmBus today.
The intended backend is a KMDF HID source driver using Microsoft VHF. The
driver exposes a dedicated control symlink,
`\\.\HBashtonVirtualDualSenseControl`, for DS4Windows IOCTLs. The root PnP
device still publishes `GUID_DEVINTERFACE_HBASHTON_VIRTUAL_DUALSENSE` and the
compatibility symlink `\\.\HBashtonVirtualDualSense`, but new DS4Windows builds
open the control device first so VHF/HID stack IOCTL handling cannot intercept
private pad commands.

Current contract:

- `IOCTL_HBASHTON_VDS_CREATE_PAD`
  Creates one VHF child HID device and returns a `PadId`. New callers may pass
  `HBASHTON_VDS_CREATE_PAD_IN` to request either USB mode or Bluetooth mode.
  Old callers that pass no create input still receive USB mode.
- `IOCTL_HBASHTON_VDS_DESTROY_PAD`
  Destroys the VHF child HID device for a `PadId`.
- `IOCTL_HBASHTON_VDS_SUBMIT_INPUT_REPORT`
  Sends one input report for a `PadId`. USB pads accept the 64-byte report ID
  `0x01` layout. Bluetooth pads accept the 10-byte report ID `0x01` basic
  Bluetooth layout.
- `IOCTL_HBASHTON_VDS_READ_OUTPUT_REPORT`
  Returns the latest HID output report written by a host application to the
  virtual DualSense. DS4Windows can use this for future rumble, lightbar,
  adaptive trigger, mute LED, and haptic passthrough logic.

The HID identity should present as Sony VID `054C`, DualSense PID `0CE6`, and
product string `Wireless Controller`. The initial DS4Windows client reports
buttons, d-pad, sticks, and triggers in Bluetooth mode. USB mode still has code
for touchpad contacts, gyro, accelerometer, packet counters, and battery. Output
reports for ordinary rumble, adaptive triggers, lightbar, player LEDs, and the
microphone mute LED are captured from both USB report `0x02` and Bluetooth
report `0x31`/data `0x10`; DS4Windows normalizes the Bluetooth report back to
the existing USB parser before applying it to a real DualSense. Speaker/audio
transport and advanced Sony haptic audio are intentionally outside this VHF
path.

Backend implementation:

- `driver/Driver.c`
  Creates the KMDF root device, compatibility symbolic link, device interface,
  dedicated control device, and sequential IOCTL queues.
- `driver/Pad.c`
  Owns up to eight VHF-backed virtual DualSense pads. Each create request calls
  `VhfCreate` and `VhfStart`; each submit request calls `VhfReadReportSubmit`.
  Host write reports are captured by `EvtVhfAsyncOperationWriteReport` and
  exposed back to DS4Windows through `IOCTL_HBASHTON_VDS_READ_OUTPUT_REPORT`.
- `driver/DualSenseDescriptor.h`
  Contains the USB and Bluetooth DualSense HID report descriptors used by VHF
  children.
- `HBashtonVirtualDualSense.inf`
  Installs the root-enumerated KMDF device and configures `vhf` as the lower
  filter required by the Windows Virtual HID Framework.

Build:

```powershell
.\build-driver.ps1 -Configuration Release -Platform x64
```

Install on a test-signed machine from an elevated PowerShell window:

```powershell
.\install-driver.ps1
```

The install script stages the INF with PnPUtil, creates the
`Root\HBashtonVirtualDualSense` devnode through SetupAPI, then binds the driver
package to that root device. This replaces the old manual `devcon install`
step.

Uninstall from an elevated PowerShell window:

```powershell
.\uninstall-driver.ps1
```

Use `.\uninstall-driver.ps1 -Force` if Windows refuses to remove the root
device while it is still considered active. If this was the only reason Windows
test-signing mode was enabled, `.\uninstall-driver.ps1 -DisableTestSigning`
also turns test-signing back off; reboot Windows afterwards.

To inspect the current machine state:

```powershell
.\check-driver.ps1
```

The driver must be signed. A development machine can use test-signing with a
test-signed package; a public release needs a proper driver signing pipeline.
If `HBashtonVirtualDualSense.sys` or `HBashtonVirtualDualSense.cat` is missing
from the packaged `VirtualDualSenseDriver` folder, the app can show the
DualSense output option but Windows cannot create the backend yet.

Useful references:

- Microsoft VHF is the Windows-supported way to write a virtual HID source
  driver:
  https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/virtual-hid-framework--vhf-
- The public DualSense HID descriptor/report maps in `nondebug/dualsense` are
  used as the compatibility target for the USB and Bluetooth report layouts:
  https://github.com/nondebug/dualsense
- SDL's DualSense Bluetooth work documents the important mode split: Bluetooth
  starts in basic report mode, while enhanced reports are needed for sensor,
  touchpad, advanced LED, and rumble paths:
  https://github.com/libsdl-org/SDL/issues/10086
- The current Windows controller tooling ecosystem is moving toward
  profile-defined HID identities for DualSense-class virtual pads, which matches
  this split between DS4Windows mapping code and a dedicated HID backend:
  https://github.com/nefarius/DsHidMini/discussions/424
