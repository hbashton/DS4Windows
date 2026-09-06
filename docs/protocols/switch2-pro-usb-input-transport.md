# Switch 2 Pro USB read-only input transport owner

Status: the production-shaped contract, concrete Windows read-only discovery,
native overlapped-read lease, and lifecycle are implemented and replay/unit-test
verified. The adapter is deliberately not registered or enabled in DS4Windows,
no controller was contacted by this tranche, and no live hardware or latency
claim is made.

## Scope

`Switch2ProUsbInputTransport.cs` is the next boundary after
`Switch2PhysicalInputBoundary.cs`. It owns one candidate-specific, read-only
USB input lifetime without treating the legacy `SwitchProDevice`/`HidDevice`
path as the wire contract.

The factory accepts two deliberately separate injected dependencies:

- `ISwitch2ProUsbOsDiscoveryAdapter` supplies one atomic, descriptor-only
  composite observation. It exposes no path, serial, instance ID, handle, or
  printable controller identity.
- `ISwitch2ProUsbNativeAdapter` opens a read-only composite lease only after the
  existing exact admission gate succeeds. The returned registration must equal
  the admitted registration or the lease is rejected and quiescently released.

The lease retains the admitted relationship between HID input MI_00 and the
MI_01 topology/presence node, but its public surface can only start, cancel,
quiesce, and release a 64-byte MI_00 input read. It has no command, output,
feature, LED, rumble, haptic, or initialization method. This tranche therefore
cannot transmit anything to a controller.

`Switch2ProUsbWindowsAdapter` implements both injected seams as a one-shot
Windows capability. Its SetupAPI/HID/WinUSB implementation follows the audited
hardware-verifier discovery algorithm: complete present-interface enumeration,
exact VID/PID/MI component matching, explicit HID-parent and Windows-container
edges, exact bcdDevice and HID report capabilities, one MI_01 device-interface
path, default alternate setting, and exact two-pipe topology. Matching HID
collections are grouped by opaque container, and each container must
independently contain exactly one admitted MI_00 and one admitted MI_01. An
ambiguous or incomplete container is rejected without first-matching and does
not suppress another independently valid controller. Once a target-looking
node has an attributable nonempty container, its metadata, parent, service, or
interface-list failure invalidates only that container; failure of the global
SetupAPI enumeration itself still fails the complete snapshot closed. An active
entry whose identity or container cannot be read is also a conservative global
failure: it could be a duplicate interface for a later candidate, so continuing
would leave exact per-container cardinality unproven. Raw paths, instance IDs,
serial-bearing strings, and handles remain inside internal types.

Opening consumes the preceding observation. The adapter re-enumerates and
compares the exact private device-tree identity before acquisition, opens MI_00
with `GENERIC_READ`, `FILE_SHARE_READ`, and overlapped semantics, opens MI_01
with read access and read/write sharing only to retain and query its presence
and topology, validates both live handles, and re-enumerates the complete
composite again before returning the lease. The MI_01 handle is not exposed to
the transport lease and has no output method.

Immediately after consuming an observation, and before native re-enumeration or
handle acquisition, the default Windows adapter atomically reserves the
admitted opaque container in a process-wide registry. Reserved containers are
skipped by later observations, while different container identities can hold
independent leases. Candidate selection advances by opaque-container cursor so
a persistently inaccessible first candidate cannot starve a later valid pad.
The reservation transfers to the lease and is released only after both retained
resources report successful terminal disposal. Quiescence alone does not
release it, and a partial or failed disposal retains it until a successful
retry. An aborted open releases its reservation only after every partially
acquired handle was successfully released; an unproven partial release
deliberately leaves the container fenced fail-closed.

The registry strongly owns ambiguous cleanup capabilities. A cleanup ambiguity
before container attribution enters a process-wide unattributed quarantine and
blocks all later discovery/reservation. Once reserved, the exact reservation
entry roots the corresponding native cleanup owner. Terminal reservation
publication first seals native starts, runs the release hook with the terminal
gate open to stale callbacks, then commits removal under that same gate. A
reentrant or concurrent stale callback therefore latches quarantine before
removal, while a throwing hook retains the exact release capability for retry.

## Admission and ownership

The transport owner delegates admission to
`Switch2PhysicalDeviceFactory.TryAdmitProUsb`; it does not add a second or
weaker matcher. The admitted device remains exactly:

- Nintendo `057E:2069`, `bcdDevice 0201`;
- exactly one HID-class MI_00 and exactly one WinUSB MI_01 in the same nonempty
  opaque Windows container;
