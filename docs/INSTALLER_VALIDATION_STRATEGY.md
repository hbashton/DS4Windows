# Signed native installer validation strategy

The production installer is a per-machine WiX/Burn transaction. It contains no
portable elevation route and performs no network retrieval. Its only privileged
native-backend caller is the signed `DS4Windows.SetupActions.exe` cached by
Burn.

## Trust and ownership boundary

1. Burn captures the initiating interactive SID before elevation. A successful
   native install records that same SID in 64-bit HKLM. Repair, upgrade,
   reboot-resume, and direct uninstall restore it; they never replace it with
   an over-the-shoulder administrator.
2. The MSI installs the application, package manifest, native metadata,
   manager, and complete native package under
   `C:\Program Files\DS4Windows`.
3. The elevated helper rejects a reparse path, an untrusted owner, or any
   install-media write grant outside trusted installer principals. It always verifies the manager
   and metadata against the deterministic MSI manifest. Install/repair also
   verifies the application executable and exact native package inventory;
   uninstall delegates its exact broker/helper media checks to the already
   verified native manager so a damaged unrelated app/package file cannot make
   the machine permanently unremovable.
4. The helper invokes only the installed manager with `-Operation Install`,
   `-Operation Uninstall`, and the exact persisted SID. Repair maps to Install.
   Production never passes local-test switches.
5. The helper requires exactly one
   `DS4WINDOWS_VIIPER_NATIVE_RESULT` JSON record. Schema, operation, process
   exit, success, reboot, rollback, and manual-recovery values must agree.
6. VIIPER alone owns service, broker image, driver package, credential ACL,
   legacy-owner retirement, durable journal, rollback, readiness, and exact
   cleanup.
   The manager creates its ProgramData transaction stage atomically with a
   protected SYSTEM/Administrators DACL, locks and hashes the source broker
   through its exact copy, and holds a deny-write/delete handle on the hashed
   staged broker through process creation and join. The installed service
   broker is exact `C:\Program Files\VIIPER\viiper.exe`; only its credential
   and log live under ProgramData.
7. Exit `3010` is accepted only with a safely-settled receipt. Burn persists
   its plan and SID and resumes the same protected transaction after restart.
8. Burn's reverse uninstall order runs a tail preflight and native removal
   helper before the MSI removes Program Files. If later MSI removal fails,
   the helper package's rollback direction calls native Install to restore the
   backend.

## Locally provable gates

These gates run without changing machine driver or service state:

- Windows PowerShell 5.1 and PowerShell 7 parse and run
  `Test-ViiperNativePackageContract.ps1`.
- Both runtimes run `Test-InstallerSecurityContracts.ps1`, which exercises
  default Program Files ReadAndExecute ACLs, atomic protected-directory
  creation, and deterministic source-copy and hash-to-launch write/delete
  races without starting the broker or changing machine service/driver state.
- Release composition additionally requires `-RequireProduction
  -RequirePackage`, exact package inventory, and a Microsoft WHCP catalog
  signer.
- x64 DS4Windows tests pass.
- Both .NET Framework 4.8 installer executables compile with zero warnings.
- The deterministic package generator rejects reparse points, path escapes,
  and case-insensitive duplicates, then binds every publish file by length and
  SHA-256.
- Source validation enforces the exact five-package chain, per-machine MSI and
  common shortcuts, minimal persisted variables, protected manager arguments,
  reverse uninstall order, SID preservation, one-plan concurrency gate, and
  absence of online or portable execution paths.
- State-machine tests cover clean install, idempotent repair, deferred related
  upgrade, isolated native recovery, outgoing related uninstall suppression,
  direct uninstall ordering and rollback direction, reboot resume, missing SID
  failure, alternate-admin maintenance, and valid/invalid structured receipts.
