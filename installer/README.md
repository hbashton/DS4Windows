# DS4Windows standard installer

`build-installer.ps1` composes the standard x64 distribution as a WiX 5 Burn
bundle with a custom WPF interface. It contains the managed DS4Windows MSI,
VIIPER 0.0.7, USB-IP 0.9.7.7, and optional HidHide/FakerInput packages.

The installer intentionally has no portable mode or destination selector. The
portable ZIP remains a separate CI artifact.

```powershell
.\installer\build-installer.ps1 `
  -PublishRoot .\bin\x64\Release\output `
  -ProductVersion 5.0.0.0 `
  -BundleVersion 5.0.0.0 `
  -DisplayVersion 5.0.0.0 `
  -SkipApplicationPublish
```

Installer logs are written by Burn and by the elevated infrastructure helper.
The helper's persistent log is under
`%ProgramData%\DS4Windows\Installer\setup-actions.log`; the hardened VIIPER and
USB-IP log remains under the managed VIIPER installation.

Set `DS4W_SIGN_CERT_PATH`, `DS4W_SIGN_CERT_PASSWORD`, and optionally
`DS4W_SIGN_TIMESTAMP_URL` to Authenticode-sign the final EXE in a protected
release environment.
