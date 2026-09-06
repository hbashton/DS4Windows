# Switch 2 Pro USB hardware verifier

This is a standalone lab utility for one narrow, explicitly
authorized mechanism check. It is not referenced by DS4Windows, is not part of
controller discovery or startup, and has no registry, API, profile, or CLI
integration. Building or testing the project does not run it. Its hardware
entry point has been exercised only in separately authorized runs documented
below.

Do not run it merely because it builds. Every hardware run requires separate,
explicit authorization and a review of this fixed procedure against the exact
connected controller and driver state. The admitted run precondition is that
the controller is not unplugged, replaced, reset, or re-enumerated until the
procedure exits. Windows identity and topology are revalidated exactly when
observed, but Windows exposes no physical-generation token that this utility
can honestly claim.

The current source has a fixed, closed live-haptic safety gate. A run can
perform the battery, player-LED, and passive input-rate phases, but it then
returns `HapticMutationSafetyGateClosed` without sending any nonzero or zero
haptic report. This is deliberate: closing a process handle does not prove
physical zero amplitude after a kernel write that never returns, and neither a
documented device watchdog nor independent physical stop measurement is yet
admitted. Consequently, the current utility cannot return overall success.

The only admitted target is one Nintendo Switch 2 Pro Controller enumerating
as `057E:2069`, `bcdDevice 0x0201`. The default schema-4 procedure accepts an
optional new `.json` result file:

```powershell
Switch2UsbHardwareVerify.exe
Switch2UsbHardwareVerify.exe --output .\switch2-usb-result.json
```

An output file is created with `CreateNew`; an existing file is never
overwritten. A separate, explicit evidence mode requires a new local file:

```powershell
Switch2UsbHardwareVerify.exe --capture-startup-evidence --output .\switch2-usb-startup-private.json
```

There is no raw-command, report, identity, count, amplitude, frequency,
duration, pipe, or timeout argument in either mode.

## Private startup evidence mode

`--capture-startup-evidence` is a narrow extension of the same verifier and
same `WinUsbCommandChannel`; it is not a second transport or mapping stack. It
does not change the default schema-4 procedure. It exists only to collect the
source-justified evidence required by the dormant startup transaction. An
authorized Desktop-portable run was completed on 2026-08-31 as recorded below.

The mode uses one read-only MI_00 handle and one exclusive MI_01 WinUSB handle.
On that same MI_01 lifetime it performs this fixed single-flight order:

1. `03:03` enable USB HID reports;
2. `0C:02` set the exact `0x27` feature mask;
3. `0C:04` enable that exact `0x27` feature mask; and
4. `03:0A` select common input report `0x05`.

Every ordinal is one host-side `FlushPipe(IN 0x82)` -> exact single write to
`OUT 0x02` -> one bounded `IN 0x82` read. Initialization and feature responses
are checked by the production codec. For the pinned `057E:2069`, bcdDevice
`0x0201` target, feature responses must be exactly 12 bytes and match the exact
request step; the codec accepts no variable-length or arbitrary response. A
successful result serializes `ExistingValidatorAccepted`. The protocol still
provides no transaction identifier, so the result is bounded host transaction
evidence rather than a general causal-attribution mechanism. Captured bytes are
never copied into validator constants automatically.

Only after all four host transfers does the mode perform the existing 1,024
report warm-up and 256-report aggregate Common05 cadence/counter capture. It
then sends the existing Player-1 command. Cleanup is armed before that command
can enter native I/O and `finally` attempts the existing exact Player LED
`AllOff` command, using a fresh exclusive command channel only after bounded
release if the first channel was faulted. The result never contains raw input
reports or individual counters.

Nonzero haptics remain source-closed and the evidence mode never opens a HID
output handle. It sends no nonzero or zero-amplitude haptic report. It also has
no command form for a host address, pairing, association, memory, calibration,
firmware, driver, or persistent-storage operation. The `0x27` commands do
change controller session configuration, however, and no source-backed inverse
or restoration command is established. That configuration may remain for the
current connection until another owner reconfigures it or the controller is
disconnected; only the Player LED has explicit `AllOff` cleanup.

