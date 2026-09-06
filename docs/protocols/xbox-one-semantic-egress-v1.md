# Xbox One semantic egress contract v1

Status: production-wired behind explicit external-identity authorization and
offline verified. DS4Windows can select the persona for any bound physical
controller, while Switch 2 feedback uses its exact transport-generation owner.
This is not yet evidence of Windows API visibility, representative-game
compatibility, hardware latency, or physical feedback delivery. Guide and
Share have exact offline codec/lifecycle coverage but still require the same
Windows/game matrix as the rest of the persona.

`XboxOneEgressState` is DS4Windows' target-specific semantic state for the
authorized VIIPER Xbox One/Series persona. It is not a GIP message and must never
be sent directly to a USB endpoint. Profile mapping remains in DS4Windows;
VIIPER owns GIP lifecycle, sequence values, keep-alive, Guide status, Share
extensions, endpoint scheduling, and USB/IP completion.

The exact v1 frame is 24 bytes, little-endian:

| Offset | Size | Field | Constraint |
|---:|---:|---|---|
| 0 | 2 | contract version | `1` |
| 2 | 2 | embedded size | `24` |
| 4 | 4 | semantic buttons | bits 0..15 only |
| 8 | 2 | left trigger | `0..1023` |
| 10 | 2 | right trigger | `0..1023` |
| 12 | 2 | left stick X | signed 16-bit |
| 14 | 2 | left stick Y | signed 16-bit |
| 16 | 2 | right stick X | signed 16-bit |
| 18 | 2 | right stick Y | signed 16-bit |
| 20 | 4 | reserved | all zero |

Button bits, in order, are Menu, View, A, B, X, Y, D-pad Up, Down,
Left, Right, left bumper, right bumper, left stick, right stick, Guide,
and Share. Opposing D-pad directions remain independent semantic facts; SOCD
policy belongs to profile mapping.

The cross-repository golden vector is:

```text
01 00 18 00 65 DA 00 00 34 02 CD 03
02 01 FE FF 00 80 FF 7F 00 00 00 00
```

It represents Menu, A, Y, D-pad Up/Right, right bumper, left-stick click,
Guide, and Share; triggers `0x0234`/`0x03CD`; sticks `0x0102`, `-2`,
`-32768`, and `32767`.

The compatibility adapter deliberately reuses the existing final Xbox 360
projection instead of creating another profile stack. It expands the legacy
eight-bit trigger range monotonically to ten bits and preserves the existing
signed stick projection. A future high-resolution profile path may construct
`XboxOneEgressState` directly and retain all ten trigger bits.

Both implementations reject a wrong length, version, embedded size, reserved
button bit, reserved tail byte, or trigger above 1023. Serialization is exact,
atomic on validation failure, and allocation-free after warm-up.

## Authenticated acknowledged duplex broker boundary

`DormantRetainedUSBAdapter.PublishSemanticInputWire` now decodes this exact
24-byte frame directly into the retained persona's existing latest-state cell.
It does not add another input queue, mapper, or sequence pool. The existing
`XboxOneEgressScheduler` remains DS4Windows' sole ordering policy and the
persona remains responsible for its own final admission. Guide transitions
enter VIIPER's bounded ordered Command-7 edge owner, while Share is admitted
only by the strict official Console Function Map metadata variant and its
32-byte input payload. The standard fourteen-byte base form continues to
reject either control rather than silently losing it.

In the reverse direction, VIIPER translates a delivered persona-local Direct
Motor or `ClearOutputs` action into the existing 72-byte CFBK v1 contract.
DS4Windows' `XboxOneBrokerFeedbackIngress` validates an exact Xbox One/Series
source, device generation, transport generation, and ownership epoch before
publishing that frame into the existing `NativeGame` slot of the physical
controller's one `ControllerFeedbackRuntime`. It owns no arbitration,
mailbox, sequence watermark, expiry policy, or physical-device translation.

The action-to-CFBK policy is deliberately narrow:

- left/right vibration become canonical body-low/body-high;
- left/right impulse become canonical left/right trigger;
- enabled-mask bits gate their corresponding percentage values;
- non-zero percentages are rounded to the nearest normalized `0..65535` value;
- Direct Motor duration zero is `Neutral`, retaining the ownership lease;
- only persona `ClearOutputs` is terminal `Stop`;
- a non-cancelling Delay or Repeat program is rejected because CFBK v1 is a
  latest-state lease, not a timed-program transport; and
