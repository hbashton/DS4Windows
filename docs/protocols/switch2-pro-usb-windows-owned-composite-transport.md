# Switch 2 Pro USB Windows owned-composite transport

Status: **production-selected by `Switch2ProUsbProductionCoordinator` and
offline-tested**. `ControlService` now constructs this adapter for an admitted
057E:2069/bcdDevice 0x0201 exact composite. No controller I/O was performed
while making or validating the current revision.

## Evidence boundary

The transport was reconciled against these pinned reference revisions:

| Reference | Pin | Use |
| --- | --- | --- |
| Switch2Connect | `4487322a306f04efa27682e3f3a508635a84fd98` | USB interface/pipe and request-order corroboration |
| SDL current | `c71abd08605b8bb7078372307a93274725c99fe0` | independent USB startup/output corroboration |
| SDL hifihedgehog fork | `d98c5804a9d20b0d96e993741797878c86b8f1e1` | independent fork comparison |

The implementation is original DS4Windows GPL code. The references establish
the exact admitted topology already frozen by the repository: MI_00 HID input
and 64-byte output, plus MI_01 WinUSB bulk OUT `0x02` and bulk IN `0x82`. They do
not by themselves establish a causally valid response tuple for the two USB
feature requests. A separate authorized hardware capture established the exact
step-specific `00/F8` responses now enforced by
`Switch2UsbCommandCodec.TryValidateFeatureResponse`. The concrete command lease
therefore performs an exact write/read/validate pair for both feature steps and
for the capture-backed Player-1 LED request; it still rejects SDL's unchecked
read as proof and does not borrow Bluetooth response semantics.

## One physical owner

`Switch2ProUsbWindowsOwnedCompositeAdapter` reserves the opaque physical
container before opening anything. Before a lease can escape it:

1. discovers one exact admitted MI_00/MI_01 pair;
2. opens one overlapped MI_00 `SafeFileHandle` with read/write access;
3. opens one overlapped MI_01 file and adopts one exact WinUSB lifetime;
4. re-enumerates and matches the complete private Windows identity while both
   handles remain retained; and
5. transfers the reservation and both handles into one lease object.

That revalidation is the final discovery for the lifetime. After escape, input,
HID output, command output, command input, cancellation, and disposal operate
only on retained handles. There is no path rediscovery and no second HID or
WinUSB handle.

The existing public `Switch2ProUsbWindowsAdapter` remains the legacy read-only
discovery/open path. Production Switch 2 Pro USB registration uses the
full-duplex adapter through `Switch2ProUsbProductionCoordinator`.

Cleanup ambiguity is ownership, not an ordinary failed open. After a container
reservation exists, its exact registry entry strongly roots every file,
WinUSB, preparsed-data, device-information-set, or composite cleanup owner whose
release was not proved. If discovery fails before a container can be attributed,
the process registry strongly roots an unattributed quarantine owner and rejects
all later discovery and reservation attempts. Neither path depends on a local
stack frame, a finalizer, or garbage-collection reachability to preserve the
native lifetime.

Before either a successful legacy read-only lease or owned full-duplex lease
escapes, that same entry also adopts and strongly roots the exact lease object.
The root remains for the entire reservation lifetime, not only after an error,
and therefore transitively retains every live or ambiguous MI_00/MI_01 handle,
operation, buffer, and release capability if a caller drops its last reference.
Final registry removal authenticates that reference-identical lifetime owner and
removes the root only in the terminal-fence publication which releases the
container. A failed facet cleanup, release hook, reentrant callback, or retryable
native release leaves both the root and the exact retry capability intact.

## Independent submission lanes

The MI_00 owner has one retained, preallocated read operation and one separate
retained, preallocated write operation on the same bound handle. The two lanes
have independent submission state and fixed pinned buffers, so one input read
and one output write can coexist without allocating a replacement operation.

MI_01 has one preallocated operation for bulk OUT and another for bulk IN.
Startup admits only one command transaction at a time, and at most one operation
can exist on either exact pipe. `WinUsb_AbortPipe` is therefore exact within
this unescaped command lifetime: it cannot cancel a second owner's operation or
a second operation on the selected pipe.

A start result distinguishes a rejected new read from a prior busy submission.
Neither the legacy nor owned wrapper releases on a generic false start. A
completed-but-unretired prior input operation remains owned until its exact
claim retires it.

One atomic terminal fence is shared by input, HID output, and command facets.
Every native start admission, native begin call, and exact-operation publication
is serialized by that fence. Every input/output/command ambiguity latches it.
An operation admitted before a concurrent latch must recheck the same fence and
retain/quarantine its exact capability before any success escapes; an operation
admitted after the latch performs no native I/O. The lock order is terminal
fence, then facet gate. The input IOCP callback follows that order and never
acquires the terminal fence while holding the input gate.

A thrown native begin is not converted to a clean rejected start. The exact
pin, `OVERLAPPED`, operation object, handle, and reservation remain retained and
the shared fence latches. A late input completion may cross the callback only to
establish native/managed drain; it is suppressed before the canonical input
consumer and cannot publish controller state after quarantine. Failure to free
storage for an otherwise rejected read is treated the same way, not as an
ordinary `false` start.

## Deadline, uncertainty, and retirement

