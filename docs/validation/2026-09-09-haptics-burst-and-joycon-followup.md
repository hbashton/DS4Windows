# Native burst delivery and Joy-Con rumble follow-up

## User reports

- Test Heavy / Test Light on Switch 2 hardware has persistent slight stutter and
  crackling, reported on USB and Bluetooth.
- A separate GTA V Enhanced report describes missed closely spaced machine-gun
  effects with a physical DualSense emulating DualSense, on USB and Bluetooth.

These are distinct paths. The local Switch 2 TestPreview lane does not traverse
VIIPER's native DualSense output queue. Fixing one does not establish the other.

## Live baseline: Bluetooth Joy-Con 2 (L)

On September 9 at 22:56 local time the existing installed RC4.5.4 process
remained running, with profile `ds`, a standalone Bluetooth Joy-Con 2 (L),
100% rumble, no rumble delay, and virtual DualSense output. Neither app was
restarted and no saved profile was changed. Test Heavy was started and stopped
through the existing UI.

An isolated 25-second observer enabled only the local BTHPORT event 402 v0,
keyword `0x8000000000000000`. Its parser accepted only structurally complete
36-byte events containing the exact 20-byte rumble-shaped ATT write. It saved
waveform hashes, counters, amplitude summaries and timestamps, not raw packets
or an ETL. The unique observer session stopped successfully, with zero ETW lost
events. Its strict parser passed synthetic valid, malformed, short, non-rumble,
neutral and direction-separation tests before capture.

Capture: `Desktop/Controller-Diagnostics-2026-09-09/joycon-heavy-rc454-402-20260909-225612`.

| Active host observations | Result |
| --- | --- |
| Accepted rumble writes | 1,438, one alias / BIP4 |
| Waveform hashes | One unchanged waveform |
| Active subframes / maximum amplitude code | 3 / 453 |
| Counter discontinuities | Zero |
| Median interval | 15.5632 ms |
| 95th / 99th percentile | 16.2156 / 16.6734 ms |
| Maximum interval | 17.5856 ms |
| Intervals over 20 ms | Zero |

This sample does not show large host-side stalls, alternating neutral payloads
or skipped counters. It is not radio-delivery, motor-motion or perceived-quality
proof. The stop was clicked after the accepted capture window; the trace does
not establish its physical completion. Earlier event-263 attempts returned no
events and provide no write-timing evidence. The user explicitly confirmed that
this captured Test Heavy run still crackled/stuttered.

## Native DualSense command defects

Failing-first broker fixtures use the actual USB output callback and framed
feedback writer. A pulse followed by stop, or a trigger update followed by an
LED-only command, previously lost the first exact command in a latest-state
latch. The broker correction uses its existing bounded ordered queue and
reports rejection to USB/IP before committing cumulative state when admission
fails. It does not block the USB/IP reader or retire the whole controller.
Host retry after explicit overload is not guaranteed.

Separately, actual DS4Windows dispatch-to-physical-owner fixtures reproduce
oldest-command eviction at capacity and expiration of an admitted stop/LED
command at 20 ms. Downstream retained-head/admission changes now pass 175 focused
tests, including actual gamepad-only dispatch, full physical-ring rejection
without scalar fallback, lifecycle cancellation, and irrevocable admission even
when subsequent diagnostics fail. Exact commands retain their accepted head;
PCM and legacy newest-state policies are not converted into an unbounded FIFO.

## Source-backed Joy-Con experiments, not a validated fix

- [Hifi SDL's BLE implementation](https://github.com/hifihedgehog/SDL/blob/feat/hidmaestro-filter/src/joystick/windows/SDL_ble_switch2joystick.c)
  at local pin `d98c5804a9d20b0d96e993741797878c86b8f1e1` uses 10 ms rumble
  servicing. Its BLE work lives on `feat/hidmaestro-filter`, not default main.
- [Switch2Connect's input-stall report](https://github.com/TommyWabg/Switch2Connect/issues/21)
  remains an important counterexample to assuming faster rumble is free:
  reporters describe controls stalling during joined Joy-Con rumble. It does
  not establish that our controller has the same cause.
- A standalone-only 10 ms candidate preserves Pro/joined Bluetooth at 15 ms,
  USB at 12 ms, and exact waveform/carrier/amplitude bytes. Nine new cadence
  cases are included in a 165-test integrated green run. Hardware acceptance
  remains open; constants do not prove actual timer or radio cadence.
- SDL USB fills one active subframe; Hifi SDL BLE fills one active subframe and
  two neutral-amplitude subframes. Our three-active-subframe held pattern agrees
  with Switch2Connect's real held sender at local pin
  `61ac6642ce12fe7217e38a860b14863b18ca7e28`, so this is an implementation
  difference, not a proven invalid packet. Do not simultaneously change frame
  count, carriers, strength and timing, or modify incoming PCM/one-shot groups.
- An isolated .NET 8.0.29 one-shot timer check measured 11.4463 ms median for
  a 10 ms request with successful `timeBeginPeriod(1)`. It also showed long
  scheduling tails; it is not controller evidence. The exact runtime's private
  TimerQueue clock uses `QueryUnbiasedInterruptTime` on this Windows version,
  not the `Environment.TickCount64` discussed in issue dotnet/runtime#122680.
  Therefore a universal 15.6 ms ceiling was not established.
- The existing 20-report latency average is not an adequate input-tail guard.
  Opt-in accepted-report gap telemetry now reuses the portable diagnostic
  mechanism without input-path I/O or allocations. It is disabled in normal
  production launches and rejects all audio/GATT commands in input-only mode.
  Its focused runtime/pipe/cancellation/allocation run passed 77 tests.

## Combined source validation

The first full run caught two source-inspection tests whose extraction markers
still named the former `void` dispatch method. Those markers were corrected to
the new `bool` signature, retaining their assertions and extending the persistent
scratch check to retained native admission. The second complete unfiltered run
passed **5,007**, failed **0**, and skipped **11** explicitly gated hardware/cross-
runtime integration rows. Allocation assertions remained enabled. Evidence:
`isolated_results/haptics-candidate-full/full-haptics-candidate-reviewed.trx`.

Neither source reproduction is a captured diagnosis of the GTA user's session.
No fix described here is included in immutable RC4.5.5. Hardware acceptance and
complete portable delivery remain open.
