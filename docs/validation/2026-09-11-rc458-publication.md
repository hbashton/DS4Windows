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

## First CI attempt

Source `747e4b4664bde932561703693c6453ad21c9e9d7`, CI run `34657242268`,
passed all **5,413 tests** with 11 gated skips, packaging, offline layout, and
MSI install/repair/uninstall. The next gate stopped before attempting the
previous-release upgrade: the optional real-MSI path of the metadata checker
still passed the historical fixture version `5.0.5.7` to `Inspect-Msi`, while
the actual new package correctly reported `5.0.5.8`.

The release remains untagged/unpublished at this point. The checker must accept
an explicitly supplied expected build version (not infer it from the MSI under
test), retain mismatched-version rejection, and pass again on hosted CI. No
installer or haptics production-code change follows from this checker failure.

## Publication completed

- Final source: `1f1c33864ecfc23d4bb74ec31406854d37fba062`, pushed to main
  and annotated as `VIIPERRC4.5.8`. The checker now requires the caller's
  explicit expected version and has 14 passing metadata regressions, including
  newer-version acceptance and wrong/missing-expectation rejection.
- [Exact-source CI 34658071204](https://github.com/hbashton/DS4Windows/actions/runs/34658071204)
  passed: **5,413 tests, zero failures, 11 gated skips**, startup checks, package
  build, offline layout, MSI install/repair/uninstall, and an upgrade from the
  hash-pinned published RC4.5.5. Both synthetic profiles survived the upgrade;
  final uninstall left zero related MSI registrations.
- [Tagged draft build 34658785739](https://github.com/hbashton/DS4Windows/actions/runs/34658785739)
  passed and supplied all **13 workflow-owned assets**. No private candidate
  binaries were substituted or uploaded over an existing asset.
- Published [RC4.5.8](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.8)
  at **2026-09-11T23:47:07Z**, release ID `387389513`, as an unsigned prerelease.
- [Post-publication verification 34659347803](https://github.com/hbashton/DS4Windows/actions/runs/34659347803)
  passed. Public asset bytes, Actions uploader, source and successful draft-build
  receipt match; publication did not rebuild or overwrite any asset.

Final downloads:

- `DS4Windows_5.0.5.8_Setup_x64.exe`: 202,335,217 bytes, SHA-256
  `6C9A0C04DE8B607FEF97D7307222C7AD9F1857CC1A2C746C182A7C1A12C19CB2`.
- `DS4Windows_VIIPER_x64.zip`: 137,621,540 bytes, SHA-256
  `1D41BE1028EC49D655F5B2617D3538D65AA50D1715228074C9C29B76647515FE`.
- App DLL: `B7779175E6DAE6F68560DD7DAD6F1A91DE75516988870C8A02F60DE744781B28`.
- App EXE: `CB633823E90AA457E30D0F70C947F3DC15EB532510AAB34326EFF8E0E0E7A2BB`.

Independent checks verified all **553 portable files**, **297 dependency assets**,
23 language satellites, unchanged .NET/WindowsDesktop 8.0.30 dependencies, both
broker aliases, authorized Xbox persona, licenses and matching source archives.
Offline installer inspection verified **8 embedded Burn payloads**, **551 MSI
files**, and exact equality of all **549 shared payloads** with the ZIP.

The immutable updater 2.0.6 applied the actual final ZIP in an isolated synthetic
RC4.5.7 filesystem transaction: **553 files** matched, **8 user-data sentinels**
survived, the obsolete owned file was removed, and no staging directory remained.
The actual draft stayed ineligible. After publication, **20 policy checks** passed
against the unmodified public API and receipt, including forward eligibility from
RC4.5.7 and exact version resolution to 5.0.5.8 without a hardcoded RC map.

Downloads and package evidence are retained under
`Desktop/DS4Windows-RC4.5.8-Release`; updater evidence is under
`isolated_results/rc458-publication`. Final package-audit SHA-256:
`CA7139B7848C982DF7B8BE1628589B438075762ECEBC40492516CCE11D5C6054`.
No running application, installed driver, or Program Files installation was
changed during publication. Physical in-game haptics and trigger acceptance
remain distinct from these successful automated and distribution checks.
