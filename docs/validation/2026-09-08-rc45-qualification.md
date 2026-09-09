# RC4.5 tester-package qualification

Local date: 2026-09-08; build work continues into 2026-09-09 UTC.

This records the initial `12b2485` RC4.5 checkpoint, before normal portable
broker auto-start was added. Its package hashes and counts below are historical.
The [portable startup follow-up](2026-09-08-portable-broker-startup.md) records
the replacement packages and their qualification. The current distribution's
`SHA256SUMS.txt` identifies the files to distribute.

## Candidate identity

- Release/channel marker: `VIIPERRC4.5`.
- DS4Windows assembly, file, MSI and bundle version: `5.0.5.0`.
- Package version: `5.0.5.0-rc4.5`.
- Bundled VIIPER: `v0.1.3-rc4.5`, source
  `6205074f209e488c237d7eb475383f49d7f3cab9`, PE `0.1.3.0`.
- VIIPER SHA-256:
  `F4A86C6D00CDD30FACF4F7E2C0FB3C69761576119514678D055E65EA28CDBB95`.
- USB/IP remains the pinned 0.9.7.7 package and signed driver files.

This is an unsigned tester candidate composed through the existing local build
path. The public workflow's required signing policy is unchanged. Automated
qualification does not substitute for a disposable-machine install/upgrade/
repair/uninstall pass or the remaining physical-controller acceptance matrix.

## Update and registration guards

The previous update policy offered any different prerelease to an installed
prerelease. A locally installed RC4.5 could therefore be offered the older
public RC4.3. Named VIIPER Beta/RC releases now compare phase and numeric
ordinal, separately from the application's numeric Windows file version.

- Initial regression run: 12 failed, 17 passed, proving the rollback cases.
- First correction: all 29 release-policy cases passed.
- Independent review identified a second rollback after a future transition
  from a named RC to a numeric prerelease. Added cases reproduced 10 failures
  among 41 policy cases, including partial numeric-tag parsing.
- Final focused run: **54 passed, zero failed/skipped**, covering all 41 policy
  cases, six new installer registration cases and seven existing helper cases.
  Evidence: `rc45-upgrade-and-update-final.trx`.

Only genuinely unmarked legacy prereleases retain the one-time bootstrap rule.
Unknown marked prereleases do not receive guessed automatic ordering. Numeric
prereleases require a strictly newer binary base; different suffixes at one
base remain manual. Stable promotion at the same normalized binary version
remains permitted. Selection is still publication-date based: republishing an
older release may suppress a newer offer, but must not cause a downgrade.

The [registration audit](2026-09-08-installer-duplicate-arp-entries.md) ties the
duplicate-entry causes to actual install logs and production helper planning.
No installer upgrade-family GUID was changed and no broad automatic registry
cleaner was introduced. A separate, authorized machine repair reversibly hid
18 obsolete entries after backups; one current visible entry remains. It did
not uninstall or delete the old cache, alter installed application bytes, or
restart either running application.

## Broker source and license provenance

The broker was rebuilt from a clean checkpoint with the release build tag,
code generation excluded, and embedded VCS state `modified=false`. A repeat
build produced the same executable hash. Version/help, the release Go suite,
targeted command/tray race tests and changed-line lint passed. Existing
unrelated format findings were not silently suppressed.