- exact HID usage/report shape and exact two-pipe MI_01 topology already
  documented by `switch2-physical-input-boundary.md`.

Discovery rejection happens before native acquisition. Native acquisition is
candidate-specific and is checked again through the opaque registration.
Multiple admitted Pro composites may coexist when their opaque Windows
containers differ; duplicate candidates within one container remain rejected.
The registry coordinates all default instances of this new Windows adapter in
the current process but does not yet coordinate DS4Windows' legacy enumerator.

One owner has one preallocated 64-byte input buffer and permits one read claim
at a time. A claim contains the immutable device generation, transport
generation, a monotonically increasing nonzero read sequence, and a private
owner fence. A duplicate, old, or foreign completion is rejected before parser
state or publication changes. Sequence exhaustion closes the lifetime instead
of reusing a token.

## Completion and lifecycle

The native implementation may complete a successful start synchronously or
asynchronously. The owner never calls discovery, native start/cancel/wait/
dispose, or the canonical-frame sink while holding its lifecycle lock.

```text
Open
  -> one native MI_00 read claim
  -> exact claim + generation check
  -> exact 64-byte/report-05 physical adapter
  -> immutable Switch2CanonicalInputFrame
  -> injected sink (outside the lifecycle lock)

Open -> StopRequested -> native quiescence -> managed callback quiescence
     -> Disposing -> Disposed
```

`RequestStop` is idempotent and reserves at most one cancellation for the
active claim. `TryQuiesceAndDispose` requires an explicit timeout from zero to
5,000 milliseconds. It first waits within that same budget for an in-progress
native begin or cancellation call to return, so `TryWaitForInputQuiescence`
never overlaps either control call and the native lease need not make them
concurrency-safe. Native transition timeout, native-quiescence timeout, or a
still-running managed callback leaves the stopped owner retryable and does not
close a possibly active lease. A true native-quiescence result is the seam's
guarantee that no old callback can arrive; only then can a cancelled read
without a completion be retired.
`DisposeQuiesced` requires prior native/managed quiescence, submits no new
controller I/O, and invokes no callback. Its synchronous managed/native resource
release (`ThreadPoolBoundHandle.Dispose`, `WinUsb_Free`, and `CloseHandle`) has
no hard wall-clock bound. Input and presence resources record successful release
independently. If one release fails, a retry releases only the remaining
resource and cannot report terminal disposal while either handle is retained.

The Windows lease binds its overlapped MI_00 handle to the thread pool, pins
only the caller's single preallocated report buffer for the duration of the
read, and allocates one native `OVERLAPPED` for the exact claim. Cancellation
uses `CancelIoEx(handle, exactOverlapped)` and never performs handle-wide
cancellation. The native operation signals quiescence only after its managed
completion has returned. The outer lease additionally drains callbacks and
suppresses stale, duplicate, or post-quiescence delivery before either retained
handle can be released.

The lease reuses one claim context and one completion delegate. The native
input handle reuses one operation, one wait primitive, one persistently pinned
64-byte owner buffer, and one `PreAllocatedOverlapped`; completed submissions
must cross explicit exact-claim retirement before the same storage can be reset
for the next claim. Begin never performs hidden zero-time retirement. A
lease-local control fence serializes per-read retirement and terminal
quiescence, while an immutable operation reference plus monotonic submission
epoch is revalidated before and after native wait and release. Exact
cancellation intentionally remains callable while retirement is waiting; the
native operation serializes `CancelIoEx` against release of that same
`OVERLAPPED`. The injected
steady-state test observes zero managed allocations across 20,000 successive
claim/completion cycles.

The owner itself has no timer, worker, polling loop, idle renewal, queue, log,
or persistent state. `Switch2ProUsbInputReadPump` is the separate dormant
completion-driven worker. It atomically acquires exclusive use of an idle
owner, issues exactly one read, waits for native and managed completion of that
exact submission, retires its reusable native storage, and immediately issues
the next read only when the atomically returned claim-keyed completion result is
`Published` and the owner lifetime is still open. Native quiescence alone never
authorizes rearm: cancellation which reaches quiescence without a callback is a
stop-only result. It uses one blocking background thread and no timer, sleep,
or fixed polling interval. A retirement timeout stops the lifetime fail-closed;
it never skips retirement or starts a second read. Public manual read methods
are fenced after the pump takes ownership, and a second pump cannot attach.

