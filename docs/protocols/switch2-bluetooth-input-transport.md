# Switch 2 Bluetooth read-only input transport foundation

Status: production-routed by `Switch2BluetoothProductionCoordinator` into the
existing ControlService slot transaction, with fake-platform tests. Hardware
rate/firmware validation remains incomplete; association, registration, and
feedback stay in their separate owners.

`Switch2BluetoothConnectionAdmission`, `ISwitch2BluetoothInputLease`, and
`Switch2BluetoothInputOwner` define the narrow handoff from the existing
advertisement registry to the existing canonical input session.

## Explicit virtual-output replacement

The profile output handoff executes on the serialized controller action queue.
Native detach/attach can take seconds, exceeding a bounded BLE notification
FIFO even at 67Hz. Switch2RuntimeInputDevice.RunVirtualOutputTransition marks
only that cold action; Pro/standalone and joined sinks expose a nonblocking
read of the same exact runtime's scope. During it, ingress retains one latest
state, checking against the newest queued QPC even with a wrapped FIFO head.
Old-output transitions are intentionally not replayed into the replacement pad.
After the scope exits, normal active FIFO and fail-closed overflow resume.

Only the current serialized publication owner outside Report callbacks may
enter the scope. Nested scopes and exceptions restore its depth in finally.
The current pre-action snapshot is skipped if the output changed before Report;
the next current baseline goes through the existing mapper. Terminal reports
are never skipped. No queue growth, polling, alternate mapper or change to
disconnect/generation authority is involved. Production-wired fake BLE burst
tests cover Pro and each standalone half; joined sink tests cover the shared
scope, while existing active overflow and paired-lifecycle tests remain active.

## Admission and GATT invariants

- A connection admission can be created only by
  `Switch2BluetoothCandidateRegistry`, under its current scan lock, from a
  non-wake `RememberedThisHost` observation whose scan token, model, product ID,
  and non-quarantined registry entry all agree. Zero-host association candidates,
  foreign-host observations, duplicate dispositions, stale generations, and
  quarantined peers cannot create an admission.
- An admission is a scan-local capability, not an authentication result or
  persistent identity. A future adapter must consume it during that scan and
  must never log, serialize, cache, or reuse it for reconnection. The concrete
  Windows lease may return only its exact opaque reservation after proving a
  complete unambiguous teardown. A later matching advertisement may then issue
  one new admission within the same scan; no address or old admission is reused.
  Production may defer that unconsumed candidate until the predecessor's exact
  registration token has completed profile/output removal.
- Registry handoff consumes the scan token: neither the admission nor the
  successful lease surface retains a peer token, Bluetooth address, or session
  key. Exact one-shot binding after handoff comes from the private shared
  reservation reference plus scan generation and physical model/product tuple.
- Admission issuance is atomic and single-use. Registry peers can have only
  one live admission, and copied values share one opaque reservation
  which exactly one input owner may consume. A failed subscription burns that
  capability fail-closed until its concrete lease proves complete teardown;
  only a subsequent advertisement can publish a new reservation. There is
  never a second concurrent subscriber to the same peer.
- The read-only lease must be tied to the exact admission. GATT admission
  requires exactly one matching Nintendo service
  `ab7de9be-89fe-49ad-828f-118f09df7fd0` and exactly one matching Common05
  characteristic `ab7de9be-89fe-49ad-828f-118f09df7fd2`, with properties exactly
  `Read | Notify`. Missing, duplicate, stale-scan, dedicated 07/08/09, writable,
  or otherwise different shapes fail before subscription.
- The lease capability exposes only Common05 CCCD Notify subscription, CCCD
  None unsubscription, and their callbacks. It has no generic characteristic
  write, pair/unpair, application association, controller-memory/NVM, command,
  vibration, LED, or other output API. A future concrete implementation must
  keep those capabilities out of this interface rather than disabling them with
  a flag.

## Two-phase activation invariants

- `TryPrepare` consumes the exact scan admission and establishes the Common05
  notification subscription while canonical publication remains parked. Exact
  notifications that arrive inline during subscription, or after prepare but
  before commit, replace one latest-state sample in fixed storage. Older-QPC
  callbacks cannot replace a newer sample. `DrainOne` returns `Inactive` until
  activation commits. No pre-activation button history is replayed: the first
  state belongs to the newly activated lifetime, not to the startup wait.
- A successful prepare returns one credential bound to the issuing owner by a
  private reference fence and to the exact scan, device, and transport
  generations. Default, forged-fence, cross-owner, and wrong-generation
  credentials cannot change owner state. Copies of the authentic credential
  share one logical use: commit and abort are mutually exclusive under the
  owner lock, and every later use fails.
- Commit is the only transition from `Prepared` to `Active`. Abort retires the
  unpublished lifetime, clears its queue, and requests CCCD None without a Pro
  clear or Joy-Con half-loss. The legacy `TryCreate` entry point is retained as
  an immediate `TryPrepare` plus exact commit wrapper.
