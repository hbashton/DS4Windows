# VIIPER Windows 11 local-test validation bundle

This directory builds and drives a source-bound, boot-resumable validation run
on a disposable Windows 11 laptop. It is deliberately a local-test workflow.
Building the bundle makes no runtime claim. A successful `Live` phase requires
both the VIIPER reference gate and the real DS4Windows HID/media/reconnect
runner. A successful `LatencyMatrix` phase records only that native latency was
lower in every observed balanced cycle on that exact machine session; it makes
no iid, confidence, population, or cross-machine claim.

No step downloads anything. The builder requires explicit paths for an exact
clean VIIPER Git checkout, exact local-test package, exact clean DS4Windows Git
checkout, an explicit published DS4Windows artifact and entry point, Git
executable, and Go executable. It copies the complete VIIPER
checkout (including `.git` and initialized submodules), the exact package, and
the DS4Windows package-maintenance boundary. The generated manifest binds every
published DS4Windows artifact file, its executable, the critical scripts, and
both tool executables. Transfer its printed manifest
SHA-256 separately from the bundle and supply it on every laptop invocation.
The DS4Windows artifact input must contain both the published app and the
published `DS4Windows.ViiperLiveValidation` runner/harness.

## Build the bundle

Run the deterministic contract first in both available PowerShell hosts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\extras\validation\Test-ViiperWin11ValidationContract.ps1

pwsh.exe -NoProfile -File `
  .\extras\validation\Test-ViiperWin11ValidationContract.ps1
```

Build the source-pinned SDL binary used by the latency gate before creating the
bundle:

```powershell
$viiperSourceRoot = '<clean VIIPER checkout at the exact committed revision>'
cmake -S (Join-Path $viiperSourceRoot '_testing\e2e\deps\SDL') `
  -B (Join-Path $viiperSourceRoot '_testing\e2e\deps\SDL\build') -A x64
cmake --build (Join-Path $viiperSourceRoot '_testing\e2e\deps\SDL\build') `
  --config Debug
```

After these files are committed and the DS4Windows checkout is clean, build to
an empty path outside every input checkout. Every identity is mandatory; there
is no implicit package or network fallback.

```powershell
& .\extras\validation\New-ViiperWin11ValidationBundle.ps1 `
  -ViiperSourceRoot $viiperSourceRoot `
  -PackageRoot '<exact local-test package for the committed VIIPER revision>' `
  -DS4WindowsSourceRoot '<clean DS4Windows checkout at the committed revision>' `
  -DS4WindowsArtifactRoot '<clean DS4Windows publish directory>' `
  -DS4WindowsExecutableRelativePath 'app\DS4Windows.exe' `
  -DS4WindowsLiveRunnerRelativePath 'runner\DS4Windows.ViiperLiveValidation.exe' `
  -DS4WindowsLiveHarnessRelativePath 'runner\Invoke-ViiperDs4WindowsLaptopValidation.ps1' `
  -OutputDirectory '<new output directory on a fixed local NTFS volume>' `
  -ExpectedViiperSourceRevision '<full committed VIIPER revision>' `
  -ExpectedDS4WindowsSourceRevision '<committed DS4Windows revision>' `
  -ExpectedPackageLockSHA256 '<lowercase package-lock SHA-256>' `
  -GitExecutable '<byte-identical git.exe path>' `
  -GoExecutable '<byte-identical go.exe path>'
```

The laptop must have byte-identical Git and Go executables available at paths
you explicitly pass. The script prepends only those validated executable
directories for source-bound live work. It never searches for or downloads a
replacement.

## Run on the disposable laptop

Use same-account elevation: the administrator prompt must belong to the same
interactive SID that will run DS4Windows. Keep the bundle read-only after
transfer, choose an evidence directory outside it, and retain the builder's
manifest SHA-256 out of band.

Every invocation repeats the same mandatory identity arguments:

```powershell
$common = @{
  ExpectedBundleManifestSHA256 = '<out-of-band bundle-manifest SHA-256>'
  EvidenceRoot = 'E:\VIIPER-evidence'
  TargetUserSID = 'S-1-5-21-...'
  GitExecutable = 'C:\Program Files\Git\cmd\git.exe'
  GoExecutable = 'C:\Tools\go\bin\go.exe'
}