- The production builder has no skip-signing option. It requires a pinned
  certificate SHA-256, private key credential, HTTPS timestamp endpoint, and
  Windows SDK verification. It signs inner application media first, then
  helper/bootstrapper, MSI, and Burn. Every output must be valid,
  timestamped, and signed by the pinned certificate before atomic publication.
- The final sidecar binds the DS4Windows commit, VIIPER source revision, driver
  package/build identity, inner hashes, signer identity, bundle length, and
  bundle SHA-256.
- CI may compile an unsigned synthetic bundle only as a disposable
  non-release composition test. The production build entry remains
  fail-closed and such output is never uploaded.

## Offline Windows VM gates

Run these on clean supported Windows 10 and Windows 11 x64 VMs. Use a snapshot
and do not use a developer workstation as the first live target.

### Identity and install

- Install once as a standard user using same-account UAC.
- Restore the snapshot and install with over-the-shoulder administrator
  credentials.
- Confirm Burn/MSI/helper signatures and Program Files ACLs.
- Confirm the persisted target SID is the initiating user in both cases.
- Confirm `VIIPERNativeBroker` is an automatic own-process LocalSystem service
  with its canonical command, exact manifest-bound broker hash, protected
  credential owned for the intended SID, authenticated ping, ABI/capability
  mask, build identity, driver identity, and root device.
- Confirm the application launches unelevated as the initiating user.

### Repair and tamper containment

Independently tamper with or remove the manager, metadata, native package file,
manifest, installed broker, service registration, credential, and driver
device. Repair must either restore the exact production contract or fail before
untrusted bytes execute. Reparse and low-privilege ACL variants must fail
closed.

Run two repairs concurrently; exactly one owns the Burn transaction mutex and
the other returns Windows Installer busy. Terminate setup at each durable
VIIPER journal phase and verify retry converges without a second owner or
orphaned device.

### Reboot, upgrade, and uninstall

- Exercise every `3010` boundary. Verify the cached signed bundle resumes with
  the original SID and does not expose a second confirmation transaction.
- Upgrade from the preceding signed bundle. The outgoing related bundle must
  not remove the incoming native backend; the isolated recovery pass must
  validate the final machine state.
- Direct uninstall must stop the app, remove the exact native service, broker,
  credential, driver/root device, transaction stages, and installer markers
  before MSI media disappears.
- Inject an MSI-uninstall failure after native teardown and verify Burn rollback
  reinstalls the native package.
- Profiles, settings, logs outside installer ownership, plugins, and
  user-created files remain intact.
- Free space after uninstall must return to the pre-install baseline within
  expected Windows Installer/DriverStore bookkeeping. No repeated stage,
  cache, log, or dump growth is allowed.

## Physical Windows 11 laptop gates

After VM convergence, use the designated laptop for hardware evidence:

- DualShock 4, DualSense, and DualSense Edge input/output;
- native HID identity and media interfaces, speaker/microphone endpoints,
  audio passthrough, haptics, lightbar, rumble, adaptive triggers, and battery;
- wired/Bluetooth reconnect, sleep/resume, broker crash/restart, app
  crash/restart, rapid plug/unplug, and multiple controllers;
- DS4Windows start/stop and output-slot lifecycle with no phantom devices;
- authenticated reconnect after service or machine restart; and
- repeatable latency/continuity capture compared with the approved reference
  implementation and acceptance threshold.

No source-only or VM result substitutes for physical controller, audio, sleep,
or latency evidence.

## External release evidence

Repository tests cannot create or prove these artifacts:

- Windows Hardware Lab Kit results on every supported Windows release;
- Microsoft Hardware Dashboard/WHCP signature and submission provenance for
  the exact INF/SYS/CAT shipped by the installer;
- production Authenticode certificate custody, timestamp service response, and
  release-worker audit record; and
- GitHub artifact attestation and immutable tag-to-commit provenance.

A public installer is blocked until all local, VM, laptop, and external gates
are attached to the same DS4Windows commit, VIIPER source revision, native
metadata hash, package manifest hash, and final bundle SHA-256.
