# DS4Windows standard installer

`build-installer.ps1` composes the standard x64 distribution as a WiX 5 Burn
bundle with a custom WPF interface. It contains the managed DS4Windows MSI,
VIIPER 0.0.9, USB-IP 0.9.7.7, and optional HidHide/FakerInput packages.

The installer intentionally has no portable mode or destination selector. The
portable ZIP remains a separate CI artifact. Portable DS4Windows uses the same
protected `%ProgramFiles%\DS4Windows\VIIPER` infrastructure as the managed
installation; no elevated backend is launched from a user-writable directory.

```powershell
.\installer\build-installer.ps1 `
  -PublishRoot .\bin\x64\Release\output `
  -ProductVersion 5.0.2.0 `
  -BundleVersion 5.0.2.0 `
  -DisplayVersion 5.0.2.0 `
  -SkipApplicationPublish
```

Installer logs are written by Burn and by the elevated helpers. Process
preflight diagnostics are stored in
`%ProgramData%\DS4Windows\Installer\setup-actions.log`; VIIPER, USB-IP, startup
task, and runtime verification is stored in
`%ProgramData%\DS4Windows\Installer\infrastructure-actions.log`. The in-app
repair host records failures that occur before its helper starts in
`%ProgramData%\DS4Windows\Installer\viiper-setup-host.log`.
One transaction ID is preserved across Burn, setup actions, the infrastructure
backend, and reboot resume so those logs can be correlated without timestamp
guesswork.

Set `DS4W_SIGN_CERT_PATH`, `DS4W_SIGN_CERT_PASSWORD`, and optionally
`DS4W_SIGN_TIMESTAMP_URL` to Authenticode-sign the application, setup hosts,
MSI, and final EXE in a protected release environment. Public release builds
pass `-RequireSigning`; they fail closed unless DS4Windows and the packaged
VIIPER binary both have valid signatures. GitHub release jobs require the
`DS4W_SIGN_CERT_BASE64` and `DS4W_SIGN_CERT_PASSWORD` secrets.

The PowerShell infrastructure backend is the sole VIIPER/USB-IP mutation
engine. Burn and the in-app repair surface only validate, stage, elevate, and
report that same engine. HidHide and FakerInput are optional non-vital packages:
their failure is reported without rolling back a healthy DS4Windows + VIIPER
installation.

VIIPER's legacy Windows network installer is developer-only and fail-closed by
default. It cannot silently create a second LocalAppData/HKCU owner beside this
managed infrastructure transaction.

The transaction state machine, failure containment, pinned identities, reboot
boundary, and release gates are documented in
[`docs/INSTALLER_VALIDATION_STRATEGY.md`](../docs/INSTALLER_VALIDATION_STRATEGY.md).