Discovery, identity revalidation, channel open, every command, input capture,
LED cleanup, and disposal have fixed caller-side admission/ownership budgets,
plus a fixed 30-second whole interaction budget. A timed-out synchronous native
operation transfers its exact resource to one late disposer and no replacement
is admitted concurrently. Win32 discovery/open/cancel/free/close calls do not
offer a hard native wall-clock guarantee, so the schema says exactly that; it
does not describe those calls themselves as forcibly preemptible.

The evidence file is a distinct closed schema. It rejects known host device
identifiers, native exception text, unknown fields, duplicate fields,
noncanonical hexadecimal, changed bounds, changed warning text, semantic-ACK
claims, production-proof claims, haptic attempts, and any flag allowing
automatic commit/share. The closed schema independently revalidates every
feature response through the production codec and rejects arbitrary bytes.
This is still a local laboratory artifact rather than a production proof; the
utility neither commits nor publishes it automatically.

## Default schema-4 fixed procedure

1. Validate the source-fixed plan and open the optional result destination.
2. Enumerate HID device interfaces without write access. Require exactly one
   `MI_00` whose service is `HidUsb` and whose zero-access HID attributes are
   exactly VID `057E`, PID `2069`, version `0201`. Its preparsed caps must be
   usage page `0x01`, usage `0x05`, 64-byte input/output, and zero-byte feature.
   Require its immediate same-container USB parent to carry the exact
   `VID_057E&PID_2069&MI_00` identity and `HidUsb` service. Capture the
   collection device node, instance identity, container ID, interface path,
   and exact in-run session-identity snapshot privately. If an instance ID
   itself claims the exact target VID/PID/MI_00 identity, an unreadable,
   missing, malformed, or inconsistent hardware-ID property is fatal rather
   than silently skipped while establishing uniqueness.
3. Enumerate present device nodes and require exactly one same-container
   `MI_01` with service `WinUSB`. Open that node's global device registry key
   read-only and parse the documented singular `DeviceInterfaceGUID`
   (`REG_SZ`) and/or plural `DeviceInterfaceGUIDs` (`REG_MULTI_SZ`) forms
   strictly and within a fixed bound. Dynamically enumerate only active,
   non-removed instances of those interface classes and require exactly one
   path matching the same instance and container. Failure to open or completely
   enumerate any listed interface class is fatal; partial enumeration can never
   establish uniqueness.
4. Re-enumerate and compare the complete private snapshot, then open MI_00 for
   read access while advertising read/write sharing. The verifier still has no
   HID write access; this compatibility policy permits passive capture beside
   an existing writer and deliberately does not establish the production
   sole-writer lease. Recheck HID caps. Before any command interface is opened,
   discard 1,024
   exact 64-byte
   report-`05` warm-up records and require the final eight intervals to pass a
   fixed 0.5 ms live-tail heuristic floor. This bounds a common backlog case;
   it does not prove the host queue is empty. Then passively time 256 exact
   records. Validate forward modular movement of the Common05 little-endian
   `uint32` counter at USB bytes 1 through 4. The JSON contains
   only aggregate host-completion cadence and counter-delta summaries. The
   entire 1,024-plus-256-report phase has a fixed 15-second deadline in
   addition to the one-second per-read limit; expiration has its own failure
   code and enters cleanup. User cancellation remains a distinct result. If a
   read task ignores cancellation, an atomic ownership handoff removes that HID
   channel from Main; only its late observer may dispose it after the read ends.
   No output interface exists during this passive phase.
5. Revalidate the same private session identity, retain the shared read-only
   MI_00 lease, and open MI_01 with no sharing. Require WinUSB interface 1,
   active/default alternate setting 0, and exactly two 64-byte bulk endpoints:
   OUT `0x02` and IN `0x82`; the utility never changes alternates. Set fixed
   transfer timeouts, disable partial reads, and read that policy back exactly.
6. Commands are single-flight. Before each request, synchronously flush only
   the WinUSB IN host cache; there is no speculative pre-read or endpoint reset.
   Read into a buffer sized to the exact expected 8- or 12-byte response. An
   ambiguous transfer or malformed response poisons the session; cleanup may
   reopen only after bounded disposal and exact identity/topology revalidation.
   This does not create a transaction identifier or physical-generation token.
   Every complete command operation also has a fixed 1,250 ms ownership
   deadline covering its flush, transfer, validation, recovery, and any
   in-transaction revalidation it requests.
   If it does not return, Main abandons the command channel to exactly one late
   observer/disposer and never races that owner with cleanup, replacement, or
   final disposal.
   First send the capture-backed volatile `03:03` enable-USB-HID request and
   `03:0A` select-common-report-`05` request, requiring their exact USB ACKs.
   Neither request carries a host address, pairing material, feature mask, or
   persistent data. The codec cannot encode the host-address `03:0D` form.
