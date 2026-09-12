# Native Bluetooth write-credit allocation gate — 2026-09-12

## Release held; same-workload attribution required

The first RC4.5.9 rebuilt full run failed
`NativeCreditProbeAndSynchronousWriteAllocateZeroAfterWarmup`: **7,328 bytes**
against its unchanged zero-byte assertion. The run ended 5,537 passed, one failed,
11 existing opt-in skips. Its evidence remains in
`isolated_results/rc459-publication/rc459-full.trx`. That original process was not
profiled; subsequent observations do not supply an object trace for that event.

The test omitted the existing bounded `StrictAllocationMeasurementScope`, whose
purpose and runtime mechanism are documented in the
[Nintendo allocation investigation](2026-09-09-nintendo-allocation-gate.md).
That prior investigation alone was not accepted as attribution for this workload.

## New capture of the actual writer and credit probe

The opt-in diagnostic calls the same `CanSubmitNativeCommand` and synchronous
`TryWrite` path, warming 256 calls and measuring 10,000 calls. The 32 fake native
request slots are already populated; report history/copying are disabled.
Diagnostic pressure creates a rooted 250,000-node reference graph and large
arrays outside the measured calls. Each batch retains the exact zero assertion
and stops on its first failure; no failure is averaged away or retried to pass.

The valid `raw-boundary-03.trx`/`.txt` capture reproduced a **3,496-byte** jump at
batch 17, with zero object-allocation callbacks and zero recorder overflow.
The managed thread remained 4; collection counts remained `15,6,3`.
Native QPC window `333652282797..333652531848` contains GC-preparation
`SuspendStarted detail=7` at `333652325854`, followed by suspension/resumption
and collection-finished callbacks. The collection started before the window.
See native log lines 235–252. Native Begin/End bracket a slightly wider interval
than the managed counter pair; the recorder's derived `gcDepth` is not evidence.

Crucially, the same run's positive control copies the actual 398-byte report
inside the same writer seam. It records **424 allocated counter bytes and one
array object**, with the allocation stack through `ControlledNativeIo.TrySubmit`
and `DualSenseBluetoothRealtimeWriter.TryWrite`, and no overflow (lines 266–282).
The positive control also proves that the unchanged zero assertion rejects that
real report allocation. This corroborates allocation-context accounting repair,
not a new writer object allocation, for the reproduced 3,496-byte observation.

Captures 01 and 02 are explicitly **rejected for object-allocation attribution**:
their real-report positive control showed 424 bytes but no object callback.
In the diagnostic PowerShell host, calling
`Environment.SetEnvironmentVariable(name, $null, 'Process')` left a present-empty
`DS4W_ALLOCATION_NATIVE_SLOW_ONLY` variable. The native selector tests presence,
not its value. The corrected launcher uses `Remove-Item Env:...` and verifies
absence before launch; capture 03 includes the required successful callback.
All rejected evidence remains preserved.

## Correction and validation

Only the test's warmed synchronous counter pair now uses the existing strict
scope. Scope entry/exit failure still fails; the zero-byte limit remains zero.
No production transport, allocation behavior, GC policy, JIT setting, timeout,
or runtime configuration was changed. Raw opt-in tracing remains available.

`isolated-boundary-04` exercised **256 pressure windows**, each retaining the same
10,000-call workload and measuring exactly zero. Its positive report-copy control
still observed 424 bytes and one object. All 259 native recorder windows (including
initialization and positive-control envelopes) had zero overflow. Both tests passed.
The subsequent ordinary rebuilt, no-profiler/no-diagnostic focused run passed
**59/59 tests**, including writer ownership, native backpressure and measurement
scope lifecycle controls; no skips. Evidence: `ordinary-focused.trx`.
The subsequent ordinary unfiltered rebuilt release run passed **5,539 tests,
zero failures, 11 existing opt-in skips** (5,550 total), with no profiler or
diagnostic switches. Evidence: `isolated_results/rc459-publication/rc459-full-final.trx`,
SHA-256 `969701AE92C988D50B9E3FDF1BADD64DB1E4221DE2BD350B5B4CCF23BFE944C8`.
Independent exact-source release CI remains a separate gate.

All diagnostic artifacts are under `isolated_results/issue81-native-credit-allocation/`:

| Evidence | SHA-256 |
| --- | --- |
| `raw-boundary-03.txt` | `A46D04ECE81CB8594B2FC3622E43455BE8AD1DBD27AF86E12C53AEB52C9FA029` |
| `raw-boundary-03.trx` | `206302FC131D0387CF3615EAF3C6706F7699F3A57E9EB6B0EAB031807CD27E14` |
| `isolated-boundary-04.txt` | `96DC0E92BC573D7B8C1BC4790652BADBCCE926F5B3A20B5BFB11EC0FE0ACDB02` |
| `isolated-boundary-04.trx` | `2202045F1B062744BD5D390261A6839E462781DCDDA8C657DB86F0E17D6128FF` |

The existing v3 profiler binary was verified against
`820E64A3AF8BFCEBD79F8F6C2D6FCF02F3D2C0EA4117CF6C8C7E4109EB74A7BD`.
Profiler settings applied only to new synthetic test processes; no controller
apps, installations, drivers, or active hardware were touched by this investigation.