- Disconnect, a false/throwing subscription result, or
  any other subscription interruption before commit retires and requests the
  same unsubscribe-only cleanup. A callback running inline inside the subscribe
  call only records retirement; the preparing caller performs compensation
  after the platform call returns, avoiding unsubscribe reentry. No canonical
  loss is emitted for a lifetime that was never active.
- Once commit wins, disconnect, overflow, explicit `Stop`, sink-failure, and
  publication/retirement ordering retain the active-lifetime behavior below.
  The optional dormant drain pump below consumes this exact transition; the
  seam still creates no watcher, reconnect policy, registration owner, or
  production activation path.

## Completion-driven drain pump

- `Switch2BluetoothInputDrainPump` is an optional one-worker consumer attached
  only while an owner is `Prepared`. Attachment makes owner commit fail closed
  until the worker has reached a control-thread-proven start park. The worker
  cannot activate the owner: only the owner's exact scan/device/transport-bound
  prepare credential can commit or abort that lifetime.
- The worker starts behind its own park fence and then waits on the input
  owner's monitor. Owner commit, an accepted queued notification, drain
  completion, retirement, and exact pump-stop request pulse that monitor. The
  wait always rechecks state and queue predicates under the owner lock, so a
  commit or notification arriving between park and wait cannot be lost. There
  is no timer, polling interval, `Sleep`, UI dispatch, discovery, registration,
  reconnect, hardware, or output operation in the loop.
- A prepared owner cannot be generically stopped through the pump. Exact owner
  abort/disconnect/overflow must retire it first; that transition wakes the
  parked worker, which can then be joined. This prevents a dead pump from
  stranding a live prepare credential. A never-started attached pump is marked
  exited on the owner only after that exact retirement.
- Start-to-park and stop/join are bounded. The worker refuses to join itself.
  If a park or exit deadline expires, the pump retains the thread/owner and
  reports `RequiresQuarantine`; it never claims quiescence from a timeout. A
  later stop/join may prove exit and clear quarantine. Worker join proves only
  worker exit, not completion of a concurrent platform callback or service
  retirement operation.
- Once active, the worker drains the existing `DrainOne` FIFO until it is empty,
  then returns to the monitor wait. It never bypasses the owner's session,
  single-publication gate, sink-failure retirement, or deferred terminal
  callback ordering. Stop, disconnect, and overflow during a blocked sink
  therefore still clear only after that selected publication returns.
- One lifecycle-attention callback may be installed before worker start. Its
  exact device/transport-generation payloads are preallocated and raised at
  most once outside both owner and pump locks for active disconnect, overflow,
  sink failure, or terminal worker/control failure. Prepared abort emits no
  attention. The callback remains part of the worker lifetime: the owner-side
  exit fence is not published and bounded stop cannot succeed until it returns
  and the control thread has joined the worker. Attention is only a service
  wake-up; receipt is explicitly not worker, callback, report-delivery, or
  table-retirement proof. A throwing attention callback is not retried and is
  exposed through a sticky diagnostic failure count.
- Normal notification copy, monitor wake, canonical drain, and diagnostic
  counter updates allocate no managed memory after construction and warm-up.
  State, counts, sticky failure, and quarantine evidence are inspection-only;
  they do not add a per-report callback or logging path.

### User-requested disconnect

The ordinary Controllers-tab and special-action `Disconnect` command now uses
the same authenticated lifecycle-attention transaction as physical transport
loss. The logical runtime owns no Bluetooth address, WinRT/GATT object, raw
writer, slot token, or native-release operation. Before activation, or when no
exact registration participant is subscribed, the request is rejected. Once
active, the runtime reserves one generation-bound request and asks its exact
standalone or joined owner to publish `UserDisconnectRequested` attention.

Attention raised from inside a mapping callback only latches intent. The
registration core resumes on callback exit, arms the exact retirement claim,
drains report admission, publishes and acknowledges one terminal-neutral
report, stops the input/feedback owners, proves release of the one Bluetooth
lease (both leases for a joined pair), unsubscribes exact callbacks, removes
the ControlService presentation, and completes the table slot. Repeated clicks
coalesce, observer exceptions cannot escape into the report or UI thread, and
`callRemoval` cannot bypass the typed slot transaction.

The existing per-profile **Idle Disconnect** setting uses that same request
lane for Switch 2 Bluetooth runtimes. Because these devices do not execute a
legacy HID read loop, the runtime evaluates inactivity directly from each
already-validated canonical frame. Any physical button, including Switch 2
sidecar-only controls, or either logical stick outside the legacy DS4Windows
idle slop restarts the interval. Motion alone does not. Timing uses the frame's
monotonic QPC timestamp/frequency; a changed timebase or backward timestamp
rebaselines instead of manufacturing expiry. USB runtimes and disabled
timeouts never request Bluetooth teardown. No timer, polling worker, report
allocation, or separate native-disconnect path is introduced.

