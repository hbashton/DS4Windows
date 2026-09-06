# Legacy HID shared-slot authority

Status: the exact base `DS4Device` and `DS3Device` worker path is integrated
with `ControlService`. All other HID subtypes remain on their existing owner
and are rejected before typed registration. Switch 2 ControlService hosting
remains dormant.

No hardware was opened or exercised for this tranche.

## Integrated boundary

`ControlServiceLegacyHidSlotAuthority` owns one
`InputControllerRegistrationTable` across service runs. Every open uses a
strictly increasing service generation. Every admitted base DS4/DS3 lifetime
uses a separate strictly increasing connection generation and one retained
record containing:

- the exact device reference, table-issued slot token, service generation,
  slot generation, and connection generation;
- the host-issued legacy lifetime lease and typed worker cleanup lease; and
- the exact Removal, SyncChange, SerialChange, ChargingChanged, main Report,
  and optional UDP-motion Report delegates, plus their possibly-installed
  state.

Startup and hot-plug now reserve/bind the exact table slot before publishing
the device into `DS4Controllers`. `HotPlug` is serialized with start, stop, and
exact removal through `serviceLifecycleLock`. Preparation is split without
reordering its successful legacy phases:

1. configuration, controller settings, `slotManager`, and OSC plug state;
2. legacy lifecycle subscriptions;
3. touch/profile/output/profile hooks;
4. the exact main Report delegate;
5. the exact UDP motion delegate when enabled;
6. typed worker activation; and
7. Steam Input reclaim.

The table enters its activation-pending Attached state before the typed worker
start. A first report can therefore acquire its exact report lease without a
second queue or first-report handoff. External worker start completes through
the table's single-acquisition activation credential. A rejected or uncertain
start quarantines the table slot. If an exact worker lease exists, bounded
quarantine recovery stops it directly through that retained lease and removes
its registry lifetime; cleanup proof does not make the quarantined slot
reusable. Recovery does not re-enter a registration owner whose first Stop was
outcome-uncertain, and it does not replay Stop or registry removal when that
owner already reached Removed before a later table-completion failure.

Removal is queued off the device worker which raised `Removal`, then serialized
under `serviceLifecycleLock`. It authenticates the exact array occupant rather
than searching by MAC address. Retirement closes ordinary report admission,
retires the output, commits the existing neutral synthetic mapping state under
one terminal report lease, unsubscribes the exact retained handlers, boundedly
stops the typed worker, synchronously removes that exact device from
`DS4Devices`, and only then clears the exact array occupant. A stale generation
cannot clear or unsubscribe a newer occupant.

Service Stop closes this generation of the table before changing the running
state. If exact typed retirement is not proven, Stop records a retry-pending
phase and returns false without clearing the retained binding. A later Stop
resumes only exact controller retirement; it does not replay the one-shot
OpenRGB, timer, notification, or pre-service-stop actions. Start is rejected
while this exact cleanup retry remains pending.

Event accessor mutation and cleanup share a per-binding mutation gate. A
delegate is retained as possibly installed before an add accessor runs. If an
accessor installs and then throws, cleanup keeps the exact inverse; a
concurrent detach cannot pass the in-flight add. Motion replacement never
overwrites an old retained inverse until its removal is proven.

## Pre-integration source audit

The baseline legacy path had one hardware owner, but no generation-bound slot
authority:

1. `ControlService.Start` runs under `serviceLifecycleLock`, asks
   `DS4Devices` to discover HID devices, prepares each device, writes the next
   independently chosen `DS4Controllers[i]` slot, and then performs profile,
   output, callback, and transport setup.
2. `ControlService.HotPlug` asks `DS4Devices` to discover again, rejects an
   apparent duplicate by MAC-address equality, scans `DS4Controllers` for its
   own first empty slot, writes that slot, and performs the same setup. The
   method is not itself protected by `serviceLifecycleLock`; only discovery is
   dispatched.
3. `PrepareConnectedInputControllerSettingEvents` combines reusable profile,
   mapping, touch, and virtual-output setup with legacy-only ownership. It adds
   `ControlService` and `DS4Devices` removal callbacks, adds an anonymous
   `Report` callback, and finally calls `DS4Device.StartUpdate`. The anonymous
   callback cannot later be removed by exact delegate identity.
4. `On_DS4Removal` runs from the device lifecycle, finds a slot by MAC-address
   equality rather than device reference plus generation, retires output and
   mapping state, commits neutral state, and clears the array. The separately
   registered `DS4Devices.On_Removal` callback closes and removes the HID path.