7. Send the linked codec's exact battery-voltage `0B:03` request and require an
   exact 12-byte response. Then send Player LED 1 and require an exact eight-byte
   response. The original `10/78` and initialized-hardware `00/F8` response
   styles are admitted only as indivisible, observed pairs; mixed pairs fail.
   A successful battery, Player-1, or `AllOff` result serializes which closed
   pair matched; a boolean alone is not used as evidence of the style.
   Neither byte pair is promoted to a semantic status. The admitted protocol
   still has no transaction identifier with which to prove causal attribution.
8. Reach the fixed live-haptic safety gate and fail closed. The current build
   emits no haptic report. The dormant, offline-tested future path defines 14
   fixed Pro HD-rumble reports at 12 ms cadence, mirrored control codes `0x187`
   and `0x112`, and amplitude code 64, below SDL's packed-code clamp of 453.
   That path also has a hard 250 ms ownership deadline, but cannot be enabled
   merely because its host-side deadline works: physical stop after a
   noncooperative write still requires separate evidence.
9. In `finally`, first give Player LED `AllOff` its own bounded cleanup arm and
   require the exact captured response shape and tuple. `AllOff` is explicit
   neutralization, not restoration of a prior LED state. Only after that arm
   finishes does an independent haptic cleanup arm begin. With the current
   safety gate, no nonzero report was attempted, so the arm records zero stop
   attempts and sends no zero-amplitude report. The dormant future path would
   reserve a fresh, never-reused 4-bit value and send a canonical zero-amplitude
   report using the same control codes, retrying once. A failed/timeout HID
   write poisons that handle. A replacement output writer is never opened
   unless bounded disposal of the old writer completed; an output handle that
   arrives after a bounded reopen timeout is never admitted and is disposed.
   Identity revalidation, disposal, and reopen waits are bounded and incomplete
   cleanup is reported as failure. Each cleanup arm also has a hard task
   boundary if a callback or native operation ignores cancellation. Such an
   arm retains ownership of its channel, so the procedure never races it with
   a replacement or final dispose. Final disposal is attempted only for a
   channel whose cleanup arm returned ownership, using an independent budget.
   `AllOff` itself has the command ownership deadline above. If a possibly
   delivered Player-1 mutation loses command ownership, neutralization is
   explicitly reported as blocked and unconfirmed; no replacement is opened
   while the late owner exists.
   A late release that has been scheduled but cannot be confirmed before result
   serialization is reported separately from a confirmed disposal failure.
10. Dispose all retained channels within fixed disposal budgets and write one
    canonical closed-schema JSON result. A timed-out disposal is recorded as an
    unconfirmed late release, separately from a completed disposal that failed.
    While the haptic safety gate is closed, exit success is intentionally
    impossible and
    the result separately records LED attempts/outcomes, blocked nonzero
    haptics, and the count of any zero-amplitude writes (currently zero).

Every input HID report must transfer exactly 64 bytes with report ID `0x05`.
The dormant haptic path requires 64-byte report ID `0x02`. Every WinUSB command and response must
have the exact length admitted by `Switch2UsbCommandCodec`. Identity is
revalidated around phases and transactions. Ctrl+C and fixed per-operation
timeouts enter the same cleanup path. The reported 250 ms haptic maximum is
the nonzero emission window, not proof of time-to-physical-neutral; LED
cleanup is deliberately attempted before the HID cleanup arm.

## Safety and privacy boundaries

- A current manual execution changes volatile player-LED state but cannot send
  haptics. Close DS4Windows, SDL applications, and other MI_01 command owners
  first. The read-only MI_00 lease is share-compatible and is not evidence of
  sole physical output-writer ownership.
