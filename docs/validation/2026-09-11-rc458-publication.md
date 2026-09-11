# RC4.5.8 publication ledger

## Requested release

- Tag: `VIIPERRC4.5.8`.
- Title: `Release Candidate 4.5.8 Hotfix — Bluetooth Haptics & Disconnect Recovery`.
- Application/MSI/Burn version: `5.0.5.8`.
- Functional fixes: `14b8920b060ea9e81b64747116da543a54c93c08`.
- Reuse published VIIPER `v0.1.4-rc4.5.6`, source
  `02d93e403ffde7ad24c1b373c01c6eb04ce02f2e`, binary SHA-256
  `89808A41610997A6DAD0807579B816C18B34C039E03E96FDC8FD2BD68434FFBB`.
- Existing DS4Updater 2.0.6 remains the compatible updater. No broker or updater
  production code changed in this hotfix.

The root-cause evidence, full-suite results, complete private package and guarded
portable launch are recorded in `2026-09-11-native-rumble-and-disconnect.md`.
Physical game-feel confirmation remains pending. Startup, mocked hardware tests,
and package verification are not substitutes for that confirmation.

## Publication gates

Follow `2026-09-09-release-publication-policy.md`: exact-source main CI must pass
before the annotated tag and draft build. Verify all workflow-owned assets,
sources, hashes, complete portable dependencies, offline installer payloads, and
an isolated transaction through the unchanged updater before publishing. The
published-event workflow must verify the same bytes without rebuilding them.

This record is preparation, not evidence that a release is published. Final
source, run IDs, asset hashes, updater results, and publication status will be
appended after those steps succeed. No live installer or driver test is required
on the user's machine; install/repair/uninstall/upgrade tests run on hosted CI.
