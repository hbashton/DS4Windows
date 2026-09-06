# Switch 2 Bluetooth controller-side association

Status: implemented behind the internal Windows BLE adapter and verified with
deterministic fake-platform tests. The production `ControlService` Settings UI
hosts the explicit action; hardware validation remains a separate gate.

This is Nintendo application-protocol association, not Windows Bluetooth SMP
pairing. DS4Windows does not call `DeviceInformation.Pairing.PairAsync`, create
or remove an OS bond, persist a peer address, or expose an arbitrary GATT
command surface.

## Proven flow adopted

The implementation deliberately follows the working GPL-compatible donor
instead of inventing a replacement protocol:

- `TommyWabg/Switch2Connect@4487322a306f04efa27682e3f3a508635a84fd98`
- license: GPL-3.0 in `LICENSE.md`; DS4Windows is GPL-3.0-or-later
- source locations:
  - `src/controller.py:1160-1182` for service, command, response, and command
    identifiers;
  - `src/controller.py:2275-2350` for response-before-command subscription;
  - `src/controller.py:2664-2682` for the eight-byte command envelope and
    response acceptance facts; and
  - `src/controller.py:3984-3997` for the exact four-step host-address and key
    ceremony.

The UUIDs, command/subcommand values, the two fixed 17-byte key payloads, and
the request byte layout are directly adapted from that GPL-compatible source.
The C# ownership, cancellation, privacy, single-flight, and cleanup structure
is DS4Windows-specific. The source is named here explicitly because these are
not claimed as independently discovered constants.

PadForge remains behavioral evidence only under CC BY-NC-SA 4.0. No PadForge
code, structure, constants, text, or binary is present in this implementation.

## Transaction contract

An advertisement with zero host bytes receives a scan-generation-scoped,
one-shot raw-address capability. The capability also retains the advertised
public/random/unspecified address type; WinRT opens the device with the exact
type instead of guessing.

The temporary association owner then:

1. opens the typed BLE peer and queries the Nintendo service uncached;
2. resolves exactly one command-write and one command-response characteristic;
3. installs and enables the response notification before publishing any write;
4. sends command `0x15` subcommands `0x01`, `0x04`, `0x02`, and `0x03`, in that
   order, waiting for a successful command response after each;
5. never retries an ambiguous write or response;
6. drains the callback, disables the response CCCD, and closes the complete
   temporary object graph; and
7. records one scan-generation-fenced promotion capability only after Commit
   and cleanup are both known successful; and
8. requires a later matching advertisement carrying the selected local host
   address to consume that promotion and enter the ordinary remembered-peer
   Common05 input path.

The expected `no host -> this PC` change is therefore not treated as a generic
identity mutation. It is accepted exactly once after a clean association, and
the fresh OS observation rearms the scan-private address capability for the
ordinary remembered-device open. A failed, timed-out, cancelled, or cleanup-
ambiguous ceremony never records the promotion; the same host change is then
quarantined as an identity conflict. Model, product, peer token, scan
generation, and advertisement order must all remain exact.

The controller may begin readvertising the selected host after its Commit
response but before the temporary notification/CCCD cleanup returns. That
exact transition is classified as association-in-progress: it is neither a
connection candidate nor an identity conflict. Clean cleanup authorizes a
later matching advertisement; any failed or ambiguous cleanup terminally
quarantines the consumed admission.

The host address and command buffers are bounded private copies and are zeroed
after the transaction. Cancellation of a non-cooperative GATT query transfers
its parent service/device graph to the late-result disposer so no native object
is destroyed while WinRT may still be using it.

The donor validates response length, command id, and success status. It does
not establish the remaining header bytes as stable subcommand echoes, so the
DS4Windows codec intentionally does not invent stricter semantics. Since all
four steps share command id `0x15`, only one request can be outstanding and any
invalid matching response terminally retires the channel.

## Offline evidence

The association tests pin all four exact byte vectors and verify:

- response subscription precedes the first write, including inline callbacks;
- advertisements and opens retain the exact address type;
- every step executes once in the donor order;
- invalid identity/properties, failed writes, invalid responses, cancellation,
  timeout, and cleanup ambiguity fail closed;
- a failed or ambiguous attempt cannot reuse the same scan capability; and
- only a clean commit promotes the subsequent matching local-host
  advertisement, while an uncommitted host change remains quarantined;
- non-cooperative queries retain and eventually dispose late native results and
  every parent owner.

These tests do not claim a successful physical association. Hardware proof must
be captured later from the authorized portable build.
