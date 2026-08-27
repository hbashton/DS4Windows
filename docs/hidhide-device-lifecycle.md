# HidHide device lifecycle

DS4Windows treats a HidHide rule as ownership of a specific PnP generation,
not ownership of a controller name or of an entire USB container.

## Identity

- The exact HID instance ID resolved while the controller interface is live is
  always included.
- Container expansion follows HidHide Configuration Client semantics. HID
  siblings in the same non-system Container ID may be included. The base node
  is included only when it is HID/XUSB class and every immediate child is a
  positively identified HID in that same container.
- A mixed USB controller/audio base is therefore never blacklisted. A missing,
  unreadable, foreign-container, or non-HID child also makes the base
  ineligible.
- VIIPER outputs use different containers and are explicitly removed from the
  persistent blacklist after creation.

## Ownership and reconnect

Released HidHide 1.5 builds do not provide the process-lifetime session IOCTL,
so DS4Windows may have to add a persistent fallback rule. Only instance IDs
that this process actually inserted are removable ownership. A matching rule
that was already present protects the controller but remains user-owned.

The live identity set is captured before PnP removal. On disconnect,
DS4Windows removes only its persistent additions after the virtual output has
retired. Session entries are process-scoped and are cleared only for a full
service stop, never for one controller. Overlapping old/new generations are
reference-counted; if a reconnect arrives during a persistent removal write,
the rule is reasserted before the driver mutation boundary is released.

Stop closes a service-lifecycle generation before controller teardown. The
process-owned snapshot, binding invalidation, and process-wide session clear
then run under the same driver-mutation boundary. A HotPlug operation that
entered before Stop cannot add a rule afterward, and a later Start opens a new
generation only after cleanup completes.

This cleanup allows a wired controller whose instance ID changes from A to B
to enumerate unblocked, after which B is hidden as the new generation. It also
prevents a user-created HidHide entry from being adopted and deleted by
DS4Windows.

Application-list inverse mode, active state, and blacklist reads are treated as
fallible driver queries. DS4Windows does not mutate whitelist policy, cache a
restore baseline, or erase its last verified affected-device snapshot unless
those queries succeed.

## Steam Input reclaim

Automatic Steam Input reclaim never invokes `pnputil /restart-device` for an
already active wired controller. Restarting the HID collection after the input
reader owns it can tear down the current generation, race the path/serial
registries, and return as a generic HID collection. A physical reconnect or
Steam restart is required when Steam held a wired controller before HidHide was
enabled. The existing Bluetooth reclaim path is unchanged.
