# Switch 2 Phase 1 codec and replay boundary

Status: read-only protocol foundation. Nothing in this module discovers,
connects, registers, initializes, pairs, reads from, writes to, or publishes
runtime controller state.

## Scope

`DS4Windows/DS4Control/Switch2` contains:

- strict USB framing: one report-ID byte plus a 63-byte body;
- strict BLE framing: the exact primary-service UUID, characteristic UUID,
  required `Read | Notify` properties, and one 63-byte body;
- raw decoders for common report `0x05` and the evidenced basic fields of
  model-specific reports `0x07`, `0x08`, and `0x09`;
- a body-relative descriptor and zero-copy slice helper for model-specific
  motion bytes, whose format remains opaque;
- privacy-safe advertisement classification as no remembered host, this host,
  or a foreign host, without returning the advertised address;
- a nine-byte factory stick-calibration parser with model-aware left/right
  address metadata and explicit rejection of sentinel calibration records;
- immutable, versioned, input-only fixture envelopes and deterministic replay;
- raw modular counter deltas plus first/forward/duplicate/backward-or-out-of-
  order classification; and
- a pure Joy-Con half-pair skew evaluator requiring one host clock domain,
  frequency, and explicit coordinated pair epoch.

The decoder returns raw values. It does not apply calibration, dead zones,
axis transforms, sensor scaling, fusion, mappings, or virtual-pad publication.
The dictionary-based replay engine is test/offline infrastructure and is not a
live hot-path design.

## Byte-level input specification

All offsets below are relative to the 63-byte body. For USB, add one to obtain
the offset in the complete 64-byte packet. BLE notifications omit the report
ID and already consist of the body.

### Common report `0x05`

| Body offset | Size | Phase 1 interpretation | Applicability/status |
| --- | ---: | --- | --- |
| `0x00` | 4 | little-endian raw counter | all supported models |
| `0x04` | 4 | little-endian button bits | all supported models |
| `0x08` | 2 | uninterpreted | unknown |
| `0x0A` | 3 | packed 12-bit left-stick slot | left Joy-Con 2 and Pro; absent-side Joy-Con bytes may be garbage |
| `0x0D` | 3 | packed 12-bit right-stick slot | right Joy-Con 2 and Pro; absent-side Joy-Con bytes may be garbage |
| `0x10` | 8 | four raw little-endian mouse words | Joy-Con 2 only; not exposed as a Pro control |
| `0x18` | 1 | uninterpreted | observed zero in public research |
| `0x19` | 6 | three raw signed magnetometer words | feature-dependent |
| `0x1F` | 2 | little-endian battery millivolts | raw |
| `0x21` | 1 | charging state/rate | raw; scale not promoted |
| `0x22` | 2 | possible battery-current bits | raw; meaning remains uncertain |
| `0x24` | 6 | uninterpreted | includes the observed `0x29 == 0x01` byte |
| `0x2A` | 4 | little-endian motion timestamp | units unknown |
| `0x2E` | 2 | temperature raw bits (`ushort`) | signedness and scale unknown |
| `0x30` | 6 | three raw signed accelerometer words | scale/orientation unknown |
| `0x36` | 6 | three raw signed gyroscope words | scale/orientation unknown |
| `0x3C` | 2 | uninterpreted for supported models | analog triggers only on unsupported NSO GameCube controller |
| `0x3E` | 1 | uninterpreted/reserved | unknown |

The `HasLeftStick`, `HasRightStick`, and `HasMouseData` properties are the
model-applicability boundary. Raw absent/inapplicable slots remain available
for forensic comparison but must not be published as controls.

### Model-specific reports

| Report/model | Basic fields | Opaque motion descriptor |
| --- | --- | --- |
| `0x07`, left Joy-Con 2 | counter `0x00` (8-bit), power `0x01`, buttons `0x02..03`, packed stick `0x05..07` | declared length `0x0F`, body region `0x10..37` (40-byte capacity) |
| `0x08`, right Joy-Con 2 | counter `0x00` (8-bit), power `0x01`, buttons `0x02..03`, packed stick `0x05..07` | declared length `0x0F`, body region `0x10..37` (40-byte capacity) |
| `0x09`, Pro Controller 2 | counter `0x00` (8-bit), power `0x01`, buttons `0x02..04`, sticks `0x05..0A` | declared length `0x0E`, body region `0x0F..36` (40-byte capacity) |

Declared motion length above 40 is rejected. Lengths 0, 30, and 40 are
marked publicly observed; other bounded lengths stay opaque. The decoded
struct owns only the body-relative descriptor. Motion bytes remain owned by
the caller or immutable fixture, and `TrySliceOpaqueMotionBody` returns the
declared span after validating the exact body length.

## Evidence and license ledger

### Licensed implementation reference

