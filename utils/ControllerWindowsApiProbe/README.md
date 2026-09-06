# Portable Windows gamepad API consumer

This test application consumes the existing Windows.Gaming.Input API; it does
not create a virtual pad, map input, open the physical controller, or modify
installed apps, tasks, driver policy, or authentication. Run only in the Desktop
portable lab with the canonical DS4Windows/VIIPER stack already running.

Only one WGI Gamepad matching the reviewed synthetic lab VID/PID F00D:BEED may
receive a pulse. The target is rechecked at button activation; the neutral is
sent to that same captured object, never a new device in the same list position.
Each pulse sets exactly one of four motor channels to 0.2 for a nominal 200 ms,
then requests zero for all channels in finally. Closing during a pulse also
requests neutral. Timing is bounded in intent, not a real-time guarantee.

The visual readout polls at about 30 Hz. JSONL records under the executable's
results directory are functional observations, not latency/rate measurements.
An API returning without error is not evidence of physical delivery; record
user tactile confirmation or instrumented physical evidence separately.

References consulted, without copying their implementation:

- [Microsoft Gamepad and vibration](https://learn.microsoft.com/en-us/windows/uwp/gaming/gamepad-and-vibration)
- [RawGameController.FromGameController](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller.fromgamecontroller)
- Local HIDMaestro `test/probes/native_wgi_vibration`, `wgi_read_probe`, and
  `docs/investigations/wgi-silent-sink-2026-04/finding.md`. Their ROOT/UMDF2
  findings are not assumed to describe this USB/IP-backed GIP device.

Publish with .NET 8 for win-x64 into a new Desktop lab subdirectory, then run
the executable. This utility is not part of the production application package.