The previously unlicensed kong-yaml v0.2.0 dependency was explicitly advanced
to `v0.2.1-0.20240409173824-a56dc1466c79`, whose source includes
[the upstream MIT grant](https://github.com/alecthomas/kong-yaml/blob/a56dc1466c79ffc467aac6e158b878b2d7903bd2/COPYING).
The existing nested YAML behavior is preserved with a resolver adapter and
four regression cases. The local tray fork's complete Apache license and
modification notice, the broker's GPL text, aggregate dependency notices and
unabridged build notes are explicit package assets. Matching broker source is
distributed alongside the installer. This closes that identified notice/pin
gap; it is not a blanket legal certification.

The strict provenance format retains its original 17 key/value fields plus
heading. Independently pinned hashes cover the full build notes and license
documents. Narrow Git `-text` attributes preserve those byte-hashed artifacts
across Windows/Linux checkouts. Four Python packaging fixtures passed, including
byte preservation, managed-file ownership, ZIP inclusion and WiX harvesting of
all required broker notice files.

Staging confirmed all six byte-pinned artifacts (binary and five documents)
match their raw working-tree Git blob identities. The two original CRLF license
documents have file-specific whitespace attributes; the aggregate's original
trailing blank lines are retained. Neither document was reformatted to satisfy
source whitespace checks or silently given a new release hash.

## Final package acceptance

- `rc45-complete.trx`: final Release/x64 source rebuilt, **4,463 passed,
  zero failures, 11 existing opt-in skips** (4,474 total).
- `rc45-repeat.trx`: separate repeat, **4,463 passed, zero failures,
  the same 11 skips**.
- `rc45-allocation.trx`: all **152 allocation-named cases passed**, no
  failures or skips. No allocation assertion was disabled or loosened.
- `rc45-xbox-process-interop.trx`: all **eight opt-in process interop cases
  passed**, no failures/skips, with all eight success markers. The separately
  compiled, release-tagged Go test peer came from clean broker source
  `6205074f209e488c237d7eb475383f49d7f3cab9`. It exercised real authentication,
  input/feedback streaming and retirement with ephemeral loopback listeners,
  synthetic keys and fake native attachment. No hardware/driver was contacted;
  every test peer exited. This establishes source-level protocol compatibility,
  not physical input or a server startup test of the packaged product EXE.
  The peer and synthetic key are confined to private `_build` test staging.
- Both full runs retain the three existing CI exclusions (`CheckSettingsSave`,
  `CheckWriteProfile`, `CheckJaysProfileRead`); no new exclusion was added.
- Self-contained Windows x64 publish succeeded in a new, empty Desktop staging
  directory. Published PE versions and channel marker match the identity above.
- The published file inventory contained no reparse points, user profiles,
  controller stores, logs, lab-data, dumps, traces or private key files. The
  portable ZIP was composed from that new publish, not a running lab directory.

- WiX MSI and Burn builds succeeded with zero warnings/errors. Default ICE
  validation remained enabled, with the pre-existing ICE61 exception unchanged.
- The final bundle was extracted and its MSI, setup-helper and bootstrapper
  payload hashes verified by the build gate. Required payload, provenance,
  localization and release-marker validation passed before atomic publication.
- USB/IP reboot-boundary simulation, exact-SID startup-task ownership/rollback
  simulation, and installer transaction-state simulation passed. These are
  simulations, not a real installation or reboot of the build host.
- Two resource-only child probes against packaged assemblies passed from both
  package and outside working directories: localized and fallback strings,
  TaskScheduler satellites and rejection of an unrelated assembly. They did
  not initialize the application or controller service.
- Independent audit checked all **547 published files**; the installer manifest
  matches the other 546 files by size and SHA-256. Every one of the portable
  ZIP's **546 files** matches the fresh publish byte-for-byte (the additional
  `package-manifest.json` was created later for the installer). No private
  profiles, authentication keys, captures, test peers or lab clients ship.
- The final installer is **200,191,091 bytes**, SHA-256
  `982023115E9C5D11D0391D9EFB631D9818617C519B18909457A465AFB74F0228`.
  Authenticode explicitly reports `NotSigned`.
- The portable ZIP is **132,192,631 bytes**, SHA-256
  `77F2D07A90ED3F54757F7E6C9D33398D17012940BA0B5766E5387CA44DBA3480`.

The distribution folder includes both matching source archives, preserved
license notices, this qualification record and the cumulative RC assessment.
`SOURCE-REVISIONS.txt` and `SHA256SUMS.txt` identify the final source checkpoint
and exact delivered artifacts. No public tag, push or release was performed.

Earlier source-checkpoint results in the
[controller rollup](2026-09-08-controller-followup-rollup.md) are historical
evidence, distinct from the new runs recorded here.

## Remaining acceptance boundaries

The live portable b93 session was not replaced for packaging. No actual MSI
transaction or new controller/game acceptance is claimed on this host in this
release-preparation pass. Bluetooth headset audio remains unsupported; the
native-driver backend and sub-millisecond end-to-end latency are not shipping
claims. See the [RC4.5 assessment](../RELEASE_CANDIDATE_4_5.md) for the full
feature scope, demonstrated hardware evidence and focused tester scenarios.