Switch 2 Controls also exposes the source-compatible three-way policy
**Off / Inactive / Absolute** with one day/hour/minute duration. `Inactive`
uses the physical-activity baseline above. `Absolute` starts at logical-runtime
activation and does not restart while the user plays; a joined pair therefore
retires as one controller. A zero duration is disabled, USB remains
ineligible, and both modes reserve the same exact generation-bound lifecycle
attention transaction. Existing profiles serialize `LegacyProfile` and keep
their ordinary Idle Disconnect behavior until the new control is explicitly
changed. Policy changes are sampled on the existing report path and retain the
actual session/activity baselines; they create no one-second checker, task, or
additional clock owner.

## Notification and lifetime invariants

- Each callback carries the transport generation, exact service and
  characteristic identities, completion QPC, and body. The owner queues only
  the preparing, prepared, or active generation with an exact 63-byte Common05
  body; publication is admitted only after commit.
- Accepted bytes are copied into construction-time fixed storage before the
  platform callback returns. Before activation only the latest initial state
  is retained, permitting slow virtual enumeration and user-paced Joy-Con joins.
  After activation no unread slot is overwritten. When the bounded
  queue is full, the owner increments `OverflowCount`, invalidates the complete
  transport generation, discards the queue, and unsubscribes. It emits exactly
  one loss/clear for that active lifetime. Latest-state replacement is strictly
  pre-activation; active ordering and overflow protection are unchanged.
- `DrainOne` feeds the copied body and completion timestamp to the existing
  `Switch2InputSession`. Pro frames go to the injected Pro canonical sink;
  Joy-Con L/R frames go to the injected Joy-Con canonical sink. The production
  Joy-Con sink must serialize each half through
  `Switch2JoyConJoinedCoordinator`; this dormant layer does not create a second
  pairing or mapping stack.
- Canonical sink calls run outside the queue/session lock. Only one selected
  publication may be in progress; a concurrent drain reports `Busy`. Stop,
  disconnect, overflow, or a reentrant sink stop retires the generation
  immediately but defers its one loss/clear callback until that selected
  publication returns. Clear therefore cannot overtake a frame already handed
  to the sink, and sink reentry cannot deadlock the owner. A throwing publish
  retires fail-closed with `SinkFailure`; a throwing retirement callback is
  counted and is never retried as a second logical clear.
- Disconnect or explicit stop changes the owner state to retired and clears the
  queue before requesting CCCD None. A callback synchronously triggered by
  unsubscribe therefore observes the retired generation and cannot publish.
  Pro receives one clear; Joy-Con receives one generation-fenced half-loss for
  its exact side. Repeated or stale disconnect callbacks have no effect.
- Calibration is an already-validated, device-generation snapshot injected by
  the caller. This layer never reads controller memory. The notification and
  drain path allocates no managed memory after construction and warm-up.

## Evidence and clean-room boundary

Protocol facts were independently expressed from pinned local sources:

- `TommyWabg/Switch2Connect@4487322a306f04efa27682e3f3a508635a84fd98`:
  `src/controller.py` (`Controller.connect_ble`,
  `Controller.enable_input_notify_callback`) and `src/discoverer.py`
  (`run_system_bluetooth_discovery.callback`). These corroborate the custom
  service, Common05 characteristic, physical product IDs, manufacturer company
  ID, and remembered-host advertisement classification. Only protocol facts
  were used; no control flow, association payload, table, or source text was
  copied.
- `SDL@c71abd08605b8bb7078372307a93274725c99fe0`:
  `src/joystick/hidapi/SDL_hidapi_switch2.c` leaves Bluetooth initialization
  unsupported, so it supplies no production Windows BLE lifetime semantics.
- `hifihedgehog/SDL@d98c5804a9d20b0d96e993741797878c86b8f1e1`:
  `src/joystick/windows/SDL_ble_switch2joystick.c` is treated only as a
  hardware-unvalidated negative control. Its address identity, writable setup,
  single-latest-report slot, and callback-lifetime workaround are not adopted.
- PadForge stable `0794fd01bd19f4c096b982ffc824b88bce5ed743` and
  v4-dev `b7f58cf852b4028eae582b14d2173b4b716a73ee` are behavioral evidence only
  under CC BY-NC-SA 4.0. No PadForge code was copied or adapted. HIDMaestro has
  no relevant physical BLE GATT client.

The repository-wide reuse restrictions remain defined by
`VIIPER/docs/architecture/controller-platform-provenance-ledger-2026-08-29.md`.

## Production composition and remaining evidence gates

`Switch2BluetoothWindowsAdapter`, `Switch2BluetoothWinRtPlatform`, and
`Switch2BluetoothProductionCoordinator` now provide the concrete Windows
watcher/lease path, uncached GATT enumeration, callback-generation ownership,
safe asynchronous CCCD teardown, and production `ControlService` registration.
Discovery preserves the Windows adapter identity used for association; a
multi-radio hardware matrix is still required to prove deterministic behavior
on hosts with more than one usable radio.

Hardware tests must still measure callback QPC/counter cadence and exercise
disconnect, reconnect, suspend, radio-loss, and GATT failure races. No input
rate is inferred from an application polling interval or a best-effort Windows
connection-parameter request.
