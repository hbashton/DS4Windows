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
cmake -S D:\CodexTemp\viiper-cece307-fullclone\_testing\e2e\deps\SDL `
  -B D:\CodexTemp\viiper-cece307-fullclone\_testing\e2e\deps\SDL\build -A x64
cmake --build D:\CodexTemp\viiper-cece307-fullclone\_testing\e2e\deps\SDL\build `
  --config Debug
```

After these files are committed and the DS4Windows checkout is clean, build to
an empty path outside every input checkout. Every identity is mandatory; there
is no implicit package or network fallback.

```powershell
& .\extras\validation\New-ViiperWin11ValidationBundle.ps1 `
  -ViiperSourceRoot 'D:\CodexTemp\viiper-cece307-fullclone' `
  -PackageRoot 'D:\CodexTemp\viiper-local-test-35cba66-abi14-r1' `
  -DS4WindowsSourceRoot 'D:\CodexTemp\ds4w-native-udecx-7270672f' `
  -DS4WindowsArtifactRoot '<clean DS4Windows publish directory>' `
  -DS4WindowsExecutableRelativePath 'app\DS4Windows.exe' `
  -DS4WindowsLiveRunnerRelativePath 'runner\DS4Windows.ViiperLiveValidation.exe' `
  -DS4WindowsLiveHarnessRelativePath 'runner\Invoke-ViiperDs4WindowsLaptopValidation.ps1' `
  -OutputDirectory 'D:\CodexTemp\viiper-win11-validation' `
  -ExpectedViiperSourceRevision '35cba66dc61a9d85240d44d5e9a2519fd471f9db' `
  -ExpectedDS4WindowsSourceRevision '<committed DS4Windows revision>' `
  -ExpectedPackageLockSHA256 'fd1dd14a5d78fe9636ea7b47835241d83decd93b9f64284fcb36733a17fd34eb' `
  -GitExecutable 'C:\Program Files\Git\cmd\git.exe' `
  -GoExecutable 'D:\CodexTemp\go1.26.5\go\bin\go.exe'
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

The uninstall phase uses the manifest-bound DS4Windows maintenance script,
removes only local-test trust that the preflight proved was not preexisting,
and restores the recorded crash policy. Follow its final reboot prompt.

## Evidence behavior

Each child invocation gets a unique step directory containing `command.json`,
`stdout.log`, `stderr.log`, and `result.json`. ETL, trace evidence JSON, machine
snapshots, state transitions, uninstall cleanup, and copied dumps remain under
the explicit evidence root. Failures keep their step directory and do not
overwrite earlier evidence. A sudden verifier crash may interrupt the wrapper;
after reboot, preserve dumps first and inspect the last state/history entry.