- active TTL is capped by both the configured 250 ms CFBK ceiling and the
  Direct Motor duration. Longer effects need a bounded renewal service before
  live enablement.

VIIPER's `ClearEpoch` is not the CFBK `OwnershipEpoch`. The former is a local
exactly-once executor ledger and never crosses this wire. The latter names the
DS4Windows NativeGame lease and is supplied by the exact outer physical-target
binding. They are deliberately neither copied nor compared; persona
generation/order and canonical source/device/transport/ownership form two
different, nested lifecycle fences.

The ingress publishes a structurally valid newer frame even when it is already
expired at consumption time. That is intentional: the canonical runtime must
advance its one ordering watermark and deliver a stop for an admitted older
effect rather than permit stale resurrection. `Stop` terminality, source-local
sequence order, TTL/future-skew evaluation, winner priority, and physical
release remain entirely in `ControllerFeedbackRuntime`.

The production device stream uses an Xbox-only `X1BR` v1 frame envelope over
VIIPER's authenticated ChaCha20-Poly1305 stream. Its fixed 16-byte header is:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII `X1BR` |
| 4 | 1 | version `1` |
| 5 | 1 | message type |
| 6 | 2 | little-endian payload length |
| 8 | 8 | little-endian correlation/revision |

The closed message set is ConsumerReady `0x01`, SemanticInput `0x02`,
CanonicalFeedbackAck `0x03`, ConsumerReadyAck `0x81`, SemanticInputAck
`0x82`, and CanonicalFeedback `0x83`. Input is committed only after VIIPER
accepts the exact successor revision and returns an accepted ACK. Canonical
feedback completes the persona executor only after DS4Windows accepts the
complete 72-byte CFBK value into the exact physical generation and ACKs it.
A rejected, lost, malformed, duplicated, or out-of-order ACK retires the
one-shot persona; the Xbox stream is never reopened in place.

An exact rejected **input** ACK now installs a permanent input-only fence.
The authenticated state writer enters the existing Disconnect transaction
without first closing the broker or clearing feedback callback admission.
Canonical feedback and its ACKs remain available through bounded teardown.
An already-acknowledged terminal Stop is preserved for that incarnation;
the duplex reader must accept Stop before or after the rejected input ACK.
The failure owner must be the exact current writer thread, writer generation,
stream and stream generation. Connect joins that writer before replacement.
Ambiguous timeout, malformed/duplicate ACK, transport failure or failed
canonical feedback still closes the transport; none becomes a successful Stop.

The source-level rejection/Stop path is covered by loopback tests with the
actual writer, reader, feedback dispatcher and a test physical-state owner.
This does not establish physical HID/BLE flush or actuator timing. Xbox startup
and cleanup now use a captured registration capability instead of address-only
`bus/{id}/remove` followed by `bus/remove`. Independent public USB/IP aliases
prevent old imports selecting a numeric-address successor. Client lifetime
integration and server selection are software verified; native retry-job
retirement and portable Windows reconnect behavior remain unproven.
See [the versioned removal contract](xbox-one-registration-removal-v1.md).
Do not consider live fault-recovery validation production-safe yet.

Semantic-input acceptance is not downstream USB presentation. VIIPER validates
the exact successor and publishes to the retained generation's ordered semantic journal, then
returns the ACK without waiting for Windows to issue or complete an interrupt-
IN request. Because DS4Windows resolves every ordered-egress claim at that exact
ACK boundary, the Xbox broker reader runs in MMCSS `Games` at
`ThreadPriority.Highest`, matching the state writer it releases. Optional
allocation-free diagnostics (`DS4WINDOWS_VIIPER_LATENCY_DIAGNOSTICS=1`) now
separate `claim->socket`, `socketWrite`, `xboxSocketStart->accepted`, and
`xboxAckReceived->writerWake`. The last two distinguish broker/transport
acceptance delay from Windows scheduling delay; neither is evidence of the
later USB/IP interrupt-IN completion time.

