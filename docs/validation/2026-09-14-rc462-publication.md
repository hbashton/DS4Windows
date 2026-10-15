# RC4.6.2 and updater 2.0.7 publication evidence, 2026-09-14

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Release identity

- DS4Windows tag: `VIIPERRC4.6.2`, annotated at `6147981d7720636d1b46d3586efe48e6aaecd9a4` after exact-source main CI passed.
- Title: `Release Candidate 4.6.2 — Game Mods, Safer Updates & Startup`.
- Windows application/MSI/Burn version: `5.0.8.0`; package version: `5.0.8.0-rc4.6.2`.
- DS4Windows release ID: `388716585`; published **2026-09-14 21:24:29 UTC**, retaining prerelease status and not selected as stable/latest.
- Companion updater: `v2.0.7`, source `8ee7b0c0fd7ff6045f75159ee06f7cedd192150b`, release ID `388719659`; published **2026-09-14 21:24:00 UTC**, verified as the stable updater `/latest` before DS4Windows publication.
- The DS4Windows package is an explicitly authorized **unsigned named release candidate**, not a signed stable release. Neither older published assets nor release tags were replaced.

Changes and compatibility limits are in the [tagged release notes](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/release-notes-rc4.6.2.md): optional local DSX trigger/light support, #99 custom-named portable updates, #77 startup preference/recovery, #46 shifted extras, and reviewed contributor tests. The original `RunDS4Windows` and `RunVIIPER` task names remain. Nintendo rumble gain/cadence and PCM audio encoding are not retuned.

## Source and build gates

