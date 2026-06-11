# Virtual DualSense Follow-Up Notes

This branch has the first DS4Windows-side virtual DualSense output path and a
first-pass KMDF/VHF backend. The next work should be done in this order.

## Build And Driver Bring-Up

1. Build on a machine with Visual Studio, WDK, and KMDF tooling installed.
   Local build could not be run here because MSBuild/WDK were not installed.
2. Fix any WDK signature/API mismatches first, especially around:
   - `VHF_CONFIG` field names and callback members.
   - `EvtVhfAsyncOperationWriteReport` callback signature.
   - `VhfAsyncOperationComplete` parameter order.
   - INF setup for root-enumerated KMDF plus `vhf`.
3. Test-sign the driver and install with:
   ```powershell
   devcon install .\HBashtonVirtualDualSense.inf Root\HBashtonVirtualDualSense
   ```
4. Confirm `\\.\HBashtonVirtualDualSense` opens from a simple userspace test
   before launching DS4Windows.

## HID Identity Validation

1. Confirm Device Manager and HID tools report Sony VID `054C`, DualSense PID
   `0CE6`, and product string `Wireless Controller`.
2. Confirm the virtual device exposes the expected DualSense USB report
   descriptor.
3. Confirm games/tools that look for a DualSense see the virtual device as a
   DualSense, not just a generic HID gamepad.

## Input Report Validation

1. Use DS4Windows with output type `DualSense`.
2. Validate every field independently:
   - face buttons
   - d-pad
   - shoulders
   - analog triggers
   - trigger click bits
   - sticks
   - PS button
   - touchpad click
   - mute button
   - touch contacts
   - gyro
   - accelerometer
   - battery
3. Compare live reports against a real USB DualSense capture.
4. Verify multiple virtual pads can be created and destroyed without stale VHF
   handles or pad ID reuse bugs.

## Output Report Passthrough

The backend captures host write reports and DS4Windows polls them through
`VirtualDualSenseDriverClient.TryReadOutputReport`.

Implemented:

1. `VirtualDualSenseOutDevice` starts a lightweight output-report polling timer
   while the virtual DualSense is connected.
2. It tracks the last output report sequence number so the same report is not
   processed repeatedly.
3. It parses USB DualSense output report `0x02` first and ignores other report
   IDs for now.
4. If the source controller is a physical DualSense, DS4Windows maps ordinary
   rumble, lightbar color, player LED mask, mute LED mode, and raw adaptive
   trigger bytes back to that physical controller.
5. Advanced haptics/audio/audio-device behavior remains intentionally out of
   scope until the base HID loop is proven stable.

Next validation:

1. Confirm VHF write callbacks deliver output report `0x02` with the report ID
   in byte `0`. The driver now normalizes this when VHF supplies the report ID
   separately from the report buffer.
2. Use a DualSense-aware game or HID test app to write rumble/lightbar/trigger
   output reports to the virtual controller and confirm the physical DualSense
   receives matching effects.
3. If effects are stale, inspect whether the physical DualSense input thread is
   processing queued output events quickly enough.

## Packaging And Release

1. Add a driver build job only after local WDK build works.
2. Decide whether the public release ships the driver separately or bundled with
   a clear installer step.
3. Driver signing is required for real users. Test signing is acceptable only
   for development builds.
4. DS4Windows should detect a missing driver and show a clear message instead
   of treating it like a ViGEm failure.

## Known Limitations

- This is a USB-shaped virtual DualSense, not Bluetooth transport emulation.
- Bluetooth audio passthrough to the controller speaker is not solved here.
- Advanced Sony haptics over Bluetooth still require deeper transport/driver
  work.
- The DS4Windows app can select DualSense output now, but the backend must be
  installed and working before a virtual controller appears in Windows.
