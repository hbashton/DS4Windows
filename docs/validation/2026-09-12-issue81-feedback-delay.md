# Issue 81: dense native feedback delay after RC4.5.8

## Evidence and limits

The [reporter's RC4.5.8 follow-up](https://github.com/hbashton/DS4Windows/issues/81#issuecomment-5642437358)
says missing shots are resolved, but feedback becomes delayed during dense
effects and catches up after a few quiet seconds. Input remains responsive.
This is a new acceptance failure, not evidence that updating to 4.5.8 again
will help.

[c.txt](https://github.com/user-attachments/files/32135103/c.txt) records a
physical Bluetooth DualSense, virtual DualSense and RC4.5.8. The native queue
repeatedly has four pending commands, delivery approaches 200 commands/s,
maximum measured queue age is 23.93 ms, and no command drops are recorded in
that DS4Windows queue. Speaker PCM counters remain zero. These counters do
not measure upstream TCP residence or controller presentation time.

[The older a.txt](https://github.com/user-attachments/files/32021032/a.txt)
records 294,903 native commands in one 30-second interval (about 9,830/s).
Neither ordinary log contains raw native payloads, so the exact proportion
of redundant packets in this user's current game is **not established**.

Local evidence copies under `isolated_results/issue81-feedback-delay`:

- `reporter-c.txt`, SHA-256 `F91FB55FA5A5C13BCF4BCECC59CD40147F42720364AC7667BEA8BC6CB60BF86C`.
- `reporter-a.txt`, SHA-256 `0321697FF956D6E9C515A8D5CF68E352F2F3BF233C5B7694EAB5AF20FD485853`.

## Source-level cause addressed

Every repeated native command previously occupied a separate slot. The
Bluetooth helper retains the existing 5 ms aggregate command spacing, so a
repeat flood fills downstream queues and blocks the dedicated feedback
reader. Input uses an independent path. TCP may retain many commands before
the broker's bounded native queue also applies backpressure. Small measured
post-read queue age does not bound total delay.

The VIIPER native queue is bounded (32 buffers, including in-flight output).
Its admission failure returns a USB no-space completion; host retry is not
guaranteed. This change does not promise lossless delivery of arbitrarily
fast **distinct** commands or zero physical latency.

## Reference review

[PadForge a29583c](https://github.com/hifihedgehog/PadForge/commit/a29583c1b3e4af63dfc35a7061b5c4bb29111be5)
identifies a GTA duplicate-report flood and suppresses repeats before
dispatch. The useful principle is early repeat suppression. Comparing only
against a historical last-sent report or replacing all pending state is not
safe for our validity-masked commands: A -> B -> A must preserve B and the
final A, including a stop. SDL's broader latest-pending replacement and
PadForge's separate pacing rules are not copied.

[PadForge discussion 434](https://github.com/hifihedgehog/PadForge/discussions/434)
is a similar unresolved report, not a proven solution. Its attached trace
also contains a 5,346.9 ms dispatcher write excursion despite a shallow queue.

## Change

Only consecutive byte-identical, recognized state-setting native DualSense
reports can share an **unpeeked pending tail**. The check runs before full
queue rejection. It retains the first copy's bytes, receipt and enqueue
timestamp. It never compares against historical last-sent state, so
post-consumption keepalives are sent again.

Eligibility requires known 76/217/474-byte envelopes, a zero media suffix,
ordinary rumble/known trigger state/LED fields, and zero reserved/configuration
fields. Unknown commands, trigger calibration, audio routing, LED reset or
animation and PCM remain untouched. Full-envelope equality also preserves
the compatibility magnitudes used for Trigger Lab overrides.

Stream generation, device index, reference-identical target, binding,
profile and pending-boundary revisions must match. Consumer peek, media,
legacy state, direct legacy callbacks and lifecycle clear are barriers.
The check and queue mutation use the existing queue lock and preallocated
storage; no queue growth or new worker is introduced.

Existing 30-second statistics add `nativeRepeatsSuppressed`,
`nativeAdmissionWaits` and `nativeAdmissionWaitMaxMs` so the next tester log
can distinguish repeat pressure from distinct-command pressure. These are
not end-to-end latency measurements. No per-report logging is added.

Bluetooth pacing, PCM gain/codec, persistent native rumble mode, per-trigger
Trigger Lab ownership, input, Nintendo translation and VIIPER protocol remain
unchanged. No running application, controller, profile, driver or installed
file was changed during this source investigation.

The conservative eligibility check is **not a native-report validator**:
reports outside its allowlist are still queued and delivered unchanged.
In particular, extra/opaque trigger bytes are not cleared or rejected.

## Why not only increase queue throughput?

The 5 ms interval is software policy, not a documented hardware maximum.
`11385bfc` introduced a 30 Hz latest-state latch, `cc58a36b` raised it to
200 Hz while adding atomic composition, and `16e854b` reused it for ordered
native commands. No 200 Hz hardware limit is established by those changes.

Increasing service rate merits a separate controlled experiment with
genuinely distinct commands. However, even 1,000 writes/s cannot absorb
9,830 repeated reports/s indefinitely. Windows' successful overlapped HID
submission/completion also does not prove a packet has reached Bluetooth;
removing the timer can move a backlog into the driver rather than remove it.
The production writer has 32 overlapped slots and descriptor-sized writes.

A follow-up native-only 200/250/500 Hz comparison should leave local-state
and media pacing unchanged, retain Busy/ordering/lifecycle guarantees, and
measure input intervals, media deadlines, write completion age and outstanding
writes on real hardware. This investigation does not select a new rate from
queue-only or synthetic-writer timing.

## Validation

- With only repeat suppression temporarily disabled, all four selected
  regressions failed: the fifth identical command filled the four-slot queue,
  and the actual reader-admission fixture blocked until its bounded test
  cleanup cancelled it. Result: `repeat-suppression-disabled-red.trx`.
- Re-enabling the fix passed **361 focused cases**, including native helper
  ordering, finite media, rumble persistence, queue and new repeat regressions.
  Result: `repeat-focused-green.trx`.
- The complete rebuilt Release x64 suite passed **5,511 tests, zero failures,
  11 existing gated skips**, 5,522 total. Result:
  `full-final-release-green.trx`. All allocation assertions remained enabled;
  the new warmed 10,000-repeat comparison loop allocated zero bytes.
- The hardware-free reader-admission integration accepts 10,000 identical
  reports plus a stop without waiting for a consumer drain, retains two
  commands and 9,999 suppressed repeats, and verifies pulse then zero at the
  actual physical writer's final test hook. This is not a measured Bluetooth
  or game latency result.
- A separate physical-writer-hook regression verifies that both complete
  11-byte trigger blocks, including opaque extra bytes, survive unchanged in
  both repeated reports. The reports do not qualify for suppression.
- Independent source and regression review found two gaps (legacy direct
  callback fencing and opaque trigger-byte eligibility); both were corrected
  before the final run. No remaining blocking review finding was reported.

GTA hardware confirmation remains required. The logs do not prove every
current command is eligible for suppression, so the exact reporter outcome
is still pending. Issue 81 is not closed and no release is published by this
investigation.
