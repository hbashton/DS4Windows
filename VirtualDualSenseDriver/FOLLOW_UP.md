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

## Bug Audit Notes

Fixed after the first backend pass:

1. Pad destruction no longer marks a slot reusable before `VhfDelete(TRUE)`
   returns. The pad now enters a destroying state, preventing create/submit/read
   paths from reusing or touching the context while VHF drains callbacks.
2. Input report submission now keeps the pad lock held until
   `VhfReadReportSubmit` returns, so destroy cannot invalidate the VHF handle
   between lookup and submit.
3. Output report reads now take the pad lock before copying the latest report,
   preventing a read from racing against slot teardown.
4. The WDF device requests passive-level callbacks because the IOCTL path uses a
   wait lock. VHF write-report callbacks remain spin-lock-only and suitable for
   higher IRQL.
5. DS4Windows now stops the output-report polling timer on reset/polling
   failures, reconnects clear the previous one-time failure log state, and the
   failure log guard is thread-safe across mapper/polling threads.

Still needs validation on a WDK machine:

1. Confirm `WdfExecutionLevelPassive` is accepted at the device object level for
   this project configuration. If not, move the passive execution constraint to
   the IO queue object attributes.
2. Confirm holding `PadLock` across `VhfReadReportSubmit` does not introduce
   unexpected callback reentrancy. Microsoft documents that without
   `EvtVhfReadyForNextReadReport`, VHF uses default buffering and the report
   buffer can be reused after `VhfReadReportSubmit` returns.
3. Run Driver Verifier with create/destroy spam while DS4Windows submits reports
   to catch lifetime issues around `VhfDelete(TRUE)`.

## HID Identity Validation

1. Confirm Device Manager and HID tools report Sony VID `054C`, DualSense PID
   `0CE6`, and product string `Wireless Controller`.
2. Confirm USB mode exposes the expected DualSense USB report descriptor.
3. Confirm Bluetooth mode exposes the expected DualSense Bluetooth report
   descriptor and accepts the 10-byte basic input report `0x01`.
4. Confirm games/tools that look for a DualSense see the virtual device as a
   DualSense, not just a generic HID gamepad.
5. Inspect the virtual child devnode instance IDs. VHF should expose a HID child
   with the Sony VID/PID, but it is not expected to create a real
   `BTHENUM\...` Bluetooth stack child. That likely requires a dedicated
   Bluetooth/profile/bus driver below HID, not only VHF.

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
3. It parses USB DualSense output report `0x02` first.
4. It also accepts Bluetooth DualSense output report `0x31` with data report
   byte `0x10`, then normalizes bytes `2..48` into the existing USB `0x02`
   parser shape.
5. If the source controller is a physical DualSense, DS4Windows maps ordinary
   rumble, lightbar color, player LED mask, mute LED mode, and raw adaptive
   trigger bytes back to that physical controller.
6. Advanced haptics/audio/audio-device behavior remains intentionally out of
   scope until the base HID loop is proven stable.

Next validation:

1. Confirm VHF write callbacks deliver output report `0x02` with the report ID
   in byte `0`. The driver now normalizes this when VHF supplies the report ID
   separately from the report buffer. If VHF ever supplies a max-sized buffer
   without the report ID included, the driver preserves the ID and truncates one
   byte from the payload; validate this never affects a real DualSense output
   report.
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

- This is a USB or Bluetooth-HID-shaped virtual DualSense over VHF, not real
  Bluetooth transport emulation.
- Bluetooth mode currently submits the basic 10-byte report `0x01`, so it does
  not include gyro, touch contacts, battery, or mute button state yet.
- Bluetooth audio passthrough to the controller speaker is not solved here.
- Advanced Sony haptics over Bluetooth still require deeper transport/driver
  work.
- True `BTHENUM` hardware instances are not created by this driver. If an app
  hard-requires the Microsoft Bluetooth stack's exact device tree rather than
  the HID VID/PID/report descriptor, the next step is a real virtual Bluetooth
  transport/profile driver or a bus driver that can safely enumerate that stack.
- The DS4Windows app can select DualSense output now, but the backend must be
  installed and working before a virtual controller appears in Windows.

## Fake Bluetooth Adapter Decision Point

Do not spoof Bluetooth class or `BTHENUM` IDs from the current VHF source
driver. Microsoft's Bluetooth profile-driver model expects drivers to
communicate through `BthPort.sys`, and a software adapter would need to emulate
the lower HCI/radio or profile transport layer well enough for Windows to own
enumeration. That is a separate signed bus/transport driver, not an INF tweak.

Build that adapter only if CI plus hardware/app testing proves that HID
descriptor-level Bluetooth emulation is not enough for target games. If needed,
start from the WDK Bluetooth profile samples and design a separate
`HBashtonVirtualBluetoothDualSense` transport that feeds HID reports into this
same DS4Windows mapping path instead of replacing it.