& .\Invoke-ViiperWin11Validation.ps1 @common -Phase Status
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase Preflight
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase Install
```

### Recover an exact zero-change failed predecessor Install

`RecoverFailedInstall` is a narrow migration boundary for a predecessor run
whose immutable state and captured child evidence prove the exact
`install-journal-broker-image-hash` rejection with `changed=0`, no reboot, and
zero trust before Preflight. Use a newly manifest-bound bundle extracted
outside OneDrive or any other sync/placeholder tree. Preserve the predecessor
bundle and evidence read-only. Supply every reported predecessor digest; none
is discovered or substituted:

The source-bound R4 stdout proof is exactly 582 bytes of UTF-8 without a BOM:
these five lines in this fixed order, each terminated by LF (including the
last line). Its SHA-256 is
`ca95fac3b8bd6fe7871a7f42400031f01ea946dc88786e9e9a746084144c205b`.

```text
local-test-trust store=Root action=add result=added
local-test-trust store=Root action=verify-add result=present
local-test-trust store=TrustedPublisher action=add result=added
local-test-trust store=TrustedPublisher action=verify-add result=present
VIIPER: error: install native driver and broker transaction: native driver helper failed with exit 4: exit status 4: result=error operation=install changed=0 rebootRequired=0 rollback=not-needed exitCode=4 phase="install-journal-broker-image-hash" win32Error=23 message="protected broker evidence differs from its immutable digest"
```

The bare canonical helper outcome is intentionally not an alternate proof.
Arbitrary prefixes or suffixes, another VIIPER command context, near-miss
fields, duplicate wrappers, any extra native result outcome, CRLF line endings,
and a UTF-8 BOM are rejected.

```powershell
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase RecoverFailedInstall `
  -PredecessorEvidenceRoot '<retained predecessor evidence root>' `
  -PredecessorInstallStepDirectory '<retained failed Install step directory>' `
  -ExpectedPredecessorStateSHA256 '<validation-state.json SHA-256>' `
  -ExpectedPredecessorInstallCommandSHA256 '<command.json SHA-256>' `
  -ExpectedPredecessorInstallResultSHA256 '<result.json SHA-256>' `
  -ExpectedPredecessorInstallStdoutSHA256 '<stdout.log SHA-256>' `
  -ExpectedPredecessorInstallStderrSHA256 '<stderr.log SHA-256>' `
  -ExpectedPredecessorBundleManifestSHA256 '<predecessor manifest SHA-256>' `
  -ExpectedPredecessorViiperSourceRevision '<predecessor VIIPER revision>' `
  -ExpectedPredecessorDS4WindowsSourceRevision '<predecessor DS4Windows revision>' `
  -ExpectedPredecessorPackageLockSHA256 '<predecessor package-lock SHA-256>' `
  -ExpectedPredecessorCertificateSHA256 '<predecessor certificate file SHA-256>'
