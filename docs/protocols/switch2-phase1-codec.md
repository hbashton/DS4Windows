# Switch 2 Phase 1 codec and replay boundary

The subsequent exact USB/BLE Pro Common05 profile projection is documented in
[`switch2-pro-profile-input.md`](switch2-pro-profile-input.md). It preserves the
12-bit source observation across the existing mapping pipeline and keeps the C
button distinct from DualSense Mute.

Status: read-only protocol foundation plus a pure physical USB admission/input
lifetime boundary. Nothing in this module performs OS discovery, connects or
registers a runtime controller, pairs, reads from, writes to, or publishes
runtime controller state. The command codec can construct a closed set of
volatile startup requests, but it owns no transport and cannot send them.

That status describes the original Phase 1 tranche. Later documents own the
production USB/BLE composition. In particular, the production BLE adapter now
uses the calibration parser and precedence rules described below.

## Scope

`DS4Windows/DS4Control/Switch2` contains:

- strict USB framing: one report-ID byte plus a 63-byte body;
- strict BLE framing: the exact primary-service UUID, characteristic UUID,
  required `Read | Notify` properties, and one 63-byte body;
- raw decoders for common report `0x05` and the evidenced basic fields of
  model-specific reports `0x07`, `0x08`, and `0x09`;
- lossless common-report retention and re-encoding, including every opaque
  byte range, for deterministic replay/property tests;
- an exact-identity, transport-neutral input session and versioned canonical
  frame retaining the device/transport generations, completion QPC timestamp,
  12-bit sticks, raw counters, unknown fields, and an owned copy of every body
  byte; the bounded Pro USB replay projection validates through this same core;
- a body-relative descriptor and both zero-copy replay slice and owned-frame
  copy helpers for model-specific motion bytes, whose format remains opaque;
- privacy-safe advertisement classification as no remembered host, this host,
  or a foreign host, without returning the advertised address;
- a bounded, generation-fenced discovery candidate registry using per-scan
  keyed peer tokens, documented in
  [`switch2-bluetooth-discovery-boundary.md`](switch2-bluetooth-discovery-boundary.md);
- nine-byte factory and marked eleven-byte user stick-calibration parsers with
  model-aware primary/secondary storage metadata plus a separate fail-closed
  adoption gate for sentinel and out-of-domain calibration records;
- immutable, versioned, input-only fixture envelopes and deterministic replay;
- raw modular counter deltas plus first/forward/duplicate/backward-or-out-of-
  order classification; and
- a pure, value-owned Joy-Con pair reducer with explicit pair epoch, one clock
  domain, bounded skew, stale-half reporting, generation-fenced loss/replacement,
  and terminal split behavior; and
- an exact, side-effect-free Pro USB composite admission and canonical input
  lifetime adapter, documented in
  [`switch2-physical-input-boundary.md`](switch2-physical-input-boundary.md),
  that has no `HidDevice`, path, handle, discovery, or live-I/O dependency.

The decoder returns raw values. It does not apply calibration, dead zones,
axis transforms, sensor scaling, fusion, mappings, or virtual-pad publication.
The dictionary-based replay engine is test/offline infrastructure and is not a
live hot-path design.

USB ingress deliberately accepts only Pro Controller 2 report `0x05`. Reports
`0x07`, `0x08`, and `0x09` remain available only through their exact BLE
characteristic identities. Public sources describe `0x09` as the Pro
model-specific report, but the project-owned Pro USB `bcdDevice 0x0201`
evidence contains only `0x05`, and the licensed SDL USB initialization
explicitly selects `0x05`. Wired/Charging-Grip Joy-Con 2 is not promoted from
the BLE layout without its own evidence. Treating a model-specific BLE body as
a USB report merely because it also has 63 bytes would be a length-based
protocol guess.

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
| `0x21` | 2 | little-endian battery-current field | raw; scale/direction not promoted |
| `0x23` | 1 | uninterpreted | no charging-state meaning assigned |
| `0x24` | 6 | uninterpreted | includes the observed `0x29 == 0x01` byte |
| `0x2A` | 4 | little-endian motion timestamp | units unknown |
| `0x2E` | 2 | temperature raw bits (`ushort`) | signedness and scale unknown |
| `0x30` | 6 | three raw signed accelerometer words | scale/orientation unknown |
| `0x36` | 6 | three raw signed gyroscope words | scale/orientation unknown |
| `0x3C` | 2 | uninterpreted for supported models | analog triggers only on unsupported NSO GameCube controller |
| `0x3E` | 1 | uninterpreted/reserved | unknown |

### Dynamic battery status

The common-report power layout follows the corroborated Switch2Connect
implementation: voltage is the little-endian word at `0x1F..0x20`, current is
the little-endian word at `0x21..0x22`, and `0x23` remains opaque. Earlier
worktree code incorrectly split byte `0x21` into a purported charging flag and
decoded current one byte late; strict round-trip and boundary tests now pin the
correct layout. Current direction and charging semantics are not inferred.

