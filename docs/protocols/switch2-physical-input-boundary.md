# Switch 2 physical input admission boundary

Status: implemented and offline-test verified; not wired to OS discovery and
not hardware-runtime verified.

## Outcome and scope

`Switch2PhysicalInputBoundary.cs` is the first production-facing boundary
between transport-specific discovery/read workers and the existing canonical
Switch 2 parser/session. It performs no enumeration, driver change, handle
open, input read, command, initialization, association, memory access, output,
logging, sleep, or state publication.

The current admission factory recognizes only the project-evidenced Switch 2
Pro USB composite revision. It does not add PID `0x2069` to
`DS4Devices.knownDevices`, append `InputDeviceType`, construct a `DS4Device`, or
route the device into profiles. Those steps remain blocked on a transport-
neutral runtime controller adapter; the existing `DS4Device`, `DS4Devices`, and
`InputDeviceFactory` ownership model assumes one `HidDevice` supplies identity,
input, output, removal, and close. Pretending the vendor command interface or a
future GATT connection is that `HidDevice` would preserve the wrong ownership
model.

## Exact USB admission

Admission requires all of the following in one side-effect-free composite
observation:

| Layer | Required observation |
| --- | --- |
| device | Nintendo `057E:2069`, `bcdDevice 0201` |
| ownership | one nonempty Windows container identity; root, input child, and command child identities all equal |
| ambiguity | exactly one matching input interface and exactly one matching command interface; there is no first-match fallback |
| input | inbox HID-class relationship, interface `0`, alternate `0`, Generic Desktop / Game Pad usage `0001:0005`, 64-byte input, 64-byte output, no feature report |
| command | WinUSB relationship, interface `1`, alternate `0`, exactly two pipes |
| command pipes | bulk OUT `0x02` and bulk IN `0x82`, either enumeration order, each 64-byte maximum packet and interval zero |

VID/PID is therefore necessary but never sufficient. A discovery
implementation must walk the Windows device tree/container relationship and
count all matching children before constructing the observation. It must not
search all `057E:2069` interfaces and pick the first command path. The admitted
container identity becomes an opaque equality/hash token: its underlying GUID
is not exposed, formatted, or serialized by the registration.

The 64-byte physical bulk maximum is deliberately distinct from the 512-byte
maximum in a high-speed virtual USB descriptor. Using the virtual descriptor's
value for this physical controller was falsified by the project-owned WinUSB
topology observation.

## Runtime-facing lifetime seam

`Switch2PhysicalInputRegistration` carries only the admitted protocol identity,
opaque controller identity, interface roles, and report length. It contains no
path or live object. `Switch2PhysicalInputLifetime` adds caller-owned nonzero
device and transport generations plus the QPC frequency.

`Switch2PhysicalInputAdapter` accepts an exact lifetime, packet span, and QPC
read-completion timestamp and delegates to `Switch2InputSession`. A successful
frame retains all 63 body bytes, 12-bit sticks, unknown fields, raw controller
counter, host timestamp, calibration provenance, and both generations. Exact
64-byte framing and report ID `0x05` remain mandatory.

The hot path is single-writer and allocation-free after warmup. A transport
worker must cancel and quiesce its pending read before resetting the adapter or
disposing any transport handle. The adapter owns no disposable resource, so it
cannot race a handle close. After a reset, an old completion still carrying the
retired lifetime is rejected before parser state changes. Replacing a container
requires a new registration/adapter; a reconnect of the same admitted
container must advance the appropriate generation.

The adapter's API is transport-API-neutral, but the only registration factory
implemented in this tranche is the exact USB Pro gate. BLE registration awaits
an independently verified stable discovery identity and connection lifecycle;
it is not inferred from advertisement remembered-host bytes.

## Integration point and remaining gate

A later Windows discovery/worker tranche should sit beside the current HID-only
enumeration rather than enter `HidDevices.EnumerateDS4`:

