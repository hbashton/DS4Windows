# Switch 2 DSU observation integration, 2026-09-02

Status: implemented and deterministic/loopback-tested; not a hardware or
controller-to-game latency result. Existing legacy HID DSU handlers are not
migrated by this change.

## Production path

The reversible Switch 2 slot host registers an optional observation handle
after profile/Mouse/output preparation. Its exact registration report lease
already owns the physical state. Gyro and the canonical `On_Report` pipeline
run first; only after that pipeline returns does the observer capture an owned
raw state and the current UDP session. No second raw Report/SixAxis subscriber
or mapper is introduced. In particular, target encoding and virtual-input
publication precede observation capture.

`UdpMotionObservationWorker` provides one fixed three-buffer mailbox per
registration in the four DSU-supported slots. One admitted producer owns the
write buffer, one worker owns the read buffer, and an atomic exchange transfers
the pending buffer. The producer does not run filters, enumerate clients,
format packets, or call networking. It does not wait for worker capacity.
Owned current/previous motion values never alias mapper or runtime storage.

This is a **latest-value, optional observation channel**, not an input event
journal. Overload coalesces/counts intermediate DSU observations (including
controls carried in those observations). It cannot preserve every button edge
for DSU clients. Canonical virtual-pad transitions do not use this mailbox and
are unaffected by its overflow policy. Publication/coalescing is bounded and
the warmed producer test measures zero managed bytes; this is not a latency
distribution or a zero-allocation claim for network processing.

The consumer owns smoothing histories and send scratch per exact source.
Session or policy changes re-prime filters; absent motion also re-primes on
resume. Coalesced motion uses elapsed controller time between consumed samples,
not the worker's scheduling delay. Existing five-level Cemuhook yaw adjustment
applies to the observer copy only. Its source provenance remains in
`Switch2CemuhookYawSensitivity.cs` (Switch2Connect GPL source pin).

## Lifetime and identity

Each source retains the full slot token, not merely an index. Terminal input
and profile undo retire that exact handle and discard its pending admission.
Retirement never joins optional networking. The single consumer may finish an
already-admitted old dispatch under its old source/session; it finishes before
dispatching any successor. Such a callback cannot read successor buffers,
filters, socket, server identity, or client registrations. Already-enqueued UDP
datagrams cannot be recalled, and UDP arrival order across retirement is not
guaranteed. No reliable terminal DSU-disconnect notification is added here.

Worker shutdown closes publication first. Publisher references protect wake
signaling until the worker exits; the last participant releases the event.
No controller-removal path waits for a stalled network callback.

Switch 2 has no legacy Sony serial. The DSU port-info callback now consults the
exact observation registration rather than reporting it disconnected based on
that missing serial. Metadata is reserved before the first admitted report,
then connected with the runtime's existing compatibility battery/charging
status. Charging semantics are not newly inferred from Switch 2 raw current.
The DS4-compatible DSU layout/model flag does not claim physical Sony identity.

A cold-generated locally administered unicast six-byte DSU address identifies
each registration. It survives UDP enable/disable and port restart but changes
on reconnect or pair replacement. It is not the physical MAC and does not
export the installation's pairing pseudonym, key or Windows hardware identity.
**Compatibility caveat:** MAC-bound DSU clients must rediscover after a new
registration; pad-ID clients retain their ordinary slot-based selection. A
stable public hardware pseudonym requires a separate explicit identity policy.

UDP enable/port changes are observed through the facade's running session;
Switch 2 does not rely on the legacy HID device enumeration used by UI motion
subscription changes. Existing upstream queued UI Start/Stop request ordering
and legacy synchronous handlers remain separate work.

## Evidence and outstanding gates

- `UdpMotionObservationWorkerTests`: 10 cases for immutable ownership,
  coalescing, metadata, pending and already-admitted session replacement,
  blocked dispatch plus source replacement/disposal, smoothing, failures,
  shutdown signaling races, and warmed zero-allocation publication.
- Production gyro/mapper integration adds five executions: raw USB, BLE Pro,
  joined Joy-Con; automatic enable/restart/reconnect; observer failure. It uses
  the real runtime owner, registration transaction, reversible host, real
  Mouse/canonical mapper and target encoders, with fake OS discovery/profile
  persistence and a manually scheduled DSU worker. Capture-time assertions
  prove the current report was mapped before observation admission.
- `switch2-udp-observer-adversarial-20260902.trx`: 57 passes, no failures/skips,
  including existing UDP session and reversible staging cases. Loopback uses
  ephemeral ports, never configured external listeners or controllers.
- `switch2-udp-observer-full-20260902.trx`: 2,904 passed, 3 opt-in live-audio
  skips, no failures (2,907 total). The earlier intermittent stick-filter
  allocation failure is still unattributed; this passing run does not fix it.

Physical rates, haptics/LED behavior, full API/game compatibility, Bluetooth
pairing, legacy joined-controller ownership, and end-to-end latency still
require their separate production acceptance gates. No installer is declared
ready from these tests.
