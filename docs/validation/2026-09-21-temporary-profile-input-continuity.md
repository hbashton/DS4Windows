# Temporary-profile input continuity — 2026-09-21

## Report and scope

The reporter describes input freezes on both pressing and releasing a Profile
special action with automatic return on trigger release. This change joins the
mouse ownership fixes in RC4.6.3. The starting point remains published main
`62f206ab6a7d4e83d50387ed52cc5e967a076e48`; earlier private haptics edits are excluded.

## Established code defects

1. While an activation was pending, the automatic-untrigger branch cleared the
   action latch on the next held frame. Subsequent frames resubmitted the same
   load with new revisions, superseding preparation already in flight.
2. Release depended on committed `useTempProfile`. A release before activation
   completed therefore failed to cancel that activation.
3. The asynchronous ordinary worker reset and repopulated live mappings without
   the publication boundary already used by guarded named/automatic selections.
   The profile mutation gate serialized writers, not report readers.
4. Automatic activation directly released the switch trigger's keyboard mapping.
   If another source still held the same key, the next aggregate commit saw no
   count transition and did not reassert it.
5. Every post-load event ran the full backend/audio reconciliation on the physical
   input queue, despite same-output switches already retaining their primary pad.

The historical 500 ms report-pause value was a maximum handshake wait, not a
specified half-second activation delay. No fixed activation delay is introduced.

## Corrections

- A per-slot automatic-return intent captures its original regular or temporary
  profile and retains one activation through the hold. Release supersedes pending
  activation and skips resetting an original profile that was never replaced.
- Automatic transitions use normal aggregate key ownership, not raw key-up calls.
- Queued requests capture source/registration identity. Preparation is cold;
  revision and intent are rechecked at a short publication/action admission
  boundary before live mapping mutation. Invalid destinations preserve the live
  mapping. Busy admission retries the same prepared object outside all pauses.
- Only bounded physical/mapping settings run on the source queue. Audio, backend
  and sidecar reconciliation use a per-slot latest-only cold consumer, exact
  registration/revision checks and nonblocking ordered gate admission. The
  overview refresh uses the same guarded path. Startup stays synchronous.
- Same-output primary pads remain connected. Switching output type is explicitly
  not promised to be instantaneous: it still needs a real virtual-device change.

## Validation boundaries

`TemporaryProfileIntentTests` exercises real special-action dispatch and worker
completion. `ProfileSwitchInputContinuityTests` uses a synced registered synthetic
DS4 source, real report leases/base publication pause, real mapper and commit,
and recording virtual-pad/keyboard/mouse sinks. It exercises both regular and
temporary origins, held movement, and both press/release directions. Queued
hardware/audio work is retained but not executed against real devices.

`ProfileMutationBoundaryTests` covers final-boundary invalidation, admission and
registration generations. `ProfileColdOptionsTests` covers coalescing, retry,
stale admission and lock cleanup. These are deterministic software simulations,
not a claim that the reporter's machine/game or physical transport was tested.

## Final local validation

The combined source passed **6,681 tests, 0 failed, 12 opt-in hardware cases
skipped** (6,693 total). Command:

`dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --logger "trx;LogFileName=rc463-complete-final.trx"`

The full-suite receipt is `DS4WindowsTests/TestResults/rc463-complete-final.trx`.
The five profile-focused classes contain 82 checks, and the three mouse-focused
classes contain 276. **All 358 combined checks passed three consecutive runs**,
recorded separately as `rc463-combined-repeat-*.trx`. Earlier profile-only snapshots passed 78 checks
and three repeats before the final four UI admission cases were added.

Independent reviews checked action/KBM/publication ordering, nonblocking cold
admission, exact registration identity and neutral-retirement intent cleanup.
The hosted MSI expected-version parameter contract also passed 17 native-free
cases. CI, tagged release and final downloaded-package evidence are separate
publication gates. No installer was run on the user's machine for validation.