- Disconnect, driver failure, or an unresponsive controller can prevent
  neutralization. The JSON reports that limitation and never claims physical
  output is off merely because a cleanup was attempted or a HID write completed.
  A noncooperative abandoned operation is observed and its sole late owner
  attempts disposal after completion; if it never completes, process exit is
  the final OS handle-release boundary.
- Host read-completion cadence is not interrupt rate, transport latency,
  input-to-output latency, or proof of 500 Hz. Common05 `+4` is raw forward
  counter movement, not four lost packets.
- The default schema-4 experiment performs only capture-backed volatile USB-HID enable and
  common-report selection. It does not send host-address USB initialisation,
  pair, associate, select features, read memory/calibration, query firmware,
  change a driver, or persist controller state. It does not query serial
  numbers or MAC addresses.
- The explicit private evidence mode additionally sends the closed `0C:02/27`
  and `0C:04/27` session-feature requests described above. It does not claim
  they are restored on exit. Their exact allowlisted responses contain only the
  command, direction, step, fixed header/reserved bytes, and the zero payload
  admitted by the production codec; arbitrary feature bytes fail the schema.
- Device paths, instance IDs, container IDs, and similar private identifiers
  remain only in memory. They are neither serialized nor hashed. Raw input
  reports are never serialized. Errors are fixed codes, not native exception
  text. The source-generated schema is the primary privacy boundary. Before
  output, JSON must deserialize into that exact required tree, pass closed
  type/range/state checks with no duplicate properties, and byte-for-byte match
  its source-generated canonical reserialization. As additional defense in
  depth, every serialized string must also be on a fixed allowlist; unrecognized
  and common path, device-ID, or MAC-shaped values are rejected.
  This narrow check is not represented as a general PII detector.
- Oscillator physical naming, perceived haptic strength, accepted watchdog
  behavior, and broad firmware compatibility remain unknown. A successful run
  would verify only this fixed controller revision and procedure.

## Source boundary

