# Switch 2 Pro USB headphone verification

Opt-in hardware probe, not part of controller startup, CI, or the mapper.
Uses Windows' existing WASAPI endpoints. It sends no HID/WinUSB commands,
changes no driver, default endpoint, endpoint volume, mute or routing setting,
and writes no recorded audio to disk. All capture samples stay in process RAM.

Only run after the user authorizes a physical audio test. Connect the physical
Switch 2 Pro by USB and connect its headphone jack to the PC's **Line In**,
not another output or microphone input. Do not use a microphone for this test.
The current verification command deliberately restricts capture to the
explicitly named Realtek Line In used in the measured setup.

```powershell
dotnet run --project utils/Switch2UsbAudioProbe -- --list
dotnet run --project utils/Switch2UsbAudioProbe -- --verify-line-in '<Headphones endpoint ID>' '<Realtek Line In endpoint ID>'
```

Choose the exact active endpoint IDs returned by `--list`; do not use a default
device. The renderer must also be named `Headphones (Switch 2 Pro Controller)`.
Verify its physical USB identity separately before running (the Windows MEDIA
device is `USB\VID_057E&PID_2069&MI_02`, service `usbaudio`). Friendly names are
a safety check, not physical-device authentication. This utility deliberately
refuses renamed endpoints until the operator re-audits and adapts it.

`--tone '<Headphones endpoint ID>'` is a playback-only alternative. It cannot
confirm analog delivery. Neither command opens the controller microphone.

The signal is 500 ms of stereo PCM at 48 kHz / 16 bit, left 440 Hz and right
660 Hz, peak 0.02 (-34 dBFS), with 10 ms fades. The physical endpoint's existing
volume is left alone. The capture command records a short baseline before and
after the tone and reports RMS, peak and both frequency amplitudes per channel
in 100 ms windows. Capture is bounded to three seconds of samples and waits
for playback/recording completion are bounded. It fails on unexpected capture
format, endpoint state, playback error, or nonfinite data.

Interpret the metrics, not just exit code 0: the tone must emerge above the
baseline, 440 Hz primarily on left and 660 Hz primarily on right, then return
to baseline. `AnalogDeliveryConfirmed: false` on the playback event means that
WASAPI success alone is not an analog verdict. The tool supplies measurements;
it does not automatically certify cable delivery, microphone input, lossless
quality, sample-clock accuracy, or end-to-end latency.

See `docs/validation/2026-09-06-switch2-pro-usb-audio.md` for the measured result
and the distinction between native USB audio and unimplemented Bluetooth audio.
