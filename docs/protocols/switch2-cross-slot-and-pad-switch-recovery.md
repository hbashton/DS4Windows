# Cross-slot input isolation and virtual-pad recovery, 2026-09-06

This records bounded b75-b78 source fixes, not controller-platform completion
or a measured end-to-end latency claim. Installed artifacts are unchanged;
testing uses separate Desktop portable runtimes.

## Evidence and cause

The local b74 application log records a Switch 2 Pro being removed immediately
after a joined Joy-Con activates, then the joined controller being removed after
the Pro activates. The private live heap records `QueueOverflow` on the Pro and
both joined input owners; the corresponding disconnect observation is false.
These are not evidence that Bluetooth association evicted another peer.

`Switch2ControlServiceReversibleProfileSlotHost.TryDispatch` acquired the same
global lifecycle gate as another slot's profile preparation and cleanup. Those
cold operations include virtual USB setup/teardown and can take seconds. The
16-report ordered Bluetooth queue can overflow while its worker waits for this
unrelated gate. Increasing the queue would mask the blocking and accumulate lag.

The new two-slot regression holds a different slot's prepare/undo operation
open and requires 256 existing-slot reports to finish before that operation is
released. Both variants failed on the original code and pass after the fix.

## Changes and invariants

- Cold reversible preparation/cleanup remains globally serialized and acquires
  its own slot gate in the order global lifecycle gate -> slot gate.
- Report admission/completion acquires only its own slot gate. The callback
  still executes outside that gate; `DispatchActive` protects its borrowed state.
- Exact generation/lease checks, terminal-neutral acknowledgement, same-slot
  exclusion, cleanup retries and zero-allocation report assertions remain.
- No new input worker, packet dropping, queue expansion or production GC change.

Separately, the legacy VIIPER stale-port sweep inferred startup from an empty
active legacy-port collection. Removing/replacing the sole output therefore
repeated the ten-clean-snapshot startup policy: nine 100 ms waits plus ten
USB/IP process queries, potentially at both removal and creation. A live Xbox
One registration was not included in that policy decision.

Successful process recovery is now remembered under the native mutation gate;
ControlService startup explicitly invalidates it. Ordinary replacement requires
one fresh clean snapshot when recovery or a live output is established. Any
query/recovery failure invalidates the remembered proof. A detached stale port
still requires another query, and exhaustion now fails rather than certifying
an unresolved cleanup. Exact ownership filtering and protection of active,
Xbox One authority-owned, remote and foreign imports are unchanged.

One read-only USB/IP query on this machine took 23.4116 ms. That is an example
query cost, not a distribution or a measured post-fix pad-switch duration. Real
USB enumeration and authenticated retirement still have to finish. The user's
roughly six-second switch time is not fully attributed or declared solved.

## Verification

- Before: two cross-slot regression cases failed as expected.
- After: 59 targeted host/port-recovery cases passed, zero skipped.
- Full final suite: 3,751 passed, zero failed, three opt-in live-audio skipped;
  3,754 total. Includes all eight Go/C# interoperability cases with the pinned
  synthetic test peer, which is not used as the runtime broker.
- Release x64 framework-dependent b75 portable publish succeeded. Existing
  compiler/resource warnings remain; the allocation assertions were not relaxed.

Private evidence is retained locally under the Desktop lab's
`evidence/cross-slot-20260906` directory. Dumps, device identifiers, deployment
credentials and user profiles are not source or release artifacts.

## Acceptance still open

1. Multi-Switch-2 hardware coexistence while changing the other slot's output.
2. Timed Xbox One/Series <-> DualSense switching, including input continuity on
   other controllers; no claim that a USB interval equals end-to-end latency.
3. Separate first-feedback Xbox One rejection/EOF seen before Joy-Con pairing.
4. Automatic standalone Joy-Con activation and safe standalone/joined ownership
   transitions; this patch does not change pairing behavior.
5. Power-off retirement and Game Bar physical-input loss. The Game Bar capture
   found a neutral companion with no canonical reports and a physical HID read
   waiting; it did not establish sender ownership or authorize driver changes.

## b76: expired first-feedback acceptance and failure diagnostics