```text
SetupAPI composite discovery
  -> exact Pro USB admission and interface count
  -> separate shared HID-input and owned WinUSB-command leases
  -> read-completion QPC timestamp
  -> Switch2PhysicalInputAdapter
  -> Switch2CanonicalInputFrame
  -> transport-neutral runtime controller/profile adapter (not implemented)
```

That runtime adapter must provide the lifecycle/removal surface currently
expected of `DS4Device` without manufacturing a fake `HidDevice`. Only after
every `InputDeviceType` consumer, UI capability switch, serializer/migration,
auto-profile rule, mapping vocabulary, and stop/removal path has an explicit
Switch 2 policy should new enum values be appended and actual registration be
enabled.

Blocked work includes SetupAPI enumeration, duplicate-node diagnostics, handle
leases and cancellation, unplug/rebind/suspend behavior, HID read worker,
WinUSB transaction worker, initialization/feature selection, calibration
reads, runtime mapping, BLE GATT, Joy-Con 2, output, and neutral release. This
boundary does not make any of those features available.

## Provenance and clean-room treatment

- Project-owned hardware evidence on 2026-08-29 directly observed a physical
  Pro Controller 2 as `057E:2069`, `bcdDevice 0201`, HID MI_00 with Generic
  Desktop / Game Pad usage and 64-byte IN/OUT/no feature report, plus WinUSB
  MI_01 with bulk `0x02` OUT and `0x82` IN, 64-byte maximum packets, interval
  zero. Device paths, instance IDs, container IDs, serials, and raw reports are
  neither embedded nor hashed in this module or its tests.
- Upstream SDL commit
  [`c71abd08605b8bb7078372307a93274725c99fe0`](https://github.com/libsdl-org/SDL/tree/c71abd08605b8bb7078372307a93274725c99fe0),
  zlib license, `src/joystick/hidapi/SDL_hidapi_switch2.c`, symbols
  `FindBulkEndpoints` and `HIDAPI_DriverSwitch2_InitUSB`, corroborates interface
  1 with one bulk direction each. Its auto-detach, claim, flash reads, and
  initialization sequence are not copied or executed here.
- Switch2Connect commit
  [`4487322a306f04efa27682e3f3a508635a84fd98`](https://github.com/TommyWabg/Switch2Connect/tree/4487322a306f04efa27682e3f3a508635a84fd98),
  GPL-3.0-or-later authored source,
  `src/usb_hid_controller.py:resolve_command_transport`, is behavioral/design
  evidence that MI_00 and MI_01 must be related through their composite parent.
  That revision can fall back to unbound/global matching when resolution fails;
  this boundary deliberately rejects missing, duplicate, or cross-container
  interfaces instead. No Python source or architecture was copied.
- The unlicensed `ndeadly/switch2_controller_research` revision and other
  protocol-fact sources remain facts-only as recorded in
  `switch2-phase1-codec.md`; none supplied this boundary's C# code.
- DS4Windows is GPL version 3. This code is an independent implementation built
  from project-owned observations, descriptor facts, and the licensed SDL
  corroboration above. PadForge's CC BY-NC-SA source supplied no code.

## Offline verification

The focused suite exhausts every byte or ushort value for all numeric admission
fields. Exactly one value is admitted for each identity, interface, HID usage,
report-length, endpoint-count, address, transfer-type, maximum-packet, and
interval field. It also checks both pipe enumeration orders, missing/duplicate
interface counts, cross-container binding, all input lengths `0..128`, all 256
report IDs, generation/reset behavior, stale observations, retained frame
ownership, absence of `HidDevice` from the boundary type surface, and zero
managed allocations after warmup.

```text
dotnet test DS4WindowsTests\DS4WindowsTests.csproj -c Release \
  -p:Platform=x64 --no-restore \
  --filter "FullyQualifiedName~Switch2PhysicalInputBoundaryTests"

Passed: 11, Failed: 0, Skipped: 0
```

This is replay/simulation verification only. No controller was contacted by
these tests, and there is no physical-to-game latency result.
