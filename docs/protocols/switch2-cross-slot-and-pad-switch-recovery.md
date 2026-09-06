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
- Release x64 framework-dependent b78 publish succeeded. Portable hardware
  acceptance remains pending at this checkpoint.