`Switch2BatteryStatus` adapts Switch2Connect's validated `2.5..5.0 V` input
range and its three visible bands: greater than `3.25 V` is high, greater than
`3.125 V` is medium, and the remaining valid range is low. DS4Windows' legacy
controller API accepts only a percentage, so the bands are exposed there as
stable `90`, `50`, and `10` compatibility markers. Those values are UI
categories, not a lithium state-of-charge estimate; in particular, high is not
reported as fully charged. Invalid readings preserve the last valid status.

Pro and standalone Joy-Con runtimes publish their own status. Joined Joy-Cons
retain both physical statuses and present the lowest currently valid half to
the legacy combined-controller UI. `BatteryChanged` is emitted only after the
corresponding input report has been published and only when the visible band
changes, so same-band voltage noise cannot add work to the input hot path.
Charging remains false until captures establish a direction/flag contract.

The four opaque regions (`0x08..09`, `0x18`, `0x24..29`, and `0x3C..3E`)
are retained as raw little-endian values. Together with the named raw fields,
they cover all 63 body bytes. `TryEncodeCommon05` reconstructs the exact body
and exists solely as a losslessness/replay invariant; it is not a controller
output command. Deterministic property tests round-trip 20,000 arbitrary exact
bodies byte for byte.

The `HasLeftStick`, `HasRightStick`, and `HasMouseData` properties are the
model-applicability boundary. Raw absent/inapplicable slots remain available
for forensic comparison but must not be published as controls.

### Model-specific reports

| Report/model | Basic fields | Opaque motion descriptor |
| --- | --- | --- |
| `0x07`, left Joy-Con 2 | counter `0x00` (8-bit), power `0x01`, buttons `0x02..03`, packed stick `0x05..07` | length byte at body offset `0x0F`, payload region `0x10..37` (40-byte capacity) |
| `0x08`, right Joy-Con 2 | counter `0x00` (8-bit), power `0x01`, buttons `0x02..03`, packed stick `0x05..07` | length byte at body offset `0x0F`, payload region `0x10..37` (40-byte capacity) |
| `0x09`, Pro Controller 2 | counter `0x00` (8-bit), power `0x01`, buttons `0x02..04`, sticks `0x05..0A` | length byte at body offset `0x0E`, payload region `0x0F..36` (40-byte capacity) |

Declared motion length above 40 is rejected. Lengths 0, 30, and 40 are
marked publicly observed; other bounded lengths stay opaque. The decoded
struct owns only the body-relative descriptor. Motion bytes remain owned by
the caller or immutable fixture, and `TrySliceOpaqueMotionBody` returns the
declared span after validating the exact body length.

### Pro Controller 2 USB canonical projection

The offline projector accepts only the exact identity
`VID 057E / PID 2069 / bcdDevice 0201`, model `ProController2`, USB transport,
report `0x05`, and fixture firmware evidence `unknown`. This is intentionally
narrow. `bcdDevice` is a USB device-descriptor revision, not a firmware version;
the passive audit did not query firmware. A fixture claiming a numeric firmware
revision is rejected until that revision has its own source/capture review.

The legacy replay projection uses the replay envelope's monotonic host
timestamp for ordering and validates its packet through the same canonical
builder as a future live transport. A live input session instead requires its
caller to provide one nonzero device generation, transport generation, QPC
frequency, and read-completion QPC timestamp. The session rejects descriptor
drift and timestamp regression. Reset accepts only an advanced lifetime fence,
then clears only the controller-counter baseline so a reconnect cannot inherit
device sequence state. It preserves the accepted completion-time baseline
because QPC remains one absolute monotonic host clock across reconnects.

The frame owns all 63 body bytes in bounded value storage, so reuse of a USB or
BLE read buffer cannot mutate an already-published frame. It carries the 32-bit
controller counter and modular delta only as raw movement; `+4` does not mean
four lost reports and does not establish a report rate. A backward/out-of-order
observation is retained and classified but does not replace the last accepted
counter baseline. The body `0x2A` motion timestamp is retained raw because its
clock units and firmware behavior are not established.

Pro button facts use the common report's original bit positions. Physical face
labels are included only to make the positional names unambiguous:

| Body byte/bit | Canonical control | Physical label |
| --- | --- | --- |
| `0x04.0` / `.1` / `.2` / `.3` | face west / north / south / east | Y / X / B / A |
| `0x04.6` / `.7` | right shoulder / right digital trigger | R / ZR |
| `0x05.0` / `.1` | back / start | Minus / Plus |
| `0x05.2` / `.3` | right-stick / left-stick click | R-stick / L-stick |
| `0x05.4` / `.5` / `.6` | guide / capture / C | Home / Capture / C |
| `0x06.0` / `.1` / `.2` / `.3` | D-pad down / up / right / left | same |
| `0x06.6` / `.7` | left shoulder / left digital trigger | L / ZL |
| `0x07.0` / `.1` | right / left paddle | GR / GL |

