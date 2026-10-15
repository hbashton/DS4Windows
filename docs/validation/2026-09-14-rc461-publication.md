# RC4.6.1 publication evidence, 2026-09-14

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Identity and scope

- Release tag: `VIIPERRC4.6.1`.
- Title: `Release Candidate 4.6.1 Hotfix — Dial In Your Aim, Safer HidHide`.
- Tagged source: `198d62295e3677ea74a333c79fdc1039283adbc0`.
- Windows application/MSI/Burn version: `5.0.7.0`; package version: `5.0.7.0-rc4.6.1`.
- Original reserved startup task names remain `RunDS4Windows` and `RunVIIPER`. There are no alternate task names.
- This is an explicitly authorized **unsigned named prerelease**, not a signed stable release.

Changes are described in [the tagged release notes](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/release-notes-rc4.6.1.md): Axis Config 360-degree flick-stick calibration, removal of legacy trigger-effect UI, the HidHide alias guard, and backed-up original-name startup-task recovery for #98/#103. Audio/haptics, adaptive-trigger transport and Nintendo rumble tuning remain unchanged from RC4.6.

## Unchanged compatible dependencies

- VIIPER `0.1.5-rc4.6`, source `c2fc304bc21f829ddb35b9e3e3d6b6ac0a9198a7`, published release `388483169`.
- VIIPER binary SHA-256: `9A334912E26FAC09C6DF17BA75A272A41D871D9BBE14934D06F983E101F3DF3C`.
- VIIPER corresponding-source archive SHA-256: `19C59D787B04F9597B47D40D7F6BA18F8837A8F590397A41F5E456F4A80A5787`.
- DS4Updater `2.0.6`, source `173c27b4eb31f369280abce173e31264b317231e`; tested immutable assembly SHA-256: `04B8E564F25CBEC14C65192D9E10408C01218B95B31EAFD1F0511B799E7873F1`.
- USB/IP `0.9.7.7`.

No new broker or updater release is required. Broker assets and provenance were independently downloaded from the existing published release and hash-checked before final DS4Windows package verification.

## Source and CI gates

