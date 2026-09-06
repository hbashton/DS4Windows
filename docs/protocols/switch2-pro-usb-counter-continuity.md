# Pro USB counter continuity

September 5 follow-up: corresponding Pro Bluetooth counter-reset evidence now
justifies the same Pro/Common05 arrival-order policy on BLE. See the
[Bluetooth audit](switch2-pro-bluetooth-counter-continuity.md). The USB capture
and original scope below are retained as dated evidence; Joy-Con is unchanged.

The Switch 2 Pro USB Common05 report counter is not a reliable admission
sequence. The project-owned passive capture from September 3, 2026 contains
the following consecutive successful 64-byte reports:

| ordinal | raw counter | host QPC ticks | raw motion timestamp |
|---|---:|---:|---:|
| 250486 | 1,431,649 | 10,019,111,853 | 10,405,176 |
| 250487 | 1,431,653 | 10,019,151,310 | 10,408,926 |
| 250488 | 2 | 10,019,191,303 | 10,412,676 |
| 250489 | 6 | 10,019,231,966 | 10,416,426 |
| 250490 | 10 | 10,019,271,815 | 10,421,426 |

Host QPC frequency was 10,000,000 Hz. The boundary interval was 3.9993 ms;
input continued on the same read-only handle. This is capture completion
timing, not a calibrated latency measurement. The whole 260,000-report capture
is kept privately in the Desktop lab at
`results/switch2-usb-counter-transition-20260903.jsonl`, SHA256
`0A9018D8B9D50D1CA7133507CCCB04C220A5FE0660B28DCA0D0C6FBDA6916495`.
No output, initialization, calibration or pairing commands were sent by the
capture tool. Full opaque input reports are not added to the repository.

The old production mapper rejects row 250488 as `BackwardOrOutOfOrder`.
`Switch2ProUsbRuntimeOwner.TryPublish` turns that rejection into lifecycle
attention, retires the physical runtime and unplugs its virtual output. The
b48 log repeats this cycle about every 1,431.68 seconds. The continuous capture
and deterministic production replay establish the counter admission bug; the
precise firmware clock implementation and modulus are not established.

## Policy and references

Process each valid Pro USB Common05 arrival immediately through the canonical
mapper. Keep the raw counter and its ordinary 32-bit modular classification
as diagnostics, including `BackwardOrOutOfOrder` on the discontinuity itself.
Advance its baseline after each valid arrival so the successor reports do not
remain classified against an obsolete high value. Apply the same baseline
rule to `Switch2ReplayEngine`.

This follows the reference implementations' use of host arrival timing for
USB input, without copying an assumed controller-clock modulus:

- Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`, GPL-3.0:
  `src/controller.py` stores the field in `ControllerInputData.time` (line
  1422); `enable_input_notify_callback` processes input without using that
  value as a monotonic admission fence (line 4000 onward).
- SDL-hifihedgehog `d98c5804a9d20b0d96e993741797878c86b8f1e1`, zlib:
  `src/joystick/hidapi/SDL_hidapi_switch2.c`,
  `HIDAPI_DriverSwitch2_HandleStatePacket` (line 1188 onward) supplies
  `SDL_GetTicksNS()` to button/axis publication. Its independent handling of
  sensor timestamps does not gate basic input using the report counter.

No new transport, queue, timer, report-rate cap, per-report logging or delay is
introduced. No PadForge code is copied. This policy applies only to the
Pro/USB/Common05 tuple; live identity validation still admits only
`057E:2069`, `bcdDevice 0201`, `ProUsbCommon05Bcd0201`. Offline fixtures encode
model/transport/report identity, not a USB descriptor or firmware attestation.

Exact device and transport generations, native read claims, framing,
calibration and host-QPC checks remain authoritative. The raw counter cannot
distinguish queued transport input from new physical sampling; this repair
does not assert otherwise. BLE Pro/Joy-Con policies are unchanged until
corresponding observations justify a change.

## Regression coverage

`Switch2ProUsbCounterRolloverTests` uses the observed counter sequence with
synthetic button/stick data to exercise a release at the discontinuity,
successor baseline, live/replay agreement, and host
clock/lifetime rejection. The runtime-owner regression verifies four regular
reports across rollover, no lifecycle attention, then a successful explicit
terminal neutral. Existing native read tests retain stale/foreign/duplicate
claim rejection. Full-capture replay and live portable validation are recorded
separately in the dated validation ledger.
The September 5 BLE regression replaces the former BLE-unchanged assertion;
explicit Joy-Con exclusions remain covered.
