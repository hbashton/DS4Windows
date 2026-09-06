# Profile-load content preparation, 2026-09-04

This records the preparation and guarded cold-load prerequisites for the
controller-operated profile picker, plus changes to the existing `LoadProfileNew`
production call path. The latest named-worker checkpoint is at the end; it is
not yet connected to the picker runtime/UI. No physical or game validation is claimed.

## Implemented behavior

`DS4Control/PreparedProfileLoad.cs` performs the existing migration and DTO
deserialization, checks the original root before migration can replace it, and
maps the candidate into a private validation `BackingStore`. That deliberately
uses the canonical mapper: colors, regular/shift macros, curves, controller
settings and mode-shift conversion are not reimplemented in a second validator.
The private store never calls `ResetProfile`, because that method also resets
live static Mapping state.

Validation stores skip only backend-dependent key alias refresh. They still
parse and populate regular/shift key actions. Preparation consequently works
without a running keyboard mapping backend; live application resolves aliases
using the current backend as before. Existing curve normalization may log a
warning during shadow mapping and again during application. Preparation is cold
file/CPU work and is not suitable for a report callback or publication lock.

A prepared candidate owns its parsed DTO privately, fixed slot/path and migration
flag. Application atomically consumes that ownership once; it never rereads the
file. Preparation neither saves migration results nor changes live profile,
mouse, lightbar, output or temporary-profile metadata. It reports Missing,
Unreadable or Invalid separately. Wrong-root XML is rejected before the legacy
migrator can turn it into a default `DS4Windows` document.

The existing loader now calls preparation unconditionally. `File.Exists` is not
an authority for destructive fallback because it can also return false on access
or path errors. Only Missing retains the historical startup/reset/unplug path.
Invalid/Unreadable return false before live reset. After successful preparation,
the existing reset, canonical mapping, device post-load and migration-save path
runs. A private overload reports whether the profile state was changed so
`Global.LoadProfile` preserves active temporary metadata on preparation failure.

## Deliberately not claimed

Content preparation is not an atomic transaction or switch authority. Resource
failures, corrupted destination state and concurrent live mutation can still
fail after reset. Existing generic load/request revision semantics are unchanged:
an attempted regular/temporary load can advance its transition revision before
preparation. Existing UI callers can also change their selected name beforehand.

The picker must prepare first, then obtain the exact cold action lease and
atomically compare the expected old revision under a common per-slot mutation
boundary shared by direct auto-profile and worker loads. It must revalidate its
named catalog entry, exact runtime/token/pair lifetime and temporary-profile
state before changing the selected name. It also must stabilize the keyboard
backend during actual application: `RefreshOutputKBMHandler` can replace that
backend under its own lock, and the existing live mapper does not share that
lock. Skipping aliases in the shadow removes this dependency from preparation;
it does not fix the pre-existing live-apply race. Do not expose picker controls
until these guards, runtime suppression/drain and overlay integration are done.

## Evidence and review

The first two eight-failure runs (`profile-prepare-before-20260904.trx` and
`profile-prepare-after-20260904.trx`) failed in test setup: a sentinel key mapping
requested an uninitialized global keyboard backend. They are **not evidence of
the loader regression**. The fixture was corrected to set local sentinel state.
`profile-prepare-fixture-corrected-20260904.trx` then passed seven and failed one:
wrong-root migration reset the sentinel rumble value from 77 to 100. Original-root
validation fixed that case.

`profile-prepare-snapshot-20260904.trx`: 14 passes. Subsequent integration and
existing-profile/migration tests passed 42, then 43 cases. The final focused run
`profile-prepare-keyboard-isolation-20260904.trx` passes 44 cases. New cases cover
malformed/empty/wrong-root/namespaced XML, bad/overflow colors, invalid/overflow
regular macros, invalid shift macros, duplicate mappings, locked files through
both preparation and the actual loader, separate missing-file fallback, snapshot
ownership/file replacement, deferred migration save, a successful loader test
slot, preservation of temporary metadata and key preparation without a backend.
The existing full-profile fixture compares every serialized setting after the
prepared path against the prior canonical migration/DTO mapping path.

Full `profile-prepare-full-20260904.trx`: **3,151 passed, 3 existing live-audio
skips, zero failures**, 31 seconds. Tests use synthetic service/test slots and
file fixtures, not device discovery, driver installation or OS input injection.