The proven-bit mask is `0x03CF7FCF`. Every other set bit remains available as
`UnknownButtonBits`; it is never silently cleared or promoted to a control.
Sticks remain packed-source 12-bit X/Y values. The canonical session retains
the raw values alongside signed center offsets and effective positive/negative
ranges. A structurally valid factory record is adopted only when every value is
interior and both endpoints remain inside the 12-bit domain; missing, malformed,
sentinel, and out-of-domain records receive explicit statuses and a symmetric
neutral fallback. Y-axis orientation and final normalization remain mapping
concerns, so this tranche does not reduce input through 8-bit `DS4State` or
invent an axis convention. Each calibration snapshot is bound to its physical
device generation; advancing that generation requires a newly bound snapshot,
while a transport-only reset must retain the structurally identical snapshot.

### Pure Joy-Con pair reducer

Pairing is deliberately separate from discovery and transport I/O. The reducer
accepts only exact BLE left/right canonical frames carrying the caller's
coordinated nonzero pair epoch. It joins halves only when their QPC frequencies
match and completion-time skew is within the configured microsecond budget.
Otherwise it names the stale half without discarding either value.

A newer generation may replace one half. An older generation or timestamp is
rejected without changing state, and a loss event removes a half only when both
device and transport generations exactly match the stored frame. Loss retains
a per-side lifetime tombstone, so buffered input from the retired or any older
lifetime cannot resurrect a missing half; rejoining that side must advance its
lifetime and preserve absolute QPC chronology. Split clears both halves and is
terminal for that pair epoch. Rejoining after a split requires a fresh state
with a new epoch. This is a deterministic merge primitive, not a claim that
Windows discovery, ownership, reconnect, or profile integration is implemented.

### Factory and user stick calibration

An exact nine-byte record contains three packed X/Y pairs. This implementation
uses the licensed SDL ordering: center at bytes `0..2`, positive/maximum range
at `3..5`, and negative/minimum range at `6..8`. Switch2Connect v2.7 reverses
the latter two semantic labels; no asymmetric project-owned hardware golden
supports that reversal, so it is not treated as authority.

Addresses name physical storage slots, not logical axes:

| Model and logical side | Storage slot | Address |
| --- | --- | ---: |
| Joy-Con 2 left / left | primary | `0x130A8` |
| Joy-Con 2 right / right | primary | `0x130A8` |
| Pro Controller 2 / left | primary | `0x130A8` |
| Pro Controller 2 / right | secondary | `0x130E8` |

Read-only user calibration is now admitted only when the eleven-byte record
starts with little-endian magic `0xA1B2` and its nine-byte payload passes the
same bounded-range adoption gate as factory data. A valid user record overrides
factory for that side. An absent marker, malformed payload, out-of-domain span,
or optional-read failure preserves the validated factory record.

| Model and logical side | User slot | Address |
| --- | --- | ---: |
| Joy-Con 2 left / left | primary | `0x1FC040` |
| Joy-Con 2 right / right | primary | `0x1FC040` |
| Pro Controller 2 / left | primary | `0x1FC040` |
| Pro Controller 2 / right | secondary | `0x1FC080` |

Current upstream SDL (`c71abd08605b8bb7078372307a93274725c99fe0`) and the
independent GPL-2.0-or-later `hid-nintendo2` implementation
(`32a981ea7f916f1792a7e35aa0ecf79063ec4001`) agree on `0x1FC080` and the
`0xA1B2` marker. `ndeadly/switch2_controller_research@d1c5a7f7` confirms the
primary slot and eleven-byte record but lists the secondary slot at
`0x1FC060`. Switch2Connect v2.8 also uses `0x1FC060`. That conflicting address
is not queried; tests pin `0x1FC080` so it cannot be reintroduced silently.

The production Pro USB owner performs the same factory/user precedence after
the five volatile startup commands and before publishing input. All four reads
run synchronously through the already-owned MI_01 bulk OUT/IN command lane;
there is no second handle, competing reader, polling worker, or runtime hot-path
work. A codec-valid but absent/invalid user marker preserves factory data. A
missing or invalid factory record retains the established centered wired
fallback. An uncertain native command outcome retires/quarantines the shared
command lifetime instead of continuing with a potentially misassociated
response.

## Byte-level HD-rumble codec

Status: implemented and offline-test verified. The codec remains a pure byte
boundary, but production runtime owners now compose it with generation-scoped
USB and Bluetooth physical writers. The VIIPER ns2pro virtual-output boundary
also uses it for strict validation and lossless semantic retention.

