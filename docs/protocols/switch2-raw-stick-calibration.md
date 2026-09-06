# Switch 2 per-physical-stick calibration

Status, September 4: collector, cold file store, startup loading, live
capture/save/reset/cancel runtime operations and the Switch 2 Controls wizard
are implemented and software-tested. Physical validation is still required.
The immutable staged b51 artifact contains startup-loaded calibration only;
the live runtime and wizard require the subsequent b52 candidate.

## Reference and workflow

GPL-3.0-or-later Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`, `src/gui.py:2158` onward
(`JoystickCalibrationWizard`), provides the physical-side workflow: ten moving
seconds to collect raw extrema, two still seconds followed by three seconds
collecting center samples, and independent positive/negative axis travel.
`src/gui.py:2349` averages center and persists per-controller/per-side records;
`src/controller.py:3961` applies the local values. Copyright/provenance is
retained in the collector source. No PadForge code is copied.

`Switch2RawStickCalibrationCollector` consumes existing decoded canonical
frames for one exact input descriptor (model, framing, device/transport
generation and QPC frequency), one physical stick side, and the existing
opaque persistent peer identifier. Its owner must serialize observation and
cancel and provide the trusted peer-to-session binding. The collector does
not discover identity, own a transport, or write controller memory.

Extrema use every accepted raw 12-bit report. Movement-time comparisons use
approximately 50 ms observation intervals, matching the reference wizard;
applying its ten-count threshold separately to 250/500 Hz reports would make
completion depend on input rate. No polling thread, sleep or timing change is
added to the gameplay path. Accumulators have fixed size, with no sample list.

An input gap above 100 ms contributes no time and discards unfinished stationary
qualification. Touch excursions beyond the 45-count anchor threshold are
checked on every raw report. Movement resets the entire stationary center
window, so separate quiet fragments cannot qualify. Duplicate/reversed host
time, stale descriptors and absent physical sides are rejected. The confirmed
Pro USB counter discontinuity remains diagnostic; BLE retains its existing
backward-counter rejection.

The result requires at least 256 counts in each signed X/Y direction and passes
the existing `TryValidateAdoptable` 12-bit endpoint checks. The 256-count floor
is an application quality policy, not a Nintendo hardware specification. A
failed attempt exposes no calibration. A successful result uses the existing
`Switch2StickCalibration` value type and `Switch2ProfileAxisProjection` math;
factory/user-SPI snapshots remain unchanged. Physical accuracy still requires
proper user rotation/release and hardware acceptance.

## Cold storage

`Switch2RawStickCalibrationFileStore` stores each physical side separately in
the selected app-data root's `StickCalibration` directory. File names contain
only the existing opaque peer identifier and model/side discriminator. One
record is exactly 51 bytes:

| Offset | Content |
| --- | --- |
| 0 | Four-byte `S2S1` magic |
| 4 | Version 1 |
| 5 | 16-byte local peer pseudonym |
| 21 | Model byte |
| 22 | Physical side byte |
| 23 | Six little-endian uint16 values: center X/Y, positive X/Y, negative X/Y |
| 35 | First 16 bytes of SHA-256 over bytes 0–34 |

The digest detects corruption; it is not authentication against local tampering.
Loads check exact size before reading into fixed stack storage and validate
identity, side, digest, travel and endpoints. A unique scratch file is flushed
then atomically moved over the exact destination. Failed validation preserves
the prior record. Reset removes only the exact peer/model/side file. The store
contains no calibration queue and no report callback: callers must schedule
these blocking cold operations away from the UI and input/publication locks.

## Runtime application

ControlService opens the selected app-data store and supplies it to the USB
and Bluetooth production coordinators. Each coordinator binds the runtime at
the existing trusted physical-peer boundary before activation. Pro loads both
physical sides; standalone Joy-Cons load only their physical side; joined
Joy-Cons use two independent peer records. Invalid, missing or unreadable
optional records leave the source calibration in effect.

`Switch2RawStickCalibrationBinding` is an immutable application snapshot. Cold
loads run outside the runtime's publication lock. Adoption then checks that
the same runtime is still Created/unpublished and unbound: a slow load cannot
stall activation under that lock or install into an already active runtime.
Generic active rebinding is refused; live changes use an exact operation receipt.

The original raw stick observation, descriptor, report kind and factory/SPI
snapshot now travel through the existing profile frames. The runtime validates
that evidence and applies its current local binding under its existing
publication gate immediately before writing DS4State. The original physical
side/orientation math is shared, including horizontal right Joy-Con mapping to
logical left axes. The three former pre-mapper application calls are removed.
Consequently a previously projected queued frame or an unchanged joined half
cannot retain an old local override after save/reset. Local provenance flags
are recomputed; source calibration metadata is preserved. No input subscription,
polling delay, extra queue, per-report disk I/O or managed allocation is added.

The canonical frame retains raw bytes, descriptor, counter/timing and original
factory/SPI calibration evidence. Its local per-side overrides are separately
marked by `HasLocalCalibration` and the corresponding profile-frame flags.
Projection uses the existing high-resolution axis math; it does not reconstruct
raw stick values from byte-scale DS4State.

Peer identity is transport-scoped evidence: USB currently derives its opaque
peer from the physical container identity, while BLE uses the stable Windows
device ID. Matching USB/BLE peer values for one physical Pro have not been
established. Automatic cross-transport sharing is not claimed.

## Verified and remaining integration

`raw-stick-calibration-foundation-20260904.trx`: **20 passed, zero failures**.
Coverage includes real Common05 decoder/session input for USB and BLE Pro and
both BLE Joy-Con sides, asymmetric center/travel and ordinary axis projection,
250/500 Hz synthetic report cadence, pauses, touch between UI samples, stale
generation/time, cancellation, insufficient travel, preserved USB discontinuity,
and warmed zero managed allocation. Persistence cases cover raw field layout,
every corrupted byte, malformed travel with a recomputed digest, length fences,
peer/model/side mismatch, newest-write/reopen, exact reset and renamed records.
No physical controller is opened by these tests.

Full suite after both helpers: `raw-stick-calibration-full-20260904.trx`,
**3,023 passed, 3 existing live-audio skips, zero failures**, 29 seconds.
Four additional cases then exercise the real basic BLE 07/08/09 decoders,
correct primary/secondary physical-stick selection and eight-bit counter wrap.
The combined targeted run `raw-stick-calibration-basic-20260904.trx` has
**24 passed, zero failures**. These four cases are included in the subsequent
runtime-application full runs below.

Runtime application tests cover USB/BLE Pro, both standalone physical sides
and orientations, the real joined input sink, stale generations/transport,
malformed/throwing storage, activation racing a blocked load, immutable old
frames and zero warmed allocation/no store calls during 20,000 applications.
A fake-native-lease USB owner test verifies a calibrated raw value reaches
the existing Xbox One egress with high-resolution precision. No physical
controller is opened. `raw-stick-runtime-adversarial-20260904.trx`:
**210 passed, zero failures** across the selected integration suites.

The first full runtime run exposed a concurrent joined shutdown race:
`raw-stick-runtime-full-20260904.trx` had **3,040 passed, 3 skipped, 1 failed**.
A controlled-window regression reproduced the overly broad callback fence;
it is documented with the repair in the September 4 platform validation
ledger. The joined-repair full run `raw-stick-runtime-stop-fixed-full-20260904.trx`
passed **3,043 tests, 3 existing audio skips, zero failures**. This is software
verification of the loaded calibration path, not wizard or hardware completion.

## Live runtime workflow and concurrency

`Switch2RuntimeRawStickCalibration` binds one physical side to the exact runtime,
immutable basis, opaque peer, slot and profile revision. Begin requires a valid
admitted raw observation and excludes overlapping raw/magnetometer operations.
It immediately emits an ordinary neutral report through the canonical mapper,
suppresses subsequent gameplay state while collecting, and disables automatic
disconnect for the operation. Joined duplicate timestamps do not count twice.
No terminal report, physical output command or controller flash write is made.

Begin also clears all high-rate mouse sources. Source setters and clear serialize
under the runtime gate; a presenter revision rejects older worker snapshots and
resets fractional integration state. An external-output fence outside that gate
waits for any already-entered mouse output before Begin acknowledges release.
Begin accepts a cancellation token retained by the exact operation receipt.
Cancellation revokes that receipt even while Begin is waiting for a neutral
report callback or external mouse output. The cold mouse fence checks cancellation
at 20 ms lock-attempt intervals; this adds no polling to report publication.
An already-entered external mouse call cannot be undone, but cancellation stops
waiting and the revision fence prevents subsequent stale output admission.
The worker remains usable after cancellation/completion. Real registration,
Mouse, Mapping and Xbox egress tests verify gyro-stick and directional-swipe
release on USB Pro, BLE Pro and joined input without retiring the runtime.

Explicit completion runs on a cold worker. Storage failures preserve the live
binding and ready capture for retry/cancel. Successful storage is adopted only
after already-reserved publications finish and exact runtime/profile/slot/basis
admission is rechecked. A one-second admission wait is bounded; success on disk
without live adoption returns `StoredNotApplied`, never a false live success.
Cancellation/retirement cannot undo a write that already entered persistence;
the UI must accurately distinguish that outcome from an unstarted cancelled save.

All stores for the same normalized, case-insensitive directory share a cold
serialization gate. Binding loads (both joined peers as one snapshot) and each
entire mutation use it. After acquiring it, queued workers recheck cancellation
and exact runtime/profile/slot before touching disk. An already-entered retired
write finishes before a successor load/save/reset; a merely queued cancelled
write is skipped. Thus old I/O cannot undo a successor's newer save or reset.
Cancellation and physical report publication do not wait on this cold gate.
This ordering is process-local, consistent with the app's single-owner process;
it is not a cross-process persistence protocol.

Verification before independent review: `raw-stick-live-full-20260904.trx`,
**3,067 passed, 3 skipped, zero failures**. Review then found a cross-runtime
late-write ordering race and a lingering gyro-mouse source. Both were reproduced
in `raw-stick-review-races-before-20260904.trx` (**2 failed**) before repair.
`raw-stick-review-ordering-20260904.trx` passes **72 focused cases**, including
queued cancellation/slot/profile, successor load/reset, shared directory gates,
mouse output fencing, and zero warmed allocation. The full repaired run
`raw-stick-live-reviewed-full-20260904.trx` passes **3,074 tests, 3 skipped**.
Six subsequent real canonical mapping release cases pass in
`raw-stick-canonical-release-20260904.trx`. Independent source re-review found
no remaining blocker within this scope; it does not establish hardware parity.

The subsequent full canonical run had **3,079 passed, 3 skipped, 1 failed**:
an existing legacy Joy-Con zero-allocation test measured 1,960 bytes once. Its
strict assertion is unchanged. Eight fresh-process focused repeats passed;
a supplemental dedicated-thread/no-inline-loop diagnostic also verifies both
zero peer-access allocation and detection of a known positive allocation.
Latest full run `raw-stick-live-allocation-diagnostic-full-20260904.trx` passes
**3,081 tests, 3 existing audio skips, zero failures**. The earlier intermittent
reading's cause is not established; details remain in the September 4 ledger.

## Switch 2 Controls wizard

`Switch2StickCalibrationWindow` is opened from the profile editor's Switch 2
Controls section for the current active Switch 2 runtime. Its dedicated view
model binds the exact runtime, slot and profile revision. Pro and joined pairs
offer both physical sides; a standalone Joy-Con offers only its actual side,
regardless of horizontal mapping orientation. Calibration is PC-local and is
not a profile edit or controller-memory write.

The window shows rotate/settle/center progress from the existing collector,
explicit Save, Cancel and confirmed per-stick Reset. A 100 ms UI timer reads
progress only; it does not collect samples or subscribe to controller input.
Begin and storage run on cold workers. Disk outcomes remain visible across
progress refreshes, including retryable failure and StoredNotApplied. Closing
or invalidating the window cancels capture and live adoption, but cannot undo
an already-entered disk write. No late async continuation updates a closed UI.

Independent review found a worker-to-UI receipt handoff gap: closing while Begin
was waiting could leave capture suppressed until the worker returned. The view
model now owns a cancellation source before scheduling Begin and through the
await continuation. The runtime registers exact-receipt cancellation after
reservation and outside its publication gate, checks the retained token on every
admission, and disposes registration outside that gate. Cancellation after the
worker returns but before the UI claims the receipt is reaped before the next
physical frame's suppression check. Independent re-review found no remaining
scoped blocker; an additional runtime test covers cancellation with an actual
blocked fake mouse-output callback and resumed input before that call returns.

Verification:

- `raw-stick-wizard-visual-fixed-20260904.trx`: 98 focused passes after fixing
  a real read-only progress property's default two-way WPF binding error. Earlier
  failed UI runs also exposed a test-only application resource setup issue;
  those failures remain in their original TRX files.
- `raw-stick-wizard-handoff-joined-20260904.trx`: 18 editor executions including
  USB/BLE Pro, standalone physical sides/orientations, joined chosen-peer-only
  persistence, delayed UI handoff, cancellation, reset retry and context changes.
- `raw-stick-wizard-full-20260904.trx`: 3,102 passed, 3 existing audio skips,
  zero failures. `raw-stick-wizard-runtime-fence-20260904.trx`: 56 focused passes
  after adding the end-to-end blocked-mouse cancellation case; no production
  source changed after that full run.
- Final complete suite including that additional runtime case:
  `raw-stick-wizard-reviewed-full-20260904.trx`, **3,103 passed, 3 existing audio
  skips, zero failures**, 32 seconds.
- The existing theme-resource test renders start/ready at 460 and 620 pixels in
  light and dark themes without showing a window or accessing hardware. The
  default layout keeps Reset visible; compact content scrolls and Close remains
  outside the scrolling area. Eight PNGs are retained in the Desktop lab under
  `evidence/stick-calibration-ui-2026-09-04`. These are offscreen WPF checks, not
  live app, input, calibration accuracy, DPI or hardware acceptance evidence.

Remaining gates: physical raw input through canonical mapping and each applicable
virtual target, identity-bound reconnect/pair replacement, and the guided hardware
workflow. The separate profile-picker parity gap and full platform matrix remain
open. Do not infer physical completion or latency from software test counts.
