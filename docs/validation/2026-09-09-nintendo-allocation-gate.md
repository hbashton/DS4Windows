# Nintendo allocation-counter release gate — 2026-09-09

## Release held on independent CI failure

CI run `34380417498`, source `6e99c44bef78dddd0c1ba440e62a1e9066f71643`,
failed `WarmInputPublicationAndNativeAdmissionAllocateNothing (1)` with
4,752 bytes against the unchanged zero-byte assertion. The other four transport
variants and the corrected active-rumble shutdown tests passed. No tag or
release was published in response to that partially successful run.

The exact CI process was not profiled. Passing local repeats, source inspection,
and earlier unrelated allocation-counter diagnoses do not attribute that
specific observation. A new same-workload capture was required.

## Captured same-workload failure with object-level control

The opt-in diagnostic retains the production encoder, registered connection
authority, mailbox and writer admission through the test's actual `Target.Write`.
It warms 2,000 calls and measures 20,000 synchronous calls. The recording HID sink
does not copy reports. Controlled pressure uses a rooted BCL object graph and
large arrays **before** the measurement, not allocating work inserted into the
measured operation. The strict assertion fails on the first nonzero window;
results are not averaged or discarded.

`isolated_results/nintendo-allocation/native-pressure-boundary-04.trx` and its
native `.txt` log captured failures in all five transport variants. In the
registered left Joy-Con case (kind 1, batch 15):

- The counter increased by **7,480 bytes**, with managed thread 4 unchanged.
- Native recording found **zero object allocations and zero overflow**.
- Collection-count snapshots were unchanged at `16,9,4`; this does not exclude
  a later phase of an already-started background collection.
- Native window QPC `2909629206157..2909629254820` contains reason-7
  GC-preparation suspension and resume events at log lines 889–892. The
  collection-start event precedes the window.
- The same run's deliberate allocation recorded **144 bytes and exactly one
  `AllocationPositiveControl` object**, with its actual allocation stack and no
  overflow (native log lines 2140 and 2153 onward).

The native envelope is slightly wider than the managed counter pair; its QPC
timestamps are not exact managed-read timestamps. The profiler's experimental
`gcDepth` field is invalid and is not used as evidence.

Evidence SHA-256:

- Native TXT: `752C33FC8F86FF006E5D57BEB2D2CFAD7CE2963FB7AD78F0FFE19EBB5D2CB6DE`
- TRX: `10965BC0667611DCE3A28E9B3904EA24A6AE43A6FF897366B7D57A9F7202261F`

The existing local v3 native recorder preserves concurrent GC and captures full
object callbacks. Its binary SHA-256 is
`820E64A3AF8BFCEBD79F8F6C2D6FCF02F3D2C0EA4117CF6C8C7E4109EB74A7BD`.
Profiler variables were applied only to a new synthetic test host. Installed
software, running controller apps, drivers and production GC policy were not
changed. The native profiler is not a release dependency or payload.

The first `native-whole-01` diagnostic is rejected: an explicitly empty
slow-only environment selector suppressed object recording and invalidated its
positive control. `native-boundary-02` had a valid positive control but no failing
workload window, so it alone was not clearance. The pressure capture above is
the useful same-workload evidence.

## Runtime mechanism and scope of conclusion

The reproduction uses .NET 8.0.29. The corresponding accounting mechanism also
remains in the official .NET 8.0.30 source used to investigate the CI difference:

- [Per-thread allocation counter](https://github.com/dotnet/runtime/blob/v8.0.30/src/coreclr/vm/comutilnative.cpp):
  accumulated allocation bytes minus the unused allocation-context tail.
- [GC allocation-context repair](https://github.com/dotnet/runtime/blob/v8.0.30/src/coreclr/gc/gc.cpp):
  `void_allocation` clears context pointers without subtracting the discarded
  tail from accumulated bytes. The background marking phase calls
  `repair_allocation_contexts(FALSE)` during GC-preparation suspension.

Removing the unused-tail subtraction can raise the counter without allocating
an object. The new same-writer capture and positive control support this
explanation for the reproduced failures. They do not fabricate a native trace
for the original CI 4,752-byte event.

The correction uses the existing `StrictAllocationMeasurementScope` around only
the warmed synchronous counter window. Its bounded no-GC reservation is test
infrastructure that temporarily covers the test-host process, entered before
the initial counter and ended after the final counter. Entry/exit failure fails
the test. The zero-byte limit is unchanged;
real-allocation positive controls remain necessary. This is not a production
latency optimization, permanent runtime configuration or production GC-policy
change, skipped assertion, added tolerance, or retry-until-pass policy.
Raw opt-in diagnostic mode remains
available to reproduce unisolated accounting interference.

## Isolated-pressure and positive controls

`native-pressure-isolated-05.trx` passed **22 tests**: five transport cases,
five same-writer real-report-clone positive controls, eleven measurement-scope
lifecycle checks, and the native recorder's intentional-allocation control.
The five pressure cases cover **1,280 windows**, each retaining the same
20,000-call workload. Every isolated workload window measured zero; the native
positive control still recorded its actual 144-byte object with no overflow.

The real-report controls enable the recording sink's clone for one actual
writer call, retain the resulting active report, and assert that the unchanged
zero-byte gate throws for that allocation. Measurement isolation does not hide
the object or change the production writer.

Isolated-control evidence SHA-256:

- Native TXT: `C0A2E1D8B3A185DF2580B06D26EEC3F5A869B6A6174F09E35DDB09B0CCEF5293`
- TRX: `A77AC5A211D254E36CFFEF64D04B94BCBD9E296610CB3F9544C97A16F47C59AE`

These diagnostic runs are attribution and measurement controls, not the ordinary
release-validation run. The subsequent ordinary run, without profiler or
diagnostic switches, passed **144 focused tests** and the **unfiltered full
suite: 4,886 passed, 0 failed, 11 existing opt-in skips** (4,897 total).
Results: `isolated_results/nintendo-allocation/nintendo-allocation-fixed-focused.trx`
and `nintendo-allocation-fixed-full.trx`. Exact-source GitHub CI still must pass
separately before tagging.