SDL was reviewed at main commit
[`c71abd08605b8bb7078372307a93274725c99fe0`](https://github.com/libsdl-org/SDL/tree/c71abd08605b8bb7078372307a93274725c99fe0)
and stable `release-3.4.14` commit
[`147a8ee32dbf9ac02f3794964490687b6bbda1bc`](https://github.com/libsdl-org/SDL/tree/147a8ee32dbf9ac02f3794964490687b6bbda1bc).
SDL uses the zlib license. The commits contain the same
`SDL_hidapi_switch2.c`. Facts corroborated there include 64-byte USB input,
common button/stick offsets, the six-value packed-12 calibration layout, and
factory left/right stick addresses `0x130A8`/`0x130E8`. No SDL source text was
copied; the C# implementation is independent.

### Public research used as facts only

The following pinned revisions contain no `LICENSE`, `COPYING`, or `NOTICE`.
Their source, prose, private-key material, and encrypted/decrypted captures
were not copied or redistributed:

- [`ndeadly/switch2_controller_research@d1c5a7f7`](https://github.com/ndeadly/switch2_controller_research/tree/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92)
- [`Nadeflore/switch2-controllers@e79720b`](https://github.com/Nadeflore/switch2-controllers/tree/e79720b6a3042f710b3a1c7470dc9575c42e560a)

Only independently expressible facts were used: UUIDs/properties, exact
lengths and offsets, advertisement layout and reversed remembered-host field,
known product IDs, observed motion lengths, and calibration addresses.

Additional implementations were inspected but not sourced:

- [`TommyWabg/Switch2Connect@4487322`](https://github.com/TommyWabg/Switch2Connect/tree/4487322a306f04efa27682e3f3a508635a84fd98), GPLv3-or-later in authored source;
- [`TheFrano/joycon2py@9d80b76`](https://github.com/TheFrano/joycon2py/tree/9d80b764682a171580fd134b96f00866df6ae398), MIT;
- [`TheFrano/joycon2cpp@1db6999`](https://github.com/TheFrano/joycon2cpp/tree/1db6999a17a36e24e9d5d09f9fafb4fb0adce6f1), MIT; and
- [`hifihedgehog/SDL@d98c580`](https://github.com/hifihedgehog/SDL/tree/d98c5804a9d20b0d96e993741797878c86b8f1e1), not treated as protocol authority.

### Project-owned hardware evidence

On 2026-08-29, a physical Switch 2 Pro Controller enumerated read-only as USB
`057E:2069`, `bcdDevice 0x0201`: HID MI_00 (`HidUsb`, usage page 1/usage 5,
64-byte IN/OUT and no feature report), WinUSB MI_01, and USB audio MI_02. No
write, serial query, association, or memory command was performed.

Two passive USB HID captures were kept outside Git and decoded with the exact
compiled strict codec:

- 2,048 records, source SHA-256
  `FF00B4A066CE00F4D8BBC11B1E1F4979EAB794B372ADAFD63CC3CF0F0D5D7CBC`:
  all were exact 64-byte report `0x05` and all decoded. Raw counter deltas were
  `+4` 2,043 times, `+3` twice, `+5` once, and `+556` once; the `+556` followed
  a 0.683-second host-capture gap.
- 4,096 records, source SHA-256
  `6731B888836A955AD5722B4C37DD89E8D830EBAC45641C19227778659B202829`:
  all were exact 64-byte report `0x05`, all decoded, and every raw counter delta
  was `+4`.

No wrap was observed in hardware; 8-bit and 32-bit wrap behavior is verified
with synthetic vectors. A raw `+4` is therefore classified as forward movement,
not four packets lost. Duplicate/backward classification uses a documented
half-range modular ordering policy, not a firmware guarantee.

The committed golden fixture contains only two adjacent records from the
second capture plus non-identifying enumeration facts. It omits the raw
capture's device path and derived path hash. Its counter, sticks, framing, and
`+4` replay classification are asserted. The full captures are not committed.

Coordinate note: an observed change at complete USB packet offset `0x3C` is
body offset `0x3B` after removing report ID `0x05`, i.e. the high byte of gyro
Z. The supported-model body bytes `0x3C/0x3D` are not exposed as triggers.

## Defensive policies versus unknowns

Defensive policies, not undocumented firmware claims:

- exact lengths only; never zero-pad a truncated report;
- exact service and characteristic identity plus required GATT properties;
- privacy-safe host classification compares the six advertised reversed bytes
  transiently with the selected radio address and returns only an enum;
- calibration decodes every exact nine-byte record but `IsUsable` rejects zero
  or saturated 12-bit components;
- stream, clock, synthetic-fact, and capture IDs require type-specific prefixes
  plus caller-generated 128-bit lowercase nonces; formatted MAC addresses and
  arbitrary provenance prose are rejected; firmware is `unknown` or a bounded
  numeric `fw-` version;
- project-owned hardware sources additionally require a source SHA-256 and
  nonzero redaction-manifest revision;
- timestamps are monotonic per declared host-clock domain, device generation
  is per stream, frequency is stable per clock domain, and pair epoch is
  independently caller-coordinated;
- model, transport, firmware, source, clock identity, and pair epoch cannot
  drift within one `(StreamId, Generation)`; and
- skew cannot compare different clocks, frequencies, or pair epochs.

Unknown and intentionally unimplemented:

- OS USB/HID, WinUSB, libusb, BLE scanning, GATT, and connection parameters;
- registration, runtime selection, mapping, and virtual-pad publication;
- Nintendo application-layer association, SMP interaction, LTK derivation,
  Windows key provisioning, wake, and automatic reconnect;
- every controller-memory read/write and initialization command;
- user-calibration address/marker/validation conflicts around `0x1FC040`,
  `0x1FC060`, and `0x1FC080`;
- packed motion decoding, timestamp units, scale, orientation, and fusion;
- HD-rumble encoding, scheduling, pacing, and stop behavior;
- firmware-dependent GATT changes and achievable Windows BLE interval; and
- Joy-Con ownership/merge policy beyond explicitly tagged offline replay.

Before a live adapter, obtain sanitized USB and BLE captures for every model,
common/model-specific modes, reconnect/wake states, more than one Windows
Bluetooth chipset, and both single and concurrent Joy-Con operation. Any
association, key, memory, or write experiment requires explicit user consent,
a console re-pair recovery procedure, and a separate secret-redaction review.
