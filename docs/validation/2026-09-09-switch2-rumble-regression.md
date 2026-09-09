# Switch 2 RC4.5 rumble regression, 2026-09-09

## Report and scope

After installing RC4.5, the user reported stuttering Test Heavy and Test Light
on both USB and Bluetooth, and an unpleasantly strong connection cue. The user
asked to distinguish one-shot effects from sustained output while preserving
DualSense and Xbox Series impulse feedback. This correction does not retune
effect strength, carrier frequency, channel assignment, or the HD-rumble codec.

The installed DS4Windows.dll was verified against the published RC4.5 digest:
`8139FDD552365B50F8EE25A4EDB204C9480A25C0C4A4DF469A515C3FF6DFB8CF`.
The active profile used 100% rumble, no output delay, and enabled DualSense
audio haptics and Xbox impulse translation. The ordinary application log did
not contain per-write timing or a rumble transport failure. No live application
was restarted or controller probed during this diagnosis.

## Confirmed finite-cue regression

Connection and identification cues contain finite attack/neutral subframe
groups. RC4.5 correctly avoided renewing their canonical leases, but the new
held-rumble maintenance still retransmitted their attacks while those leases
were fresh. Lease duration and physical repeat policy are different concerns.

The connection design follows Switch2Connect revision
`61ac6642ce12fe7217e38a860b14863b18ca7e28`, whose connection-cue path sends
each stage without pair sustain. No donor amplitudes need to change to restore
that finite behavior.

Failing-first actual runtime assertions counted non-neutral physical packets,
not just changes between adjacent groups:

| Case | Required active packets | RC4.5 observed |
| --- | ---: | ---: |
| Pro connection signature | 2 | 24 |
| Pro identification | 2 | 14 |
| Standalone Joy-Con connection signature | 2 | 30 |

`one-shot-repeat-red.trx` records these three failures. Exact group, neutral,
and transition assertions remain in place.

An exact rich-frame repeat policy now distinguishes `OneShot` from the default
`SustainWhileFresh`. Only the four USB/Bluetooth runtime connection and
identification publication calls select `OneShot`. Successful completion ends
repeat eligibility; it does not erase an uncertain claim, suppress its exact
retry, weaken expiry/Stop, or report enqueue acceptance as physical delivery.
Renderer changes do not replay completed one-shots. A following ordinary held
frame retains its normal sustain behavior.

The first integrated finite-cue run, `one-shot-repeat-green-compatibility.trx`,
passed **201/201** tests. It covers the cue paths, ordinary native effects,
USB/Bluetooth lifetimes, DualSense PCM correlation and HD-rumble translation,
Xbox impulse release, and the physical delivery sink. It predates the
additional Bluetooth queued-retry and maintenance-pacing corrections below;
it is not the final acceptance result for those changes.

The Bluetooth native-local publication API also returned failure when a
canonical cue had been accepted but its physical write was still queued for
retry. The runtime cue scheduler could interpret this as a reason to publish
a fresh stage after maintenance had already resolved the original claim.
Bluetooth now matches USB's queued-acceptance semantics. The two failing-first
profile/preview tests retain the same canonical frame, exact payload/counter,
pending and failure indicators, wake notification, and explicit Stop checks.
An accepted queued cue is explicitly not a physical-success receipt.

## Sustained-output timing finding

Test Heavy/Light compatibility packets contain three nonzero held subframes,
not the attack/neutral structure of the finite cues. Their stutter must not be
"fixed" by flattening incoming PCM or replaying old streaming samples.

The prior worker treated pre-write contention (`Busy`) and continued ordinary
work as the same boolean. A single busy attempt at a 12 ms USB or 15 ms
Bluetooth deadline could therefore defer the next opportunity to 24 or 30 ms.
This is a source-level scheduling defect, not a measurement of the installed
controller's actual output gaps.