Independent review confirmed the migration/DTO ownership approach and identified
the `File.Exists` fallback problem, the temporary test revision leak and the
ambient keyboard dependency. The fallback was repaired, the metadata fixture
moved to the test-only slot, and shadow mapping no longer reads the keyboard
backend. Revision/slot/keyboard synchronization at guarded live application
remain mandatory integration work, not waived acceptance gates.

This source is not in the immutable Desktop b52 candidate. No Program Files,
driver, startup task, association, running owner, Git history or release changed.

## Shared writer boundary and bounded auto-profile application

The following continuation added `ProfileMutationGate`, a per-slot, synchronous,
stack-owned Monitor scope. `Mapping` worker admission and its existing serialized
UI-edit helper now share that gate with direct `Global.LoadProfile` and
`Global.LoadTempProfile`. Generic requests still claim their revision before
waiting; stale requests are rejected after acquiring the gate and again after
cold preparation, before either live apply or the missing-file reset fallback.

`profile-boundary-before-20260904.trx` reproduced four failures in the old code:
both direct load types bypassed the held worker/UI boundary, and both stale load
types reset sentinel rumble 77 to 100 on missing-file fallback. After repair,
`profile-boundary-shared-20260904.trx` passed 45 focused boundary/preparation/mute
cases. This gate serializes these writers, not every possible profile mutation.

Independent review found that simply adding the shared gate to the old
auto-profile callback would wait for that gate while reports were paused. That
liveness risk was repaired before completion of this tranche:

- `Global.TryLoadAutoProfile` claims its ordinary auto-load revision and prepares
  the file before entering the writer gate. It rechecks revision, selected regular
  name where applicable, source identity and removal state outside and inside a
  bounded synchronous report pause. A missing/invalid/unreadable auto-profile
  preserves current state; the separate generic startup missing fallback remains.
- `BackingStore.ApplyPreparedProfileNew` is an extraction of the existing apply
  body, not a second mapper. Auto loads apply the fixed DTO snapshot with cold
  completion deferred. `CompletePreparedProfileLoad` handles program enumeration
  and migrated-file saving only after reports resume, while the writer gate is
  still held. File preparation is not repeated inside the pause.
- `DS4Device.TryHaltReportingRunAction` restores the previous reporting state in
  `finally`. The Switch 2 implementation rejects reentrant/timeout attempts
  without queuing their action onto the report thread. Existing void
  `HaltReportingRunAction` retains its Switch 2 deferred-action behavior; old
  compatibility tests still pass. Neither API is a physical slot ownership lease.
- `AutoProfileChecker` uses the new bounded path for connected controllers.
  Failed connected reverts retain the prior auto-profile marker for retry.
  Disconnected reverts also retain it if the load fails and temporary state
  remains; a missing-file fallback that already cleared temp state need not retry.

`profile-boundary-auto-refactor-20260904.trx`: 60 focused passes. Expanded
`profile-boundary-auto-tests-20260904.trx`: 68 passes, covering contention before
pause, replacement of the file after prepare, invalid pre-pause rejection,
source/revision loss inside the pause, migration saving after resume, rejection
without mutation/save, base exception recovery and Switch 2 no-replay behavior.
These are synthetic source/service and runtime tests, not hardware latency data.
Pre-review full `profile-boundary-full-20260904.trx`: 3,163 passed, 3 audio skips,
zero failures, 35 seconds. The disconnected retry correction was made afterward;
the final rebuilt full-run result is recorded in the dated platform ledger.

Final independent source re-review found no remaining blocker in this bounded
auto-profile path, but confirmed the following mandatory next work:

1. Source reference checks do **not** close the race after the check and before
   reset. Exact action-lease ownership must cover prepared apply; retirement can
   otherwise win while the old source is paused. Use the registration table's
   exact attached token on a cold worker, never wait on its own report lease.
2. Lifecycle order already includes service/runtime lifecycle -> profile gate.
   Do not acquire those lifecycle locks from inside the profile gate. Backend
   synchronization must also respect a consistent order: handler switching holds
   its KBM lock while refreshing aliases, so adding profile -> KBM blindly is an
   inversion trap. Live backend alias synchronization is still unfinished.
3. `PresetOption.ApplyPreset` performs extra DualSense writes after default loader
   wrappers return. The entire preset transaction, one claimed revision and its
   queued PostLoad transition need coverage. Merely wrapping each Global default
   loader is insufficient. Those wrappers are not yet covered by this gate.
