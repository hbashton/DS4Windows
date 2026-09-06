# DSU send ownership and session isolation, 2026-09-02

This fixes concrete UDP sender correctness/backpressure defects. It is not
automatic Switch 2 UDP integration, a fully off-thread observation publisher,
or a measured controller-to-game latency result. The later
[Switch 2 observer integration](switch2-udp-observation.md) builds on this
sender and documents its separate source/session/lifetime contract.

## Confirmed defects

The previous `UdpServer` selected a rotating shared byte buffer, then waited
on a semaphore before submitting the socket operation. The semaphore limited
the number of operations but did not own any particular buffer: out-of-order
completions could free a credit while the selected buffer remained in flight.
Both motion data and control replies used this path. `_pool.Wait()` could
block the controller report publisher until an unrelated network send finished.

Control replies also used a fixed 100-byte send length although the encoded
version and port-info replies were only 24 and 32 bytes. This included old
buffer contents beyond the logical packet and its CRC coverage.

Restarting the same object replaced socket/server-ID fields while old receive
callbacks still used that object's current fields. An old callback could rearm
or process a subscription against the successor socket and client table.

## Implemented contract

`UdpDatagramSendPool` preallocates 80 independently owned argument/buffer pairs
per production session. Publication scans once for a free entry using CAS. It
never waits for capacity or derives ownership from a ring position. It copies
the exact logical payload and recipient before calling the sender, and retains
the entry until that exact operation completes. Synchronous completion, async
completion, socket failure, and exception paths return ownership exactly once
under the documented `Socket.SendToAsync` convention. A sender must not replay
an old completion after the argument has been legitimately reused.

Capacity exhaustion rejects and counts the **optional DSU datagram**. It does
not drop or coalesce canonical virtual-controller input. UDP is already a
lossy transport; this change makes local overload explicit instead of stalling
input to wait for delivery. `CapacityDropCount` and `FailureCount` are counters,
not per-report log messages. UI exposure/aggregation is not added here.

Disposal rejects new admission and disposes free entries. In-flight arguments
remain owned until completion; each completion returns only to its original
pool. Already-admitted transport calls cannot be retracted by the pool alone:
the session owner closes the exact socket. No pool lock spans a socket call.

The `UdpServer` facade serializes cold Start/Stop operations and versions
requests. Each Start constructs a fresh `UdpServerSession` with its own socket,
receive buffer, server ID, client registrations, and send pool. A superseded
slow Start does not publish its session. Retired receive callbacks cannot use
successor fields. Every Stop caller crosses the session's cold retirement
barrier after socket close; this gate is not on the report publication path.
Initial receive-arm exceptions reach the caller; later arm failure retires the
session instead of recursively retrying a closed/faulted socket.

Packet CRC now uses the self-initialized ordinary CRC table. The former fast
CRC routine depended on separate `ControlService` startup initialization; a
standalone cold UDP session returned `0xffffffff` instead of valid CRCs. Normal
ControlService startup already initializes that table, so this test finding is
not evidence that installed sessions were generally emitting incorrect CRCs.
The state encoder also writes its fixed struct and little-endian CRC directly
to the destination span, avoiding boxed pinned structs and CRC byte arrays.
Missing/non-six-byte MAC metadata follows the port-info encoder's existing
zero-address fallback. MAC-only subscription lookup guards null keys, so an
identity-less/disconnected observation cannot fault the report publisher.
This does not choose or implement the future Switch 2 DSU identity policy.

## Verification and limits

`UdpDatagramSendPoolTests` has 16 executions covering saturation, arbitrary
completion order, exact lengths, byte/recipient ownership, synchronous and
asynchronous errors, early completion, actual completion-event dispatch,
reentrant/concurrent disposal, successor isolation, parallel publication, and
warmed zero allocation with a fake synchronous sender. The zero-allocation
claim is for that helper path, not sockets or the complete UDP/report pipeline.

`UdpServerSessionTests` has 10 executions with ephemeral loopback-only sockets:
actual version/port replies, state controls/motion, independently checked CRC,
restart isolation, a blocked old request, superseded startup, failed bind and
retry, and missing/non-six-byte metadata with a MAC-only subscriber present.
The unacknowledged data subscription is synchronously installed through
the actual parser in the state test; it does not pretend to acknowledge a
network subscription. No controller, configured DSU port, external interface,
installed application, or driver is used.

The first focused run passed 24 and failed 6 due to the cold CRC dependency.
After removing that dependency, `udp-send-ownership-focused-crc-20260902.trx`
passed all 30 tests (16 sender, 7 session, 7 existing motion-isolation cases).
The final metadata guard adds three cases;
`udp-send-ownership-final-focused-20260902.trx` passed **33/33**.
The full-suite results and recurring stick-filter allocation failure are
recorded separately in the platform validation ledger.
The latest normal run, `udp-send-ownership-default-after-traces-20260902.trx`,
passed 2,888, skipped 3 opt-in live-audio tests and failed the existing filter
zero-allocation assertion (768 bytes). Earlier normal failures measured
864/816 bytes. Opt-in fine, whole-Step and assertion-boundary traces passed
without capturing the allocation; their success is not an attribution or fix.
The zero threshold and production filter remain unchanged. Full release
validation is therefore still open despite the 33 focused UDP passes.

## Remaining work before automatic Switch 2 observation

- Move metadata conversion, smoothing, client-list construction/locks and
  socket submission off the controller-report thread via a bounded, owned
  observation handoff **after** canonical virtual submission. This change
  removes capacity waiting, not every source of UDP-related latency.
- Bind observations/filter history to exact source registration, source/service
  generation and UDP session. Invalidate pending old observations at retirement.
  Do not add an unleased raw Switch 2 Report/SixAxis subscriber.
- Wire startup, enable/disable, reconnect and port changes from complete source
  registration, not the legacy `DS4Devices` enumeration. UI delays and queued
  ControlService checks remain to be replaced by coherent lifecycle authority;
  the facade's request version does not repair those upstream request races.
- Project Switch 2 connected metadata from runtime registration: the runtime
  currently inherits a blank legacy Sony serial, and `GetPadDetailForIdx`
  treats that as disconnected. Define a nonsecret DSU identifier without
  repurposing HID identity or leaking persistent identity secrets.
- Preserve the explicit four DSU slots, physical motion precision until DSU
  encoding, source isolation and an explicit overflow policy. Preserve input
  edges in the canonical virtual-pad path independently of optional UDP loss.

No native driver, polling interval, firmware mode, or installed binary changed.