An isolated .NET 8 worker harness using the real feedback clock measured 200
intervals per transport, with the same 1 ms timer-resolution request already
used by the application. With an empty service callback, USB intervals had
p50 12.3065 ms, p95 12.4512 ms, max 12.8296 ms; Bluetooth had p50 15.3587 ms,
p95 15.4914 ms, max 15.5083 ms. These no-I/O measurements do not establish
hardware timing, but do not support replacing the timer merely on the claim
that this runtime always rounds it to 15.6 ms.

The correction gives the worker separate idle, active, pre-write-contended,
and pending-write results. It permits at most three consecutive 1 ms retries
for pre-write contention, then backs off to the normal transport interval.
An attempted or uncertain write retains normal retry spacing. Unexpected
service exceptions also retry at normal cadence instead of permanently parking
held output; Stop still seals the worker without waiting for I/O.

The physical sink additionally checks the last successful host write-call
start before repeating an unchanged held frame. This prevents a fast retry
from duplicating another publisher's newly completed send too early. The
worker schedules from that absolute due time, rounded upward to milliseconds;
it does not subtract service time twice or replay missed periods in a burst.
Missing/backward host-clock samples conservatively establish a new baseline
and wait a full interval, without permanently suppressing future maintenance.

This floor affects only successful, unchanged held repeats. New source frames,
new rich groups even with an unchanged canonical marker, renderer changes,
Xbox impulse-release revisions, unresolved retries, expiry, and Stop retain
their existing admission and ownership rules. All three production sink
creation paths explicitly select their cadence (USB 12 ms; ordinary/joined
Bluetooth 15 ms). The input hot path gains no sleep or new waiting lock.

`rumble-pacing-and-cue-retry-before.trx` records **9 failing-first cases**:
seven scheduling cases and the two Bluetooth queued-acceptance cases.
The first integrated broad run, `rumble-pacing-and-cue-retry-after.trx`, passed
168 cases and failed 11 older manual-timestamp fixtures. Those fixtures
advanced canonical time but not physical host-send time; the new floor
correctly suppressed the early physical repeat they expected. Per-instance
test clocks were aligned with their explicit maintenance timestamps;
packet-count, payload, counter, expiry, and ordering assertions are retained.
This intermediate run is not represented as a passing build.

The corrected integrated target,
`rumble-pacing-and-cue-retry-final.trx`, passes **181/181**, with no skips.
It includes all 20 dedicated pacing cases, Bluetooth queued-cue retries,
USB/Bluetooth lifetime and sink coverage, held-writer composite retirement,
and warmed zero-allocation assertions. All expected groups, native counters,
Stop ordering, and cold-retirement deadline assertions remain intact.

## Validation boundary

The complete unfiltered source suite, `rumble-fix-full-suite.trx`, passed
**4,574**, with **zero failures** and **11 existing opt-in integration skips**
(three live application-audio cases and eight external Go-peer cases).
The separate `FullyQualifiedName~Allocat` selection,
`rumble-fix-all-allocation.trx`, passed **165/165**, with no skips.

A self-contained .NET 8.0.29 x64 portable candidate was built in the dedicated
Desktop folder `DS4Windows-RC4.5-Rumble-Fix-2026-09-09`. Its product identity is
`VIIPERRC4.5-rumble-candidate.1`, not the published immutable RC4.5 asset.
Candidate DS4Windows.dll SHA-256:
`072E1E5F48239C51EC461D386A4D7E2E0EA71054D28D419CF88F171D4E74CC4D`.
It includes the unchanged published VIIPER 0.1.3-rc4.5 binary, SHA-256
`F1ECEF158F02D0BDCD1296C8D5097A281169081D0D59C1A8971592FAC78155EF`.
The isolated lab launcher passed parser and read-only digest verification;
it has not launched either application. It snapshots saved settings into its
own `app/lab-data` only on first launch after the existing applications exit.

Physical smoothness and connection-cue feel remain unconfirmed until a
portable candidate is exercised on the controller. Published RC4.5 assets,
installed application files, and user profiles are not modified by this work.