The broker reader and physical-feedback delivery are deliberately separate.
VIIPER permits only one unacknowledged canonical-feedback revision, so
DS4Windows hands that exact value to one single-outstanding delivery worker;
there is no feedback queue. The reader continues consuming semantic-input ACKs
while a bounded Bluetooth or USB output operation is in flight. The feedback
ACK is emitted only after that exact physical delivery succeeds; a rejected
delivery sends a rejected ACK and closes the exact one-shot stream. This keeps
slow physical output completion out of the 1 kHz input acknowledgement path
without weakening feedback order or completion evidence.

Creation and Windows attachment are separate authenticated operations. VIIPER
first constructs a dormant retained persona, DS4Windows opens its capability-bound stream
and completes ConsumerReady, and only then may the activation route attach the
USB/IP device. This prevents enumeration-time GIP output from preceding the
physical feedback consumer. Stream opening and activation authenticate the
captured creation token and revalidate before lifecycle transitions. Duplicate
streams cannot displace the existing feedback consumer, and deferred startup
cannot acquire a replacement at the same numeric address.

The pinned usbip-win2 0.9.7.7 native attach IOCTL returns the assigned positive
hub port but has no attach-time ownership-serial field. Activation therefore
accepts that exact ABI shape and additionally requires the exact public alias
in the activation receipt. DS4Windows binds a conditional local port lease to
the captured lifetime. It never uses that numeric port to detach an Xbox
registration. The entire `x1-` alias namespace is excluded from legacy port
cleanup, including ports visible before local lease binding or after removal.
The versioned removal contract records the implementation and residual native
retry-work limitations.

Virtual-output selection is deliberately independent of physical-controller
capability filtering. Every supported physical controller may select this
Xbox persona, and Switch 2 Pro, left/right Joy-Con 2, and joined Joy-Con 2 keep
all six virtual-output choices. The exact physical object is bound before
persona creation so feedback cannot move to a replacement controller that
reuses the same slot.

The route still requires `xbox-one-authorized-persona.json`; the code does not
invent or borrow a Microsoft identity. Windows binding/API visibility and the
hardware/game matrix remain release gates.

### Multiple virtual Xbox registrations

`importDeviceId` is the broker's retained-import lifecycle key, not the authorized
USB/GIP device identity. Each request allocates a distinct nonzero key on the
cold path and keeps it for that exact virtual lifetime. A cryptographically
seeded process-local sequence is synchronized and refuses exhaustion rather
than wrapping; separate process launches receive independent random seeds.
The broker's existing exact owner, registration and lease checks remain
authoritative. Import identifiers, including ones used in USB serials, are not
secret capabilities.

The earlier serial-only deployment option
`"derivePerRegistrationSerial": true` authorizes a distinct USB serial for
each virtual registration. The serial becomes the unchanged configured GIP
Device ID as 16 lowercase hex digits followed by the allocated import ID as
16 lowercase hex digits. Manufacturer, product, VID/PID, firmware and the GIP
identity remain unchanged. The strings object is cloned; the shared deployment
configuration is not modified. This option is local configuration only and is
not sent as an additional field in VIIPER's closed factory schema.

This serial-only option is insufficient for multiple or recreated Windows GIP
devices: Windows also indexes their primary Hello DeviceID. The September 6
dump investigation found a real collision and blocked native teardown despite
distinct serials. Use the new explicit deployment permission
`"derivePerRegistrationIdentity": true` to derive both a fresh primary GIP ID
and its matching serial. The fixed primary-device prefix is preserved; VID/PID,
versions, manufacturer/product and USB configuration remain exact. Identity and
strings are cloned without mutating the deployment bundle. Neither local
permission is sent as a new field in the closed VIIPER factory request.

When both permissions are omitted/false, the configured identity and serial
remain exact. The broker now rejects a previously registered primary GIP ID,
even after USB/IP retirement, because Windows PDO removal remains unproven.
Fixed-identity bundles therefore require explicit migration for repeated
creation; the old permission is not silently widened. Derived identities mean
new Windows per-device associations across recreations, not persistent device
identity. Process-local issuance never wraps; random process seeds are only
probabilistic separation, not machine-global uniqueness. Hardware acceptance
remains separate from the request and retained-transport tests.
