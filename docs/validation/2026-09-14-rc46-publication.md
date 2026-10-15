# RC4.6 publication ledger

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Published identity

- [VIIPERRC4.6](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.6):
  **Release Candidate 4.6 Hotfix — Clearer Audio, Reliable Feedback & Disconnects**.
- Published **2026-09-14T15:26:47Z**, release ID `388498525`, as an unsigned
  named-RC prerelease under the existing draft-first policy. No signing-policy
  exception was added or broadened.
- Final tested source: `96b59e37bb7649d0f00004f5e58c57d988959dfb`, pushed to
  `main` before tagging. Annotated tag object:
  `01c0de4ed34008b9ba0e6327a3ed86773f91ec85`.
- Application, MSI and Burn version: **5.0.6.0**; display/update-channel identity:
  **VIIPERRC4.6**. Both changelogs, installer defaults and upgrade fixture agree.
- Detailed changes since RC4.5.9 are in the [tagged release notes](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/release-notes-rc4.6.md).
- USB/IP remains **0.9.7.7**. DS4Updater remains the published **2.0.6**, source
  `173c27b4eb31f369280abce173e31264b317231e`; no new updater release was needed.

## Companion broker publication and source binding

[VIIPER v0.1.5-rc4.6](https://github.com/hbashton/VIIPER/releases/tag/v0.1.5-rc4.6)
was published first at **2026-09-14T14:57:52Z**, release ID `388483169`, from
source `c2fc304bc21f829ddb35b9e3e3d6b6ac0a9198a7`. The Windows PE version is
**0.1.5.0**; product version is **0.1.5-rc4.6**.

- [Main CI 34856384005](https://github.com/hbashton/VIIPER/actions/runs/34856384005)
  and [tagged build 34857881208](https://github.com/hbashton/VIIPER/actions/runs/34857881208)
  passed. Infrastructure-only download/artifact failures required retries;
  source, workflow and checksum protections were not weakened. The successful
  Windows AMD64 artifact remained the first-attempt bytes.
- All 12 published broker assets were downloaded and checked. The exact
  corresponding-source ZIP has 808 entries and its Git comment identifies the
  full broker commit. It was not reconstructed from an unrelated checkout.
- DS4Windows was repinned to the actual tagged CI executable, its notices,
  full build/provenance tuple and corresponding-source digest before DS4 CI.
  Both bundled executable aliases match the published CI executable.
- The dedicated synthetic process-test broker is not a shipped artifact.

Broker executable SHA-256:
`9A334912E26FAC09C6DF17BA75A272A41D871D9BBE14934D06F983E101F3DF3C`.
Broker source ZIP SHA-256:
`19C59D787B04F9597B47D40D7F6BA18F8837A8F590397A41F5E456F4A80A5787`.

## Tests and installer lifecycle

- The initial post-pin full run failed an exact-zero allocation assertion.
  Publication was held. A native allocation-profiler investigation reproduced
  counter growth without object allocations during GC allocation-context
  accounting; the same real writer's intentional report-clone positive control
  was detected. The existing bounded strict measurement scope was applied to
  this test, without raising the zero-byte limit, adding retries, changing
  production GC behavior, or attributing the original untraced sample with
  certainty. See the [allocation evidence](2026-09-14-ds4-policy-allocation-gate.md).
- Final rebuilt local x64 suite: **5,717 passed, zero failed, three live-audio
  gated skips**, including eight opt-in isolated C#/Go lifecycle process tests.
  Focused allocation regression/positive-control coverage: **48 passed**.
- [Exact-source main CI 34859888276](https://github.com/hbashton/DS4Windows/actions/runs/34859888276)
  passed **5,709 tests, zero failures, 11 opt-in skips**. The eight process tests
  are opt-in there; this accounts for the difference from the local total.
- CI also passed setup diagnostics, startup ownership/registration tests,
  complete packaging, offline layout, MSI install/repair/uninstall, and upgrade
  from published RC4.5.5 to 5.0.6.0 with two profiles preserved. The upgrade
  fixture found zero installed registrations after uninstall.
- These installation lifecycle tests ran on the disposable hosted Windows
  runner. The user's installed applications, drivers, profiles and controller
  sessions were not changed for publication validation.

## Tagged artifacts and package completeness

[Tagged draft build 34861077780](https://github.com/hbashton/DS4Windows/actions/runs/34861077780)
passed on its first attempt at the exact tested source and uploaded all **13
workflow-owned assets**. No local build was substituted, and no asset was
overwritten. The release body exactly matched the tagged release notes.

Verification bound the Actions uploader, run/source identity, release ID,
GitHub sizes/digests, checksum manifest, corresponding-source ZIP comments and
`RELEASE-BUILD.json` to the downloaded bytes. Changelog source files matched
their committed Git blobs with only pure CRLF-to-LF archive-export normalization
where needed. Independent broker evidence came from its already verified CI
publication, not from the DS4Windows candidate's own declarations.

The portable ZIP and extracted folder contain **553 files**, **297 dependency
assets**, **23 language satellites**, the self-contained .NET/WindowsDesktop
**8.0.30** runtime, the required unchanged authorized Xbox persona, broker and
notices. Baseline comparison verified 503 immutable byte hashes and 14 text
assets; reviewed app/broker replacements were pinned independently.

Passive WiX extraction checked **eight embedded Burn payloads**, **551 MSI
files**, **549 shared file hashes** equal to the portable ZIP, 549 ownership
entries and 550 package-manifest hashes. It did not execute setup, MSI or
extracted helpers on the user's PC. Hosted lifecycle tests used the CI package
from the same source; the final release package received these byte-level
composition checks rather than a second host installation.

## Real updater transaction and public checks

The immutable DS4Updater 2.0.6 DLL has SHA-256
`04B8E564F25CBEC14C65192D9E10408C01218B95B31EAFD1F0511B799E7873F1`.
Its actual filesystem transaction applied the final release ZIP to a fresh
synthetic RC4.5.9 fixture and verified all **553 installed files**. It preserved
eight user-data sentinels (profiles, actions, linked profiles, auto profiles,
Joy-Con pair data, key placeholder, plugin placeholder and log), removed an
obsolete owned DLL and all five old `VIIPER-0.1.4-rc4.5.6` extras files, and left
no staged transaction. Neither application was launched.

The actual draft receipt/API passed four checks including draft rejection.
After publication, **23 actual-receipt policy cases** passed: earlier RCs
advance to 4.6, equal/newer versions do not, the receipt resolves **5.0.6.0**
without a hardcoded release map, drafts and altered receipts are rejected, and
binary downgrade protection remains enforced. The existing bootstrap obtains
the published updater; no stale updater executable was added to the ZIP.

All 13 published asset sizes/digests still match the downloaded, verified draft.
The [published-event audit 34862199728](https://github.com/hbashton/DS4Windows/actions/runs/34862199728)
passed at the exact tagged source. It downloaded and verified the public bytes
against the successful draft-build receipt without rebuilding or replacing
assets; the release-build job was correctly skipped. Its byte-verification step
completed at **2026-09-14T15:27:34Z**.

## Exact downloadable bytes

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `DS4Windows_5.0.6.0_Setup_x64.exe` | 202336131 | `DBC7DBAC06864D58904D5D700677054EDEE3B8E0241E2FC704448CB77F7D57A3` |
| `DS4Windows_VIIPER_x64.zip` | 137637774 | `2A69A3D1ED9BFBCFCA50327DC604F0E4E072333FD5726E53882EAE089FBDC49A` |
| `DS4Windows-VIIPERRC4.6-SOURCE.zip` | 56242462 | `4CE65888FFE65D86914A489ECBC825B25FE6E0F498D0E524A02B64AA12A1A820` |
| `VIIPER-0.1.5-rc4.6-SOURCE.zip` | 2103125 | `19C59D787B04F9597B47D40D7F6BA18F8837A8F590397A41F5E456F4A80A5787` |
| `RELEASE-BUILD.json` | 2126 | `8049EF5ABEDD23AF777BF43F67F98F8C5BCF97D4CF0FD36C153D56A70C40B727` |

Application DLL SHA-256:
`DC573CC6C9214B968A7382E6F39AA51705F34AD307548B28624A0F89DA5ACB20`.
Application EXE SHA-256:
`9C2383FE0A911B053243AD25D9AC21DCBC7EAABFB778C0CA44F1D7398BE8A5D6`.
Remaining standalone notices/checksums are bound by the published receipt.

Local downloads and extraction evidence are under Desktop
`DS4Windows-RC4.6-Release`; test/updater proofs are under
`isolated_results/rc46-publication/`. Initial failed-test evidence is retained.

## Acceptance limits

The fixes cover audio source recovery/rate conversion/retirement, bounded and
ordered cross-controller feedback delivery, explicit manual Bluetooth removal,
and a stale Game Bar profile-eligibility guard. They do not retune Switch 2
rumble gain/encoding/cadence or replace RC4.5.9's faster DualSense feedback path.

Final listening, physical disconnect behavior and gameplay feel still require
tester confirmation. The rare once-in-a-day movement interruption remains
unattributed; the Game Bar guard is not proof that incident is solved. Genuine
disconnect/shutdown neutral-report safety remains intact. No unlimited
physical output rate, zero end-to-end latency, HidHide repair or completed
issue #81 gameplay acceptance is claimed. No issue was closed in this release
publication step.
