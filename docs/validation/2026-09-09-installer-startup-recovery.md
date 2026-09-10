# RC4.5.5 installer and startup recovery

## Reproduced cause

The reported RC4.5.3 setup failed before USB/IP installation. Its infrastructure
log refused mutation of an unmarked root `RunVIIPER` task pointing to an older
portable candidate. The candidate and installed broker both matched the pinned
VIIPER binary. This was not evidence of a broken USB/IP driver.

The installed runtime had two conflicting task-maintenance behaviors:

1. It selected the persisted portable runtime preference as the installed logon
   task's target.
2. Its task writer deleted the old registration and omitted the ownership marker.
   Its validator required High priority, while installer XML omitted priority
   and therefore produced a different setting. Ordinary startup could consequently
   replace a valid installer-owned task and strip its marker.

The previous repair used narrowly verified paths, task shape and broker hashes,
backed up the task and profile configuration, corrected the exact stale preference,
and completed the user's approved installer Retry. Logs confirmed the exact
USB/IP 0.9.7.7 ABI/driver checks, VIIPER readiness and infrastructure exit code 0.
The old runtime subsequently stripped the marker again, corroborating the
recurrence path. That repair was not testing the new RC4.5.5 installer.

## Permanent correction and safety boundaries

- Installed startup maintenance now uses the canonical installed backend. A
  verified portable runtime preference remains usable for its interactive session;
  portable sessions do not maintain installed startup tasks.
- The runtime task writer preserves the managed marker and uses in-place Update
  for an existing owned task, or Create-only when absent. It checks exact current
  account, action, arguments and trigger shape before mutation. All unmarked tasks,
  including canonical and LocalAppData paths, are left for installer migration.
- The existing single-broker process-ownership checks remain mandatory before
  direct or task-based launch; an already selected broker is probed, not duplicated.
- Installer migration retains the existing narrowly recognized canonical legacy
  contract. Noncanonical VIIPER paths additionally require a finite known packaged
  SHA-256 identity, exact basename, absolute path and no file/ancestor reparse point.
  This path is checked, not executed. Arbitrary same-name tasks remain foreign.
- Accepted unmarked registrations are exported before mutation into
  `ProgramData/DS4Windows/Installer/task-backups`. Exclusive creation, Unicode
  preservation, durable flushing and exact duplicate-content checks prevent an
  interrupted/repeated recovery from silently overwriting its original backup.
  Unsafe paths, reparse leaves/ancestors and corrupt existing backups are refused
  before task mutation. Setup does not promise success when safe recovery is
  impossible.
- Installer and runtime agree on High task priority. Historical default priority
  remains acceptable only to the legacy migration check.
- The bootstrapper uses the latest started section for the current setup
  correlation, including retries. Only recognized ownership messages are summarized;
  unrelated logs, prior attempts, arbitrary paths and exception contents are not
  copied into the user-facing summary.

The historical portable broker pin is
`F1ECEF158F02D0BDCD1296C8D5097A281169081D0D59C1A8971592FAC78155EF`.
This supplements the finite legacy contract, not blanket adoption by filename.

## Validation

The runtime seam initially reproduced 13 failures with two controls passing.
Production fixes now cover marker preservation, priority alignment, in-place
updates, failed-write preservation, foreign/unmarked tasks, portable preference
separation and exclusive broker launch behavior.

The installer fixture first reproduced rejection of the known packaged portable
task. Expanded checks cover hash/path identity, reparse points, account and action
shape, backup-before-mutation, backup-failure preservation, rollback and containment.
A separate real-filesystem backup fixture first reproduced acceptance of a reparse
backup leaf, then passed after explicit leaf validation.

Final local Release/x64 results:

- **4,952 passed, zero failed, 11 existing opt-in skips** (4,963 total), unfiltered
  and including allocation assertions. TRX:
  `isolated_results/rc455-release-verification/rc455-versioned-full.trx`.
- Startup task registration/ownership/recovery simulations passed.
- Actual backup-file Unicode, idempotence, corruption, reparse and directory
  collision tests passed in a disposable isolated directory.
- Current-attempt error-summary regression checks: **22 passed**.
- USB/IP reboot-boundary state simulation passed.
- Actual bootstrapper Release/x64 build: zero warnings, zero errors.

The three startup/backup/diagnostic scripts are now required CI steps. Fixture
tests do not mutate real Scheduler or ProgramData state. No running application,
controller or installed driver was changed during RC4.5.5 source/build validation.
Hosted exact-source CI, installer lifecycle tests, release asset closure and
compiled updater acceptance remain mandatory publication gates.

This hotfix does not claim to identify the initiating read error 995 reported
separately, fix Joy-Con rumble stutter, or prevent every possible setup failure.
Those limits are unchanged by the successful setup repair.