- Complete local x64 suite: **5,991 passed, zero failed, 11 explicitly gated skips** (`rc461-full-final.trx`).
- New pure installer task-policy coverage: 19 tests. Windows PowerShell 5 task registration/recovery, durable backups, reboot-boundary and alternate-administrator deferral fixtures passed without changing real tasks.
- Additional checks passed: 28 localization-package tests, installer state-machine tests, 22 infrastructure-failure diagnostic cases, 14 MSI metadata cases, renamed executable process guards, both installer-host Release/x64 builds, and whitespace validation.
- First main CI `34876183600` passed tests but stopped at a stale packaging source assertion expecting the removed blind `schtasks /Run` path. The follow-up changed that assertion to the validated COM-task/direct-process launch contract; no package, hash, signing or readiness gate was removed. It also preserved existing alternate-administrator startup deferral across retries.
- Final exact-source main CI [34877299470](https://github.com/hbashton/DS4Windows/actions/runs/34877299470) **passed**, including tests, offline layout, MSI install/repair/uninstall, and the published RC4.5.5 upgrade/profile-preservation fixture.
- Annotated tag creation followed final CI success. Draft release ID: `388604057`.
- Exact-tag draft release build: [34878379743](https://github.com/hbashton/DS4Windows/actions/runs/34878379743).

Independent inspection of CI artifact `10361981691` (`DS4Windows_Installer_Lifecycle_Evidence`) confirmed seven MSI operations with final server/client exit zero, success status zero, and no `Return value 3`: current install/repair/uninstall, then published RC4.5.5 (`5.0.5.5`) install followed by current upgrade/repair/uninstall. The fixture records two preserved synthetic profiles, zero installed registrations after uninstall, and no application/driver launch. It asserts one current MSI registration and expected executable version after install/upgrade/repair, with profile SHA sentinels preserved in installed and AppData profile folders. This validates the main-CI application MSI, not full driver/task runtime acceptance or the final tag-build installer bytes.

## Final package and publication verification

The exact-tag draft build **passed**. All 13 workflow-produced assets were downloaded and verified against the successful run, tag, release ID, Actions uploader, receipt, GitHub digest/size and independent broker pins. Both source ZIP comments identify the corresponding commits. Changelog source blobs match the tagged source (only Git's CRLF-to-LF text-export normalization was needed for the pretty JSON).

| Final asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `DS4Windows_5.0.7.0_Setup_x64.exe` | 202337575 | `B4EFB56959C0BD0965BC4C1C2DCE3842DA958163E5F145C44EB5F33E31CBE858` |
| `DS4Windows_VIIPER_x64.zip` | 137650321 | `0B5B05E491AA01F6EAC58A33B1742FEA62481F7ABDBBC5EBAAE64BE7E7604541` |
| `DS4Windows-VIIPERRC4.6.1-SOURCE.zip` | 56318865 | `37C508709652A0601F965EFD4F3B5B2BD2A1E598525B64C80A66E22B88521EAB` |
| `RELEASE-BUILD.json` | 2130 | `F2FC68048D88A738EF2A720B28B040306091DADEE2A65255D4C2DEF9FADEA13F` |

- Portable verification: **553 files**, 297 dependency assets and 23 application satellite assemblies. Both application PE identities are `5.0.7.0` / `VIIPERRC4.6.1`. Against the prior published RC4.6 ZIP, 503 immutable assets were hash-matched and 14 text assets content-verified against reviewed source. Both broker aliases and the authorized Xbox output identity match their exact pins.
- Passive WiX extraction: **8 Burn payloads, 551 MSI files, 549 identical shared installer/portable payloads**, 549 managed entries and 550 hashed package entries. No downloaded application, setup, MSI or extracted helper was executed locally.
- Actual immutable DS4Updater 2.0.6 policy: four checks passed against actual draft metadata/receipt, including that drafts are not offered or resolved as updates.
- Actual updater transaction in a fresh synthetic RC4.6 portable folder: **all 553 files applied and verified, eight user-file sentinels preserved, obsolete owned DLL removed, both broker aliases verified, no staged transaction left**. This executes the real updater's preparation/application implementation via reflection, not a duplicated transaction algorithm; no production application is launched.

An independent reviewer re-read all draft metadata and hashes, confirmed that the notes match the tagged notes after CRLF-to-LF normalization only, and found no publication blocker. The release was published at **2026-09-14 18:12:31 UTC**, retaining prerelease status and without selecting stable/latest or replacing any assets: [VIIPERRC4.6.1](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.6.1).

All 13 assets were verified again against public GitHub metadata. The actual updater 2.0.6 passed **16 public-release policy cases**: RC4.5.9/RC4.6 update ordering, exact receipt-driven `5.0.7.0` resolution, equal/downgrade rejection, draft rejection and corrupt receipt identity/digest rejection. No hardcoded new RC mapping or updater replacement was needed.

The publication-event workflow [34879398179](https://github.com/hbashton/DS4Windows/actions/runs/34879398179) **passed** the final public-byte download audit: identity and existing-asset verification succeeded; the release build job was skipped. It verified the prior successful draft build without rebuilding or overwriting released files.

This publication record is committed after verification; the annotated release tag remains at the tested source commit. Local downloadable assets are retained under `Desktop/DS4Windows-RC4.6.1-Release/release-assets`; passive extraction proofs are in the adjacent `verified-draft` folder. CI evidence and updater proofs remain under the ignored `isolated_results/rc461-publication` directory. Approximately 34.4 GiB remained free after verification.

## Evidence boundaries

- Local verification does not install software, restart controller apps, change real scheduled tasks or use physical controllers. Actual MSI lifecycle tests run only on a disposable GitHub-hosted Windows runner.
- The #98 log proves verification failed but does not identify its mismatched field. The raw-CIM versus adapted-enum discrepancy is separately reproduced on this machine; it is not claimed as the reporter's proven root cause.
- Windows can still deny task registration. Task-only errors become warnings/direct-launch recovery; backups, ownership checks, driver integrity, reboot/ABI and broker API readiness checks remain required.
- Automated tests do not establish in-game calibration, #81 gameplay acceptance, or an explanation for the earlier unattributed rare movement interruption.
