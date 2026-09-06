# Switch 2 Bluetooth HD-rumble envelope

Status: **production-composed and offline-test verified**. The pure codec is
used by one generation-scoped physical writer per controller lifetime. The
Windows BLE lease selects the model-specific characteristic, writes without
response only when the characteristic advertises that property, commits the
modulo-16 counter only after a completed write, and retains the identical
payload after an uncertain outcome. No physical-delivery, universal cadence,
thermal, or perceptual-fidelity claim is made until the hardware matrix runs.

## Pinned evidence

Two locally pinned implementations independently agree on the BLE vibration
characteristic UUIDs and payload shape:

- Switch2Connect at
  `4487322a306f04efa27682e3f3a508635a84fd98`,
  `src/controller.py`: `VIBRATION_WRITE_*_UUID`, `VibrationData.get_bytes`,
  and `set_vibration` build `0x00 + group` for a Joy-Con and
  `0x00 + left-group + right-group` for a Pro Controller, then use a GATT
  write without response.
- hifihedgehog/SDL at
  `d98c5804a9d20b0d96e993741797878c86b8f1e1`,
  `src/joystick/windows/SDL_ble_switch2joystick.c`:
  `BLE_EncodeVibration` and `BLE_WriteRumble` build the same 17-byte and
  33-byte payloads and select the same per-model UUIDs. The original M2/M3
  commit is `525a23d96b8d56b54205d6ec9da715ca9a43b916`.

The common 16-byte group and five-byte packed subframe are already shared
with the independently tested USB codec. The BLE codec is an envelope over
those canonical types; it is not another haptic mapper.

## Implemented boundary

`Switch2BluetoothHdRumbleCodec` provides strict allocation-free encode/decode
for:

- Joy-Con: one `0x00` envelope byte and one 16-byte group;
- Pro Controller: one `0x00` envelope byte followed by independent left and
  right 16-byte groups; and
- one modulo-16 counter shared by both Pro groups.

It rejects non-exact lengths, a nonzero envelope byte, malformed group
headers, and mismatched Pro counters. The surrounding production composition
chooses the Pro/Joy-Con vibration characteristic, requires evidenced
write-without-response support, owns bounded retry identity, and requires
terminal neutral before transport retirement. Watchdog, thermal policy, and a
universally safe keep-alive interval remain hardware gates.

## Offline validation

`Switch2HdRumbleCodecTests` covers byte identity between the BLE group region
and the existing USB group region, independent Pro sides, one-group Joy-Con
round-trip, every envelope mutation, and zero managed allocations on a warmed
10,000-iteration encode/decode path.

No statement in the reference repositories establishes a safe universal BLE
keep-alive rate. In particular, SDL's 10 ms pump and Switch2Connect's transport-
specific pacing are implementation choices, not a protocol guarantee. The
implemented writer owns the exact GATT connection lifetime, bounds each write,
and prioritizes terminal neutral; the connected adapter/controller pair still
must be measured before any cadence claim.