5. Service stop runs under `serviceLifecycleLock`, calls the existing
   connection-specific stop/disconnect operation, may call
   `DS4Devices.RemoveDevice`, tears down virtual output, and clears the array.
   It later calls `DS4Devices.stopControllers` as a registry-wide fallback.

`DS4Device.StartUpdate` is a `void` method which starts output and input
threads sequentially. A failure after the first thread starts has no typed
prepare or abort proof. `DS4Device.StopUpdate` is also `void`; it performs
unbounded thread joins and catches dependency exceptions. Therefore no new
adapter can truthfully convert either call into the bounded, synchronous proof
required by `InputControllerRegistrationTable` without a small change at the
existing lifecycle owner.

The table allocates the first `Empty` or `Removed` slot. Until every attached
legacy lifetime is represented in that same table, a Switch 2 runtime attach
can select a slot which is already occupied only in `DS4Controllers`.
Checking the array after runtime reservation cannot repair this race: the
table can repeatedly choose the invisible legacy slot, and the array scan can
concurrently choose a slot reserved by the table.

## Ownership foundation

`LegacyHidInputControllerRegistrationOwner` creates an owner-authenticated
`LegacyHid` registration from a lifetime lease which the existing lifecycle
host issued before registration. The lease binds the host's private issuer,
one already-discovered `DS4Device` reference, and one nonzero, non-reused
connection generation. A MAC address, path, slot number, copied generation, or
different device reference cannot recreate the credential. Host
authentication is pure because the host has already retained that exact
lifetime; it must not trust or adopt the first lease presented to it.

The owner delegates lifecycle proof through
`ILegacyHidInputControllerLifecycleHost`, implemented by the integrated
authority at the existing `ControlService`/`DS4Devices` owner; it is not a
second implementation of HID lifecycle. Authentication is pure. Stop and
remove are invoked outside the owner's gate, must authenticate the exact lease,
and must return operation-matched proof. Stop must be proven before remove.
Operation-specific `StopRejected`/`RemoveRejected` proof permits an ordinary
owner retry. Lost credential, stale generation, invalid host state, timeout,
malformed/wrong-operation proof, dependency exception, or otherwise uncertain
cleanup quarantines the owner. The authority's separate quarantine-recovery
path can then use only its retained exact worker lease and registry record; it
does not weaken or reset the quarantined owner state.

Registration creation and table reservation still perform no HID I/O. The
ControlService integration is the sole caller which installs delegates and
commits the typed worker activation.

## Remaining production boundary

The integrated authority deliberately covers only the subtypes for which the
typed worker audit proves the complete worker lifetime: exact base
`DS4Device` and `DS3Device`. `DualSenseDevice` has several composite workers;
`SwitchProDevice` and `JoyConDevice` perform operational HID setup before input
publication; unknown subtypes are unaudited. They continue through the
unchanged ordinary lifecycle and are not represented in this table. Therefore
the current table is not yet a truthful universal HID/runtime slot authority,
and no production Switch 2 runtime discovery may use it to claim an arbitrary
ControlService slot.

`ISwitch2ControlServiceSlotHost` also requires exact inverse records for the
shared profile/mapping staging. Today `TouchPadOn` and
`SetupInitialHookEvents` install touch, six-axis, trigger, smoothing, and
global profile handlers which are not all retained and do not have a complete
inverse operation. Host `Abort` and `Remove` promise that the exact staged slot
is gone. Implementing that interface now would make that promise false.

The next safe tranche is to make shared staging return a typed inverse record,
including touch and every profile hook, and to give each unsupported HID
subtype a complete typed lifecycle owner. Only then can all physical and
Switch 2 runtime occupants share this table and can `ControlService` implement
the existing Switch 2 host interface without weakening abort/remove semantics.

## Deterministic coverage

`ControlServiceLegacyHidSlotAuthorityTests` covers:

- exact attach, report admission, terminal neutral, bounded stop, registry
  removal, and exact delegate detachment;
- service/connection generation replay rejection against a newer occupant;
- terminal publication blocked by a concurrent report lease and succeeding
  only after exact drain;
- clean and partial worker-start failures quarantining without fallback;
- bounded cleanup of a retained partial-start worker lease while leaving the
  table slot quarantined;
- retryable service retirement after a proven-rejected worker Stop;
- direct exact-lease recovery after an outcome-uncertain Stop quarantines the
  registration owner;
- recovery after the owner reached Removed but table completion failed,
  without replaying either worker Stop or registry removal;
- production exact-type rejection before table or event mutation;
- an event accessor which installs and then throws, retaining the exact inverse
  until cleanup; and
- concurrent detach waiting for an in-flight subscription mutation.
