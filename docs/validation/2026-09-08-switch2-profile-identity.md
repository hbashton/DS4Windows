# Switch 2 local identity and profile linking, 2026-09-08

## Confirmed source defect

Switch 2 runtimes intentionally use the no-HID `DS4Device` constructor. It leaves `MacAddress` at `00:00:00:00:00:00` and does not grant legacy HID persistent-identity authority. That is not a decoded Nintendo serial or a failed read of a MAC. The controller list/overview and Link Profile/ID still used that legacy field. Consequently, the controllers looked identical and normal linked-profile lookup could not select a per-controller link.

The existing trusted transport boundary already derives an opaque 128-bit `Switch2PersistentPeerId`, used for calibration, holding-style persistence, and remembered Joy-Con pairing:

- BLE: `Switch2PersistentPeerIdentityDeriver` consumes the Windows device's stable association `DeviceId` through the existing restricted copy API.
- USB Pro: the same deriver consumes the admitted physical Windows `ContainerId`.
- The existing install key is persisted/protected by `Switch2JoyConPairFileStore`. Neither that key nor a Bluetooth pairing key, raw device path, or Bluetooth address is exposed by this change.

## Bounded implementation

`ProfileLinkId` is now separate from `MacAddress`. Legacy devices keep their valid MAC as the profile key. Switch 2 binds its existing peer identity once, while its runtime is still Created and before slot/profile publication. All four production composition paths bind it: USB Pro, BLE Pro, standalone Joy-Con, and joined Joy-Cons.

The human-readable namespaces are `S2-USB-PRO`, `S2-BT-PRO`, `S2-BT-L`, `S2-BT-R`, and `S2-BT-PAIR`. Single-controller keys retain the complete 128-bit peer ID. Joined keys retain both complete peer IDs in left/right order; they do not use transient runtime generations or newly generated pair IDs. Replacing a half changes the pair identity; recreating the same pair with new runtime/pair epochs does not.

Both startup profile-selection paths and UI add/update/remove operations use the full key. Existing LinkedProfiles XML serialization accepts and retains the namespaced key without a format migration. Existing MAC-based records are preserved. No old blank-key link is guessed to belong to a particular controller.

The controller list, overview, and options list display the new local ID. Existing trimming is retained, with full-ID tooltips, rather than shortening the persistence key. The list's latency tooltip remains a separate latency value. No existing Copy ID command was found; the IPC `macaddress` property and DSU/HID MAC semantics are unchanged.

Missing identity remains unavailable and disables Link Profile/ID; it does not create a random or shared placeholder identity. Legacy HID ownership flags and registration identity authority remain unchanged.

The existing legacy MAC-change event also refreshes link availability, so a legacy device that obtains a valid serial after its row was created is not left with a disabled checkbox.

## Related options-dialog failure

`ControllerRegDeviceOptsViewModel` constructed an unused dictionary keyed by MAC, throwing on a second zero-MAC Switch 2. The unused dictionary was removed. Transport-owned runtimes also have no legacy `optionsStore`; selecting them now uses the existing empty options tab and clears any previous legacy data context. Invalid/cleared selection is safe. Legacy config saving skips absent stores, while retaining the same save dispatch for actual legacy stores. No unsupported native options store is manufactured.

## Stability limits

These are local Windows/installation identities, not claimed Nintendo factory serials or actual MAC addresses. Stability depends on retaining the existing install key and Windows association/container identity. A different configuration identity store, re-created Windows device identity, or re-created container can produce a different ID. The change does not establish that a USB container ID stays constant across every port/re-enumeration scenario.

USB and BLE remain explicitly distinct: the current evidence does not prove a safe cross-transport hardware-identity match. Joining a pair preserves the existing virtual-output/profile handoff policy; the change does not override the active profile during a pad-preserving handoff.

During that handoff, the successor's Link Profile checkbox is recomputed from its own full ID and the exact retained profile. An absent link or a link to another profile is unchecked; an already matching link is checked. The predecessor's checkbox cannot implicitly create or overwrite a standalone/pair link. No link is automatically migrated and the retained profile/output is not switched.

## Validation scope

`Switch2ProfileIdentityTests` covers stable full identities across runtime generations, model/transport/peer separation, pair replacement/recreation, invalid and post-publication binding rejection, unchanged real-MAC/legacy authority, in-memory full-key XML round-trip and independent add/update/remove operations, display/link availability, mixed legacy/Switch 2 options selection, and actual filtered legacy-save dispatch through a callback. Source-bound checks cover all production identity binding paths and both startup profile lookups.

Initial validation: **12/12 identity cases passed** in `switch2-rumble-identity-first-green.trx`. The broader identity, peer-store, pairing/transition, and runtime filter passed **63/63**, with no failures/skips, in `switch2-profile-identity-regressions.trx`. These used Release/x64; the broader run reused the successful build with `--no-build --no-restore`. An earlier test compile needed the `DS4WinWPF` namespace imported; a no-build run against the older DLL had no matching identity tests and is not counted as validation.

Final identity validation after the retained-output checkbox and legacy notification regressions: **22 identity cases passed**, included in **73/73** broader passes (zero failures/skips), `switch2-profile-identity-final-regressions.trx`, Release/x64 `--no-build --no-restore`. A second agent independently reviewed the identity and handoff delta and found no material blocker.

No user association store or profile was edited, no physical controller commands were sent, and no running app was replaced. Existing file-store tests use isolated temporary test stores, not user associations. Physical reconnect/UI acceptance is separate from these offline checks.