The b75 broker log records `DS4Windows rejected canonical feedback` immediately
before the Xbox stream closes. The mapper's later disposed-stream message is a
consequence, not the first failure. The exact rejected packet was not captured.

A source-level reproducer found that a newer, valid but expired canonical frame
could pass ingress ordering, change the sink configuration, and fail the forced
presentation refresh solely because there was no fresh frame to render. Both
expired Bluetooth variants failed before the correction. The BT and USB owners
now allow that refresh to succeed without manufacturing a frame and still run
the physical pump, including any pending Stop. Stale sequence, writer identity,
active publication and owner retirement checks remain authoritative. Expired
feedback never actuates a motor; a subsequent fresh frame can resume normally.

Failure-only diagnostics record the canonical command/sequence/age/TTL and
owner phase/pump result before teardown. GUI logging occurs after feedback ACK.
No physical identity, credential, raw input packet or full feedback payload is
included. This diagnostic is observation, not feedback admission authority.

- Targeted feedback/runtime suite: 93 passed, zero failed or skipped.
- Full b76 suite: 3,758 passed, zero failed, three opt-in audio skipped.
- Multiple b76 hardware Xbox output changes did not repeat the first-feedback
  rejection. This supports the fix, but does not establish that the uncaptured
  b75 packet was expired or attribute its age to a specific scheduling delay.

## b77: exact output-slot assignment after concurrent profile changes

The b76 mapper log records a joined output created in slot 3 but associated in
the log with slot 2. A later transition reports no associated output, followed
by exact-binding/ownership rejection on retirement. Both Switch 2 inputs used
the same profile, so editing that profile triggered concurrent output changes.

`PluginOutDevCore` retained a mutable `FindOpenSlot` hint across another input's
cold Connect. `DeferredPlugin` independently selected and committed the actual
slot under its write lock. The caller then used the earlier hint to decide
whether its output existed and the input was ready.

`DeferredPlugin` now returns the exact committed slot (null on failure), and the
caller verifies both its produced output and exact manager/input-array binding
before leaving input-only mode. Failed retirement stops the replacement path;
it must not declare the retained output gone and create a competing device.

- Targeted atomic-slot/profile-ownership tests: 24 passed.
- Full b77 suite: 3,760 passed, zero failed, three opt-in audio skipped.
- Hardware revalidation of joined readiness is still open; the b77 restart
  exposed the separate startup input-admission defect described below.

## b78: prevent orphaned virtual USB/IP HID from becoming mapper input

The b77 startup log found one Bluetooth DualSense and a USB DualSense before
creating that process's own output registrations. A read-only PnP ancestry
walk identified the USB entry beneath `ROOT\\USBIP_WIN2\\UDE`; the subsequent
USB/IP port snapshot identified the same port as this local broker's DualSense
output. It was not a second physical controller. The startup snapshot of the
old import itself was not captured, so its precise driver/PnP removal timing
is not asserted.

The active-output registry and before/after HID path fallback only identified
outputs owned by the current process. An older USB/IP HID could therefore pass
the virtual-input policy during the gap before fresh output ownership existed.
Input discovery now also requires a resolved current import for any known
USB/IP ancestor. A local VIIPER endpoint, missing/ambiguous port, or failed
query is excluded from input discovery. A later pass may admit a resolved
external import under the existing virtual-input policy.

This read-only exclusion is separate from destructive cleanup authority:
protected Xbox aliases and foreign serials on the local broker are not inputs,
but this check never detaches them. Physical USB/Bluetooth and eligible remote
USB/IP sources are not globally disabled. No driver, device or installed
settings changes are part of the fix.

- Targeted port-admission, virtual-input and output-slot tests: 62 passed,
  zero failed or skipped.
- Full b78 suite: 3,772 passed, zero failed, three opt-in audio skipped;
  3,775 total, including the eight pinned Go/C# interoperability cases.
- Release x64 framework-dependent b78 publish succeeded. Its portable restart
  found only the physical Bluetooth DualSense as input, then created one
  virtual DualSense output. The false USB input and its extra Xbox 360 output
  did not recur in this startup. Joined readiness and repeated output changes
  still require separate hardware acceptance.