4. The serialized UI-edit helper does not itself invalidate a queued revision;
   several quick setters mutate globals before the delayed gated save. Explicit
   logical-edit invalidation and atomic complete mutations are still required.
5. The picker must prepare before CAS of the expected old revision, and still
   needs guarded named worker integration, runtime suppression/drain, opener and
   overlay. No picker-facing feature was exposed by these changes.

No physical source/target/feedback matrix, game or latency gate is closed by these
tests. The immutable b52 candidate and installed software remain unchanged.

## Exact auto-profile lifetime admission

The next continuation closes the table-backed auto-profile check/apply race.
`ControlService` now retains the same single readonly registration table already
shared by its Switch 2 and typed legacy owners. `TryCaptureAttachedToken` captures
the exact sender/service/slot/registration lifetime without a lifecycle lock,
external owner callback, report pause or snapshot allocation. Capture is not a
lease. It happens before file preparation, not after waiting for the writer gate.

`ControllerProfileActionTarget` requires this token for Switch 2 runtimes and
supported typed legacy HID sources (exact DS4/DS3). Missing, closed, stale,
quarantined or unattached table identity cannot downgrade to reference checks.
Untabled DualSense, original Switch/Joy-Con and other existing sources retain
their prior reference/removal compatibility path; their universal typed lifecycle
coverage is not claimed by this change.

Admission order is profile writer gate -> synchronous source pause -> zero-wait
exact action lease -> current revision/name/source check -> prepared apply and
temporary metadata. The action lease is disposed **inside** the pause callback,
including on exceptions. A pending Switch 2 terminal-neutral publication in the
pause's finalization therefore sees a drained table. No table lease spans file
preparation, writer contention, migration saving or program enumeration, and no
service/runtime lifecycle lock is taken after the profile gate.

The auto-profile's queued `PostLoadSnippet` carries that same captured target and
reacquires a nonwaiting action lease for the whole synchronous output transition.
It cannot rebind its authority to a replacement lifetime, even if the same source
object and profile revision survive. Queue drains currently run outside admitted
Report callbacks for both Switch 2 and typed legacy HID; acquisition must remain
nonwaiting and must not move into a Report callback. Other existing PostLoad
callers now check source reference and removal state, but do not acquire a token
merely because an optional auto-profile target was absent. Startup Bound-state
and generic worker/preset integration remain separate obligations.

An admitted action may finish after retirement **begins**; its lease prevents
terminal admission, drain completion and slot reuse until it finishes. To make
that ordering truthful for legacy presentation, normal removal/service-stop now
perform lightbar/output/audio/feature teardown inside the terminal callback,
after action/report drain and before the existing neutral Commit. An exception
in presentation is now a failed terminal publication and quarantines the lifetime;
it is not reported as completed neutral. Quarantined retirement recovery also
waits the retained retirement claim's drain before touching presentation. A
Bound activation quarantine cannot have admitted an Attached-state action.

Software evidence:

- `profile-action-capture-20260904.trx`: 21 table tests, including zero warmed
  allocation across 100,000 token captures and no report-admission suppression.
- `profile-lifetime-integration-20260904.trx`: 71 passed, one outdated architecture
  assertion failed because it forbade any ControlService table field. That test
  now requires exactly one readonly shared field and keeps discovery independent.
- `profile-lifetime-races-20260904.trx`: 94 focused passes. Added cases include
  auto-load snapshot/lifetime replacement while waiting, exact target rejection
  of a stale queued output, busy admission/fresh retry, no missing/closed-table
  fallback, already-active action versus retirement/Close, real synthetic Switch 2
  pause plus concurrent pending terminal (including an exception), and legacy
  drain/partial-teardown quarantine/recovery without callback replay.
- `profile-lifetime-full-20260904.trx`: 3,175 passed, 3 existing audio skips,
  **one failure**. The previously intermittent filter zero-allocation test
  measured 2,312 bytes. Its isolated 15-test rerun passed. Neither that isolated
  success nor the absence of direct filter calls from these changes attributes
  or fixes the allocation. The zero threshold is unchanged; the dated platform
  ledger records subsequent diagnostics and ordinary full verification.

This completes the table-backed auto-profile admission change, not picker
integration, general transactional profile rollback, live keyboard-backend
synchronization, preset/UI revision invalidation or physical acceptance. The
single-use candidate must not be retried after consumption: the extracted apply
method resets before invoking its one-shot MapTo. A future retry-capable guarded
worker must claim the candidate before any destructive reset, or enforce unique
ownership by construction. No runtime candidate or installed files were changed.

