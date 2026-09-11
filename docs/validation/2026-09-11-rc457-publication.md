# RC4.5.7 hotfix publication ledger

## Release identity

- Requested title: `Release Candidate 4.5.7 Hotfix — Sorry, broke Vibration/Triggers`.
- Tag: `VIIPERRC4.5.7`; Windows/MSI/Burn version: `5.0.5.7`.
- Functional fix commit: `16e854bca19bd3738618d33c7c6212469deea0ec`.
- Reuse unchanged published VIIPER `v0.1.4-rc4.5.6`, source `02d93e4`; binary SHA-256 `89808A41610997A6DAD0807579B816C18B34C039E03E96FDC8FD2BD68434FFBB`.
- DS4Updater 2.0.6 remains compatible. No updater or broker source change is part of this hotfix.

## Evidence and boundaries

The regression analysis, original-release failing reproduction and corrected full-suite evidence are recorded in `2026-09-11-dualsense-bt-rc456-regression.md`. The complete private candidate was built from the functional fix and verified before physical testing.

The initial isolated lab launch omitted DualSense support and the new executable's HidHide allowance. Both test-environment omissions were corrected; the log then confirmed Bluetooth DualSense detection, profile `dsm`, virtual DualSense association and direct VIIPER PCM passthrough. The lab handoff checks now enforce both discovery prerequisites. These were isolated lab setup changes, not a change to normal portable startup or a controller protocol regression.

The user reports having tested haptics on the private candidate. Native game-trigger transport and per-trigger Trigger Lab arbitration were reviewed; live acceptance in a trigger-supporting game remains pending. Both lab applications later disappeared without application exception/normal-shutdown records around a host application update; the precise termination mechanism is not established, and no unproven crash fix is claimed.

Publication follows the existing draft-first policy: final source on main and CI green, annotated tag, exact-tag draft release workflow, verified workflow-owned assets, then publish the prerelease. Public run IDs, artifact hashes and publication confirmation will be recorded after verification; this source record alone is not proof of publication.

## Publication completed

- Final release source: `5cf73e05f546df4ff8bf558189faaf5c8b6dbad9`, pushed to `main`, then annotated as `VIIPERRC4.5.7` without rewriting history.
- Exact-source [CI 34651273372](https://github.com/hbashton/DS4Windows/actions/runs/34651273372) passed: **5,394 tests passed, zero failed, 11 gated skips**; 11 read-only MSI metadata cases; complete package build; install/repair/uninstall; hash-pinned published RC4.5.5 upgrade with profile preservation and single-registration checks.
- Exact-tag [draft release build 34652096385](https://github.com/hbashton/DS4Windows/actions/runs/34652096385) passed and uploaded all **13** workflow-owned assets. No local private binaries were substituted.
- Published [Release Candidate 4.5.7 Hotfix — Sorry, broke Vibration/Triggers](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.7) at **2026-09-11T22:12:23Z**, release ID `387359081`, as an unsigned prerelease.
- [Post-publication verification 34652956615](https://github.com/hbashton/DS4Windows/actions/runs/34652956615) passed. Exact source, successful draft-build receipt, Actions uploader and all public asset bytes matched; nothing was rebuilt or overwritten during publication.

Final payloads:

- Installer `DS4Windows_5.0.5.7_Setup_x64.exe`: 202,271,241 bytes; SHA-256 `272A0DBE3ED292A2351888B0F728EA65A4B24C389ABFCFF8290BD6D8CC807B46`.
- Portable `DS4Windows_VIIPER_x64.zip`: 137,620,880 bytes; SHA-256 `AAE27EEDE230EFEC73DCE0BBC3504E209E0FB3C4A10A7BE92BF47EE163C40985`.
- Application DLL SHA-256 `42DEEACED59C1B9E221C5F204696A4510223E7212BAAFEE0A4BB0783EE775363`; EXE SHA-256 `DFB32DC8B15A95A3D8E2B10E00E2E6DACF7CDE45909AA64B5C4204CE5B323090`.
- Independent inspection checked all **553 portable files**, **297 dependency assets**, **23 language satellites**, unchanged .NET/WindowsDesktop 8.0.30 dependencies, both broker aliases, authorized Xbox persona and notices. Offline installer extraction verified **8 embedded Burn payloads**, **551 MSI files**, and exact equality of all **549 shared payloads** with the ZIP.
- Both source archive commit comments matched. The pretty changelog's Windows `git archive` CRLF export was compared to its exact committed Git blob after only CRLF-to-LF conversion; minified JSON matched raw. The local checker needed an ordinal BOM test because the culture-sensitive string operation treats that character as ignorable. Those checker corrections did not alter any release asset, raw checksum or provenance gate.
- Immutable updater **2.0.6** applied the actual final ZIP in an isolated synthetic RC4.5.6 filesystem transaction: **553 files** verified, **8 user-data sentinels** preserved, obsolete owned file removed, no staged transaction left, both broker hashes correct. The actual draft stayed ineligible. After publication, **18 policy checks** passed against the unmodified published API/receipt, including forward eligibility from RC4.5.6 and exact PE version resolution to 5.0.5.7 without a hardcoded version map.
- Final downloads and evidence are retained under `Desktop/DS4Windows-RC4.5.7-Release`. No live application, installed driver or Program Files installation was changed during publication. This ledger update does not move the release tag or alter its bytes.
