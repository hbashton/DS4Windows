# Rare movement interruption: investigation and admission guard, 2026-09-14

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Incident status: not yet attributed

The user reported one unexpected movement stop in approximately 24 hours and
suspected a neutral report. The exact time, physical/output controller, and
whether movement resumed without releasing the stick are not yet confirmed.
No live applications or controllers were interrupted, no hardware tests were
run, and no installed files or settings were changed.

The current log covers Xbox One/Series sessions beginning September 13 at
17:11 and 23:20/23:21. Across their recorded writer-health samples:

- `orderedOverflowFaults=0`, `orderedStaleFaults=0`, `orderedRejected=0`.
- `orderedLifecycleFaults=1`, `orderedNeutralCommits=1`, `orderedResyncs=1`
  remain unchanged from initialization. There is no recorded additional
  scheduler reset/neutral commit during these sessions.
- Ordered queue high water remains at most 1.
- The 18:03:45 sample records a 101.19 ms publication gap and a 101.27 ms
  write gap, but only 1.09 ms packet age and 0.93 ms write duration.
- The 19:36:46 sample similarly records approximately 100 ms publication/write
  gaps with 1.70 ms maximum packet age. These are maxima over sampling windows,
  not exact incident timestamps or end-to-end controller latency.
- A 2.35-second gap in the 23:20:54 sample belongs to the reconnection/startup
  interval, not evidence of a steady-session stall.

The gap measurements locate missing fresh publication upstream of queued
packet delivery. They do not distinguish physical radio/HID delay, report
processing delay, or process scheduling. They also do not prove an actual
neutral report or that either gap caused the user's movement interruption.

## Definite takeover race fixed

Game Bar's timer can decide to activate compatibility before acquiring the
routing lock. Home-button work is also queued asynchronously. Previously,
`ActivateGameBarCompatibilityOutputCore` did not recheck the current profile's
eligibility. A completed change from PlayStation to Xbox output, disabling
compatibility, or selecting DInputOnly could be followed by that stale work
creating a companion, routing input away, and resetting the current native pad.

Activation now rechecks the current enabled/output/DInputOnly policy under the
existing routing lock, before allocation, reset, or routing. No per-report
delay, neutral suppression, new queue, or driver change was added. This is a
verified prevention fix, not an attribution of the reported once-per-day event.

## Validation

- Baseline input/egress/slot/Game Bar tests: 137 passed.
- Four actual-service-entrypoint admission tests fail with the guard removed:
  stale work reaches the deliberately absent output manager instead of exiting.
  Fixtures create no service, driver, discovery, or physical workers.
- Four held-input tests pass before changing product code: Xbox One/Series,
  Xbox 360 and Switch 2 retain the last complete held state across logical
  clock jumps up to a day; the shared PlayStation mapped queue emits nothing
  merely because it is empty. Real releases remain immediate. These are
  simulated-clock/unit checks, not a 24-hour physical-controller soak.
- Guarded targeted run: all 145 tests passed.
- Full Release/x64 run: 5,706 passed, 11 hardware-gated tests skipped, 0 failed.
  The existing six compiler warnings remain; no new warnings were introduced.
- Independent review found no blocking issue in the guard or fixtures;
  `git diff --check` passed.

Evidence directory: `isolated_results/rare-input-neutral-20260914`.

## Safety and remaining investigation

Do not globally discard neutral reports or synthesize held input indefinitely:
real releases, disconnects and terminal safety actions must remain effective.
The ordered scheduler's existing overflow/explicit-age recovery can send neutral
then resynchronize, but its counters exclude that path in the recorded Xbox
sessions, so its safety policy was not changed.

Physical DualSense microphone packets, rejected report tags and CRC failures
skip publication rather than generating neutral. A successful short native HID
read is not length-validated by the current pipeline; a reused old report is a
conditional risk, but no evidence establishes such a completion here. No
speculative physical transport change was made.

DualSense currently uses the untyped composite-worker fallback, not the DS4/DS3
typed worker/report lease. The code review must not claim universal typed
generation protection. Deferred missing-profile unplug work also deserves a
separate guarded-callback audit if profile-related evidence appears.

The next useful evidence is the approximate incident time, controller/output
type, and whether a fresh stick movement was needed to resume. No release,
portable deployment, or hardware confirmation is claimed by this document.

Workspace DS4Windows.dll SHA-256:
`85C1F0D1AEBA1005476E99BFF3389C5FF6BB55DF40ED3794FA813F99EA39DA9C`.
