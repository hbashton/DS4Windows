# HidHide client alias crash and DS4Windows safeguard

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Confirmed local cause

The installed HidHide 1.5.230 configuration client failed while populating its
Applications list. Its driver was Running; the official CLI could read policy
and enumerate game controllers. The GUI inserted rows only as far as the
WindowsApps `python.exe` execution alias before showing its generic fatal error.
This alias had been added during our development/testing setup.

The [exact installed-tag application-list code](https://github.com/nefarius/HidHide/blob/v1.5.230.0/HidHideClient/src/WhitelistDlg.cpp#L213)
calls throwing `std::filesystem::exists` immediately after inserting each row.
An independent VS2022 C++17 probe of that alias reproduced `filesystem_error`,
system error **1920**, "The file cannot be accessed by the system."

Local recovery backed up the full configuration, closed only the failed GUI,
and used the official CLI to unregister exactly that alias. All other
application permissions, hidden-device rules, active and inverse settings were
unchanged. The client then opened both Applications and Devices successfully.
No file deletion, driver reinstall/restart or controller-app restart occurred.
This is distinct from a broken driver installation or a DS4 input failure.

## Production source safeguard

- Startup and auto-profile registration reject positively identified Windows
  app-execution aliases instead of registering a launcher stub or resolving it
  into permission for a different executable.
- Native inspection uses read-only metadata with `OPEN_REPARSE_POINT` and the
  exact `IO_REPARSE_TAG_APPEXECLINK` tag. Unsupported namespaces, directories and
  failed reads are not considered safe new registrations. Existing missing,
  ordinary symlink and unrelated application permissions are not pruned.
- The configuration button runs a cold preflight off the UI thread, reports
  actionable failures and prevents repeated concurrent launches. It never
  launches the known-bad list blindly or swallows startup exceptions.
- Exact alias paths are shown for confirmation, default No. Inverse mode blocks
  automatic repair. After confirmation the policy must still match all four
  fields before a durable backup and targeted mutation can proceed.
- The short driver transaction retains one exclusive control handle throughout
  reread, backup, fresh alias checks, mutation and verification. This agrees
  with [HidHide's control-device exclusivity](https://github.com/nefarius/HidHide/blob/v1.5.230.0/HidHide/src/ControlDevice.c).
  No handle is held during the user question or external GUI launch.
- Every repair writes a unique JSON backup under the configured settings
  folder's `Backups/HidHide`. Missing/failed backups prevent mutation; failed
  writes and mismatched readbacks retain the backup and report uncertainty,
  never an unproven success or an automatic destructive retry.
- Strict configuration-only MULTI_SZ reads require successful size/fill calls,
  bounded even lengths and valid termination. They accept the released driver's
  two-byte empty list and stop at the first real terminator before oversized
  padding. No trimming, case folding or entry filtering can erase another
  application's permission. Existing runtime getters are not rewritten.
- Policy text that cannot be backed up and rewritten without replacement
  (including unpaired UTF-16 surrogates in unrelated or stale entries) blocks
  repair before any backup or mutation. Valid surrogate pairs round-trip.

## Validation evidence

The rebuilt focused suite passed **135 HidHide tests, zero failures, zero
skips**, covering native DOS/NT-volume metadata reads, exact alias recognition,
ordinary/missing files, strict list parsing and driver lifetime, state changes
after confirmation, inverse mode, backup failures, alias replacement, uncertain
write/readback results, duplicate entries, exact unrelated-state preservation,
the actual registration/launch source wiring, and lossless policy text.

The first focused run found an assertion left enabled across two different
phases in the new test fixture (transaction requires one handle; later audit
must have zero). The fixture was corrected without weakening production or
changing the required handle-lifetime assertions, then all 127 tests passed.
An independent final review identified the unpaired-surrogate preservation
edge case. The guard was tightened, eight regression cases were added, and
the reviewed focused run passed all 135 tests.

A read-only reflection call into the rebuilt application assembly identified
the actual problematic alias in both DOS and HidHide NT-volume form, with
`Inspected=true`, `IsAppExecutionAlias=true`, error zero. The installed
DS4Windows executable was identified as a non-alias. No application entry was
re-added to the live driver and no live repair was performed by this probe.

The reviewed full Release suite passed **5,826 tests, zero failures**, with
11 opt-in tests skipped (eight live Go/DS4 process interop cases and three live
audio-capture cases), 5,837 total. The earlier pre-review full run also passed
5,818 tests with the same 11 skips. No assertion was disabled to obtain a pass.
Independent review found no remaining blocking defect after the text guard.

Focused/full TRX and actual-path proof are retained in
`isolated_results/hidhide-guard-20260914/`; the final runs are
`hidhide-guard-focused-reviewed.trx` and `hidhide-guard-full-reviewed.trx`.

## Scope and remaining boundaries

These changes are source work after RC4.6; they do not alter its already
published assets, installed copy, or the upstream HidHide binary. An external
tool can still add unsafe entries; opening HidHide through DS4Windows offers
the guarded repair. No catch-all promise is made for unrelated HidHide errors.

Checks run only for application registration and an explicit configuration
launch/repair, not on input or haptic report processing. A final metadata check
does not pin the filesystem path against a hostile concurrent replacement.
Native inspection proves metadata, not PE validity. The safe automatic repair
is deliberately restricted to positively identified app-execution aliases.
