# Release Candidate 4.3 - Installer Reliability

RC4.3 pairs DS4Windows 5.0.3.0 with VIIPER 0.1.0 and tightens every standard,
portable, update, repair, and uninstall path around one verified offline
package.

## Highlights

- **VIIPER 0.1.0 everywhere.** The tray, Windows executable metadata, API
  version, release package, DS4Windows compatibility gate, and both installer
  paths all identify the same production backend build.
- **Exact portable compatibility.** A portable VIIPER can run from any
  location when its executable matches this release's pinned SHA-256. Users
  are not forced into Program Files merely because DS4Windows is portable.
- **Deterministic updates.** Stale installer registrations are retired rather
  than relaunched or allowed to block the current package.
- **Fail-closed backend checks.** Missing, outdated, or modified VIIPER
  binaries cannot silently start. Repair uses the exact bundled offline
  payload and verifies it before virtual controllers are enabled.
- **Safer shared infrastructure.** Updates preserve healthy VIIPER and USB-IP
  infrastructure. Uninstall removes only files and tasks owned by the managed
  installation.
- **Broader release gates.** Clean install, upgrade, repair, internal repair,
  USB-IP removal and recovery, uninstall, reinstall, payload integrity, and
  startup behavior are validated before publication.

## Included software

- DS4Windows **5.0.3.0** (self-contained x64)
- VIIPER **0.1.0**
- usbip-win2 **0.9.7.7**
- Optional offline HidHide and FakerInput installers

The standard installer preserves profiles and settings while upgrading only
package-owned files. Portable users can continue using the release ZIP.