## b78 follow-up: Bluetooth discovery silence, not accepted as repaired

After the b78 restart the user reported that the Pro and Joy-Con 2 player LEDs
flashed while attempting to connect. The mapper logged discovery startup but
no Switch 2 candidates, opens or admission failures. The coordinator, Windows
adapter, WinRT platform and discovery-boundary code were unchanged from the
earlier checkpoint where the controllers connected.

A separate unfiltered Windows advertisement observer counted every Received
event before Nintendo decoding. Two 45-second runs (extended advertisements
enabled and disabled) received zero events, with Started status and no callback
errors. A further 10-second observation with DS4Windows and VIIPER closed also
received zero events. These observations locate the missing evidence before
mapper candidate admission; they do not prove a particular driver or firmware
caused the failure, and silence after controllers sleep is not a recovery test.

An explicitly scoped restart of the RZ616 Bluetooth adapter remained pending
for more than 15 minutes. A non-invasive native stack inspection found the
PnPUtil main thread in DeviceIoControl beneath
CM_Query_And_Remove_SubTreeW. The adapter still reported ProblemCode zero.
An exact-thread CancelSynchronousIo attempt returned ERROR_NOT_FOUND; the
restart process did not exit during the following bounded wait. Neither a
successful restart nor a completed cancellation is claimed.

No Windows bonds, controller association records, drivers or Program Files
installations were removed or changed. Portable mapper/broker processes were
stopped before the adapter operation. A normal, non-forced computer restart is
the next recovery step under the user's earlier restart authorization. Actual
post-restart advertisement reception and controller readiness remain open;
this evidence must not be represented as a Bluetooth fix or hardware acceptance.

## b79: distinct GIP Hello identities, not just USB serials

The authorized normal reboot was requested at 03:17 local on September 6.
Windows bugchecked during shutdown with DRIVER_POWER_STATE_FAILURE, 0x9F,
parameter 1 = 4 (PnP synchronization timeout). It was not a clean restart.
After boot, the same independent 45-second advertisement observer received
1,361 advertisements, versus zero before reboot. None was a Nintendo candidate;
this establishes restored scan reception, not Switch 2 reconnect acceptance.

Read-only analysis of the local kernel dump found the PnP lock owner waiting
inside Microsoft's dc1-controller release-hardware callback for device rundown
references to drain. Two virtual Xbox USB instances had different USB serials
but had been configured with the same primary GIP Hello DeviceID. The second
device had the driver's identity-collision flag set; the first was stuck in
removal. The collision branch retains a reference to the earlier device without
the matching release. The first removal began at 02:25:58, before the attempted
02:57 Bluetooth-adapter restart and before the reboot request. This is direct
evidence of an Xbox identity collision and a supported explanation for the
blocked PnP teardown. Attribution of every missing BLE advertisement to that
PnP wait remains an inference. No filter driver is blamed merely for appearing
in the device stack, and no kernel code or driver was modified.

The mapper's earlier `derivePerRegistrationSerial` varied only the USB serial;
the GIP ID remained fixed. The new explicit deployment permission
`derivePerRegistrationIdentity: true` allocates a fresh primary GIP ID and
matching serial together. The ID retains the protocol's primary-device prefix.
VID/PID, release/firmware/hardware versions, manufacturer/product, USB intervals,
feedback generations and exact ownership binding are unchanged. The shared
configuration is not mutated. The old serial-only permission is not silently
expanded to authorize a different GIP identity.

VIIPER additionally rejects reuse of a primary GIP ID before publication,
including concurrent and provisional registrations and reuse after bus removal.
USB/IP retirement is not proof that Windows has removed the old native PDO.
Reservations therefore remain for the broker lifetime, bounded at 65,536;
exhaustion fails closed without eviction. This is not a cross-process or
machine-global identity registry. Mapper process seeds reduce cross-process
collision probability but are not a global uniqueness proof. Fixed-identity
deployments must explicitly authorize fresh identities for repeated creation.

Verification before portable hardware testing:

