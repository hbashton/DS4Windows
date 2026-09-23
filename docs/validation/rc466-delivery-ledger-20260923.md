# RC4.6.6 startup, dependencies and Edge delivery

## Requested outcome

Audit startup, detection, dependency checks, repair and update as one lifecycle;
fix demonstrated races and failures; finish the evidence-backed Edge review;
publish the verified RC4.6.6 installer and complete portable package.

## Delivery identities

- DS4Windows: `VIIPERRC4.6.6`, Windows binary `5.0.12.0`.
- VIIPER: `v0.1.9-rc4.6.6`, Windows binary `0.1.9.0`.
- DS4Updater: `v2.0.8`.

Existing publication rules remain in force: exact-source CI, immutable artifacts,
draft-first verification, matching source/notice hashes, updater and broker
published before DS4Windows, no replacement of mismatched release assets.

## Implemented and reviewed

- Prior source checkpoint: startup deadlines and cleanup ownership, bounded
  prerequisite output collection, diagnostic exit codes, verified update
  deployment selection, Edge common feedback and configuration guards.
- Follow-up audit: final single-instance acquisition, invalid portable identity,
  cancellation during prerequisite checks, accepted API connection cleanup,
  physical firmware-report validation, Edge profile-unlock acknowledgement.
- Root: updater release, broker build/provenance pinning, application versions,
  final tests, installer/portable verification, release publication.

Additional verified fixes: refresh dependency snapshots at start/repair/readiness
boundaries; dispose WMI enumeration resources and reject unverified loaded Citrix
filters; gate VIIPER's unconfigured standalone tray autostart behind its existing
developer opt-in. The WMI timeout is enumeration-only, not a universal COM
deadline. Accepted API clients are canceled and closed, while the USB ownership
fence rejects late mutations; shutdown does not promise to join arbitrary code.

Edge follow-up also covers source-or-target configuration guards, high-quality
Bluetooth haptics negotiation for all four base/Edge pairings, and synthetic gyro
calibration matching the canonical 16 units per degree/second. Native trigger
blocks and independent PCM delivery retain regression coverage.

## Test evidence before final packaging

- Full Release/x64 DS4Windows suite: **7,130 passed, 12 opt-in skipped**.
  `artifacts/rc466-final-audit/rc466-final-source.trx`.
- Separately enabled all eight real Go/C# synthetic process interoperability
  cases: **8 passed, 0 skipped**. Encrypted API, activation/cancel/deadline,
  loopback retained USB input and haptic channels, impulse policy and failed-stop
  fencing; no installed drivers or physical-controller writes.
  `C:/Users/hbash/Desktop/DS4Windows-RC466-XboxInterop-20260923/results/rc466-xbox-interop.trx`.
- All eight isolated installer/startup PowerShell CI fixtures passed, covering
  renamed executables, prompt/mutex ownership, task collision/recovery/backup,
  diagnostics and MSI version metadata. Actual MSI lifecycle runs are reserved
  for disposable hosted CI machines.
  `artifacts/rc466-startup-audit/powershell-ci-fixture-results.json`.
- Broker full normal and Release-tag Go suites passed. Seven scoped concurrency
  packages passed with `-race`; final Edge/calibration additions separately
  passed normal/race checks. Independent lifecycle reviews found no remaining
  release blocker.
- Updater 2.0.8 source CI
  https://github.com/hbashton/DS4Updater/actions/runs/35871043966 and published
  release verification
  https://github.com/hbashton/DS4Updater/actions/runs/35872323421 passed.
  Exact source `20004b6cc14a8d321dea534cb9fb77cc8f61246f`; both published x64/x86
  assets match the source-CI artifacts by SHA-256 and size.
- Actual CI updater assembly, passively extracted from the published x64
  executable: **412 passed, 0 failed, 1 optional RC4.6.2 archive test skipped**.
  Real RC4.5.1 and RC4.6.1 portable archive transactions passed. No downloaded
  apphost was launched. Evidence:
  `D:/DS4Windows-RC466-Audit-20260923/actual-ci-updater-208-proof/IMPLEMENTATION-PROOF.json`.
- Final version/payload-pinned application suite repeated successfully:
  **7,130 passed, 12 opt-in skipped**;
  `artifacts/rc466-final-audit/rc466-final-pinned.trx`.
  All **34** localization/package-composition tests also passed.
- Compiled RC4.6.6 emitters and the actual CI 2.0.8 updater policy/parsers:
  **5/5** handoff cases passed using the prior published release receipt as a
  non-installing fixture. Exact new-release receipt verification follows asset
  creation. No updater launch or application shutdown was performed.

## Verification boundaries

Do not claim zero races or universal hardware compatibility from tests alone.
No production controller session, driver, registry or installed app is mutated
for these tests. Temporary fixtures, owned synthetic helper processes and local
loopback servers are permitted. Physical Edge hardware acceptance remains open;
virtual onboard-profile storage is not implemented or advertised as working.

The initial broker tag `v0.1.8-rc4.6.6` did not publish: Linux lint found an
unused non-Windows wrapper after startup switched to the context-aware helper.
It was removed, without changing Windows behavior. The failed tag is not moved
or reused; the replacement broker uses `v0.1.9-rc4.6.6`.
Its source is `b78e31e93e84b4cb7c4b4c15b8aace10749106d2`; Linux lint passed on a
fresh LF checkout after that correction. The previous local Windows checkout's
line-ending-only gofmt diagnostics were not fixed by rewriting unrelated files.

The corrected tagged broker CI passed all Linux/Windows normal and Release
suites, lint, notice checks, binaries and client builds:
https://github.com/hbashton/VIIPER/actions/runs/35874100761.
Published https://github.com/hbashton/VIIPER/releases/tag/v0.1.9-rc4.6.6 after
verifying archive/flat executable/source ZIP hashes. Bundled executable SHA-256:
`9392A49E619E1D9B7956EA4BEDAFD2E74CBF5065C011554E80F756D1524F892B`.
The exact tagged CI receipt and matching notice/source hashes are recorded in
`extras/VIIPER-0.1.9-rc4.6.6-BUILD-NOTES.txt`; all five notices retain their bytes.

Final results, source identities and published URLs will be recorded here.
