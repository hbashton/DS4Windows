# Xbox One exact-registration removal, version 1

Status: server/API and DS4Windows startup/lifetime integration implemented and
software verified. No post-change hardware or portable artifact is validated
by this document. This is a prerequisite for safe multi-controller
disconnect/reconnect, not completion of that product gate.

## Authority and wire contract

The authenticated `bus/{busId}/add-authorized-xboxone` factory receipt includes
`removalToken`, an independent cryptographically random 32-byte nonce encoded
as exactly 64 lowercase hexadecimal characters. The server binds it once to
the exact production registration's lifetime, not just its numeric address or
device pointer. Tokens are not USB identities and must never appear in device
listings, logs, settings, traces, or saved profiles.

The receipt also contains a public `usbipBusId`: `x1-` followed by 26 lowercase
unpadded base32 characters encoding 16 independently random bytes. The final
character is one of `a/e/i/m/q/u/y/4`; total length is 29 ASCII bytes. This alias
is not derived from the token. It is issued before publication and carried
unchanged through DEVLIST, IMPORT, and every native/CLI attach path. Protected
production registrations do not accept numeric USB/IP import fallback. Numeric
bus/device fields still determine the USB/IP URB transfer ID independently.

The same strict `{version:1,removalToken:...}` payload authenticates
`bus/{busId}/{devId}/stream-authorized-xboxone` and
`bus/{busId}/{devId}/activate-authorized-xboxone`. Stream framing after the NUL
command remains X1BR v1. A duplicate stream cannot replace the sole feedback
consumer. Admission revalidates the captured incarnation before broker
acquisition, ConsumerReady, activation reservation and activation completion.
Short registration leases never span network/native attach I/O or acquisition
of the stream-coordinator mutex.

Activation requires the existing feedback reader and returns a closed response:
`version:1`, the exact `usbipBusId`, positive `usbipPort`, and optional
`usbipOwnerSerial` (empty for the pinned native ABI). The client rejects wrong,
duplicate, case-mismatched or missing identity fields. The port is bookkeeping,
not permission to detach that number later.

Removal uses the authenticated command:

```text
bus/{busId}/{devId}/remove-authorized-xboxone
```

Its closed JSON payload has exactly `version` (integer 1) and `removalToken`.
The closed reply has exactly `version` (integer 1) and `removed` (Boolean).
Unknown, repeated, case-mismatched, missing, or incorrectly typed properties
are rejected. Addresses select a candidate; the nonce must authenticate the
captured registration before mutation. There is no address-only device/bus
removal fallback.

- `removed:true`: the selected registration completed the server's exact
  retained-stream close/join and removal. An active imported persona must
  complete its ordinary DisconnectNeutral/Stop acknowledgement first.
- `removed:false`: this request removed nothing (for example, missing or
  mismatched registration). It proves neither physical neutral nor removal of
  a Windows port, and must not authorize a second cleanup method.
- An uncertain/failed close remains a failure, not a successful acknowledgement.

`XboxOneAuthorizedRegistrationV1` additionally checks expected bus, canonical
16-bit device address, `xboxone` type, VID/PID and dormant creation metadata.
The optional zero `usbipPort`/empty `usbipOwnerSerial` fields can be omitted,
matching Go's factory DTO. `deviceSpecific` is opaque object metadata and does
not grant cleanup authority. The removal capability is not exposed by ordinary
object formatting.

`ViiperClient.RemoveAuthorizedXboxOneRegistration` uses the real management
connection, rejects replies reaching 1,024 bytes, and applies one absolute
post-connect deadline across authentication, request write and response reads.
Connect retains its separate three-second bound. Partial reads cannot extend
the deadline. Remote errors are replaced with a fixed local error, without an
inner exception that could leak an echoed token. There are no automatic retries.

Creation requires `removalTimeoutMilliseconds`, a positive integer at most
300000 advertising `ceil(3 * effective ConnectionTimeout)` in milliseconds.
The CLI default yields 90000; an unset embedded timeout uses 15000. Unsupported
budgets fail before production creation; they are not clamped. The captured
lifetime uses this budget plus 2000 ms for authentication/framing when activating
or removing. Bus/create, device creation and broker startup each retain a
five-second absolute post-connect budget. A timeout never proves cleanup.

The first lifetime disposer issues exact removal. Concurrent disposers join it
before closing the broker needed to acknowledge terminal feedback. Disposal
releases only its own port-lease object, so an old lease cannot erase a newer
one. Failed creation, lost activation replies and late activation after dispose
have no numeric cleanup fallback. Xbox streams cannot reopen in place.

### Client activation cancellation (September 5 source follow-up)