```

The phase invokes only the current manifest-bound recovery manager; it never
manually deletes the protected ProgramData journal or certificate stores. The
native child lifetime-owns Trust -> Package -> Service, verifies the exact
recordless predecessor topology, and removes only the exact certificate bytes
whose predecessor Preflight counts establish were newly introduced. Its atomic
`state\failed-install-recovery.json` receipt makes cuts retryable without
creating `validation-state.json`, so a successful recovery is followed by a
fresh `Preflight` using the same current bundle and EvidenceRoot. TESTSIGNING is
not changed, and this verify-only recordless recovery has no reboot-success
boundary: any nonzero native result remains a hard stop.

### Generic Run4 predecessor parser boundary (design only)

The exact R4 parser above remains the only implemented failed-install
admission path. A future DS4-side generic Run4 predecessor parser is currently
a contract design, not recovery authority: it is not connected to a validation
phase, manager operation, native command, argument, or capability schema.

Before that API can be wired, one canonical source-bound input must name an
exact predecessor evidence root and exact evidence files, with independently
supplied hashes for every file. The parser must bind the predecessor manifest,
VIIPER and DS4Windows revisions, package lock, machine, same-account target SID,
lifecycle and pending transaction, exact command argv, captured result, and the
complete stdout/stderr bytes. It must reject missing, extra, reordered,
duplicated, prefixed, suffixed, malformed, or contradictory native outcomes and
retain verified read-only, reciprocal-`FileShare.Read` leases while authority
is consumed. Callers may provide expected identities; they may not manufacture
authority by selecting a directory, digest, substring, or outcome grammar.

No generic Run4 recovery invocation is permitted until the recovery API and
its exact schemas are frozen together. That future contract must be introduced
with executable PS5/PS7 adversarial fixtures before any native argv is added;
it must not broaden or reinterpret the frozen 582-byte R4 proof.

`Preflight` captures Windows build and architecture, boot identity/uptime,
TESTSIGNING and pending reboot state, active power plan, AC/battery state,
VBS/HVCI/hypervisor state, free disks, and a background-process snapshot. It
also records read-only installed USB/IP comparator provenance: matching
services and image hashes/signatures, signed-driver provider/version/signer,
published INF hashes, and root hardware/instance IDs. This is provenance only,
not comparative latency evidence.

If Install, Repair, or Uninstall stops at VIIPER's safe reboot boundary, reboot
manually and run `-Phase RebootResume` with the identical arguments. The state
file checks that the boot identity changed before rerunning the exact pending
transaction.

After installation, perform the physical checks only when prompted:

```powershell
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase ManualChecks `
  -AcknowledgePhysicalHotplug -AcknowledgeSleepWake `
  -AcknowledgeHibernateWake

# Perform the prompted full reboot, then:
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase ManualChecks `
  -AcknowledgeManualReboot
```

The prompts require physical DS4/DS5 disconnect/reconnect, sleep/wake,
hibernate/wake, and a full reboot. A switch records the operator's completed
check; it does not simulate hardware or power transitions. Start only the
manifest-bound DS4Windows executable named by `-Phase Status` for these manual
integration observations.

Continue with crash diagnostics and one-boot Driver Verifier:

```powershell
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase EnableVerifier
# Reboot when prompted.
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase VerifierResume
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase Live
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase Performance
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase LatencyMatrix
```

`Live` invokes the VIIPER reference lifecycle/HID/media harness with exact
LocalTest signature flags, source-built probes, owner-crash recovery, root
restart recovery, and active Driver Verifier, then invokes the manifest-bound
DS4Windows runner against the actual installed broker and Driver Store images.
`Performance` retains WPR ETL and its evidence JSON; it is not an ABBA
comparison. `LatencyMatrix` runs eight ABBA/BAAB cycles at each of Normal and
High priority, binds raw ETL/decoded markers/package/test signer/USB-IP runtime,
and requires native mean/p95/p99 to be lower for every controller transition in
every observed cycle. Use `CollectDumps` after a crash (including after Safe
Mode recovery):

```powershell
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase CollectDumps
```

Finally, uninstall and restore the prior crash policy:

```powershell
& .\Invoke-ViiperWin11Validation.ps1 @common -Phase Uninstall
```

The uninstall phase uses the manifest-bound DS4Windows maintenance script. Its
native child holds Trust -> Package -> Service, durably marks the exact trust
owner `uninstalling` before topology mutation, restores only the recorded
certificate baseline after exact topology absence, and then publishes
`cleared`. The orchestrator only verifies that baseline and restores the
recorded crash policy. Follow its final reboot prompt.

## Evidence behavior

Each child invocation gets a unique step directory containing `command.json`,
`stdout.log`, `stderr.log`, and `result.json`. ETL, trace evidence JSON, machine
snapshots, state transitions, uninstall cleanup, and copied dumps remain under
the explicit evidence root. Failures keep their step directory and do not
overwrite earlier evidence. A sudden verifier crash may interrupt the wrapper;
after reboot, preserve dumps first and inspect the last state/history entry.
