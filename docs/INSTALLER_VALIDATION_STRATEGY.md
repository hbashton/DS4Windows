# Installer validation and recovery strategy

The DS4Windows installer is treated as a transaction with one owner and one
commit point. A package is not considered installed merely because its child
process returned zero; the complete DS4Windows, VIIPER, USB-IP, task, ABI, and
API contract must be verified first.

## Transaction rules

1. A global Burn mutex rejects overlapping install, update, repair, and
   uninstall plans with Windows Installer error 1618.
2. A second global infrastructure mutex serializes VIIPER and USB-IP mutation,
   including the built-in repair flow.
3. Install/repair and uninstall have separate process preflights at opposite
   ends of the Burn chain. This respects Burn's forward installation and
   reverse uninstallation order.
4. The original interactive user's SID and profile folders are captured before
   elevation and persisted by Burn across a reboot resume.
5. The readiness marker is cleared before mutation. It is restored only after
   all pinned identities and runtime probes pass.
6. A failed transaction stops VIIPER, disables only startup tasks whose full
   executable/action/principal contract still belongs to this package, and
   records a failed state.
7. A newer related bundle blocks an older installer with error 1638 before any
   package is planned.
8. One interlocked Plan gate rejects duplicate UI, passive, resume, or recovery
   requests until the active Apply pass completes or Retry explicitly resets
   the gate.
9. Process preflight acquires the same infrastructure mutex as in-app repair,
   preventing one transaction from stopping executables while another is
   replacing or probing them.
10. Burn creates one persisted correlation ID and passes it through every
    preflight, elevated helper, PowerShell phase, and reboot resume. In-app
    repair creates the same kind of transaction ID. Bounded append retries
    preserve diagnostics when a log reader briefly owns the file.
11. The backend always lives under Program Files. "Keep DS4Windows portable"
    changes only the UI/package location; it never creates an elevated task for
    a LocalAppData VIIPER executable.
12. Start-menu and optional desktop shortcuts are all-users shell integration
    owned by the infrastructure host, not per-user MSI components.
13. Program Files ACL normalization is a required safety gate for both the
    managed application and VIIPER backend; setup never registers an elevated
    task against a directory it could not protect.

## Pinned runtime contract

- VIIPER must match the SHA-256 of the bundled 0.1.1 executable.
- `usbip.exe` must report version 0.9.7.7 and match the pinned executable
  SHA-256.
- The active `usbip2_ude` and `usbip2_filter` driver files must match the two
  pinned signed-driver SHA-256 values.
- `usbip.exe port` must exit successfully without any known ABI/structure
  mismatch diagnostic.
- The VIIPER API must answer its local readiness probe.
- The machine readiness marker must be
  `VIIPER-0.1.1+USBIP-0.9.7.7 / Ready` in the 64-bit registry view.

DS4Windows repeats these identity and ABI checks at startup. Missing or
mismatched prerequisites open an offline repair prompt; suppressing a location
recommendation never suppresses a verification failure. The main UI remains
available in degraded mode for Settings and diagnostics, while virtual output
continues to fail closed until a fresh readiness check passes.

## USB-IP 0.9.7.8 downgrade

The downgrade is intentionally split across boots:

1. Verify the exact 0.9.7.8 uninstall record, quiesce DS4Windows/VIIPER, detach
   imports, remove 0.9.7.8, and persist the source/target versions plus the
   current boot identity.
2. Leave 0.9.7.7 uninstalled in that boot, disable the two verified startup
   tasks, and return 3010.
3. After reboot, prove the boot identity changed and the old root device,
   running services, and DriverStore packages are gone.
4. Install the bundled 0.9.7.7 package, validate executable and driver hashes,
   validate ABI and VIIPER API, enable the owned startup tasks, then atomically
   publish Ready.

The release gate runs a no-driver-mutation simulation of same-boot rejection,
next-boot continuation, failed-uninstaller rollback, and task suspension. The
test deliberately avoids installing a known-incompatible kernel driver on the
build host.

## Release gates

- PowerShell parser validation for the backend installer.
- WPF clean-configuration construction tests, including mandatory repair UI.
- Full unit/regression suite.
- WiX MSI ICE validation.
- Burn/bootstrapper and setup-action compilation.
- Self-contained .NET 8 publication for both installer hosts.
- Real MSI install, repair, and uninstall execution on the Windows CI runner.
- Content-addressed payload manifest and hash validation.
- Fail-fast equality between `DS4Windows.release` and the requested package
  identity before WiX or manifest generation can run.
- USB-IP reboot-boundary simulation.
- Installer state-machine simulation covering clean install, update, repair,
  uninstall, downgrade, cancellation, concurrency, failure, and reboot/resume.
- Atomic publication of the completed installer only after every gate passes.
  The verified manifest is committed first and the installer EXE last.
- Mandatory Authenticode verification for public release artifacts; release
  composition is blocked when signing material or a first-party signature is
  missing.