Final review added explicit stale-target rejection and direct exact legacy-HID
classification/missing-table/retirement coverage: 95 focused passes. Independent
re-review confirmed both requested gaps closed and found no production blocker.
The opt-in allocation-boundary full run measured zero raw loop bytes; the next
ordinary full run passed 3,176 with 3 skips. After the final test additions,
`profile-lifetime-reviewed-full-20260904.trx`: **3,177 passed, 3 existing audio
skips, zero failures**, 33 seconds. The earlier 2,312-byte intermittent failure
remains unattributed; successful repeats do not establish consistent zero
allocation or close that release gate. No filter implementation or threshold
changed.

## Guarded named worker and legacy deferred-output correction

`Mapping.RequestNamedRegularProfileLoad` now submits an owned work item to the
existing coalescing worker. It does not create another mapper or claim a live
revision at enqueue. It captures the exact source target, requested/expected
names, profile root and expected revision. Cold preparation uses the canonical
snapshot described above. Admission is writer gate -> bounded synchronous source
pause -> zero-wait exact action lease -> nonwaiting keyboard-backend gate. A
final request-ticket check and expected-revision CAS are serialized with enqueue.
No request lock spans preparation, publication waiting, application or completion.

The context callback must be **lock-free and non-reentrant**: owned immutable
catalog/runtime snapshot reads only, with no monitor, dispatcher, I/O or profile
API calls. Temporary profiles, changed expected name/context, superseding requests
and stale lifetime authority reject before reset. Prepared ownership is now
claimed before reset, so accidentally reusing a candidate cannot destroy live
settings. Pending named requests displaced by coalescing get a terminal result;
pending ordinary requests are not replaced by uncommitted picker intent.
Completion runs asynchronously after mutation/admission locks are released.

Outcomes distinguish preparation failure, supersession, context change, busy
admission, unavailable source, applied, applied-with-cold-completion-error and
application failure. A failed migration save after apply is not rollback.
Application failure after reset may still leave partial settings. Keyboard alias
stability applies to this new named path only, not every existing writer.

Independent review found a real composition bug in the preceding auto-profile
change: base legacy HID drains its event queue while `FireReport=false`. The
queued post-load closure could run while the outer action lease was still active,
fail its nonwaiting reacquisition, and vanish. Deferred application now stages
the enqueue in the prepared item; `CompletePreparedProfileLoad` enqueues it once
after the outer action/pause has returned, before cold saving/program work.
The eventual callback still checks the exact lifetime and revision.

Regular worker requests now freeze the selected filename at enqueue. Explicit
UI selection passes its captured local name, since an older named apply can
publish another `ProfilePath` before UI enqueue. The ordinary loader uses that
fixed path and republishes the name only after a current state-changing load.
Direct callers omitting the optional name retain their current-profile fallback.
This does not make all UI selection/preset/linked-profile persistence transactional.

`profile-regular-name-before-20260904.trx` reproduced the wrong-name load (rumble
21 instead of requested 42). The first base-queue regression fixture was unsynced
and therefore never queued a transition; that setup failure was corrected without
starting HID workers. `profile-named-output-admission-20260904.trx`: **82 passes**,
including the actual deferred closure acquiring/releasing exact admission, UI
selection interleaving, coalescing completions, stale lifetime/CAS, invalid files,
backend contention, key aliases, save failure and unique candidate ownership.
The dated platform ledger records full-suite verification and review outcomes.

Final `profile-named-reviewed-full-20260904.trx`: **3,202 passed, 3 existing
live-audio skips, zero failures**, 38 seconds. Independent re-review found no
remaining production blocker in this bounded change. `AdmissionBusy` also covers
a refused/expired publication pause: that API does not distinguish contention
from an inactive source. `SourceUnavailable` classifies exact admission failures
inside an admitted pause, not every possible retirement race. A retry must capture
fresh authority and submit a new request, never replay the consumed work item.

Still required before exposing the picker: immutable catalog authority and
rename/delete invalidation; exact confirmation-to-worker attachment; runtime
neutral/Commit and motion/mouse suppression/drain; opener/overlay/UI completion
and persistence; full preset/UI logical-edit invalidation. No production caller
currently invokes the named API. The full physical/game/latency matrix and the
intermittent filter-allocation attribution remain release gates. b52 is unchanged.
