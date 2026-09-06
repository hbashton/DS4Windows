# Switch 2 ControlService slot boundary (dormant)

Status: offline foundation only. This boundary is not constructed by
`ControlService`, does not discover a controller, does not touch hardware, and
does not make Switch 2 input available in a production build.

## Audited legacy lifecycle

The existing legacy path has one internally consistent HID lifecycle, but it
is not yet a safe runtime-transport lifecycle:

1. Startup and hot-plug discover through `DS4Devices`, independently scan
   `ControlService.DS4Controllers`, write the selected array element, and then
   call `PrepareConnectedInputControllerSettingEvents`.
2. That preparation routine loads controller and profile settings, creates the
   touch/mapping state, may create the virtual output, installs removal/sync/
   serial events, adds an anonymous `DS4Device.Report` handler, and finally
   calls `DS4Device.StartUpdate`.
3. `On_DS4Removal` finds a slot by MAC-address equality rather than an exact
   object/generation token, tears down output and mapping state, and clears the
   array element. `DS4Devices.On_Removal` separately removes the HID device and
   dereferences `DS4Device.HidDevice`.
4. Service stop calls `StopUpdate`, `DS4Devices.RemoveDevice`, and clears the
   slot arrays as part of the legacy HID loop.

A `Switch2RuntimeInputDevice` deliberately has no HID interface. Sending it
through `DS4Devices`, HidHide, the legacy removal callback, or the legacy
`StartUpdate` ownership path would violate its registration contract.

## Audited table/core timing

`Switch2RuntimeRegistrationTransactionCore.TryAttachExactSlot` provides the
required pre-commit timing without changing the established lifecycle order:

1. `InputControllerRegistrationTable.TryReserveAndBindExactSlot` validates the
   externally selected slot before owner inspection and either binds that
   exact slot or fails without choosing a fallback. It issues the exact slot
   token only after the service table owns the slot.
2. The participant adopts that token and the core retains the binding.
3. The participant installs exactly one report callback.
4. The participant prepares activation.
5. Only after table activation admission does the participant receive the
   one-shot commit credential and release its transport worker.

`Switch2ControlServiceSlotRegistrationParticipant` decorates step 4. Its host
must atomically stage the exact `Token.Slot`, device reference, registration
generation, profile state, mapping state, and virtual-output policy before the
inner transport can prepare or commit. The decorator itself does not subscribe
to `DS4Device.Report`; the wrapped transport participant remains the sole
subscriber and invokes the decorator's one `MappingCallback` under the core's
existing report lease.

The admitted regular-report decorator path uses its construction-time mapping
delegate and direct synchronous host call; it creates no per-report managed
allocation and introduces no queue or cadence source.

`Switch2RuntimeRegistrationService` now exposes typed exact-slot overloads for
USB Pro, Bluetooth Pro/individual Joy-Con, and an explicitly joined Joy-Con
owner which accept one `ISwitch2ControlServiceSlotHost`. A per-attachment relay
is allocated only on the control path. The transaction core constructs the
decorated participant only after exact-slot binding, publishes it before any
transport commit can emit a report, and uses one stable method-group callback
for synchronous dispatch. An invalid or occupied exact slot does not construct
the participant, invoke the host, or fall back to another slot. Offline tests
exercise three mixed owners through one host and prove exact prepare, terminal
neutral dispatch, removal, and zero retained host slots after close.

On pre-commit failure, inner abort proof is required before the exact host slot
is aborted. On active retirement, the terminal-neutral report must be accepted
by the host before stop can succeed; inner removal proof must precede final host
slot removal. A malformed result, exception, slot/sender/generation mismatch,
reentrant lifecycle call, terminal rejection, or uncertain cleanup prevents
slot reuse and drives the table toward quarantine.

All participant and host calls run outside the decorator, transaction-core,
and table gates.

## Production wiring blockers

The service relay is intentionally not constructed by `ControlService`
because the existing owner still cannot safely bridge its legacy array and the
registration table. Production activation requires these remaining changes at
the existing lifecycle owner:

1. **One slot authority.** `ControlService` must own the service-generation
   `InputControllerRegistrationTable`, and legacy HID attachment/removal must
   reserve and retire through that same table before mutating
   `DS4Controllers`. The new exact-slot operation prevents fallback, but it
   cannot see an independently mutated legacy-only array slot; both paths must
   share the same service lifecycle serialization and table.
2. **Atomic exact runtime staging.** Under `serviceLifecycleLock` and the
   service dispatcher, add an internal token/generation-aware host method that
   verifies the same service epoch, exact table token, exact device reference,
   and empty array slot, then stages that slot. It must retain an exact cleanup
   record and reject a stale/copy/foreign token or a newer occupant.
3. **Split legacy setup.** Separate the reusable profile/mapping/slot/output
   preparation from the legacy-only event and transport ownership in
   `PrepareConnectedInputControllerSettingEvents`. The runtime staging method
   must not add another `Report` handler, call `StartUpdate`, subscribe
   `DS4Devices.On_Removal`, perform HidHide work, or create a second mapper.
4. **Exact dispatch and teardown.** Add one internal synchronous dispatch
   method that validates the retained token/device/generation and enters the
   existing `On_Report` mapping path for that slot. Add exact abort and removal
   methods that undo only that retained lifetime. MAC-address lookup is not
   sufficient evidence. Terminal neutral must pass through the same mapping
   method before output/profile/touch state is removed.
5. **Service close ordering.** Close the runtime registration service and prove
   terminal/removal completion before clearing shared slot state. A cleanup
   timeout or exception must retain/quarantine the exact slot rather than let a
   later generation reuse it.

No further change to the transaction core is required for host hook timing.
The remaining changes belong at `ControlService`'s existing slot authority and
in legacy registration-table adoption. Until those changes are reviewed and
implemented together, the runtime service relay and decorator must stay
dormant.

The next audited subset is documented in
[`switch2-controlservice-reversible-profile-staging.md`](switch2-controlservice-reversible-profile-staging.md).
It adds exact same-table slot-array/slot-manager staging and retained inverse
records, but deliberately leaves production construction absent because the
required profile/touch/output inverse facet is still unimplemented.