- Full local DS4Windows x64 Release suite: **6,367 passed, zero failed, 11 gated skips**. A subsequent strengthened bootstrap test passed all 27 focused cases. Its valid-name positive control prevents the minimum-updater-version gate from falsely satisfying the unsafe-path negative test.
- The native-shadow allocation assertion remains strict: **zero allocated bytes over 6,000 warmed observations**; the deliberate-allocation positive control also passes.
- All **33** Python package/localization/source-notice checks passed. The final package adds the full MIT attribution for the adapted DSX trigger effects; the committed notice blob is `7fc3bab4621fdcc4d6d56ab3d705e96ecb48d949`.
- Exact-source main CI [34896350226](https://github.com/hbashton/DS4Windows/actions/runs/34896350226) passed tests, package validation, offline installer layout, MSI install/repair/uninstall, and upgrade from published RC4.5.5 with profile preservation. These MSI operations run on a disposable hosted runner, not this PC.
- Exact-tag draft release build [34897487486](https://github.com/hbashton/DS4Windows/actions/runs/34897487486) passed and produced the complete 13-asset draft without overwriting assets.
- Updater source CI [34896470250](https://github.com/hbashton/DS4Updater/actions/runs/34896470250) and independent exact-source rebuild [34897283512](https://github.com/hbashton/DS4Updater/actions/runs/34897283512) passed. Both architectures were **byte-identical across both builds**. Seventeen pure release-asset identity tests cover duplicate, mismatch and no-overwrite behavior.

## Final downloaded artifacts

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `DS4Windows_5.0.8.0_Setup_x64.exe` | 202500917 | `F9767A5EED2F96A8F5708C2B62371A76F9DE19E6E48C8CCD4C62BC0F59695357` |
| `DS4Windows_VIIPER_x64.zip` | 137685250 | `6C3E7C13A04A2ECAFD69E6A2BC3C4E6F060EAF885DBED063AB5A29E88C730917` |
| `DS4Windows-VIIPERRC4.6.2-SOURCE.zip` | 56414923 | `1D6884E09354E4C22C422454EDC26337E76DC193566F366E85929796C42ACB7B` |
| `RELEASE-BUILD.json` | 2130 | `1F83E166EE46C704AAB9C0A81CF2E4C7184025C3D21BF7B65F5FBC47E041A08D` |
| Updater `DS4Updater.exe` (x64) | 71742432 | `F038106ADD2E33B03EA5689A87FAB9AAB76C01C4E2AC2CDC5C07A5F3C012ACE1` |
| Updater `DS4Updater_x86.exe` | 66158910 | `5C5A1C6BCB39FB4BE3947E4A284B9918E76B9BBCC0ABBD4465CD7443DBBD2DB5` |

The 12 receipt-listed assets match the successful exact-source build receipt; all 13 assets, including that receipt, match GitHub digests/sizes, Actions uploader, release ID and tag. The two source archive comments identify their corresponding commits. Both changelog sources and the added MIT notice match the tagged Git blobs, permitting only Git's CRLF-to-LF text-export normalization where required.

- Portable package: **554 files**, 297 dependency assets and 23 application satellite assemblies. The EXE, DLL and application satellites carry `5.0.8.0`; application release identity is `VIIPERRC4.6.2`.
- Against the published RC4.6.1 package, 503 unchanged binary assets were hash-matched and 14 text assets content-verified; the additional MIT notice is explicitly required and source-bound.
- Passive WiX extraction: **8 Burn payloads, 552 MSI files, 550 byte-identical shared installer/portable payloads, 550 managed entries and 551 hashed manifest entries**.
- Both portable VIIPER paths match the unchanged binary pin `9A334912E26FAC09C6DF17BA75A272A41D871D9BBE14934D06F983E101F3DF3C`. VIIPER remains `0.1.5-rc4.6`, source `c2fc304bc21f829ddb35b9e3e3d6b6ac0a9198a7`; corresponding-source ZIP hash `19C59D787B04F9597B47D40D7F6BA18F8837A8F590397A41F5E456F4A80A5787`.
- The required Xbox output identity is present and matches `2A85D3395529C7305F55338E4965A7FFD4E269DDE535FA41BD98BC54D67111C4`. USB/IP remains `0.9.7.7`. No separate broker release is required.

## #99: delivered updater behavior

The actual CI single-file updater was passively inspected using the official .NET bundle format; no updater apphost was launched. The extracted x64 production assembly has SHA-256 `0ECE33AC3575BEDD7030A1E4BB76BC4D9F64A0E77DEFE5F13BB337F0E0C3FC52` and product identity `2.0.7+8ee7b0c0fd7ff6045f75159ee06f7cedd192150b`.

An isolated test harness bound to that actual delivered assembly passed **386 existing cases**, including both hash-pinned historical package fixtures. The remaining actual RC4.6.2 package fixture then passed separately: **387 cases covered, zero failures**. It applied the final ZIP through two selected custom EXE names, checking every payload, canonical DLL/runtime sidecars, transformed ownership, exact version/channel and synthetic user-data preservation. This exercised the real updater transaction implementation, not a second implementation of its algorithm.

The fix remaps only the apphost, removes an obsolete default EXE only when owned, and preserves required canonical dependencies. Newly selected unowned destinations are rejected before file changes. Legacy adoption is restricted to the exact initiating apphost and matching canonical identity/sidecars; locked identity checks and rollback regressions passed. A verified target older than `5.0.8.0` cannot receive the custom-only layout. This is a target compatibility boundary, not a hardcoded release-tag allowlist; older initiating apps can upgrade to RC4.6.2 or a compatible future release.

The actual CI updater also passed **6 draft-policy checks** and **22 public-policy checks** against the real release receipt/API metadata. RC4.5.9, RC4.6 and RC4.6.1 select RC4.6.2; exact `5.0.8.0` identity resolves. Equal/downgrade, draft, damaged receipt, mismatched semantic identity, URL, duplicate asset and size cases reject. RC4.6.2 requires updater 2.0.7 or a compatible later verified version.

## Publication audit

Independent review cleared both complete drafts before publication. The updater was published first with both verified assets already present, then DS4Windows. Public metadata confirmed stable/latest updater selection and DS4Windows prerelease status. All 13 DS4Windows asset IDs, sizes and hashes remain unchanged from the audited draft.

The DS4Windows publication-event workflow [34898634102](https://github.com/hbashton/DS4Windows/actions/runs/34898634102) passed its fresh public-download audit. The build job was skipped: no released bytes were rebuilt or replaced.

The updater publication-event workflow [34898586876](https://github.com/hbashton/DS4Updater/actions/runs/34898586876) also **passed** on the exact tagged source. Both rebuilt architectures matched the already published assets; the same-hash guard accepted them without replacement. Anonymous public metadata and download URLs were verified, and the two older updater 2.0.6 assets retain their original IDs, sizes and hashes.

## Evidence and limitations

- Local evidence: `isolated_results/rc462-publication/draft-final/FINAL-PACKAGE-AUDIT.json`, `public-assets-final.json`, and `rc462-updater-{draft,public}-policy.json`; updater extraction, dual-build and transaction evidence is under the sibling updater repository's `_results/updater-2.0.7` directory.
- Final downloadable files are retained in `Desktop/DS4Windows-RC4.6.2-Release/release-assets`. Source and package checks did not execute the downloaded app, setup, MSI, broker, updater apphost or extracted installer helpers on this PC.
- No live controller session, installed application, real startup task, registry entry or driver was changed locally by release preparation. Automated tests do not replace physical controller/game-mod or full reboot/logon/UAC acceptance; those limits remain explicit in the release notes.
- This record is committed after verification. The release tags remain at the tested source commits, not at this later evidence-only commit.
