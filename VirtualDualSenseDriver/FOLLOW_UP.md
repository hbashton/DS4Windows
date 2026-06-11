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

The backend now captures host write reports and DS4Windows can poll them through
`VirtualDualSenseDriverClient.TryReadOutputReport`. The next step is to wire
that into `VirtualDualSenseOutDevice`.

Recommended path:

1. Add a lightweight polling loop while the virtual DualSense is connected.
2. Track the last output report sequence number so the same report is not
   processed repeatedly.
3. Parse USB DualSense output report `0x02` first.
4. Map ordinary rumble, lightbar, player LEDs, mute LED, and adaptive trigger
   commands back to the real input controller when it is a physical DualSense.
5. Leave advanced audio/haptic/audio-device behavior out of this path until the
   base HID loop is proven stable.

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
