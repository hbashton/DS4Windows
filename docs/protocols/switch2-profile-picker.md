# Switch 2 controller-operated profile picker

September 4 source checkpoint: input projection, deterministic selection/release
reducer and a guarded named-load API in the existing coalescing worker are
implemented. The reducer and worker are not yet connected to each other, live
runtime suppression, a configurable open action, the catalog owner or overlay.
This is not a user-accessible picker or a hardware/feature-parity completion.
The staged b52 calibration candidate is unchanged and does not contain this work.

## Source facts and provenance

Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`:

- `src/controller.py:6754`, `_handle_profile_selection_input`: held-orientation
  stick navigation at strict normalized magnitude above 0.6, D-pad navigation
  only for Pro/joined, physical A/B interpretation under the selected layout,
  initially held controls seeded rather than fired, rising-edge navigation,
  confirm/cancel and post-interaction A/B release drain.
- `src/controller.py:4299`: raw interaction input is neutralized before the
  virtual callback, including sticks, buttons, triggers and motion.
- `src/gui.py:15096–15200`: a change-list snapshot, initial next-profile
  selection, wraparound, a 180 ms navigation debounce and explicit confirmation
  or cancellation. Auto mode is a separate one-second inactivity apply, not
  equivalent to the manual picker. The overlay shows neighboring/selected names.
- `src/config.py:76`: physical button masks used by that picker. The relevant
  source files state Copyright (C) 2026 TommyWabg, GPL-3.0-or-later. Attribution
  is retained in the new source. No PadForge or opaque WinUHid code is used.

The audited manual picker is not identical to SDL mini gamepad labeling.
SDL-hifihedgehog `d98c5804a9d20b0d96e993741797878c86b8f1e1`,
`src/joystick/hidapi/SDL_hidapi_switch2.c:901` / `:981`, maps horizontal left
raw bit 18 and right raw bit 2 to SDL South. Switch2Connect's manual picker
instead uses left bit 19 and right bit 3 as Xbox-layout confirmation. Its
virtual-controller mapping code is another layer, with layout preprocessing
in `src/controller.py:5074` onward; do not infer behavior from one layer alone.
This implementation follows the source's picker-specific controls explicitly
and leaves the existing game mapper unchanged. Physical ergonomic correctness
and consistency of in-app labels still require the Joy-Con hardware matrix.

| Picker mode | Xbox-layout confirm | Xbox-layout cancel | Navigation |
| --- | --- | --- | --- |
| Pro / joined | physical B, bit 2 | physical A, bit 3 | Either stick, real D-pad |
| Left vertical | Down, bit 16 | Right, bit 18 | Physical stick |
| Left horizontal | Left, bit 19 | Down, bit 16 | Rotated physical stick |
| Right vertical | B, bit 2 | A, bit 3 | Physical stick |
| Right horizontal | A, bit 3 | X, bit 1 | Rotated physical stick |

Nintendo layout swaps confirm/cancel. The profile frames supply existing
calibration and orientation math; no independent packet parser, stick mapping
or transport owner is introduced. Navigation compares signed 16-bit axes, not
byte-quantized DS4State. Both joined halves form one navigation observation.

## Implemented reducer contract

`Switch2ProfilePickerInput` accepts existing admitted Pro/Joy-Con profile frames
and records the exact model/mode/transport, physical generations, pair epoch,
QPC frequency and face layout. A resolved open/cycle binding may be supplied by
the future owner; no binding is currently registered. D-pad or A/B input cannot
also act as Cycle in that observation.
The owner must retain the exact opening binding and continue evaluating that
same physical binding throughout drain, even after the profile changes. Looking
up only the new profile's action can miss a still-held rail/shoulder/custom-stick
opener and prematurely release it. This identity is not inferable from a boolean
`cyclePressed`; frozen binding ownership is mandatory at integration.

`Switch2ProfilePickerSession` is single-owner, bounded-state and allocation-free
after construction. The future cold owner supplies an immutable profile catalog;
the reducer holds only its fixed count and an index. It never loads profiles,
reads files/globals, calls UI, creates tasks or owns a timer/queue. Initial entry
selects the next index, or zero when the current profile is absent. Held inputs
are seeded, navigation wraps and debounces at 180 ms of the input QPC domain,
and confirm/cancel are not delayed by that navigation debounce. Equal timestamps
are accepted for distinct already-admitted edges; reversed time is rejected.

Opposed directions do not issue two changes; simultaneous newly pressed
confirm/cancel resolves to cancellation. Confirmation transfers intent once
through `TryTakeConfirmation`, and does not itself authorize a slot mutation.
An external profile/basis change before transfer invalidates intent. After
transfer, the request's own new profile revision cannot re-enable confirmation
or prematurely end release drain. Foreign physical lifetime frames cannot
navigate or drain an operation.

The transfer API requires the owner to supply freshly read current context and
profile revision under its publication gate; a setting can change without a new
report. Physical release alone does not resume gameplay while a confirmed intent
is waiting on a delayed worker. Suppression ends only after intent transfer or
revocation AND valid release evidence. Cancel/Invalidate cannot rewrite an
already transferred or terminal outcome, and completed drain is a one-way latch.

Release is recorded independently of layout/orientation semantics. Drain waits
until the physical face/directional clusters and the open/cycle binding are
released, with both sticks within a separate 20% center band. This deliberately covers
more than the donor's A/B-only drain: held navigation must not leak into the new
profile, including when it changes layout/orientation. Navigation still starts
above 60%; backing off to 59% is not release. The 20% band is an explicit UI
hysteresis policy, not a controller specification, configurable game deadzone,
or assertion that the sticks have reached exact physical center. Completed
drain cannot repress ordinary subsequent input or reopen the operation.

## Mandatory runtime and UI integration still open

Independent architecture review found that `CompositeDeviceModel`'s existing
UI selection method mutates `Global.ProfilePath`, linked-profile storage and UI
state before queuing the regular profile worker. That path is unsuitable for
a delayed picker confirmation. The current guarded temporary loader is not a
replacement for choosing a regular profile.

The main review also checked `BackingStore.LoadProfileNew` in `ScpUtil.cs:6328`
onward: it resets live profile/mouse/mapping values before deserializing the DTO
and can return false after those resets. Restoring only `ProfilePath` after a
failed named load would therefore not restore the old live profile. Stage and
validate the target before destructive application, and define truthful failure
semantics; do not bolt a name-only rollback onto this loader and call it atomic.

Required implementation:

1. Add a guarded, named regular-profile request to the existing `Mapping`
   coalescing worker. Atomically compare the expected old profile revision before
   claiming a new revision, with an exact slot/token/runtime guard. Move selected
   regular-profile mutation to the serialized profile-mutation boundary. A stale
   confirm must not bump past a newer UI/auto-profile revision or write the name
   of a replacement slot. Revalidate the named catalog target, not a mutable UI
   index. Define load failure and linked-profile persistence outcomes explicitly.
2. Use the registration table's exact action lease only from a cold worker after
   the confirming report returns. Acquiring that lease inside the report callback
   waits on its own report lease. Revalidate the exact receipt at actual load,
   since a short-lived enqueue lease cannot cover a later asynchronous load.
3. Attach picker processing under the runtime publication gate, before motion and
   `On_Report` side effects. Entry must send one ordinary neutral through Mapping
   and Commit so existing keyboard/mouse holds release; suppress game controls,
   extra raw mappings and motion throughout selection/drain. Clear and revision-
   fence high-rate mouse output without blocking a report on external OS output.
   Explicitly exclude overlapping calibration; revoke on lifecycle/pair/slot,
   temporary-profile and active orientation/layout changes. Physical drain and
   one-shot switch authority must remain separate.
4. Bind a configurable controller open/cycle action before game mapping consumes
   its activation report. Snapshot ordered stable names on the UI side, render
   neighboring/selected profiles without taking game focus, and cancel cleanly.
   Target deletion/reorder, delayed dispatcher work, quick-setting flush and UI
   updates require exact-context revalidation. Do not rebuild the game mapper.
5. Cover real runtime/Mapping/Commit/output composition, held synthetic input,
   delayed confirm versus external revision, slot reuse, profile deletion,
   calibration, layout/orientation and terminal races, plus zero warm allocations.
   Then test physical Pro USB/BLE and Joy-Con standalone/joined, every target and
   actual game behavior. Keep the feature inaccessible until its runtime path is
   complete and fail-closed; software tests do not establish physical acceptance.

## Software evidence

`profile-picker-foundation-20260904.trx`: 19 initial focused passes.
`profile-picker-drain-20260904.trx`: 22 passes after separating intent transfer
from physical drain, with pending-cancel, own-revision/layout and standalone
orientation-change coverage. Full `profile-picker-foundation-full-20260904.trx`:
3,125 passed, 3 existing live-audio skips, zero failures, 33 seconds.

Two additional precision/allocation cases pass in
`profile-picker-precision-allocation-20260904.trx` (24 total): strict signed
thresholds remain correct with a deliberately centered legacy byte, and both
Pro/Joy-Con frame projection allocate zero managed bytes after warm-up over
100,000 successful projections. The reducer separately checks 100,000 warmed
observations. These are component tests, not whole-report latency measurements.

The complete pre-review precision run `profile-picker-precision-full-20260904.trx`
passed 3,127 tests with 3 existing audio skips. Independent adversarial review
then found the delayed-confirm premature-resume race and insufficient release
threshold. Both were reproduced in `profile-picker-review-before-20260904.trx`
(2 failures) before repair. The final-context transfer fence and one-way outcome
handling were also strengthened. `profile-picker-reviewed-20260904.trx` passes
30 focused cases, including foreign neutral, delayed transfer/cancel/invalidate,
basis changes without an intervening report, post-transfer invalidation, 59%
held versus 19% release and unchanged warmed zero-allocation assertions.

Independent final re-review found no remaining reducer/helper blocker. It
confirmed the race repair and highlighted the frozen open-binding obligation
above. Runtime/worker/overlay composition is still unimplemented; a helper review
does not close those gates or prove controller/game behavior.

Final complete suite `profile-picker-reviewed-full-20260904.trx`: **3,133 passed,
3 existing live-audio skips, zero failures**, 32 seconds. Only an integration
contract comment and documentation changed after that run; no executable behavior
changed. The new code is not in the staged b52 portable candidate.

## Subsequent profile-load prerequisite

[Profile-load content preparation](profile-load-preparation.md) is now in the
existing loader. It validates a private canonical mapping snapshot before live
reset, preserves temporary metadata on invalid/unreadable rejection, and retains
the legacy missing-file fallback. Full suite: 3,151 passed, 3 audio skips.

This supersedes the earlier reset-before-deserialization finding for that loader,
not the integration requirements above. Guarded named selection still needs
prepare-before-revision-CAS, shared per-slot serialization with direct auto-profile
loads, exact runtime/action-lease guards, and synchronization with keyboard-backend
replacement during live apply. Runtime suppression, configurable opener and
overlay are still unimplemented. The picker remains inaccessible to users.

The subsequent shared-writer/auto-profile work is also recorded in that document.
Direct regular/temp loads now use the worker/UI gate and reject stale revisions
before reset. Auto-profile parsing and lock contention no longer occur inside
report suppression, and failed reverts remain retryable. Exact token action
leases, live KBM synchronization and full preset/UI transaction invalidation are
still prerequisites for guarded picker commits. This is not completed live picker
integration and does not change the physical acceptance requirements.

The subsequent exact auto-profile lifetime change is documented in
`profile-load-preparation.md`. Auto loads now capture an attached token before
preparation and reacquire it inside the bounded synchronous pause; their queued
output work retains the same exact target. This supplies a proven composition
for the picker worker, but the picker must capture authority at confirmation,
not recapture the slot after dispatch. It still needs expected-revision CAS and
named selection, runtime suppression/Commit/mouse fences, opener and overlay.
The feature remains inaccessible. The first full suite of this continuation had
one recurring filter-allocation failure; focused lifecycle passes do not waive
that gate or the required controller/game validation.

The latest named-worker checkpoint is detailed in `profile-load-preparation.md`.
It prepares before exact admission and expected-revision CAS, stabilizes key-alias
mapping without waiting for backend replacement, and completes displaced requests.
Generic regular reloads freeze their name, including the UI's local selection.
Legacy output enqueue is deferred until the outer action lease has been released.
The worker API has no production caller yet: catalog lifetime, runtime neutral/
Commit/mouse fencing, opener, overlay and guarded UI persistence are still required.
These changes are not in b52 and do not establish hardware or feature parity.
