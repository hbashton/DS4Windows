# RC4.5.9 publication ledger

## Published release identity

- [VIIPERRC4.5.9](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.9):
  **Release Candidate 4.5.9 Hotfix — Faster Feedback, Same Detail**.
- Published **2026-09-12T16:46:22Z**, release ID `387633008`, as an unsigned
  named-RC prerelease under the existing draft-first policy.
- Source: `93ce8899a66ab3702824b879e1d7a8f378386db4`, pushed to main before
  tagging. Annotated tag object: `37af38f542688cae4d31e99f959b9bc7c6cc8623`.
- Application, MSI and Burn version: `5.0.5.9`; display/portable identity:
  `VIIPERRC4.5.9`. Both changelogs and the installer upgrade fixture were updated.
- VIIPER remains the complete published **0.1.4-rc4.5.6**, source
  `02d93e403ffde7ad24c1b373c01c6eb04ce02f2e`, binary SHA-256
  `89808A41610997A6DAD0807579B816C18B34C039E03E96FDC8FD2BD68434FFBB`.
- DS4Updater remains **2.0.6**, source
  `173c27b4eb31f369280abce173e31264b317231e`; no broker/updater release needed.

## Validation before publication

- The first local full run was held on an allocation assertion. The same-workload
  investigation, actual-object positive control and unchanged strict-zero gate
  are documented in [the allocation ledger](2026-09-12-native-credit-allocation-gate.md).
  No production GC/JIT/transport change resulted from that investigation.
- Final rebuilt local suite: **5,539 passed, zero failed, 11 existing gated
  skips**. Allocation assertions stayed enabled; an intentional report-copy
  control proves the exact-zero assertion still rejects real allocations.
- [Exact-source main CI 34705080033](https://github.com/hbashton/DS4Windows/actions/runs/34705080033)
  passed the same 5,539 tests, startup/ownership checks, complete packaging,
  offline layout, MSI install/repair/uninstall and published-RC4.5.5 upgrade
  with profile preservation. These installation tests ran on the disposable
  hosted Windows runner, not on the user's installation.
- [Tagged draft build 34705645228](https://github.com/hbashton/DS4Windows/actions/runs/34705645228)
  produced all **13 workflow-owned assets**. No private candidate was substituted,
  and no asset was overwritten. Actions uploader, GitHub size/digests, source
  commit, corresponding-source ZIP comments, checksums and receipt were verified.
- Source archive version/changelog files were independently compared with the
  final Git blobs; only Git's pure CRLF-to-LF text export normalization was allowed.
- Complete portable checks verified **553 files**, **297 dependency assets**,
  **23 language satellites**, **507 immutable byte hashes**, 18 text assets,
  .NET/WindowsDesktop 8.0.30, both broker aliases, Xbox persona and notices.
- Offline installer extraction verified **8 embedded Burn payloads**, **551 MSI
  files**, and exact byte equality of all **549 shared payloads** with the ZIP.
  The bundle/MSI/helpers were not executed on the user's machine.

## Actual updater and publication checks

The immutable updater DLL SHA-256 is
`04B8E564F25CBEC14C65192D9E10408C01218B95B31EAFD1F0511B799E7873F1`.
Its real filesystem transaction applied the final ZIP to a fresh synthetic
RC4.5.8 folder, verified all **553 files**, preserved **eight user-data sentinels**,
removed an obsolete owned file and left no staging transaction behind.
No real profiles, keys, installations or controller apps were modified.

The actual draft receipt/API passed four checks, including rejecting an update
to an unpublished draft. After publication, the unchanged updater passed **21
actual-receipt policy checks**: earlier RCs advance, equal/newer versions do not,
the receipt resolves `5.0.5.9`, drafts and altered receipts are rejected, and
binary downgrade protection remains enforced. The portable bootstrap obtains
this published updater; no stale updater executable was added to the ZIP.

Published metadata and all 13 local/downloaded asset hashes match the verified
draft. The [published-event audit 34706247688](https://github.com/hbashton/DS4Windows/actions/runs/34706247688)
passed against the exact tagged source and successful draft-build receipt.
It downloaded and checked the published bytes without rebuilding or replacing
any asset; the release-build job was correctly skipped for this event.

## Exact downloadable bytes

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `DS4Windows_5.0.5.9_Setup_x64.exe` | 202335027 | `02CBF501A6D1B251070B43A2487EE711B6E55139453E3E0BAFA77E83D7957B09` |
| `DS4Windows_VIIPER_x64.zip` | 137623554 | `1341D3023E1D4712E03B68E6D9D473B2A6CE87143BBB2DEFB21022EAE073B6D3` |
| `DS4Windows-VIIPERRC4.5.9-SOURCE.zip` | 56168246 | `B1D97430799B488B180ED0C97FB663AD70E99240F3111EB9B019FF69C8E1887D` |
| `RELEASE-BUILD.json` | 2140 | `05B74CD4A9686D079A84B31C84442D3E02AD1AE36BE6C40C624F243EBD2362FD` |

Application DLL SHA-256:
`FBBD5DA81B04060B783C35E2FCA1D5950218EC2A9442512E64656C9D3D1D76FA`.
Application EXE SHA-256:
`54DC1D89C04535363C23FD3A275D4120EBE86DA2BCA7EED96AE0FFAF35C10CAA`.

Local assets and offline extraction evidence are under Desktop
`DS4Windows-RC4.5.9-Release`; updater proofs and test logs are under
`isolated_results/rc459-publication/`.

## Feedback acceptance boundary

The [live cadence evidence](2026-09-12-issue81-native-rate.md) covers body-rumble
transition order and host-side physical-writer submission timing. The faster
ordered path also carries adaptive-trigger commands, whose full payloads,
changes and stops have automated preservation coverage. In-game adaptive-trigger
feel, GTA rapid-fire smoothness, radio arrival and actuator latency are not
confirmed by these tests. Gameplay confirmation for issue #81 remains necessary;
no issue was closed as part of this publication.
PCM samples/gain, local Trigger Lab priority and Nintendo rumble tuning were
not changed by this release.