Pump stop requests exact cancellation through the owner, joins the worker
within the caller's one explicit budget, then uses the remaining budget for
quiescence and terminal disposal. A join or disposal timeout retains the owner,
native lease, and process-local container reservation for an exact retry. No
thread priority/MMCSS policy is asserted, and no rate or latency result is
inferred from this scheduling shape. Exact cancellation is allowed to overlap a
worker already blocked in retirement; retirement cannot release or recycle the
submission until that cancellation and native/managed quiescence are complete.

## Integration boundary

The following work remains intentionally absent:

- global `DS4Devices` registration or an `InputDeviceType` change;
- binding the dormant completion-driven pump and canonical sink to the existing
  runtime/profile/removal lifecycle;
- a transport-neutral runtime/profile adapter and removal UI;
- calibration reads, initialization, command transactions, output, LEDs,
  haptics, audio, BLE, or Joy-Con 2 lifecycle;
- live hardware, unplug/replug, suspend/resume, or latency verification.

The exact remaining coexistence blocker is the legacy `DS4Devices`/`HidDevice`
enumerator: it neither consults this registry nor excludes or atomically hands
off an admitted `057E:2069` MI_00, so it could independently open the same
physical controller. Before live registration, that path needs one explicit
classification and atomic exclusion/handoff policy, and any future MI_01 output
owner needs a separately proven sole-writer policy. The runtime/profile and
removal lifecycles also remain absent, and controlled hardware verification
must still prove open/share, unplug, suspend/resume, report-rate, and end-to-end
presentation behavior. This adapter does not claim, hide, reassociate, rebind,
initialize, or write to the controller.

## Provenance

No new wire fact or third-party code was introduced here. The exact USB facts
and their project-owned hardware evidence are inherited from and linked by
`switch2-physical-input-boundary.md`. The Windows implementation independently
adapts the project-owned, audited SetupAPI/HID/WinUSB algorithms in
`Switch2UsbHardwareVerify`; it does not call the verifier or import captured
hardware identity. Neither production tests nor public APIs contain a device
path, instance ID, hardware serial, or captured raw report.

## Offline verification

`Switch2ProUsbInputTransportTests` verifies admission-before-open, rejection of
duplicate interface cardinality, lease-registration mismatch cleanup, exact
device/transport generation delivery, sole read ownership, duplicate/old/
foreign completion rejection, sequence-exhaustion closure, cancellation
idempotence, partial-open and throwing-start cleanup, bounded retryable
quiescence/disposal, serialization of native begin/cancel/wait calls,
cross-thread sink reentry (no callback under the owner lock), absence of
output/raw-identity/native `HidDevice` capability, and zero managed allocations
across 20,000 steady-state begin/accept/publish cycles. Pump tests add exact
completion-driven rearm, exclusive ownership, duplicate-pump rejection,
fail-closed retirement failure, non-publish rejection, no premature rearm,
repeated asynchronous cycles, cancellation-without-callback stop semantics,
late-callback suppression, bounded join timeout, and retryable disposal.

`Switch2ProUsbWindowsAdapterTests` adds injected replay coverage for exact
three-snapshot acquisition, one-shot observation consumption, per-container
enumeration of two valid controllers, rejection of same-container duplicates,
atomic concurrent reservation, reservation retention through quiescence and
failed disposal, release/re-open after successful terminal disposal,
linearized concurrent observation, non-starving candidate rotation,
same-container partial-open poisoning, concurrent terminal-disposal ordering,
same-thread and concurrent stale-callback races during reservation publication,
whole-snapshot rejection for unattributable entry failure versus isolation of
an invalid attributable container,
missing/multiple and bcd/cardinality rejection, input share/open and MI_01
presence-open failure, rebind before and after acquisition,
descriptor/topology changes, synchronous and asynchronous completion, one
outstanding read, exact/stale cancellation, cancellation/completion races,
device removal, duplicate delivery, cancel-without-callback quiescence,
post-quiescence suppression, lease-local retirement exclusion, exact cancel
concurrent with a blocked retirement, epoch-safe reuse, required disposal
ordering and retry after either retained resource rejects disposal,
read-only/overlapped access policy, and absence of raw identity or output
capability from the public boundary.

```text
dotnet test DS4WindowsTests\DS4WindowsTests.csproj -c Release \
  -p:Platform=x64 --no-restore \
  --filter "FullyQualifiedName~Switch2ProUsbInputTransportTests|FullyQualifiedName~Switch2ProUsbWindowsAdapterTests"

Passed: 75, Failed: 0, Skipped: 0
```

These include 29 transport-owner/pump tests and 46 injected Windows-adapter
tests.
They are replay/unit simulations, not hardware verification or a measured
input-rate/latency claim.
