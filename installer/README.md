# DS4Windows signed native installer

`build-installer.ps1` composes the production x64 distribution as a WiX 5
Burn bootstrapper with a custom WPF maintenance interface and a per-machine
MSI. It installs only DS4Windows and the exact native VIIPER UdeCx package
already present in the publish tree. Setup is fully offline.

The native package manager is installed under
`C:\Program Files\DS4Windows\extras`. Burn captures the initiating user's SID
before elevation, the signed helper verifies the installed MSI manifest, and
the helper invokes:

```text
manage-viiper-native-package.ps1
  -Operation Install|Uninstall
  -TargetUserSID <persisted interactive SID>
```

The helper accepts exactly one congruent structured result from the manager.
VIIPER owns all driver, service, broker, credential, legacy-owner, rollback,
and reboot-boundary mutation.

## Production build

A build has no unsigned mode. It requires:

- production native package media that passes
  `Test-ViiperNativePackageContract.ps1 -RequireProduction -RequirePackage`;
- a Microsoft HLK/WHCP-signed driver catalog in that package;
- `DS4W_SIGN_CERT_PATH` and `DS4W_SIGN_CERT_PASSWORD`;
- `DS4W_SIGN_CERTIFICATE_SHA256`, pinned to the certificate bytes;
- `DS4W_SIGN_TIMESTAMP_URL`, which must be HTTPS; and
- a validly Microsoft-signed x64 Windows SDK `signtool.exe` under the
  protected Windows Kits installation.

The release worker imports the production native package only from a protected,
pre-provisioned directory. `Import-ProductionNativePackage.ps1` requires a
pinned SHA-256 provenance manifest that exactly inventories the production
metadata and native package tree; it rejects extra files, reparses, path
escapes, low-privilege writable source ACLs, and partial commits. It never
downloads release media.

Example from a protected release worker:

```powershell
.\installer\build-installer.ps1 `
  -PublishRoot .\bin\x64\Release\installer-publish `
  -ProductVersion 4.0.2.1 `
  -BundleVersion 4.0.2.1 `
  -DisplayVersion 4.0.2.1
```

Signing order is app and installed manager, deterministic publish manifest,
setup helper and bootstrapper, MSI, then the final Burn executable. Every
signature must have the pinned signer, a valid timestamp, and pass both
PowerShell and Windows SDK verification before atomic publication.

`DS4Windows_<version>_Setup_x64.exe` is the protected Add/Remove Programs
maintenance entry. Portable builds do not elevate and cannot install, repair,
or remove the native backend.

Local source and state-machine tests do not install anything:

```powershell
dotnet build .\installer\DS4Windows.SetupActions\DS4Windows.SetupActions.csproj -c Release -p:Platform=x64
dotnet build .\installer\DS4Windows.Bootstrapper\DS4Windows.Bootstrapper.csproj -c Release -p:Platform=x64
powershell.exe -NoProfile -File .\installer\Test-InstallerSecurityContracts.ps1
pwsh -NoProfile -File .\installer\Test-InstallerSecurityContracts.ps1
python .\utils\test-installer-state-machine.py
python .\utils\validate-installer.py --source-only `
  --bundle-source .\installer\DS4Windows.Bundle\Bundle.wxs `
  --setup-actions-source .\installer\DS4Windows.SetupActions\Program.cs `
  --bootstrapper-source .\installer\DS4Windows.Bootstrapper\InstallerApplication.cs
```

Live installation, repair, reboot-resume, upgrade, and uninstall are VM/laptop
gates described in
[`docs/INSTALLER_VALIDATION_STRATEGY.md`](../docs/INSTALLER_VALIDATION_STRATEGY.md).
