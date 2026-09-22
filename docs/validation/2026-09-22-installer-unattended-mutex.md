# Unattended setup prompt and mutex correction

## Confirmed cause

The RC4.6.4 all-in-one installer repeatedly received 1618 from its
`CloseRunningApplications` helper because an older in-app backend setup still
owned `Global\DS4Windows-VIIPER-Setup`. Read-only owner inspection identified
the exact surviving PowerShell process. Its hidden `-Yes -NoPause` invocation
had waited at the foreign-VIIPER confirmation prompt for several days. There
was no active Windows Installer transaction; the UI wording was misleading.

The foreign-process confirmation checked only `InstallerMode`, ignoring
`-Yes` and `-NoPause`. The final dismissal prompt also ran before releasing
the mutex. Neither problem justifies bypassing the ownership guard.

## Correction

- Foreign VIIPER conflicts fail with instructions under `-Yes`, `-NoPause`,
  `InstallerMode`, or a noninteractive PowerShell host. These flags do not
  authorize silently stopping/removing another backend. Manual explicit
  confirmation remains available.
- Initial installation and Citrix confirmation also fail rather than prompt
  when unattended and missing `-Yes`; no extra consent is inferred.
- Failure containment stops only the exact managed VIIPER path, preserving
  foreign and unreadable-path processes. Existing authorized normal cleanup
  and startup-task ownership checks remain unchanged.
- The script releases its own mutex before a final manual dismissal prompt;
  unattended executions never use that prompt. Parent-owned mutex handling
  is unchanged.
- Busy status now describes another setup or repair, not necessarily Windows
  Installer, and does not promise that earlier installation work was unchanged.

The foreign handler refuses before its own process/startup mutation, not before
all setup work. The existing transaction starts earlier, and managed fail-closed
containment remains intentional. AIO recognized-product preflight behavior is
outside this correction.

## Validation

`utils/test-setup-prompt-ownership.ps1` extracts the real functions and outer
catch/finally blocks without running the installer entry point. Process,
filesystem, registry, startup, and prompt boundaries are mocked. A distinct
thread tests a unique **Local** mutex during the final prompt; the real setup
mutex is never acquired by the fixture. Manual Y/N cases use independent
fixture variable names to avoid PowerShell's case-insensitive scope collision.

- Published RC4.6.4 script: **21 failures, 15 passing controls**.
- Corrected script: **36/36 pass** under Windows PowerShell 5.1 and PowerShell 7.
- Existing renamed-process, startup ownership/backup, and failure-summary
  fixtures pass; installer state-machine checks and all **34** Python package
  tests pass.
- Bootstrapper Release x64 build: **0 warnings, 0 errors**.
- Full existing .NET suite: **6,808 pass, 0 fail, 12 existing opt-in skips**
  (`--no-build`, using the already validated lightbar-fix test assembly).

Ignored local receipts: `isolated_results/installer-prompt-ownership-01/`,
including `red-final02.log`, `green-final02.log`, `green-pwsh-final02.log`, and
`setup-lock-integrated-full.trx`. Earlier attempts remain preserved.

Separately, after the exact stale setup owner was safely retired, the unchanged
official RC4.6.4 installer completed with exit 0 and no reboot. Installed files
matched the published hashes, managed startup tasks were verified, and the
infrastructure state was Ready. That recovery validates the diagnosed blocker;
it is **not** a deployment test of a rebuilt patched installer.
