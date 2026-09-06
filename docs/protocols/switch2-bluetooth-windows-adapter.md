# Switch 2 Windows BLE discovery, association, and input adapter

Status: production-routed through `Switch2BluetoothProductionCoordinator` and
the ControlService registration transaction, with deterministic fake-platform
coverage and an x64 compile of the real WinRT projection. It owns the
controller-side command-`0x15` association ceremony described in
`switch2-bluetooth-association.md`; physical hardware/firmware conformance is
still not established.

This tranche connects the pure
`Switch2BluetoothCandidateRegistry` discovery boundary to the existing
`ISwitch2BluetoothInputLease` boundary for one already-remembered-this-host
Switch 2 Pro Controller, Joy-Con 2 (L), or Joy-Con 2 (R). A zero-host candidate
can now consume a separate one-shot association capability; it becomes an
input candidate only after re-advertising the selected host address.

## Windows 11 connection preference interop

After uncached GATT discovery establishes the link, the adapter requests and
retains ThroughputOptimized when available. Windows 10 and rejected requests
continue with Windows-selected parameters. Acceptance is logged once per open;
it does not prove a particular negotiated interval or end-to-end latency.

The .NET 8 projection requires `IWinRTObject.NativeObject.GetRef()`, not
`Marshal.GetIUnknownForObject`: the latter returns a CLR wrapper around the
projection rather than the native Windows object used to query Device6. The
owned GetRef is released in the existing finally; NativeObject.ThisPtr is
borrowed and must not be released. See Microsoft's
[C#/WinRT interop guide](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md#iunknown).
A real Windows in-memory-stream identity regression covers this without radio
access. Request status, close/release behavior and Windows capability guards
are otherwise unchanged.

## Discovery and privacy contract

- `Switch2BluetoothWindowsAdapter.TryStartScan` generates a fresh private
  32-byte key with `RandomNumberGenerator.Fill`. The selected host address and
  key are held in scan-owned arrays and zeroed with
  `CryptographicOperations.ZeroMemory` when that exact generation retires.
- `Switch2BluetoothWinRtPlatform` explicitly configures
`BluetoothLEAdvertisementWatcher` for active scanning. The watcher wrapper
  copies the manufacturer value to stack storage and passes it through a span;
  the adapter does not retain the value.
- Watcher publication linearizes only after `Start` returns and the same scan
  lifetime is still current. Advertisements raised inline by a failed start are
  discarded, and an inline `Stopped` event makes start fail rather than
  returning a success for an already-retired watcher. Retiring a scan publishes
  one teardown task before any concurrent observer can mistake cleanup for
  complete. A scan-local platform lifecycle gate serializes `Start` against
  concurrent `Stop`, handler detach, and disposal, so ending a not-yet-returned
  start cannot dispose a watcher which is still inside `Start`.
- Admission requires exactly one Nintendo company section (`0x0553`), the
  existing strict 24-byte advertisement codec, Nintendo VID `0x057E`, and one
  of physical PIDs `0x2066`, `0x2067`, or `0x2069`. A second Nintendo section,
  wrong reserved byte, wake advertisement, foreign remembered host, malformed
  address, or stale scan generation cannot acquire an address capability.
- A 48-bit Windows Bluetooth address exists only in a fixed-capacity private
  token-to-address table inside the active scan. The table entry is erased
  before the asynchronous device open begins. It cannot be reused while that
  admission is active; the reservation and address capability are burned
  fail-closed until an exact clean lifecycle handoff permits a fresh candidate.
  After a clean command-`0x15` Commit and complete temporary-owner cleanup,
  exactly one matching `no host -> this PC` readvertisement may rearm that same
  scan-private entry for the ordinary remembered-device open. Failed or
  ambiguous association never authorizes the rearm. An exact host transition
  that races the temporary owner's cleanup is held as in-progress and cannot
  become a connection candidate until cleanup reports a clean result.
  After a remembered input lease proves complete handler/CCCD/output/native-
  owner teardown, the registry retires that exact reservation. The next
  matching advertisement may rearm the scan-private address slot and issue one
  fresh admission. False, timed-out, disposal-ambiguous, stale, foreign, and
  identity-quarantined releases never rearm it.
  The production coordinator retains only a scan-private peer-token-to-slot-
  token fence after successful activation. If a fresh candidate arrives while
  that exact registration is still bound, retiring, or quiesced, it preserves
  the unconsumed address capability and requires another advertisement. It may
  open only after the table no longer contains the old token, which means the
  old profile and output inverse has completed removal. A quarantined or
  uninspectable predecessor rejects the candidate for the scan instead of
  permitting overlapping controller slots.
  Address rotation creates a distinct token/capability and is never merged by
  product ID. The advertised address type is carried with the capability and
  supplied to the matching WinRT open overload.
- Production source contains no diagnostic/logging surface for a raw address,
  peer token, manufacturer bytes, host bytes, or scan key. None of those values
  is serializable or persisted by this adapter. Tests may record synthetic
  addresses inside their fake platform to prove exact capability routing.

Advertisement host bytes remain a reconnect hint, not authentication, BLE SMP
bond proof, or encryption evidence. `BluetoothLEDevice.FromBluetoothAddressAsync`
is an OS device open; this code never calls a Windows pairing API. Nintendo
application association is documented separately.

## GATT and lifetime contract

The complete open has one configured deadline. A token cancelled before open
burns the one-shot capability without entering WinRT. Cancellation raised by a
source operation while the deadline is still live is attributed to that source
stage rather than mislabeled as expiry; the same rule applies to a platform's
own `TimeoutException`. The adapter requests the Nintendo
service and Common05 characteristic with `BluetoothCacheMode.Uncached` and
accepts only:

- exactly one service
  `ab7de9be-89fe-49ad-828f-118f09df7fd0`;
- exactly one characteristic
  `ab7de9be-89fe-49ad-828f-118f09df7fd2`; and
- the exact native property set `Read | Notify`, with no Write,
  WriteWithoutResponse, Indicate, or other native property.

The concrete lease installs generation-owned `ValueChanged` and
`ConnectionStatusChanged` handlers, enables CCCD Notify asynchronously, and
only then becomes available to the input owner. Reports received before the
owner publishes its transport generation are discarded. Once published, every
callback carries that exact transport generation and the exact Nintendo UUIDs.
An inline disconnect while those handlers are being installed is terminal and
prevents Notify enablement even if a later connection-status read appears true.
Subscription success has a final state/generation linearization check, so a
disconnect which wins that check cannot be returned as success.

If an uncached service or characteristic query ignores cancellation, its parent
device/service is transferred to a late-result continuation. The parent graph
is not disposed while the WinRT operation can still be using it; the eventual
query result is disposed first and then its retained owners. A completion racing
deadline cancellation is treated the same way, so a completed handle cannot
escape the cancellation branch.

Teardown first retires local input publication, removes both handlers, requests CCCD
None, and waits for two callback-drain gates: the platform source gate and the
lease consumer gate. A bounded observer reports timeout/failure to the caller,
but the underlying resource-release task continues retaining the characteristic,
service, device, delegates, and callback state until both callbacks and the
outstanding CCCD operations complete. If a timed-out Notify write ignores
cancellation, None is serialized behind that exact operation so a late Notify
cannot win after teardown; the WinRT object graph remains retained throughout.
Both handler removals and the sole best-effort CCCD None compensation are
attempted before ambiguous handler removal quarantines the complete object graph
instead of disposing beneath a possible late callback. Native WinRT event
wrappers contain malformed-buffer and callback exceptions while still exiting
their drain gates. No stale callback can enter a successor generation.
Only after the exact release task proves every drain, CCCD result, output write,
and native disposal clean does the lease offer its opaque reservation back to
the registry. That cold-path handoff has no peer token or address and adds no
input-report hot-path work. Registry quarantine can refuse reconnect without
falsifying an otherwise clean native-resource release proof.
Cancellation, watcher stop/detach, handler detach, and CCCD calls occur only
after their adapter/lease state and completion task are published and the owner
lock is released; inline platform behavior therefore cannot observe a
half-published teardown.

The notification callback copies a maximum 64-byte WinRT `IBuffer` into stack
storage. The existing input owner performs the exact 63-byte Common05 check and
copies admitted reports into its fixed queue. No discovery, service query, or
async CCCD work occurs on that input publication path.

The same concrete lease owns the closed acknowledged player-indicator command
channel. It accepts only a four-bit mask, writes that mask exactly in command
`0x09`/subcommand `0x07`, and validates the donor-established command/status
response. If an exchange is already active, player LEDs are treated as state:
only the newest complete mask is retained and sent after the active exchange.
The operation task is published while the lease lock is held, so even an
inline acknowledgement cannot let teardown miss a successor write. Retirement
stops new admission and drains the exact active command operation before the
GATT object graph is released.

## Test evidence

`Switch2BluetoothWindowsAdapterTests` verifies:

- active watcher setup, strict/monotonic scan retirement, and fresh per-scan
  token derivation;
- malformed identities, duplicate Nintendo sections, duplicates, address
  rotation, zero-host and foreign-host classifications;
- one-shot raw-address authority and late-open disposal;
- exact post-association host promotion/address rearm and quarantine of the
  same transition after a failed ceremony;
- clean same-peer lease retirement followed by one fresh same-scan candidate,
  preservation across a deferred candidate, plus non-rearm after false CCCD
  teardown and after identity quarantine;
- uncached exact service/characteristic queries and rejection/disposal of
  duplicates, wrong UUIDs, writable Common05, and extra native properties;
- open/query/notification exceptions and bounded startup timeouts;
- pre-cancelled opens, source-side cancellation attribution, completion races,
  and retention of device/service owners through late uncached-query results;
- transport-generation notification/disconnect fencing, stale callbacks,
  teardown races, serialization of late Notify before None, and retention
  through non-cooperative CCCD operations and ambiguous handler removal;
- watcher configuration/start rollback, pre-commit advertisement suppression,
  and inline stopped-callback retirement; and
- serialized factory/user calibration reads, exact `0xA1B2` user-marker
  precedence, independently corroborated `0x1FC040`/`0x1FC080` addresses,
  invalid-user factory fallback, and non-terminal optional-read failure;
- exact player-indicator masks, newest-state coalescing behind an active
  acknowledged request, inline-completion task publication, and stale-
  generation rejection; and
- zero managed allocations in 1,000 steady duplicate observations after
  warm-up in the fake-platform span path.

Those are replay/simulation results. They do not establish a controller report
rate, negotiated PHY/connection interval, radio selection, Windows bond state,
input latency, physical reconnect behavior, or hardware correctness.

## Source/provenance ledger for this tranche

| Source | License/use | Directly observed facts | Not adopted |
| --- | --- | --- | --- |
| `TommyWabg/Switch2Connect@4487322a306f04efa27682e3f3a508635a84fd98`, `src/discoverer.py:1606-1660`, `src/controller.py:1160-1182,2275-2350,2664-2682,3984-3997` | GPL-3.0; compatible donor with explicit attribution | Nintendo company `0x0553`; manufacturer VID/PID and remembered-host fields; physical PIDs; service/Common05/command/response UUIDs; notification input; exact command-`0x15` association vectors and response-first ordering | Bleak object model, raw-address/manufacturer logging, command discovery by handle order, per-device dictionaries, and UI structure |
| `hifihedgehog/SDL@d98c5804a9d20b0d96e993741797878c86b8f1e1`, `src/joystick/windows/SDL_ble_switch2joystick.c:1079-1218,1255-1339,1470-1580,1932-1940,2298-2345` | zlib; behavior/API corroboration only for this original implementation | Windows active watcher; WinRT open by address; uncached UUID-filtered GATT queries; ValueChanged and connection-status handlers; handler removal does not itself prove callback drain | Raw-address logging, connect retry sleeps, command/output paths, preferred-parameter claims, global mutable controller list, bounded process-lifetime leak workaround |
| Existing DS4Windows codec/registry/input owner on the current worktree | GPL-3; directly reused local contracts | strict capture-backed 24-byte advertisement admission; keyed scan token; exact service/Common05 snapshot; generation-owned bounded Common05 ingress | no second parser, profile mapper, joined-Joy-Con coordinator, or reconnect scheduler was created |
| PadForge stable `0794fd01bd19f4c096b982ffc824b88bce5ed743` | CC BY-NC-SA 4.0; behavioral evidence only | no required implementation fact for this Windows GATT client | no code, structure, text, constants, or binary copied |

The later read-only user-calibration addition also compared current upstream
SDL `c71abd08605b8bb7078372307a93274725c99fe0` (zlib),
`XenuIsWatching/hid-nintendo2-dkms@32a981ea7f916f1792a7e35aa0ecf79063ec4001`
(GPL-2.0-or-later), and
`ndeadly/switch2_controller_research@d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92`.
SDL and hid-nintendo2 independently agree on the secondary `0x1FC080` address
and little-endian `0xA1B2` marker. Switch2Connect v2.8
`61ac6642ce12fe7217e38a860b14863b18ca7e28` instead uses the conflicting
`0x1FC060`; that address was audited and deliberately not adopted.

The adapter, capability table, deadline policy, double callback-drain gates, and
late-result cleanup are original DS4Windows implementation policy, not claims
about the controller protocol.

## Remaining release evidence gaps

### Settings discovery status (source after portable b55)

Settings no longer equates a running ControlService with an active BLE watcher.
An immutable, address-free control-plane snapshot distinguishes host lookup,
active scanning, unavailable host, startup failure, unexpected watcher stop,
cleanup in progress, failed cleanup and stopped discovery. Late host-lookup
completion uses exact attempt identity; it cannot replace shutdown or a newer
lookup. Bounded Stop observations continue to consult actual coordinator cleanup
state, so timeout alone is not rendered as a permanent native failure.

Discovery detail and association result occupy separate text blocks. Refresh
does not erase the previous action result, preserves a still-available candidate
selection, and never clears an in-progress association. Association is enabled
only with an active scan and selected candidate, with status checked again on
click. This adds no report-path work or polling timer. The new Settings-entry
refresh ignores nested selectors' bubbled tab-selection events.

Status/coordinator tests: 29 pass. Related Bluetooth/theme tests: 276 pass. Full
suite: 3,246 pass, 3 existing live-audio skips, zero failures. Portable b56 now
contains this follow-up. Computer-use inspection verified stopped discovery,
disabled association without a candidate, active empty discovery after Start,
and Settings navigation. Nonempty/busy/error UI and wireless physical acceptance
remain pending. No pairing or radio interval is inferred from the active scan.

### Joy-Con action availability (source after portable b56)

Manual join and standalone actions require an available coordinator lifecycle,
automatic pairing off, no other association/pairing action in progress, and exact
side selections. Both Settings and Controllers use the same UI busy state.
An interrupted advertisement watcher does not alone forbid use of already-held
Joy-Con connections; stopping or failed coordinator lifetimes do. The backend
still revalidates exact candidates and owns native admission and retirement.

Refresh preserves a still-available manual selection but never substitutes a
different half, including on subsequent refreshes after the selected half leaves.
Manual selection is explicit even when only one candidate is available. Candidate
counts and action results have separate text blocks, so Refresh does not erase
failure details. Enabling automatic pairing while stopped now describes the saved
on state correctly without attempting an unavailable operation. No report-path
work or new timer is introduced. This UI follow-up is not in the live b56 payload.

Validation: 12 availability/selection cases, 409 related Joy-Con/Bluetooth/theme
tests, and the full September 5 suite pass (3,258 passed, 3 existing live-audio
skips, zero failures). XAML compilation and initially disabled action controls
are verified; manual visual and Joy-Con hardware acceptance remain pending.

### Shutdown ownership evidence

The September 4 production-coordinator stop audit first repaired a discarded
bounded semaphore-wait result (included in portable b54). Follow-up source tests
then reproduced false success on retry, premature restart during a held watcher
drain, and a watcher created after Stop had already run. The source now publishes
one cleanup task per started coordinator lifetime. Each caller has a five-second
observation budget; cancellation/timeout does not cancel cleanup, release another
gate owner, dispose the lifetime cancellation source early, or allow restart.
Cleanup also waits for the in-progress start attempt. Explicit association work
joins the same tracked control-plane task set as remembered-device opens.

Watcher retirement remains accessible by exact generation after Windows Stopped
or an expired observer. A failed unpublished watcher's handler drain also fences
successor adapter scans. Pending Joy-Con cleanup awaits the actual input lease
resource-release task, not its expired bounded observer. False unsubscribe or
dispose results remain false; ambiguous native ownership is not upgraded to
success. None of these tasks run on the notification/input-report hot path.

The 257 related Bluetooth tests pass, including real semaphore timeout,
concurrent/cancelled stop observers, startup/stop overlap, spontaneous watcher
stop, partial handler setup, late resource release and preserved failure results.
Failed startup now returns an exact cleanup task alongside its failure reason.
The coordinator retains that task and lifetime until it finishes, including
partially installed handlers, disposal failure, and a factory returning no
watcher while Stop is already waiting. Automatic failed-start cleanup is fenced
to the exact cancellation-source identity so it cannot stop a later lifetime.
Three additional failed-start regressions reproduced before this repair; a clean
failed-start/restart control passed throughout.

This is source/simulation evidence. Portable b55 was staged only and never
launched. b56 includes the follow-up and has active discovery after normal Start;
that does not validate wireless pairing or physical shutdown. The 3,228-test
shutdown suite passed with 3 existing live-audio skips and zero failures; later
discovery/UI tests are recorded above. The Bluetooth release gates remain open.

The public WinRT advertisement watcher used here cannot be bound to a selected
Bluetooth radio. The caller supplies the selected host address for strict
remembered-host classification, but that does not prove Windows opened the peer
through the same radio on a multi-radio machine. The current production route
therefore supports the default-adapter Windows path; deterministic radio
selection on a multi-radio host is unproven and must not be claimed.

Hardware validation must also establish firmware coverage, negotiated BLE
properties, exact report cadence, disconnect/sleep behavior, and callback/QPC
timing before a conformance or latency claim. The registration transaction is
now wired, but those hardware/firmware gates still apply to release claims.
