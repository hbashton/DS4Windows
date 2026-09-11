# DS4Windows standard installer

`build-installer.ps1` composes the standard x64 distribution as a WiX 5 Burn
bundle with a custom WPF interface. It contains the managed DS4Windows MSI,
VIIPER 0.1.4-rc4.5.6, USB-IP 0.9.7.7, and optional HidHide/FakerInput packages.
The VIIPER payload is accompanied by its generated dependency-license notice
and a validated provenance record containing the exact source commit and
SHA-256 used by this release.

The installer intentionally has no portable mode or destination selector. The
portable ZIP remains a separate CI artifact. The standard installer places
VIIPER under protected `%ProgramFiles%\DS4Windows\VIIPER`. The marked portable
ZIP instead starts its bundled, hash-verified broker when needed and does not
create or retarget installed startup tasks. A verified matching running copy
can be reused; a conflicting copy is reported without being terminated.
Installed startup tasks continue to target the verified installed broker.

```powershell
.\installer\build-installer.ps1 `
  -PublishRoot .\bin\x64\Release\output `
  -ProductVersion 5.0.5.8 `
  -BundleVersion 5.0.5.8 `
  -DisplayVersion VIIPERRC4.5.8 `
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

Installer composition also runs a non-mutating startup-task simulation. It
schema-parses the exact-SID XML in memory, mocks registration and verification,
checks value escaping and the two-name root-task allowlist, preserves foreign
same-name collisions across registration/removal/containment, verifies partial
pair rollback, and proves that normal absence does not become a failed CIM
query while genuine enumeration failures remain fatal.

Set `DS4W_SIGN_CERT_PATH` plus `DS4W_SIGN_CERT_PASSWORD`, or use
`DS4W_SIGN_CERT_THUMBPRINT` for a protected certificate-store identity. Set
`DS4W_SIGN_EXPECTED_THUMBPRINT` to the independently approved signer and,
optionally, set `DS4W_SIGN_TIMESTAMP_URL`. Stable and other signed release
builds pass `-RequireSigning`; they fail closed unless the first-party DS4Windows
application, setup hosts, MSI, and final EXE have that valid timestamped
signature. The bundled upstream VIIPER executable remains byte-identical and
unsigned; its fixed SHA-256 and complete source/build provenance are validated
instead. Signed GitHub release jobs require the `DS4W_SIGN_CERT_BASE64`,
`DS4W_SIGN_CERT_PASSWORD`, and `DS4W_SIGN_EXPECTED_THUMBPRINT` secrets.

The user-authorized exception is an actual GitHub prerelease with an exact
named `VIIPERRC` ordinal tag. Its draft-first workflow explicitly records an
unsigned build, retains the other integrity gates, and verifies those same
assets after publication without rebuilding them. Numeric prereleases,
unknown tags and stable releases cannot use that exception. See the
[publication policy](../docs/validation/2026-09-09-release-publication-policy.md).

Signed Burn bundles use WiX's required two-part flow: detach and sign the
cached engine, reattach that engine to the original bundle, then sign the
whole bundle. Composition verifies the detached engine immediately after
signing it. After outer signing and before atomic publication, it extracts the
final attached container and checks the MSI, setup-actions host, and
bootstrapper hashes against their signed inputs.

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
