# RC4.5 Stop/removal regression — 2026-09-09

## Report and scope

A Reddit user reports that clicking **Stop** after upgrading to RC4.5 logs
`disconnected due to read failure: 995`, removes the controller from the app,
and leaves Stop disabled until the app restarts. The report does not identify
the controller model or include a thread dump. This investigation reproduces
a matching source-level deadlock, not the reporter's complete hardware session.

No running application, driver, controller connection, or installed files were
changed during this fix. Existing release tags and binaries remain unchanged.

## Confirmed regression

RC4.4 already serialized Stop with `serviceLifecycleLock`. The RC4.4-to-RC4.5
diff adds acquisition of that same lock in `On_DS4Removal`:

1. Stop holds the service lock and waits for physical workers to finish.
2. A physical worker synchronously raises Removal before it finishes.
3. Removal waits for the service lock. Neither side can finish.

DualSense uses the fallback lifecycle because its composite physical workers
are explicitly excluded from the typed legacy-HID authority. Exact DS4 devices
use the typed path, whose removal callback is queued and detached before worker
retirement; the tests distinguish these routes rather than treating a fake
derived DS4 device as an exact production DS4.

The UI already restores the Start/Stop button in a `finally` after awaiting
the service task. Re-enabling the button without completing Stop would not fix
the worker deadlock or establish safe ownership of the controller.

Windows defines 995 as `ERROR_OPERATION_ABORTED`. Public `StopUpdate` requests
read cancellation before joining. Previously DS4 and DualSense interpreted
that intentional cancellation as a new physical failure/removal. Unexpected
995 remains an error; it is not globally filtered out.

## Fix

- Fallback removal claims the exact source and revokes mouse callbacks
  immediately, then queues serialized cleanup on a non-controller worker.
  The existing reentrant mouse-callback branch still revokes and retains its
  exact slot for cold retry; that separate retirement contract is unchanged.
- Physical callbacks no longer synchronously remove from the HID registry.
  Registry retirement follows successful exact-slot cleanup in the same cold
  transaction, avoiding the registry-lock/worker-join cycle as well.
- Hotplug publication rejects a source already marked removing or removed,
  even if its queued cleanup has not acquired the service lock yet.
- Registry deletion checks exact connection identity, so delayed old removal
  cannot erase a same-path or same-MAC successor.
- Read cancellation is recognized only when this device's public stop path
  requested cancellation, the read returned ReadError/995, and input stop is
  still requested. Normal loop cleanup remains in place. Genuine failures
  retain diagnostics and removal handling.
- Public worker joins reject joining the calling worker itself.

## Verification

Before the fix, four bounded tests of the actual Stop/StopAndShutDown entry
points failed at the intended callback-return assertion, covering Bluetooth
disconnect-at-Stop both enabled and disabled. Each recorded that Stop held
the service lock during its worker wait. The fake worker waits only 200 ms,
then unwinds cleanly instead of hanging the test runner. The exact-device
route and typed-retirement controls both passed.

Restoring the original public cancellation/self-join behavior for a separate
bounded comparison produced seven failures and five passing controls. The
failures covered four DS4/DualSense USB/BT public-Stop cancellation cases, two
input self-joins, and the output self-interrupt path. These tests use the real
public Stop method and a synthetic pending-read backend; they verify the shared
cancellation classification, not complete native HID read loops on hardware.
The final fixture also awaits the actual queued retirement Task before cleanup.

Before the registry guard, all four delayed-replacement cases failed while
ordinary exact-device removal passed. Fixtures cover a same-path/same-MAC
successor with either a shared or distinct HID object, as well as same-MAC
different-path and same-path different-MAC replacements. No native handles are
used; fixture-owned registry state is restored after each test.

After the fix, the combined focused run passed **152/152**, with no failures or
skips. This includes 29 new cases (10 service-removal, 14 cancellation, and five
registry-identity cases), plus existing mouse-callback lifetime, hotplug,
Switch Pro calibration, typed-HID lifecycle, and DualSense pipeline/isolation
coverage.

The unfiltered Release/x64 full suite passed **4,915**, with **zero failures**
and the same **11 opt-in skips** (4,926 total). Allocation assertions were not
relaxed or filtered out. `git diff --check` passed.

Commands:

```powershell
dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-build --logger 'trx;LogFileName=stop-removal-full.trx' --results-directory isolated_results/stop-removal-regression/after --verbosity minimal
```

Local evidence: `isolated_results/stop-removal-regression/before/` (service
failing-first), `isolated_results/stop-removal-cancellation/stop-removal-combined-green.trx`
(focused), and `isolated_results/stop-removal-regression/after/stop-removal-full.trx`
(unfiltered full suite). These ignored test artifacts are not release payloads.

Physical confirmation by the reporter and delivery in a new build remain
separate from source-level test coverage. No release was published in this pass.

Reference: [Microsoft system error codes, including 995](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--500-999-).