One five-byte subframe is a little-endian 40-bit value with four contiguous
10-bit raw fields:

```text
bits  0.. 9  oscillator 0 control
bits 10..19  oscillator 0 amplitude
bits 20..29  oscillator 1 control
bits 30..39  oscillator 1 amplitude
```

The `control` name is deliberate. Licensed upstream SDL interprets all ten
bits as a frequency code and calls oscillator 0 high/oscillator 1 low.
Switch2Connect and the provenance-tainted PadForge BLE experiment instead split
the top bit into a tone flag and reverse the band labels. The same bytes do not
resolve those semantics. Runtime adapters must wait for project-owned
single-field spectral basis tests; the pure codec preserves all bits without
choosing a theory.

One actuator group is exactly 16 bytes: `0x50 | counter` followed by three
five-byte subframes. The strict USB codec uses one modulo-16 counter and:

- Joy-Con report `0x01`: one group at bytes `1..16`, bytes `17..63` zero;
- Pro report `0x02`: independent left/right groups at `1..16` and `17..32`,
  bytes `33..63` zero.

Strict Pro decode rejects a non-`0x5?` group header, differing left/right
counter nibbles, wrong report ID/length, or nonzero reserved tail. It does not
require successive reports to advance by one; cadence/duplicate/skip telemetry
belongs to the eventual transport. The low/high compatibility values retain
SDL's licensed amplitude and control-code basis. For a source that provides one
sustained motor state rather than three temporal samples, DS4Windows repeats
that state in all three subframes. This follows the GPL-3.0 Switch2Connect
fill-forward behavior audited at commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28` and avoids turning a continuous
source into a one-subframe pulse. All destinations are completely overwritten.

The permissively licensed source is upstream SDL at
[`c71abd08605b8bb7078372307a93274725c99fe0`](https://github.com/libsdl-org/SDL/blob/c71abd08605b8bb7078372307a93274725c99fe0/src/joystick/hidapi/SDL_hidapi_switch2.c#L1031-L1110),
whose audited `release-3.4.x` file is byte-identical. Capture-backed facts
corroborate the independent groups and matching counters, but that repository
has no license and supplied no code or prose.

### Four-actuator synthesis policy

`Switch2HdRumbleFeedbackTranslator` provides an allocation-free, pure
translation from the versioned canonical four-actuator frame into independent
left and right HD-rumble groups. It does not perform I/O, choose a transport
envelope, own a sequence counter, schedule a cadence, or authorize physical
output.

The body-motor portion follows the pinned zlib-licensed SDL compatibility
basis exactly: control codes `0x187` and `0x112`, a 16-bit amplitude ceiling of
`29000`, and the same two integer truncation points before packing the 10-bit
amplitude (`scaled16 >> 6`, maximum packed value `453`). The resulting sustained
state is repeated in all three subframes. Body-low and body-high are mirrored
to both physical sides as SDL does.

The impulse-trigger portion is explicitly a project conversion, not a claim of
a physical trigger actuator. A left trigger controls only the high-frequency
field in the left encoded group; a right trigger does the same only in the
right encoded group. The low-frequency field remains the body lane. Dynamic
mode maps the nonzero Xbox trigger intensity monotonically from 300 through
481 Hz; fixed mode maps profile levels 1 through 10 across the same range, and
the separate 1-through-10 strength setting remains bounded by the physical
10-bit field. The profile setting is enabled by default so all four decoded
Xbox feedback lanes are represented, and can be disabled for SDL body-only
compatibility. Hardware sidedness remains a physical basis-test gate. High-band
overlap uses bounded saturating addition: body-high and side-local impulse
contributions are both retained, their 10-bit sum is capped at `1023`, and
wraparound is impossible. Total physical energy still requires measurement.
Results identify either SDL body
compatibility or side-local impulse approximation and retain the source
sequence, device generation, transport generation, ownership epoch, and
Neutral/Stop distinction. Source identity, timestamp, and TTL are also retained
so the eventual sole writer must re-check freshness immediately before physical
delivery rather than treating translation-time freshness as a durable grant.
Expired or malformed source frames fail closed;
Neutral and Stop synthesize the licensed fixed-control compatibility basis with
zero amplitudes. This is exact logical amplitude neutral, not yet a claim about
physical stop/watchdog behavior.

DualSense PCM does not use the sustained fallback. Its 32-sample, approximately
10.7 ms stereo window is split chronologically into three slices. Each side and
slice independently contributes RMS low-band energy and first-difference
transient high-band energy to the matching Switch 2 subframe. Compatibility
motors are composed with PCM rather than being discarded by a silent valid
carrier. Supported DualSense trigger modes (`0x01`, `0x02`, `0x21`, `0x22`,
`0x23`, `0x25`, `0x26`, and `0x27`) are decoded into a bounded side-local
three-region envelope while the matching digital Switch 2 trigger is held.
The source frequency byte is retained as a bounded carrier hint for vibrating
modes; position regions select bounded carriers for resistance/break modes.
This preserves available tactile detail but cannot reproduce adaptive
resistance. Unknown, off, malformed, or empty programs fail closed. Native
Switch 2 groups bypass synthesis and retain all three subframes and oscillator
fields exactly.

The production composition now routes canonical Xbox feedback through a
generation-scoped state-lane pump to exactly one physical writer per transport
(or one coordinated writer per Joy-Con half). Bluetooth writes use the
source-corroborated 17-byte Joy-Con or 33-byte Pro envelope and destination-
owned modulo-16 counters. Successful delivery commits the counter; an uncertain
write retains the identical payload for bounded retry. Runtime retirement seals
publication and requires terminal neutral before it releases the transport.
DS4Windows profile/macro rumble and test-preview rumble also use fixed origins
in this same owner; withdrawing preview produces its exact Stop before a
lower-priority source can resume.

This proves deterministic mapping, ownership, and lifecycle behavior in offline
tests. Switch 2 has no physical trigger actuator, and no claim is made that the
side-local conversion is perceptually equivalent to Xbox impulse triggers.
Spectral sidedness, safe amplitude, onset/stop timing, accepted sustained
cadence, controller watchdog behavior, thermal limits, and preference tuning
remain hardware gates. The code therefore makes no 500 Hz, energy-equivalence,
or hardware-delivery claim until the authorized controller/radio matrix runs.

VIIPER's 34-byte virtual output envelope is independently decoded as left
group, right group, flags, and player-LED mask. Rumble requires two valid group
headers with matching counters. When the rumble flag is clear, all 32 rumble
bytes must be zero; when the LED flag is clear, the LED mask must be zero.
Unknown flags fail closed. DS4Windows deliberately does not max-fold these
bytes into legacy motor magnitudes: doing so would mix the `0x5?` header and
frequency/control fields with amplitude. A physical Switch 2 target now
preserves the exact decoded oscillator groups through its authenticated output
lifetime. Other physical families still receive no guessed reduction from
these fields. LED-only traffic never changes rumble, and rejection does not
synthesize a stop. For a physical Switch 2 target, player-indicator state uses
the same virtual-output lifetime: BLE carries any exact four-bit mask while
USB remains restricted to its six capture-backed commands.

## Bounded USB command replay codec

Status: **source/replay and offline-test verified; the exact volatile startup,
battery, and player-LED paths are also verified on the project-owned
`057E:2069 / bcdDevice 0201` controller**. `Switch2UsbCommandCodec` contains no
transport, device opening, pairing, firmware mutation, runtime wiring, or
arbitrary-command forwarding. Its memory surface is restricted to four
read-only calibration records. It admits only these pinned USB forms:

| Operation | Exact request | Exact response shape |
| --- | --- | --- |
| Enable USB HID reports (`03:03`) | `03 91 00 03 00 04 00 00 01 00 00 00` | `03 01 00 03 00 f8 00 00 01 00 00 00` |
| Select common input report (`03:0A`) | `03 91 00 0a 00 04 00 00 05 00 00 00` | `03 01 00 0a 00 f8 00 00` |
| Set or enable the closed `0x27` feature mask (`0C:02/04`) | `0c 91 00 SS 00 04 00 00 27 00 00 00` | `0c 01 00 SS 00 f8 00 00 00 00 00 00` for admitted bcdDevice `0x0201` hardware |
| Get raw battery voltage (`0B:03`) | `0b 91 00 03 00 00 00 00` | `0b 01 00 03 HH AA 00 00 VV VV 00 00` |
| Player LED 1 through 4, all on, or all off (`09:01..06`) | `09 91 00 SS 00 00 00 00` | `09 01 00 SS HH AA 00 00` |
| Factory primary/secondary calibration (`02:04`) | `02 91 00 04 00 08 00 00 09 7e 00 00 AA AA AA AA` | `02 01 00 04 10 78 00 00 09 00 00 00 AA AA AA AA` plus exactly 9 bytes |
| User primary/secondary calibration (`02:04`) | `02 91 00 04 00 08 00 00 0b 7e 00 00 AA AA AA AA` | `02 01 00 04 10 78 00 00 0b 00 00 00 AA AA AA AA` plus exactly 11 bytes |

For battery and LED, `(HH, AA)` is accepted only as the indivisible observed
pair `(0x10, 0x78)` or `(0x00, 0xF8)`; mixed pairs fail. The former comes from
the pinned public capture, while the latter was observed repeatedly after the
two exact volatile startup transactions on project-owned hardware. Their
unknown semantics are not generalized. Successful battery and LED validation
also returns the closed `Switch2UsbCommandResponseStyle` value naming the exact
pair, so callers do not have to infer it from a generic success boolean.
`VV VV` is returned as an uninterpreted
little-endian `ushort`; the trailing payload bytes must be zero. `SS` is closed
per operation and invalid CLR enum casts are rejected at every entry point.
Responses are checked against the specific subcommand sent. Length, command,
direction, USB transport, header style, reserved bytes, and battery payload
reserved bytes are all exact.

The `0x27` feature request encoder admits only set and enable. The response
validator admits only the exact 12-byte tuples captured in the authorized
2026-08-31 USB run against `057E:2069`, bcdDevice `0x0201`; every byte mutation,
cross-step substitution, alternate tuple, and nonzero payload byte is rejected.
This does not generalize the tuple to another firmware, transport, mask, or
feature command. The codec does not expose `03:0D` host-address USB initialisation or
any pairing, association, arbitrary-address memory access, flash block read,
memory write/erase, firmware, clear/disable/configure, or raw command form.
`AA AA AA AA` is little-endian and is accepted only as `0x130A8`, `0x130E8`,
`0x1FC040`, or `0x1FC080` with the corresponding exact length. The response
must echo both address and length before its payload can escape the native
buffer.

The six player-LED operations are treated only as volatile controller state;
this codec contains no persistent-state command.

The authority is the command header and captured examples in
[`ndeadly/switch2_controller_research@d1c5a7f7`](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/commands.md#command-0x09---player-leds),
used as independently expressible wire facts only. SDL at the pinned licensed
revision corroborates use of a claimed USB bulk OUT/IN command path, but it is
not authority for broadening this allowlist.

Player-LED `AllOff` is the logical neutral operation available in the pinned
forms. It cannot restore a prior LED pattern that was never observed. The
bounded hardware verifier therefore calls the exactly acknowledged operation
explicit neutralization, never restoration.

## Evidence and license ledger

### Licensed implementation reference

SDL was reviewed at main commit
[`c71abd08605b8bb7078372307a93274725c99fe0`](https://github.com/libsdl-org/SDL/tree/c71abd08605b8bb7078372307a93274725c99fe0)
and the audited `release-3.4.x` tip
[`58ff755ea58cd4c64e72678f4b3e0d720aa79206`](https://github.com/libsdl-org/SDL/tree/58ff755ea58cd4c64e72678f4b3e0d720aa79206).
SDL uses the zlib license. The commits contain the same
`SDL_hidapi_switch2.c`. Facts corroborated there include 64-byte USB input,
common button/stick offsets, the six-value packed-12 calibration layout, and
factory primary/secondary stick addresses `0x130A8`/`0x130E8`. Pro maps its
logical left/right sticks to primary/secondary respectively. Either single
Joy-Con stores its only stick in primary; the right Joy-Con maps that primary
record to the logical right stick. No SDL source text was copied; the C#
implementation is independent.

The USB calibration request/response tuple was cross-checked against current
SDL's 16-byte `ReadFlashBlock` request and the independently implemented
GPL-2.0 `hid-nintendo2` `cmd=0x02/sub=0x04` path. Only the protocol facts and
closed addresses/lengths are reproduced; no reference implementation body is
copied.

For the Pro USB input tranche, the exact licensed source trace is
`src/joystick/hidapi/SDL_hidapi_switch2.c:944-1028` (button positions, digital
triggers, and packed 12-bit sticks), `:1113-1148` (64-byte input dispatch and
USB product-model gate), and `:420-423` (selection of report `0x05`). SDL's
`size < 64` check is not copied as policy: this codec requires exactly 64 bytes
so an oversized or concatenated record cannot be accepted accidentally.

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

The later read-only user-calibration tranche additionally compared current
upstream SDL `c71abd08605b8bb7078372307a93274725c99fe0`,
`XenuIsWatching/hid-nintendo2-dkms@32a981ea7f916f1792a7e35aa0ecf79063ec4001`,
`ndeadly/switch2_controller_research@d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92`,
and Switch2Connect v2.8 `61ac6642ce12fe7217e38a860b14863b18ca7e28`.

`Switch2Connect@4487322` independently agrees on the common body fields in
`src/controller.py:1422-1434` and records the complete raw button table in
`src/usb_hid_controller.py:1125-1133`. Its authored code is GPL-3.0-or-later,
so it was used only to corroborate independently expressible wire facts. Its
README's “up to 500Hz” statement is not evidence for a different decoder or
transport: the audited implementation and captures do not establish 500Hz.

PadForge stable `0794fd01bd19f4c096b982ffc824b88bce5ed743` consumes its
bundled SDL state rather than implementing a second Switch 2 report parser.
PadForge is CC BY-NC-SA 4.0 and remains behavioral evidence only. BlueRetro
`e1a9831a875f5313a923160a1379a7ebbfaa2b11` (Apache-2.0) corroborates the
Nintendo product IDs and 63-byte BLE report containers, but does not promote a
common `0x05` Pro button parser; it therefore supplied no canonical field code.

### Project-owned hardware evidence

On 2026-08-29, a physical Switch 2 Pro Controller enumerated read-only as USB
`057E:2069`, `bcdDevice 0x0201`: HID MI_00 (`HidUsb`, usage page 1/usage 5,
64-byte IN/OUT and no feature report), WinUSB MI_01, and USB audio MI_02. The
initial topology/input audit performed no write, serial query, association, or
memory command.

Two passive USB HID captures were kept outside Git and decoded with the exact
compiled strict codec. Their digests remain in the out-of-tree lab manifest so
a committed test vector cannot be used as a stable link to raw hardware data:

- all 2,048 reports in the first run were exact 64-byte report `0x05` and
  decoded. Raw counter deltas were `+4` 2,043 times, `+3` twice, `+5` once, and
  `+556` once; the `+556` followed a 0.683-second host-capture gap; and
- all 4,096 reports in the second run were exact 64-byte report `0x05`, all
  decoded, and every raw counter delta was `+4`. Host completion deltas in that
  passive run had mean 3.990 ms, p50 4.001 ms, and p99 4.112 ms, observing
  approximately 250 reports/second. Those managed-read timestamps are cadence
  evidence only, not transport or end-to-end latency proof.

No wrap was observed in hardware; 8-bit and 32-bit wrap behavior is verified
with synthetic vectors. A raw `+4` is therefore classified as forward movement,
not four packets lost. Duplicate/backward classification uses a documented
half-range modular ordering policy, not a firmware guarantee.

The committed golden fixture is now **fully synthetic**, not a redaction or
derivation of either hardware capture. Its counters, button bit, packed stick
values, clock identifier, timestamps, and opaque identifiers were deliberately
constructed from the public byte layout. Every mouse, magnetometer,
battery/current, timestamp, temperature, accelerometer, gyroscope, reserved,
environmental, and uninterpreted packet byte is zero. It contains no raw source
digest, device path/path hash, serial, MAC, key, host, console, account value,
or hardware stick fingerprint. Tests verify the zeroed ranges and replay the
synthetic `+4` counter movement.

A later separately authorized, bounded verifier run used only the exact
volatile startup, battery, Player-1 LED, and `AllOff` forms above. Before the
startup pair, the battery request completed its OUT write but received no IN
bytes and timed out; restarting only the exact composite device did not change
that result. Both startup operations then returned their exact USB ACKs, after
which battery and LED responses used the exact `(0x00, 0xF8)` header pair.
That schema-2 run observed 3533 mV, exactly acknowledged Player-1, and exactly
acknowledged `AllOff`. It passively captured 256/256 exact common-`05` reports
at 249.999730392448 host completions/second with all 255 counter deltas `+4`.
No haptic report was emitted: the live nonzero gate failed closed before the
first write because physical neutralization after a noncooperative HID transfer
is not yet provable. That build allowed HID write sharing, so the artifact is
historical command/cadence mechanism evidence, not sole-writer or current-build
evidence.

The prior schema-3 procedure instead opened MI_00 read-only with only read
sharing, recorded writer admission explicitly, and bound its result to the
executing verifier assembly SHA-256. Its authorized run failed that admission
with Win32 error 32 before any hardware I/O. This establishes the fail-closed
gate only: the error neither identifies a handle owner nor proves whether the
incompatible handle was a writer or a restrictively shared reader.

The schema-4 procedure additionally gives every WinUSB transaction an atomic
hard ownership handoff, serializes the exact admitted response-header style,
bounds input disposal, and validates canonical result shape/types/ranges. Its
authorized Release run also failed the MI_00 sole-writer admission with Win32
error 32 before opening the command interface or attempting input, battery,
LED, haptic, or cleanup mutation. The result file is
`_results/switch2-usb-hardware-2026-08-29-schema4-sole-writer-v2.json`, SHA-256
`0DFC1F5240FF3BBBFD9DD8CC0F8689039981EEEA3EF9856E9E626A099E7F8F44`.
The verifier DLL was
`87D2A053DD6FF7A447D71E27B13D2715B188547E8A33E3816EF95164184F4A4E`
and the EXE was
`B68998F98B5FA3A95E9050A0F055456169E16E6B0D678F29C1D4A2044786AA2C`.
No process was attributed as the handle owner. The out-of-tree artifacts and
limitations are documented in `utils/Switch2UsbHardwareVerify/README.md`; no
successful isolated current-source hardware run is claimed.

Coordinate note: an observed change at complete USB packet offset `0x3C` is
body offset `0x3B` after removing report ID `0x05`, i.e. the high byte of gyro
Z. The supported-model body bytes `0x3C/0x3D` are not exposed as triggers.

## Defensive policies versus unknowns

Defensive policies, not undocumented firmware claims:

- exact lengths only; never zero-pad a truncated report;
- USB accepts only Pro model report `0x05`; model-specific `0x07..0x09` bodies
  require their exact BLE characteristic identities, and wired/Charging-Grip
  Joy-Con 2 remains unsupported;
- exact service and characteristic identity plus required GATT properties;
- privacy-safe host classification compares the six advertised reversed bytes
  transiently with the selected radio address and returns only an enum;
- calibration decodes every exact nine-byte record, then a separate adoption
  validator rejects zero/saturated components and endpoints outside the 12-bit
  domain; factory address selection is by controller model plus
  primary/secondary physical storage slot, never logical side alone;
- stream, clock, synthetic-fact, derived-golden, and capture IDs require
  type-specific prefixes plus caller-generated 128-bit lowercase nonces;
  formatted MAC addresses and arbitrary provenance prose are rejected;
  firmware is `unknown` or a bounded numeric `fw-` version;
- derived golden sources carry a nonzero derivation-manifest revision but no
  raw-capture digest or redaction revision, so they cannot masquerade as either
  synthetic protocol facts or minimally redacted hardware captures;
- project-owned hardware sources additionally require a source SHA-256 and
  nonzero redaction-manifest revision;
- timestamps are monotonic per declared host-clock domain, device generation
  is per stream, frequency is stable per clock domain, and pair epoch is
  independently caller-coordinated;
- model, transport, firmware, source, clock identity, and pair epoch cannot
  drift within one `(StreamId, Generation)`; and
- the transport-neutral session fences exact protocol identity, device and
  transport generations, QPC frequency, and nondecreasing completion time; and
- the pair reducer cannot combine different frequencies or pair epochs, and
  loss/replacement events are generation-fenced.

The exact Pro USB canonical identity gate currently recognizes only
`057E:2069:bcdDevice-0201`; unsupported revisions fail closed and are not
reinterpreted by packet length. The gate records firmware as not queried and
rejects a fixture that claims firmware-specific evidence.

## Original phase-1 exclusions (historical)

The following list records the scope boundary at the original codec-only cut;
it is not current product status. The production transport, runtime, mapping,
feedback, and association documents supersede the items completed after that
cut:

- OS USB/HID, WinUSB, libusb, BLE scanning, GATT, and connection parameters;
- registration, runtime selection, mapping, and virtual-pad publication;
- Nintendo application-layer association, SMP interaction, LTK derivation,
  Windows key provisioning, wake, and automatic reconnect;
- controller-memory access and every initialization command beyond the closed,
  volatile USB-HID enable/common-report forms above;
- persistent controller-memory writes and user-calibration authoring (the
  runtime only performs bounded reads and never mutates controller memory);
- packed motion decoding, timestamp units, scale, orientation, and fusion;
- HD-rumble semantic adaptation, BLE envelope, scheduling, pacing, and stop
  behavior (raw 40-bit/group/USB codecs are offline-only);
- firmware-dependent GATT changes and achievable Windows BLE interval; and
- Joy-Con discovery, runtime ownership/association, reconnect scheduling, and
  profile integration beyond the pure pair reducer.

## Offline verification (2026-08-29)

No controller was enumerated or contacted by these tests.

```text
dotnet test DS4WindowsTests\DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Switch2"
Passed: 89, Failed: 0, Skipped: 0