The pending activation management call now belongs to one exact
`ViiperVirtualDeviceLifetime`. Entering disposal cancels that call before the
existing exact removal request, without closing the broker feedback transport.
Connect, authentication, request write and response wait observe the scoped
cancellation. Its absolute deadline remains in force. Canceling management I/O
is not a claim that Windows detached the pad; the retained Stop/ACK and exact
server removal path are still required.

The request's cancellation source is serialized with its own disposal. A
captured retirement callback cannot cancel a released source or a later
request. The lifetime refuses duplicate active requests. The request scope is
created before shared native-mutation admission, so disposal can also cancel
an activation waiting behind another controller. Admission and management I/O
recheck the scoped token before issuing the command. Port binding still requires
the same live lifetime, including when a valid response races with disposal.

`ViiperNativeMutationGate` preserves synchronous, reentrant attach/detach
serialization, including legacy create-to-cleanup nesting. Its short state
monitor is not held across native or network work. A cancellation callback
wakes waiters; the canceled caller throws without acquiring or releasing the
actual owner's lease. The callback registration is joined outside the state
monitor. Canceling an admitted scope does not release that lease prematurely:
the existing operation must finish and dispose it on its acquiring thread.
This is not a fair queue or a hard native completion deadline. No polling,
input-path queue, timer or mapping lock is added.

The real client/loopback regression initially left activation waiting after
exact removal had started. The fix verifies cancellation during authentication
and response wait, management socket closure and attach-lock release while
cleanup is held, and feedback transport retention until the exact reply.
Source-lifetime tests cover cancellation/completion overlap and a stale
request's inability to cancel its successor. A separate real-client regression
holds another controller's admission through exact disposal and verifies that
the waiting activation exits without submitting any command. Gate tests cover
reentrancy, pre-cancellation, unrelated waiters, owner-release/cancellation
races, exception cleanup, wrong-thread disposal and legacy wrapper admission.
No new EOF-as-cancel rule was
added to the broker API: a TCP send half-close alone is not treated as proof
that the client abandoned its response. The server's registration cancellation
and native completion rules remain separately authoritative.

This follow-up is not in live b56 or staged b57. See the dated validation ledger
for test results. No physical controller, driver, or Game Bar behavior is
established by these socket and managed-lifetime regressions.

Xbox aliases, including late/unregistered ports in the `x1-` namespace, are
excluded from legacy global/duplicate/bare-port cleanup. Explicit local attach
and legacy detach share a cold-path mutation lock in this DS4Windows process;
no lock, timer or topology lookup was added to the input hot path. That lock
does not serialize another process or native background retries.

## Why native retirement validation is still gated

The independent audit of usbip-win2 0.9.7.7, source commit
`7c219953101cc5d0ec9a0bcb3eb87259cf72bedd`, found:

- `drivers/ude/wsk_receive.cpp:703` asks for reattachment on receiver exit.
- `drivers/ude/device.cpp:792` schedules it using the saved location;
  `drivers/ude/persistent.cpp:454` starts the delayed attempt after 30 seconds.
- Initial attach `--once` does not suppress this later lifetime behavior.
- Stopping currently queued attempts is not a durable fence against a later
  enqueue; observing unplugged alone does not establish that enqueue finished.

Consequently, socket closure is not permanent Windows-side retirement. The
independent alias now prevents an old saved location selecting a replacement
controller, while the separate token fences API stream/activation/removal.

Pinned native source accepts string bus IDs and requires exact reply equality
(`drivers/ude/vhci_ioctl.cpp:83,121`); numeric transfer IDs are independent
(`:155`). This makes aliases source-supported without driver work,
not a hardware-verified implementation. Aliases prevent selecting successors;
they do not by themselves stop stale retry work or prove immediate port removal.
`persistent.cpp:590` treats ordinary missing-device errors as retryable; do not
emit a false busy condition, malformed reply, or wrong protocol version merely
to force the native client to stop retrying. Retry cancellation needs its own
exact-location lifecycle evidence.

## Verification scope

Client tests cover strict receipt/reply schemas, identity mismatch, token
redaction, actual loopback endpoint/payload, false/error/empty/malformed/oversize
responses, socket reset, absolute deadline under dribbling input, and absence
of numeric fallback/retry. The absolute-deadline tests use loopback sockets,
not a controller or USB/IP driver. See the dated VIIPER validation ledger for
exact suite results and server Stop/close evidence.

Portable Windows validation must exercise removal and numeric-address reuse,
including beyond the 30-second reconnect boundary, while a successor remains
usable. It must also check physical Stop delivery, pending import cleanup,
failed close/quarantine, multiple controllers and restart. None is implied by
the management-contract tests.
