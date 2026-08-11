# VIIPER native input latency contract

The native backend treats physical controller input as an event-driven path:

1. DS4Windows maps the physical report into the existing VIIPER controller
   packet format.
2. A persistent Highest-priority, Games-MMCSS writer submits it immediately.
3. The authenticated device stream delivers it to VIIPER.
4. VIIPER's persistent per-endpoint publisher submits one input IOCTL.
5. The UdeCx driver copies the latest accepted report into a parked interrupt
   URB and completes it through the endpoint completion DPC.

DS4Windows must not add another polling or cadence timer. The USB endpoint and
native host are the cadence authority. ViGEmBus follows the same useful shape:
its DS4 `SubmitReportImpl` immediately satisfies a pending USB request, while
its timer is an idle replay mechanism rather than a reason to delay fresh
state.

## Edge and media invariants

- State packet bytes, buttons, touch, motion, and controller-specific mappings
  remain those produced by `ViiperStatePacketBuilder`.
- A fixed 256-slot queue coalesces only adjacent analog/motion updates whose
  discrete signature is unchanged. Button, trigger, d-pad, touch, and tracking
  transitions retain FIFO order.
- A failed in-flight packet owns a separate preallocated retry slot, so a full
  reconnect-period queue cannot reorder the failed edge behind newer input.
- Queue exhaustion fails closed instead of silently dropping an edge.
- Input and microphone/media use independent reusable frame buffers. A fair
  final wire scheduler lets waiting input pass queued media, then admits media
  so microphone cadence cannot starve. The framed sequence and encrypted
  stream remain serialized as required by the protocol.
- VIIPER already uses persistent per-endpoint workers; no goroutine is created
  per URB. DS4Windows already registers its input writer with Games MMCSS, so
  duplicating those mechanisms would add lifetime risk without reducing delay.

## Reproducible microbenchmarks

`ViiperInputSchedulingTests` contains two non-release-claiming benchmarks:

- `ImmediateMappedSubmissionMicrobenchmarkIsAllocationFree` measures the exact
  reused packet builder plus enqueue/dequeue path.
- `SharedMemoryAndLoopbackTcpTransportBenchmark` compares persistent 33-byte
  round trips over TCP loopback and a non-persisted memory map with identical
  responder thread priority and warmup.

On the 2026-08-11 development host, 50,000 immediate mapped submissions were
0-allocation with p50/p95/p99 of 0.2/0.2/0.3 microseconds. For 10,000 transport
round trips, TCP measured 27.2/43.4/64.8 microseconds and shared memory measured
4.3/7.3/11.7 microseconds. These numbers justify designing a source-bound,
authenticated shared-memory data lane, but do not authorize replacing the TCP
control plane or claim end-to-end controller latency. The signed native package
and physical-controller/WPR gate remain authoritative.

## References

- ViGEmBus DS4 immediate submit and pending-request architecture:
  https://github.com/nefarius/ViGEmBus/blob/master/sys/Ds4Pdo.cpp
- Microsoft UDE client-driver lifecycle and endpoint model:
  https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/writing-a-ude-client-driver
- Microsoft Multimedia Class Scheduler Service:
  https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- Microsoft .NET memory-mapped IPC guidance:
  https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files
