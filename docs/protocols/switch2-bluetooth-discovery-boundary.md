# Switch 2 Bluetooth discovery boundary

Status: implemented as a pure boundary and verified by unit, concurrency, and
allocation tests. The concrete WinRT continuation is production-routed through
`ControlService` and documented in `switch2-bluetooth-windows-adapter.md`;
there is no hardware-verification result yet.

The transport-neutral, read-only connection/notification continuation
is specified in `switch2-bluetooth-input-transport.md`. The WinRT continuation
adds active scanning, controller-side association, remembered-this-host
Common05 input, and model-specific output. It does not perform Windows SMP
pairing or claim verified automatic reconnect behavior.

`Switch2BluetoothPeerToken` and `Switch2BluetoothCandidateRegistry` form the
privacy and lifetime boundary between a future Windows BLE watcher and the
existing capture-backed advertisement codec.

## Invariants

- The Windows Bluetooth address is accepted only by the keyed token derivation
  helper. It is not stored in a candidate or returned by the pure boundary.
- A caller supplies a fresh random 32-byte process-private key and a strictly
  increasing scan generation. Tokens are consequently useful only inside one
  scan and cannot be correlated across application runs by this layer. The
  registry verifies the generation embedded in each token; changing only the
  public generation argument cannot revive an old token. `TryEndScan` clears
  registry-owned copies while preserving the monotonic rollback fence.
- The caller remains the sole owner of the session key. It must create it with
  a cryptographic RNG, never log or persist it, zero its buffer at teardown,
  and discard every observation copy when a scan ends. The helper rejects an
  all-zero key and zeroes its raw-address input and full digest scratch buffers,
  but cannot erase the caller's read-only key span or returned token copies. A
  token is a keyed pseudonym, not encryption or anonymity after key disclosure.
  Neither keys nor tokens may be logged or persisted, and token copies may not
  cross the scan-lifetime boundary.
- The registry consumes the token when it issues a connection admission. The
  admission and resulting input lease retain no peer token, Bluetooth address,
  or session key; their exact one-shot identity is the private reservation
  reference plus the scan generation and physical model/product tuple.
- A remembered foreign-host advertisement is classified and ignored. It does
  not consume registry capacity.
- A zero remembered-host field remains an explicit-association candidate. The
  registry does not perform association. It issues a one-shot capability to the
  separate command owner and accepts the expected `None -> ThisHost` transition
  only after that owner records a clean Commit and cleanup. A matching
  transition observed while cleanup is still in progress is held without
  adoption or quarantine; the terminal command result must explicitly commit
  or reject the consumed admission.
- A remembered-this-host field remains a reconnect candidate. Advertisement
  bytes are discovery hints, not authentication or proof of a Windows bond.
- A remembered candidate can have only one active connection admission. While
  that admission is live, matching advertisements remain duplicates. The exact
  concrete input lease may retire its opaque reservation only after handler
  drain, CCCD None, output drain, and every native owner disposal all complete
  without ambiguity. A later matching advertisement can then publish one fresh
  admission in the same scan. A foreign/stale admission, false teardown result,
  identity quarantine, or copied public fields cannot rearm the peer. The
  production coordinator can defer that unconsumed publication until the
  predecessor's exact ControlService slot token disappears; a quarantined or
  uninspectable predecessor instead quarantines the candidate for this scan.
- Wake advertisements remain classified hints and are not promoted to
  connection candidates. Wake and automatic-reconnect behavior require their
  own evidence before a live adapter may act on them.
- Duplicate observations are idempotent. Older observations cannot move a
  candidate backward or quarantine newer identity state. Reusing one token in
  a current-or-newer observation for a different model/product/host quarantines
  it until a new scan generation, except for the exact one-shot post-association
  host promotion above. A failed or ambiguous command never authorizes that
  exception. The QPC value must come from the trusted watcher boundary, not
  advertisement-controlled data.
- A rotated OS address produces a distinct token. The registry deliberately
  does not merge candidates by product ID because two physical Joy-Con 2 halves
  of the same side can legitimately have the same product ID.
- Capacity is fixed at construction (maximum 16). The focused test verifies
  zero managed allocations for steady-state duplicate observations after
  warm-up; contended runtime-monitor implementation costs are not claimed.

## Evidence and provenance

Protocol facts were independently expressed from these pinned local sources:

- `TommyWabg/Switch2Connect@4487322a306f04efa27682e3f3a508635a84fd98`
  (`src/discoverer.py:42,1606-1660` and `src/utils.py:46-47`,
  GPL-3.0-or-later): Nintendo BLE company ID `0x0553`, little-endian VID/PID
  offsets 3/5, minimum 16-byte manufacturer value, and the zero/local/other
  remembered-host classification at bytes 10..15. That source accepts values
  of at least 16 bytes; this project's exact 24-byte and reserved-byte admission
  remains the stricter existing capture-backed codec policy and is not
  attributed to Switch2Connect. No bundled binary or control-flow architecture
  was reused.
- `hifihedgehog/SDL@d98c5804a9d20b0d96e993741797878c86b8f1e1`
  (`src/joystick/windows/SDL_ble_switch2joystick.c:133,1094-1165`, zlib): an
  independent Windows BLE implementation corroborates WinRT manufacturer-data
  enumeration and the company-ID discovery boundary. Its address-based
  identity, connection, and logging design is not adopted here.
- `SDL@c71abd08605b8bb7078372307a93274725c99fe0`
  (`src/joystick/usb_ids.h:130-133`, zlib): independently corroborates physical
  product IDs `0x2066`, `0x2067`, and `0x2069`; `0x2068` is the logical pair
  identity and is not admitted as a physical advertisement.

The keyed token and bounded generation registry are original DS4Windows
lifetime/privacy policy. They are not controller protocol claims.

## Deliberately absent

There is no Windows bond query, controller-memory write, raw-identity logging,
or claim that physical automatic reconnect is hardware-verified. The registry
can reissue only a fresh same-scan admission after an exact clean lease-release
handoff; it does not initiate a connection, retain an address, or act on a wake
hint. The production WinRT continuation keeps the key private, performs
application-level association in a separate owner, and enforces sole one-scan
address capability ownership, but its public Windows watcher cannot be bound to
a selected Bluetooth radio. No command that changes the remembered host exists
in this pure registry type.
