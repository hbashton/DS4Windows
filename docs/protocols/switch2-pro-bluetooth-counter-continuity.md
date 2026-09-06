# Pro Bluetooth counter continuity: September 5, 2026

## Observed failure

The running Desktop b65 lab logged `VIIPER XboxOne feedback reader stopped`
at 09:48:44.703. The broker's preceding event at 09:48:44.701 says DS4Windows
rejected canonical Xbox feedback; the broker then ended that stream and removed
the virtual device after its five-second disconnect timeout. The socket error
is a consequence, not the first failure.

A private, local-only heap snapshot of the exact running DS4Windows process
35472 retains the failed Bluetooth input owner, its ring buffer and mapper:

- Pro/Common05 input: 94,738 published reports, zero queue overflows, one
  rejected notification, terminal reason `SinkFailure`.
- Runtime sink: `lastProMappingFailure = BackwardOrOutOfOrder`.
- Consecutive ring slots 5 and 6 contain the counter/host-completion pair below.
  Only these scalar timing values are reproduced here, not opaque reports,
  device identifiers, association data, or heap contents.

| ring slot | raw device counter | host completion QPC |
|---|---:|---:|
| 5 | 1,431,640 | 2,200,529,540,394 |
| 6 | 1 | 2,200,529,689,392 |

At 10 MHz this is a 14.8998 ms completion interval, with no host-time regression.
The session retains the obsolete high counter after the rejected frame. The
input-owner retirement also closes the feedback lifetime, so subsequent Xbox
feedback is rejected. This is distinct from b64's output-switch queue overflow.
The physical terminal-feedback retirement also reports `TerminalDeliveryRejected`
and the owner is quarantined; fixing input admission does not prove that separate
failure cleanup is correct.

## Correction and reference

Extend the already hardware-evidenced Pro USB Common05 arrival-order policy
to Pro Bluetooth Common05. Preserve the raw 32-bit modular classification
as diagnostics, including `BackwardOrOutOfOrder` at the reset, but do not use
that controller-clock field as a transport sequence fence. Advance the baseline
after each valid arrival; live session, replay and profile mapper share the
same policy. Do not assume an exact firmware clock modulus or reset period.

Switch2Connect commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/controller.py`, stores this field in `ControllerInputData.time` and its
`enable_input_notify_callback` processes Bluetooth notifications without using
that field as a monotonic admission fence. This is behavioral reference,
not copied source. The new BLE observation supplies the evidence deliberately
left open in [the USB audit](switch2-pro-usb-counter-continuity.md).

Exact GATT identity, current lease, device/transport generations, framing,
calibration and host-QPC checks remain required. Joy-Con policies are unchanged.
No new timer, queue, delay, rate cap, reconnect workaround or per-report logging
is added. Host arrival timing is not a claim about end-to-end input latency.

## Regression coverage

`Switch2ProUsbCounterRolloverTests` uses the observed scalar pair with synthetic
buttons and a successor to prove release delivery and baseline advancement.
Both transports cover replay/live agreement and rejected stale-generation and
regressing-host-time observations without baseline mutation. Explicit tests
keep Joy-Con outside the expanded policy.

`Switch2BluetoothRuntimeOwnerTests.ProCounterResetDoesNotRetireTheLiveBluetoothRuntime`
uses the real drain/sink/mapper/runtime/table path and synthetic notifications,
waiting for each publication so queue coalescing cannot hide the reset. It
requires all reports, no unsubscribe or quarantine, and normal explicit cleanup.

Before the fix: three failures (mapper, replay, runtime), six passes. After:
nine passes. Results are in `_results/ble-pro-counter-reset-{red,green}-20260905.trx`.
The full suite passes 3,617 tests with three existing audio skips in
`ble-pro-counter-reset-full-green-20260905.trx`. Raw calibration coverage now
accepts Pro USB/BLE discontinuities while still rejecting those on either Joy-Con.
Portable deployment and a real counter-boundary soak are separate acceptance
gates, recorded in the dated validation ledger; automated tests alone are not
hardware acceptance.
