# Joy-Con 2 counter-reset disconnect: live evidence, 2026-09-08

## Incident and capture

The user reported a left-only disconnect at 19:11 and a joined-pair disconnect
at 19:33, then authorized a process dump and requested a fix. All times below
are machine local (UTC-05:00). The running portable process was DS4Windows PID
34488, version `5.0.4.93-Bluetooth-Audio-Lab`, launched September 7 at 23:51.
VIIPER PID 30276 also retained its original launch time. Both were responsive.

The app log records the joined pair ready at 19:20:32, then its virtual Xbox
360 disassociated at 19:33:14.342, USB/IP detached at .418, and output removed
at .493. VIIPER records client stream closure followed by explicit remove
commands, not a preceding broker error. Windows System logs contain no event
in the queried incident window. These logs alone did not establish the cause.

An elevated, hidden `dotnet-dump collect --type Heap -p 34488` captured the
still-running app at 19:38, without restarting either app, reconnecting a
controller intentionally, or sending controller commands. **Capture side
effect:** the heap snapshot paused the target long enough for its USB Pro to
time out at 19:38:39; normal USB discovery reattached it at 19:38:42.997. This
later diagnostic-induced pause is not either earlier Joy-Con incident. No
second live dump was taken. The dump is 405,359,237 bytes and
remains **local only** on the Desktop under `Controller-Diagnostics-2026-09-08`.
It contains process memory and must not be committed or uploaded as a public
fixture. Only the non-secret protocol fields below are recorded in source.

## Exact retained evidence

The latest joined owner has runtime generation 23, pair epoch 24, left device /
transport generations 19/20 and right 21/22. Its right input owner ended with
`SinkFailure`; the right drain pump recorded `SinkRejected` and one rejection.
The left owner was subsequently stopped normally. Neither Windows input lease
had `disconnectObserved`; both input overflow counters were zero. The runtime
had `bluetoothDisconnectRequested=0`.

The input queues retain body bytes and timestamps after resetting head/count.
Matching the right input session's last completion timestamp to retained ring
index 9 recovers the exact rejected packet. Index 8 is the last accepted right
observation, also retained by the joined mapper:

| Observation | Host QPC (10 MHz) | Common05 raw counter |
| --- | ---: | ---: |
| Accepted right, ring index 8 | 2,309,314,172,615 | 1,431,652 |
| Rejected right, ring index 9 | 2,309,314,321,224 | 13 (`0D 00 00 00`) |

Host arrival advanced normally by 14.8609 ms on the same exact transport
lifetime. The joined sink's runtime-admission wait count is zero. Its final
generic failure fields were cleared by successful terminal cleanup; those
zeros do not mean the earlier input succeeded.

The same dump independently retains the previous standalone **left** failure:
device / transport generations 13/14, one input rejection, `SinkFailure`, no
queue overflow, no Windows disconnect observed. Its standalone sink preserves
`lastJoyConMappingFailure=BackwardOrOutOfOrder`. The session's retained counter
high-water is 1,431,651; ring index 0 matches its last completion timestamp
2,296,057,651,119 and contains counter **12** (`0C 00 00 00`).

## Root cause and correction scope

The physical report counter can reset without a transport disconnect. The
session already records a modular discontinuity diagnostically, but the
Joy-Con profile admission (also used by the joined coordinator before pair staging) treated it as out-of-order input
and rejected the publication. The input owner's exception boundary then
retired the controller; joined ownership necessarily retired both halves.

This is the same class already handled for ProController2 by exact-lifetime
host-arrival ordering. Apply that policy to validated Joy-Con 2 **Bluetooth
Common05** reports across session, profile admission and pairing. Retain
counter values/classification for diagnosis, and advance the diagnostic
baseline after a discontinuity. Do not weaken framing, descriptor identity,
transport-generation, host-QPC monotonicity, or unsupported-transport checks.
Do not drop button transitions, add buffering, increase queue limits, or
guess a firmware counter modulus from two observations.

Regression coverage must include both captured reset values; standalone left
and right; joined reset on either half; recovery on subsequent input; stale
host timestamps and lifetimes still rejected; independent report rates; and
zero-allocation input processing. One-shot retained failure details should
survive cleanup so a future incident does not require reconstructing a heap.