- Mapper: 3,774 passed, zero failed, three opt-in live-audio skipped (3,777
  total), including all eight pinned synthetic Go/C# interoperability cases.
  Allocation assertions were not weakened.
- VIIPER: `go test ./... -count=1` passed all packages; race tests passed for
  USB server, registry, API handlers and Xbox persona.
- New tests cover parallel primary-ID issuance and exhaustion, immutable
  identity/serial coherence, actual two-pad Hello identities, duplicate IDs
  with distinct serials/import leases, unpublished registration collisions,
  atomic concurrent admission, invalid callbacks, and bounded reservations.
- Existing multi-pad and lifecycle fixtures that incorrectly shared a primary
  GIP ID now use distinct IDs and matching serials. Removal-token and stale
  address assertions remain unchanged.

All new work is on the cold creation path. No report queue, worker, interval,
allocation budget, authentication opt-out or feedback translation was changed.
Private dump analysis stays in the Desktop evidence directory; the dump,
device identifiers, profiles and credentials are not committed or uploaded.
Hardware reconnect, power-off, joined readiness and automatic standalone
Joy-Con activation remain separate acceptance items, not implied by these tests.

### b79 Windows virtual-pad acceptance

The personal portable b79 mapper and broker were built from `85aa60e` and
`f39b732`, respectively. An opt-in neutral-only local harness used the actual
published mapper's request factory, authenticated VIIPER client, input ACK
reader and exact retirement capabilities. It did not open physical HID/GATT,
issue vibration or non-neutral input, alter pairing, or detach bare ports.

Windows.Gaming.Input exposed two distinct lab gamepads concurrently. Both
acknowledged 50 neutral input updates. Removing the second left the first
visible and able to acknowledge another 50 updates. Three further one-at-a-time
successors also appeared, accepted neutral inputs and disappeared. Each of the
five instances had a distinct primary GIP ID and serial. The Windows Config
Manager API observed each exact USB instance started with problem zero, then
absent using CM_LOCATE_DEVNODE_NORMAL after exact removal; this did not rely
only on the broker's removal response or an empty USB/IP port.

The five removal-plus-observation samples were 120.8–124.9 ms, using 100 ms
polling. Exact activation acknowledgements were 2,090–2,144 ms; WGI visibility
arrived later. These are isolated lifecycle samples, not controller input
latency, a full profile-switch distribution, or a guarantee of future removal.
The first harness launch failed to resolve its assembly before any controller
creation; the corrected launch used the published mapper's dependency manifest.
The completed Windows run passed. Physical Switch 2 reconnection, its power-off
handling and the standalone/joined transition remain open.

Protocol reference: Microsoft's [Device Hello Enumeration](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/09351525-aa34-4a00-ac36-510fcf2fb106)
identifies DeviceID as unique-instance information, not merely a model number.
The read-only presence check follows [CM_Locate_DevNodeW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew);
it never uses CANCELREMOVE or treats an arbitrary lookup error as absence.

### Residual USB/IP reconnect timers

After those successful removals, the broker log recorded retries of the five
retired test aliases. The pinned usbip-win2 0.9.7.7 source explains why: its
PLUGIN_HARDWARE_ONCE option suppresses retries when the initial attach fails;
it does not suppress the separate delayed reconnect started by device detach
after a transport disconnect (`device.cpp`, `persistent.cpp`). The retired
aliases were rejected and could not select successors, but the retry workers
are unnecessary background work and log noise.

The five test-owned addresses were cleaned with the driver's supported
per-location `attach --stop` command, specifying localhost, the exact USB/IP
service and each captured retired alias. All commands succeeded. No bare port
detach, stop-all operation, stashed configuration, pairing record or driver was
changed. Production automatic cleanup of these delayed retry workers is still
open: an early zero-cancellation result is not proof that asynchronous native
teardown cannot enqueue a later retry. This limitation must not be hidden by
claiming that ONCE disables all reconnection or by weakening ownership checks.

The b79 mapper was then running portably with Bluetooth discovery active. A
fresh independent brief advertisement scan still received ambient BLE packets
with zero callback errors and no Nintendo candidates. Physical reconnection
cannot be certified while the controllers are not observed advertising.
