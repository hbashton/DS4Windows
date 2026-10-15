# Startup-task recovery for issues #98 and #103

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Evidence and scope

[Issue #103](https://github.com/hbashton/DS4Windows/issues/103) reports setup
aborting before USB/IP work because the original root task names were occupied
by definitions the old ownership check rejected. The supplied log does not
include their definitions, so it does not establish the exact ownership field.

[Issue #98](https://github.com/hbashton/DS4Windows/issues/98) reports a clean
RC4.5.5 installation. Its attached setup log records three registration attempts
failing **during verification**, again before USB/IP Step 2. Earlier append-only
log entries containing an invalid UserId error belong to older runs and are
not the same failure. The attached application log is from RC4.3, not that
RC4.5.5 setup invocation. Neither report supplies the registered task XML.

The manual workaround in #98 creates tasks using an account name, but omits
the VIIPER server/authority arguments and both working directories. A Scheduler
state of Ready is not proof of the full application startup contract. The
existing verifier already resolves account names and SIDs before comparison.

The user explicitly directed setup to reclaim the **original names only**:
`RunVIIPER` and `RunDS4Windows`. The unpublished alternate-name approach was
removed; no new task namespace is shipped.

## Implementation boundaries

- Installer registration may replace either reserved root name after durable
  XML backup. Both definitions are preflighted before pair registration, and
  registration rereads/backs up the current definition before replacement.
  A failed backup leaves that definition untouched.
- Existing exact managed tasks are reused. Ordinary runtime launch, removal,
  uninstall and failure containment still require verified task contents;
  installer reclaim authority does not make arbitrary task actions trusted.
- Task-only configuration, inspection, verification and suspension errors
  record a warning and select the direct-launch path for this invocation.
  The saved startup preference is not silently changed. A later Repair can
  attempt automatic startup again.
- The warning is correlated to the current setup invocation and cleared on a
  successful retry. The standard installer shows a fixed explanatory message,
  not arbitrary task arguments or XML.
- Fresh task validation precedes launch. Finish confirms that the installed
  DS4Windows executable actually appeared rather than interpreting Scheduler
  acceptance alone as launch success.
- Driver hashes, process ownership, USB/IP ABI, pending-reboot boundaries and
  VIIPER API readiness remain required. Task recovery cannot publish Ready
  while those checks fail. Later infrastructure failure still attempts owned
  task containment, even after task setup selected direct launch.

Backups remain under
`%ProgramData%\DS4Windows\Installer\task-backups`. Only the two exact
root names can be reclaimed. The API has no atomic compare-and-swap operation
against a concurrently editing administrator; rereads and bounded retries
reduce that race, but this is not an unlimited concurrency guarantee.

## Verification compatibility

Microsoft's installed ScheduledTasks type adapter exposes RunLevel and
LogonType as generated enum values. On the inspected Windows machine, the
same principal's underlying CIM properties are integers **1** and **3**.
The old string-only comparisons reject those raw integer forms.

The verifier now recognizes the exact equivalent enum/integer representations
as well as the supported friendly names. This preserves the documented
[highest run level](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_runlevel_type)
and
[interactive-token logon type](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type);
it does not accept password, S4U, service-account or interactive-or-password
logon instead. Identity, action, working-directory and argument checks remain.

This is source-backed compatibility hardening, **not proof that raw numeric
properties caused #98**. New field-by-field diagnostics report the actual
verification mismatch without dumping action arguments or task XML.

## Validation and limits

- Complete local x64 suite: **5,991 passed, zero failed, 11 explicitly gated
  skips**. This includes 19 new pure installer ownership/warning tests.
- Both installer hosts compile in Release/x64 without warnings or errors.
- Fake Scheduler regression coverage exercises clean registration, reserved
  collisions, backups, partial registration, concurrent definition changes,
  provider/verification failure, direct launch, retry warning reset and
  preserved infrastructure-failure boundaries.
- Durable-backup and reboot-boundary fixtures exercise production functions
  without changing real scheduled tasks or drivers.

Task Scheduler can still refuse access or registration. The guarantee is that
these task-only failures no longer abort setup; it is not a claim that Windows
always creates a working logon task. Reporter confirmation remains necessary
for the exact #98 environment. No live controller session, installed application
or real task definition was changed during this validation.
