# Issue #80: renamed executable setup, 2026-09-08

Source changes following [the reported portable-executable failure](https://github.com/hbashton/DS4Windows/issues/80).
Starting DS4Windows checkpoint: `ed0e983`. No real installer, process termination,
Task Scheduler entry, registry mutation, controller operation or app replacement
was performed. File fixtures use small non-executable sentinel data in unique
temporary directories.

## Confirmed mechanisms and repair

- Package staging copied only manifest paths and required `DS4Windows.exe`.
  A hand-renamed executable made the canonical manifest entry absent. Staging
  now substitutes **only that missing root entry** with the exact adjacent
  running installer host previously validated by the elevated entry point.
  Canonical destination/manifest names stay unchanged. The user file is neither
  renamed nor deleted by staging. Existing canonical bytes are not overridden;
  the downstream exact SHA-256 comparison still rejects mismatched packages.
- Source/target path containment, reparse-point rejection, read handles denying
  write/delete sharing, complete offline-package requirements and pinned VIIPER
  hashes remain in force. No executable directory search, DLL fallback or
  missing-payload download was added.
- Infrastructure setup ignored a running renamed parent and later rejected the
  exact renamed installer host solely because of its filename. Candidate
  enumeration now includes only the requested alias at its exact full path
  (plus the pre-existing canonical candidates). Unreadable candidates fail
  closed; same-named aliases elsewhere are not terminated. Standard migration
  requires the exact host PID, expected filename, full path and recognized
  product; portable mode retains its host.
- Settings' alias creator looked for `DS4Windows.exe.runtimeconfig.json`
  instead of the normal `.runtimeconfig.json` sidecar. It could leave an alias
  executable behind before failing. The new helper resolves normal sidecars,
  supports hand-renamed hosts with canonical package sidecars, opens all inputs
  before writing, refuses destination collisions, and writes the executable
  last. Failed attempts clean only files created by that attempt.
- Alias names cannot escape the package directory, target the current/canonical
  executable, or use reserved Windows device names. Settings retain the old
  preference and explain creation failure. Cleanup preserves running aliases
  and files whose bytes no longer match the current executable/sidecars.

## Verification

- Behavior-preserving extraction, then regression run `issue80-before.trx`:
  **3 failed, 1 passed** (renamed staging, normal sidecar creation, and incomplete
  alias cleanup reproduced).
- `issue80-expanded.trx`: **24 passed, 0 failed**. Includes canonical/renamed
  hosts, mismatches, missing DLLs, outside roots, unsafe/duplicate manifest
  entries, filename rejection, collisions, sidecars and ownership cleanup.
- Extracted production PowerShell process functions: initial **2 failed,
  4 passed** (renamed parent and host). Final **8 passed** under both Windows
  PowerShell 5.1 and PowerShell 7, including unreadable aliases and unrecognized
  host products. All process enumeration, termination, delay and logging calls
  are mocked; the installer entry point never runs.
- Existing installer state-machine, startup-task registration and localization
  package regressions also pass. The renamed-process fixture is now in CI.
- Independent source review found no introduced blocker in exact-host staging,
  manifest/path validation, alias preservation or process selection.

## Acceptance limits

This does not claim a live install/migrate/reboot/uninstall test. A disposable
installer environment should exercise both a manually renamed executable and a
Settings-created alias in Portable and Standard modes before release.

Standard migration still removes only source-manifest-owned files. An unlisted
hand-renamed alias is preserved rather than guessed safe to delete, and may no
longer run after its old package dependencies are removed. Similarly, updating
DS4Windows can make a previous alias differ from the new package; cleanup then
preserves and logs it. No claim is made that every old alias is removed.

VIIPER remains an elevated installed component in the existing Portable setup
workflow; this fix does not redefine that installation choice or modify the
separate portable-lab runtime.