The historical `Bounded` method/property names denote a maximum cumulative
**managed native-quiescence wait budget**, not a hard wall-clock limit for the
whole call. Accounting begins before native begin/cancel and elapsed synchronous
phase time is deducted before every wait. Win32/WinUSB begin, `CancelIoEx`,
`WinUsb_AbortPipe`, `FreeNativeOverlapped`, `WinUsb_Free`, and `CloseHandle` are
synchronous APIs without a nonblocking deadline contract. The production owner
deliberately adopts this managed-wait contract and retains/quarantines any
ambiguous native lifetime; hardware lifecycle evidence remains required before
a release claim.

When an output wait budget expires, the result carries the exact retained
operation claim. The buffer, native storage, MI_00 handle, command lifetime, and
container reservation remain owned; no replacement write is admitted. The
claim authorizes bounded retryable cancel/drain. A false cancel followed by a
non-quiescent wait remains eligible to reissue exact cancellation; a false
cancel followed by a completion race drains once without a duplicate cancel.

Startup has the analogous fence. Every `TimedOut` or `PossiblyConsumed` result,
including malformed/noncausal response and start contradiction, blocks another
`Execute` until exact transaction retirement. Command and retirement claims
authenticate the exact lease object, exact full lifetime, registration, and
both generations; matching numeric generations from another lifetime cannot
operate or dispose this lease.

## IOCP and cleanup proof

Completion callbacks contain every exception. They authenticate the exact
native-overlapped pointer, bound transferred bytes to the submitted buffer,
publish terminal failure on a callback-path fault when possible, and never let
a stale/duplicate/mismatched callback signal another submission.

Quiescence is signalled only after:

- the exact completion transitioned the current submission;
- `FreeNativeOverlapped` succeeded; and
- for input, the managed consumer callback returned.

For callback-only output/command operations, signalling is the final owned
resource access; after it returns the IOCP frame only unwinds. A failed native
storage release never signals and permanently fences the lease rather than
publishing false quiescence.

OS cleanup is observable instead of relying on `SafeHandle.Dispose()`'s void
surface. Nonzero contradictory WinUSB and HID preparsed-data results are adopted
and released. `WinUsb_Free`, `CloseHandle`, `HidD_FreePreparsedData`, and
`SetupDiDestroyDeviceInfoList` failures propagate as cleanup ambiguity. Exact
WinUSB/file owners retain the same handle for retry where a retry capability is
still owned. A native `false` release can retry the same exact capability; a
thrown release has no consumed/not-consumed fact and is permanently fenced
without reissuing a potentially double free/close. On thrown `CloseHandle`, the
file owner publishes ambiguity before immediately marking its
`SafeFileHandle` invalid and disposing the managed wrapper. This suppresses the
SafeHandle native finalizer mechanically, so it cannot later close a recycled
numeric handle; the registry's exact lifetime root retains the ambiguity record
for terminal attention. Any unproven acquisition, revalidation, or terminal
cleanup keeps the process container reservation fenced.
If two cleanup operations fail while one acquisition frame unwinds, their
retained owners are composed; a later `finally` failure cannot overwrite and
drop the earlier native capability.

Terminal order is:

1. drain and release exact command OUT/IN operations;
2. release WinUSB, then the MI_01 file;
3. prove output and input quiescence;
4. release MI_00 operation storage, bound handle, then file; and
5. publish the container reservation release.

Reservation publication itself is retry-safe: a throwing release hook retains
the exact reservation capability and its registry-rooted lease; it cannot
strand an ownerless registry entry or leave live SafeHandles reachable only by
GC.
Terminal publication is a two-phase protocol shared by the full-duplex and
legacy read-only leases. It first fences new submissions, then runs the injected
release hook without holding the terminal gate so a same-thread reentrant or
concurrent stale callback can latch quarantine. Registry removal is finally
committed while holding that terminal gate. If the callback wins, removal is
rejected and the same reservation capability remains retained; if removal wins,
later native admission is permanently sealed. This couples the final callback
fact and reservation removal instead of relying on `Monitor` ownership, which
is same-thread reentrant.

## Deliberate limits

- no feature-response semantic guess;
- Bluetooth/Joy-Con association remains a separate BLE production owner;
- no cadence/500 Hz claim;
- no claim that synchronous native calls obey a hard deadline;
- no controller, LED, or haptic hardware run in this revision.

The production composition now activates the canonical feedback pump and
requires an exact terminal-neutral operation before whole-composite disposal.
It does not add a second mapping or physical-output writer.

Hardware validation must occur only after the offline composition blockers are
closed and under the repository's authorized verifier protocol. The separate
hardware verifier must never open these interfaces alongside a live owner.

## Offline verification

The focused Windows suites cover exact pair acquisition, pre-escape-only
discovery, ambiguity/quarantine, partial-open cleanup, output/input concurrency,
retained timeout and late completion, cancellation retry/race behavior, foreign
claims and private-fence/current-operation provenance, exact feature and
player-LED response validation,
whole-lease disposal order, observable handle release retries and no-repeat
thrown-close finalization, attributed/pre-attribution acquisition ownership,
registry-rooted terminal lifetimes across caller-drop/forced-GC,
reservation publication retry, same-thread and cross-thread callback races at
the release hook, symmetric cross-facet terminal races, deadline arithmetic,
IOCP publication gates, and legacy read-owner regression cases.

Representative commands:

```powershell
dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Switch2ProUsbWindowsOwnedCompositeAdapterTests|FullyQualifiedName~Switch2ProUsbWindowsAdapterTests"
dotnet build DS4WindowsWPF.sln -c Release -p:Platform=x64 --no-restore
```
