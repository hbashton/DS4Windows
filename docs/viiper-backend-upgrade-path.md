# VIIPER backend architecture

VIIPER is DS4Windows' virtual-controller backend. The normal Windows path uses
the VIIPER UdeCx driver to expose Xbox 360, DualShock 4, DualSense, DualSense
Edge, and Switch 2 Pro devices as complete local USB devices, including the
applicable Sony audio interfaces.

## User setup

DS4Windows admits native output only when all of these checks pass:

1. Bundled `ViiperNativeRuntimeMetadata.json` is production eligible, or is
   explicit local-test evidence with the disposable-machine opt-in.
2. The installed broker bytes match the manifest-bound SHA-256.
3. `VIIPERNativeBroker` is an automatic, own-process LocalSystem service with
   the exact native UDE command line and protected credential/log paths.
4. The target user can read the protected 16-byte credential.
5. An authenticated ping reports `transport=native-ude`, ready state, and the
   exact metadata-bound ABI, capability mask, package version, loaded-driver
   build identity, and controller instance ID.

The same metadata is source-bound to VIIPER's registered controller handlers,
not to invented DS4Windows aliases. The current contract uses DS4 fixed/V3
handlers (`dualshock4`, `dualshock4audioduplexv3`, and
`dualshock4audioonlyduplexv3`), DualSense V5 combined/audio-only/gamepad
handlers, and DualSense Edge V5 combined/gamepad handlers. It also binds the
native descriptor identities (DS4 `054c:09cc`, with DS4Windows' intentional
`054c:05c4` client override; DualSense `054c:0ce6`; Edge `054c:0df2`) and the
full, audio-only, or HID-only interface profile for every type. Xbox 360 and
Switch 2 Pro remain their existing `xbox360` and `ns2pro` handlers. The package
gate rejects missing, extra, renamed, or descriptor-divergent registrations.

Setup is self-elevating but offline. It stages the exact broker in a protected
ProgramData directory and invokes VIIPER's hidden `native-package-install`
boundary with manifest-bound hashes, the interactive target-user SID, and the
production validation mode. Removal invokes the exact `uninstall --yes`
boundary with the bundled helper hash and SID. Exit `3010` is preserved as a
safe reboot boundary; other nonzero outcomes are reported as requiring review
of the durable transaction/recovery log.

The installed broker is owned by Service Control Manager. Native setup never
creates `RunVIIPER`, launches a per-user server, downloads a backend, installs
USB/IP, or detaches USB/IP ports.

The repository currently checks in metadata for a verified local-test package
only and does not check in its test certificate or runtime tree. Normal UI
installation therefore fails closed. A production release job must place the
exact Microsoft HLK/WHCP runtime tree under
`extras/viiper-native-package`, regenerate metadata with
`New-ViiperNativeRuntimeMetadata.ps1`, update the source-bound
`ViiperControllerApiContract.json` only when the corresponding VIIPER handlers
and DS4Windows client change together, and pass
`Test-ViiperNativePackageContract.ps1 -RequireProduction` before publishing.

### Disposable Windows 11 validation

Do not use the local-test route on a primary laptop. Use a snapshot-capable VM,
or a spare Windows 11 machine that can be wiped. Place the one exact generated
local-test package under `extras\viiper-native-package`; do not substitute a
certificate, broker, helper, INF, SYS, CAT, or submission manifest. First run
the normal contract gate. Then, from an elevated 64-bit PowerShell in the
DS4Windows directory, enable Windows test-signing and restart once before the
first installation:

```powershell
bcdedit.exe /set testsigning on
```

Microsoft warns that BCDEdit changes can make a machine unbootable and that
Secure Boot can reject `TESTSIGNING`; BitLocker can also affect the change.
On a spare physical laptop, first verify that its BitLocker recovery key is
available and that you have local console/recovery access. Do not disable
Secure Boot or alter BitLocker on a primary machine merely to make this test
work—use the VM route instead. See Microsoft's
[test-signed driver guidance](https://learn.microsoft.com/windows-hardware/drivers/install/the-testsigning-boot-configuration-option).

After the restart, verify `bcdedit.exe /enum '{current}'` reports
`testsigning Yes`, then make the two independent local-test acknowledgements
explicit:

```powershell
& .\extras\Test-ViiperNativePackageContract.ps1 -RequirePackage
$targetSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$env:DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST = '1'
& .\extras\manage-viiper-native-package.ps1 `
    -Operation Install `
    -TargetUserSID $targetSid `
    -AllowLocalTest `
    -AcknowledgeDisposableTestMachine
```

The manager admits only the metadata-bound public `ViiperUdeTest.cer`. Before
the driver transaction it verifies the exact bytes and installs them in the
Local Machine `Root` and `TrustedPublisher` stores if absent. If setup fails
before transaction admission, it removes only certificates it added; once a
transaction starts, it retains trust so the durable recovery path can still
load the exact prior/candidate driver. Restoring the VM snapshot or wiping the
spare machine removes this intentionally persistent test trust.

Preserve the emitted `DS4WINDOWS_VIIPER_NATIVE_RESULT` record. Exit `3010`
means restart at the declared safe boundary and rerun the identical command;
it is not permission to improvise a repair. After testing, run the same manager
with `-Operation Uninstall` and the same local-test acknowledgements, then
restore the VM snapshot or wipe the spare test machine.

## Profile migration

The retired serialized values `X360` and `DS4` remain readable solely for
backward compatibility. They normalize immediately to `ViiperX360` and
`ViiperDS4`; new saves never write the retired values.

## Runtime containment

DS4Windows records locally created VIIPER Sony interfaces before normal HID
enumeration and rejects them as physical inputs. Moonlight/Sunshine virtual
controllers use a separate opt-in admission policy, so accepting streamed
controllers cannot make DS4Windows recursively ingest its own output.

The historical USB/IP path remains available only for explicit developer/ABBA
validation through `DS4WINDOWS_VIIPER_TRANSPORT=usbip`. It has separate
prerequisites and ownership, is never an automatic fallback, and cannot satisfy
native readiness or performance evidence.

## Feedback and audio

VIIPER feedback is read by `ViiperOutDevice` and routed to the currently bound
physical controller. Xbox/standard rumble, Sony lightbar output, adaptive
triggers, advanced haptics, speaker playback, and microphone capture are
translated according to the physical controller's capabilities.