## Reference alignment

The locally pinned Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`
input notification callback (`src/controller.py`, around line 4000) builds
controller input directly from arriving notifications rather than requiring
this raw counter to advance. SDL-hifihedgehog
`d98c5804a9d20b0d96e993741797878c86b8f1e1`,
`src/joystick/hidapi/SDL_hidapi_switch2.c:1188`, timestamps delivered controller
state with `SDL_GetTicksNS()` before dispatching mini/combined Joy-Con and Pro
buttons/axes. Sensor timestamp interpretation is separate. These source paths
support using arrival order for presentation; the captured packets on this
machine, not an assumed donor counter modulus, establish the reset defect.

## Implemented counter policy and offline validation

The shared `Switch2CounterSequence.UsesArrivalOrdering` policy now covers
Pro USB/BLE Common05 and Joy-Con 2 left/right BLE Common05. It excludes unknown
models, Joy-Con USB and dedicated reports 07/08. Both Joy-Con profile-admission
counter gates use the shared policy; session/replay and raw-stick calibration
already consume that policy. The pair reducer itself has no raw-counter gate
and retains its generation, clock and skew checks unchanged.

The raw modular sequence/delta remains observable, including backward jumps.
Accepted arrivals advance its next diagnostic baseline; there is no guessed
reset threshold, special treatment of 1.431 million, or inferred modulus.

`Switch2JoyConCounterResetTests` initially reproduced the defect: **10 failures
and 2 passing safety cases** in `switch2-joycon-counter-reset-before.trx`.
The failing cases included standalone and joined publication on both halves,
all four standalone holding layouts, and subsequent session/replay baseline
classification. These fixtures contain only the recovered counter pairs and
right-side QPC values; button/stick data and successor reports are synthetic.

After correction, the expanded **17-case** fixture passed, including arbitrary
forward/backward jumps, duplicates, full uint wrap, repeated resets and resumed
advancement across all four supported model/transport combinations. A warmed
joined-runtime test processed **20,000 discontinuity-bearing inputs with zero
managed allocation**. Both standalone and joined paths remain Active and
publish button release/next input instead of requesting terminal teardown.

The broader source/profile/coordinator/replay/calibration suite passed
**147/147**, no failures/skips, in `switch2-joycon-counter-reset-green.trx`
(Release/x64). An intermediate run had 145 passes and two obsolete calibration
expectations that still demanded counter rejection; those now verify accepted
reset samples while retaining rejection of duplicate host timestamps. Host-QPC
regression, stale generation/descriptor, unsupported report and wrong-epoch
tests remain enforced.

This capture proves the two observed failures, and offline regressions exercise
their corrected path. A live patched-build endurance run is separate acceptance
evidence and has not yet been performed.

## Retained failure evidence and normal lifecycle diagnostics

Both standalone and joined Bluetooth input sinks now retain their **first**
input rejection before successful half-loss or terminal cleanup changes their
ordinary status fields. The snapshot records the model and sink/profile/pair
failure classifications. If a sink dependency throws, only its exception type
and HResult are retained. The runtime separately records the first swallowed
gyro observer, Report subscriber or queued-action exception using the same
bounded metadata; existing rejection and terminal-neutral behavior is unchanged.

A once-per-owner lifecycle observer queues a normal diagnostic off the input
thread. It reports side/model, runtime generation, end reason, pump failure and
the preserved cause for unexpected disconnect, queue overflow, sink rejection
or worker failure. Ordinary intentional Stop with no pump fault remains quiet.
No successful report formats a log entry or allocates a failure snapshot. The
snapshot stores no raw packets, controller addresses, bond keys, exception
messages, exception objects, file paths or stack traces. The first evidence is
not cleared after the message is queued or after cleanup succeeds.

`Switch2InputFailureEvidenceTests`: **12 passed**, no failures, in
`switch2-rumble-maintenance-complete-final.trx` (Release/x64). Coverage includes
concurrent first-fault retention; deferred, once-only reporting and reason
filters; privacy of exception messages; exceptional Report callbacks on both
standalone halves and the joined sink; cleanup/recovered-input preservation;
and separate gyro/queued-action fault stages. These results are offline source
validation; deployment and a live endurance test remain separate evidence.