The executable links the existing
`Switch2UsbCommandCodec.cs` and `Switch2HdRumbleCodec.cs` source files; it does
not duplicate or generalize their wire formats. Their evidence ledger and
unknowns are in [`switch2-phase1-codec.md`](../../docs/protocols/switch2-phase1-codec.md).
The haptic layout/cadence is independently expressed from the pinned,
zlib-licensed [SDL Switch 2 implementation](https://github.com/libsdl-org/SDL/blob/c71abd08605b8bb7078372307a93274725c99fe0/src/joystick/hidapi/SDL_hidapi_switch2.c#L1031-L1110).

The private evidence mode is constrained by the already-audited local pins:
Switch2Connect `4487322a306f04efa27682e3f3a508635a84fd98`
(`src/usb_hid_controller.py:130-188`) and SDL
`c71abd08605b8bb7078372307a93274725c99fe0`
(`src/joystick/hidapi/SDL_hidapi_switch2.c:392-429,498-504`). They agree on
the `0C:02/27`, `0C:04/27`, and `03:0A/05` request bytes/order. SDL sends each
request and performs a packet-sized receive but does not validate a feature
response tuple. An authorized 2026-08-31 hardware run supplied the missing
exact tuple evidence for the one pinned target revision; that evidence is now
implemented as a closed, step-specific `Switch2UsbCommandCodec` validator and
is not generalized to another firmware or transport.

Windows discovery and recovery use the documented
[`SetupDiOpenDevRegKey`](https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdiopendevregkey),
[`RegQueryValueExW`](https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regqueryvalueexw),
[`HidD_GetPreparsedData`](https://learn.microsoft.com/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getpreparseddata),
[`HidP_GetCaps`](https://learn.microsoft.com/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getcaps),
[`WinUsb_GetCurrentAlternateSetting`](https://learn.microsoft.com/windows/win32/api/winusb/nf-winusb-winusb_getcurrentalternatesetting),
the [WinUSB pipe-policy contract](https://learn.microsoft.com/windows-hardware/drivers/usbcon/winusb-functions-for-pipe-policy-modification),
and [`WinUsb_FlushPipe`](https://learn.microsoft.com/windows/win32/api/winusb/nf-winusb-winusb_flushpipe).
`WinUsb_FlushPipe` drains cached host-pipe data before a request. The utility
does not probe or reset the endpoint, and flushing cannot add a transaction
identifier to the protocol or prove causal response attribution.

## Offline verification

The pure test project checks the fixed plan, exact haptic bytes, fresh sequence
reservation after ambiguous writes, poisoned-session reopen decisions,
strict pipe topology, synthetic test-only identity matching, active-interface
flags, strict registry `REG_SZ`/`REG_MULTI_SZ` parsing (including wrong type,
missing terminator, duplicates, and multiple GUIDs), injected SetupDi
open/enumeration failures, exact volatile-startup vectors, dual observed
response-style pairing with mixed-style rejection, stale response-tuple
rejection, session-identity
mutation rejection, LED-before-haptic cleanup order, independent budgets,
old-writer release gating, late-open disposal, hung post-mutation revalidation
yield to cleanup, preservation of unconfirmed LED delivery ambiguity,
noncooperative cleanup-arm boundaries and retained ownership,
whole-input-phase timeout versus user cancellation, fail-closed target
hardware-ID reads, atomic completion-versus-abandonment races, hard command
and nonzero haptic deadlines and late-owner-only disposal,
Common05 `uint32` movement/wrap,
host-completion timing math, closed CLI, exact canonical JSON shape/types/
ranges, and property/value privacy. It never
calls the program entry point or any native discovery/I/O function.

The reviewed offline command is:

```powershell
dotnet test .\utils\Switch2UsbHardwareVerify.Tests\Switch2UsbHardwareVerify.Tests.csproj -c Release -p:Platform=x64 --nologo
```

The utility source passes 75 focused tests. This count includes strict
assembly-digest validation, canonical schema rejection, noncooperative command
ownership handoff, disposal timeout classification, and the rule that
incomplete cleanup is the top-level failure while the original procedure
failure remains separately recorded. It also covers the exact four-step
evidence order and bytes, exact step-specific feature response validation,
typed acquisition failure provenance, cleanup arming before Player-1 I/O, bounded late
owners, the private-artifact warning, immutable policy/bounds, no automatic
commit/share, and failure evidence with blocked `AllOff` cleanup.
Every serialized enum domain is also checked against its defined values, and
the outer failure code is mechanically derived from the original procedure
failure plus the exact cleanup flags used by the runtime.

Only after separate authorization, the exact built executable invocation is
one of these:

```powershell
.\utils\Switch2UsbHardwareVerify\bin\x64\Release\net8.0-windows10.0.19041.0\Switch2UsbHardwareVerify.exe
.\utils\Switch2UsbHardwareVerify\bin\x64\Release\net8.0-windows10.0.19041.0\Switch2UsbHardwareVerify.exe --output .\switch2-usb-result.json
.\utils\Switch2UsbHardwareVerify\bin\x64\Release\net8.0-windows10.0.19041.0\Switch2UsbHardwareVerify.exe --capture-startup-evidence --output .\switch2-usb-startup-private.json
```

The third form was run only from the authorized Desktop portable lab on
2026-08-31. The validated local result has SHA-256
`9FECCE4A9A53BFEFD4AD42967BD1606462162FD958439E5980C615C7F94F9824`.
It is not committed automatically and remains hardware evidence for the pinned
procedure, not a production deployment artifact.

## Authorized startup evidence, 2026-08-31

The source-built verifier was published and run only from
`C:\Users\hbash\Desktop\Controller-Platform-Portable-Lab-2026-08-31`; installed
Program Files copies of DS4Windows and VIIPER were not changed. The validated
local artifact is `switch2-usb-startup-validated-v2-2026-08-31.json`, SHA-256
`9FECCE4A9A53BFEFD4AD42967BD1606462162FD958439E5980C615C7F94F9824`.

All six exact validators accepted: `03:03`, `0C:02/27`, `0C:04/27`, `03:0A/05`,
Player LED 1, and Player LED `AllOff`. The feature responses were exactly
`0C01000200F8000000000000` and `0C01000400F8000000000000`. The input phase
recorded 256/256 exact Common05 reports at 250.0002696 host completions/second,
3.9999957 ms mean, 4.0175 ms p95, and 4.0457 ms p99, with every one of the 255
forward counter movements exactly `+4`. These are host completion intervals,
not calibrated latency or proof of a 500 Hz controller mode.

The Player-1 mutation was followed by an exactly validated `AllOff` cleanup.
The verifier opened no HID output handle and attempted zero haptic writes. Its
read-only, share-compatible MI_00 handle does not prove the production
full-duplex input/sole-output-writer lease.

## Earlier authorized hardware evidence, 2026-08-29

The user authorized iterative, bounded runs against the one exact connected
target. A composite-device restart did not change the original battery-command
timeout. Adding only the two capture-backed volatile startup transactions did:
both returned their exact USB ACKs, after which battery responses arrived
immediately. This is evidence for the required session state transition, not a
general initialization sequence. No host-address, feature-mask, memory,
firmware, pairing, association, or driver command was sent.

### Historical mechanism evidence; not writer-isolated

The privacy-safe, out-of-tree lab artifact named
`switch2-usb-hardware-2026-08-29-led-cleanup-confirmed.json` (not committed or
linked from this repository),
SHA-256
`3C973515DC6EC3C16EE97C41938462B104443B7C144D36E8174918242AEC4548`,
was generated by an earlier build that opened MI_00 with both read and write
sharing. It is historical mechanism evidence only. It does not prove sole HID
writer admission, does not identify the process that might have held another
handle, and is not evidence for the current fixed procedure. It records:

- both volatile startup operations attempted and exactly acknowledged;
- a 3533 mV battery response with exact admitted shape and tuple;
- 256/256 exact common-`05` input reports at 249.999730392448 host
  completions/second, 4.00000431372549 ms mean, 4.02 ms p95, and 4.1028 ms
  p99, with all 255 forward counter movements equal to `+4`;
- Player LED 1 exactly acknowledged, followed by an independently bounded,
  exactly acknowledged `AllOff` cleanup; and
- `HapticMutationSafetyGateClosed` before any haptic write. Nonzero attempts,
  zero-amplitude attempts, and completed haptic writes are all zero.

The process exits nonzero by design because the haptic gate remains closed.
The input values are host read-completion cadence for this run, not a 500 Hz
claim and not calibrated input latency.

### Prior sole-writer admission evidence

The prior schema-3 procedure was
`fixed-switch2-pro-usb-bcd0201-sole-writer-v1`. Every result carries the
SHA-256 of the executing verifier assembly and explicit required/succeeded
writer-admission fields. Its out-of-tree lab artifact is named
`switch2-usb-hardware-2026-08-29-sole-writer-v1.json` (not committed or linked
from this repository),
SHA-256
`1E075EBF7B17F0DF5030F4A229A86B6919ECC8B47803D0E4FD7991A1E8DC22D1`.
It binds to verifier-assembly SHA-256
`59B03199522C2905D6A518FD5AC6C17196D31C9C68719AF8A760CD481D2B9DC4`.

That prior-binary run failed closed while acquiring the MI_00 lease with Win32
error 32. It proves that the schema-3 procedure could not establish its required
sole-writer admission in the observed handle state. Error 32 does not identify
the owner or distinguish an existing writer from an incompatibly shared
reader. No input read, WinUSB command, LED operation, or haptic write followed.
There is therefore no successful writer-isolated hardware result. The current
source is schema 4 procedure
`fixed-switch2-pro-usb-bcd0201-sole-writer-v2`, adding command-channel ownership
handoff, exact response-style evidence, bounded input disposal, and canonical
schema validation.

The user-authorized schema-4 run produced the out-of-tree artifact
`switch2-usb-hardware-2026-08-29-schema4-sole-writer-v2.json`, SHA-256
`0DFC1F5240FF3BBBFD9DD8CC0F8689039981EEEA3EF9856E9E626A099E7F8F44`.
It binds to verifier-assembly SHA-256
`87D2A053DD6FF7A447D71E27B13D2715B188547E8A33E3816EF95164184F4A4E`
(launcher executable SHA-256
`B68998F98B5FA3A95E9050A0F055456169E16E6B0D678F29C1D4A2044786AA2C`).
The exact `057E:2069` composite was present, but the shared read-only MI_00
lease failed with `HidReadOpenFailed`, Win32 error 32. That proves only an
incompatible existing handle state; it does not identify an owner. The run
performed no input read, command-interface open, volatile initialization,
battery query, LED mutation, haptic write, or cleanup mutation. The historical
observations above retain their narrower scope and are not promoted to
schema-4 writer-isolated evidence.