dotnet test DS4WindowsTests\DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore
Passed: 1141, Failed: 2, Skipped: 3
```

The current full-suite failures are
`ControllerFeedbackRuntimeTests.PublishSelectClaimAdmitCompleteSteadyStateAllocatesNothing`
and `DualSenseInputReadPipelineTests.WarmSynchronousReadAndRearmCycleAllocatesZero`.
An isolated rerun of those two tests reproduced the feedback-runtime failure and
passed the DualSense allocation test. The three skips are the opt-in live
application-loopback audio tests. The focused set includes 20,000 randomized
exact-body lossless round trips, all 256 USB report IDs, malformed destination
lengths, model /
transport / firmware-evidence gates, counter wrap and host-clock separation,
calibration adoption/fallback, lifecycle reset fences, Joy-Con pair
skew/loss/split behavior, exact physical-interface admission, and zero managed
allocations after warmup for decode, raw re-encode, canonical
processing/projection, physical input adaptation, and the pure pair reducer.

Before a live adapter, obtain sanitized USB and BLE captures for every model,
common/model-specific modes, reconnect/wake states, more than one Windows
Bluetooth chipset, and both single and concurrent Joy-Con operation. Any
association, key, memory, or write experiment requires explicit user consent,
a console re-pair recovery procedure, and a separate secret-redaction review.
