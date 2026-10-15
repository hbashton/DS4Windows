# DS4 profile-output allocation gate — 2026-09-14

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Release held on the original failure

The first final RC4.6 run failed
`WarmPolicyTransitionsAndWriterCompositionAllocateNothing (0)` with **816 bytes**
against the unchanged zero-byte assertion. Evidence remains in
`isolated_results/rc46-publication/full-final.trx`. That process was not profiled;
passing repeats and earlier allocation-counter investigations were not accepted
as attribution for this observation.

The failing USB case ran at 09:56:39.150–09:56:39.184. The eight enabled Xbox
process-interop tests ran later, at 09:57:45–09:57:54, so their execution did not
cause this earlier failure. The USB writer bypasses the new Bluetooth fresh-
motor admission branch. Source review found no warmed allocation site, but
that alone did not establish the cause.

## Same-writer capture and positive control

An isolated harness loads the actual built test assembly and instantiates its
private `RecordingDevice`, with report copying disabled. Pre-created delegates
invoke the actual profile-policy, rumble and writer methods. Each window keeps
the original **2,000 warmup / 20,000 measured** Step calls: enable, publish
nonzero rumble, Pump, disable, Pump, Pump. Construction and reflection occur
before measurement; no hardware, installed app or production setting is used.

The diagnostic roots a 2,000,000-node BCL graph and requests a forced nonblocking
collection before the counter window. Pressure allocations remain outside the
measured workload. It preserves and stops at the first failed strict-zero
window rather than averaging it away or retrying until success.

`raw-usb-02` reproduces a **2,760-byte** increase at batch 13 with **zero object
callbacks and zero recorder overflow**; the managed thread remains 1.
The native QPC envelope `608040659818..608040780160` contains GC-preparation
`SuspendStarted detail=7` at `608040755726`, followed by suspension/resumption
(native log lines 192–208). Collection counts remain `16,15,9`: the collection
started before this window. Native Begin/End bracket a slightly wider interval
than the managed counters. The recorder's experimental `gcDepth` is not used.

The same process enables the actual recording sink's report Clone, after
warming its List capacity, and retains the report. This positive control
records **88 bytes and one array object**, with a stack through
`System.Object.MemberwiseClone` and `DS4Device.sendOutputReport`, with no
overflow (lines 209–225). The exact-zero condition rejects this real allocation.

This is same-workload evidence for GC allocation-context accounting repair,
whose runtime mechanism is documented in the
[Nintendo allocation investigation](2026-09-09-nintendo-allocation-gate.md).
It is not a retroactive object trace for the original 816-byte observation.
The first `raw-usb-01` capture had 256 zero windows and a valid positive control,
but no failing window; it was not sufficient for attribution.

## Narrow test correction

`scoped-usb-03` repeats the same pressure, warmup and measured calls using the
existing `StrictAllocationMeasurementScope` around only the warmed counter
pair. All **256 windows** measure exactly zero bytes and zero objects. The real
report-clone positive control still records 88 bytes and one object. All 258
native envelopes, including initialization and the positive control, have zero
overflow. Scope entry/exit failures are not ignored.

The ordinary test now uses this existing bounded scope and retains its exact
zero-byte assertion. Three ordinary positive controls exercise the actual
recording writer for USB, Bluetooth and full-duplex audio-owner cases and
require the same zero-byte gate to reject a real report clone. The focused
rebuilt suite passes **48 tests, zero failures**. The subsequent ordinary,
unfiltered rebuilt x64 run passed **5,717 tests, zero failures, three gated
live-audio skips**, including the eight opt-in isolated process-interop cases.
Evidence: `isolated_results/rc46-publication/full-final-confirmed.trx`.
No profiler or diagnostic-pressure switches were used in that full run.
Exact-source CI and release-package validation remain separate gates.

No production code, GC policy, runtime/JIT configuration, allocation limit,
feedback encoding, transport cadence or input timing was changed to clear this
failure. The temporary no-GC reservation belongs only to the synthetic test
process and surrounds a bounded synchronous counter window.

## Reproduction and evidence

Harness and captured files are in `isolated_results/rc46-allocation-audit/`.
After building `Audit.csproj` in Release, the relevant fresh-process commands
were `Run-Capture.ps1 -Name raw-usb-02 -Mode raw -Transport 0` and
`Run-Capture.ps1 -Name scoped-usb-03 -Mode scoped -Transport 0`. Existing evidence
paths cannot be overwritten; reruns must use new names. The launcher verifies
the existing v3 profiler hash and removes the slow-only selector entirely.
Profiling applies only to its new synthetic child, not installed applications.

| Evidence | SHA-256 |
| --- | --- |
| raw-usb-02-managed.txt | 54E32B330D04584D68C661EEFB08EEA54BAABCD3892AB3899B4B8E7F4F926DFB |
| raw-usb-02-native.txt | A6C9EC9263709DB4D16A45014731F0F56B29C18BC5690DAE7E798E8455578FF1 |
| scoped-usb-03-managed.txt | 5E909E204F7FCC472582605491ADF2B40A3D670A7361036CC2E4E628FE5CC10A |
| scoped-usb-03-native.txt | 759C0341EDC33C3D309523AD060E8F2417589D4EC378AE654DA5AF824F6A97ED |
| Harness Program.cs | 5E3C0B07EF5CCCAA5825B16F3B0E29FD34A9D12011C78F361EC2A38CAE86C72C |
| Harness Audit.dll | 76ACB4C46E7AD71F9949AC8881CA81A21A4FCCB6A81261BE4AE3870FF98F8021 |
| Captured DS4WindowsTests.dll | B87735EA675911D8DC9C3056BDCAA64C870AACF4BDD378C7B87D0A72386584F4 |
| Captured DS4Windows.dll | 3E38149F27DEA1791648BD3A48C28F2EC901381BD11423D8FB01A4C0C701AE48 |
| Native v3 profiler | 820E64A3AF8BFCEBD79F8F6C2D6FCF02F3D2C0EA4117CF6C8C7E4109EB74A7BD |
