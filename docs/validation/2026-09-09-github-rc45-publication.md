# RC4.5 GitHub publication

The user authorized updating the versions, fast-forwarding both repositories to
`main`, building and uploading the release candidates. The preceding tester
kit was local only; it was not a public release or a push to either main branch.

## Version and source rules

- DS4Windows assembly/file/MSI/bundle: `5.0.5.0`.
- DS4Windows package: `5.0.5.0-rc4.5`; release tag: `VIIPERRC4.5`.
- VIIPER tag/API version: `v0.1.3-rc4.5`; Windows file version: `0.1.3.0`;
  Windows product version: `0.1.3-rc4.5`.
- Both releases remain prereleases, not stable/latest promotion.
- Existing immutable release tags, including DS4Windows RC4.4, are preserved.
  VIIPER's existing snapshot workflow may refresh its separate `dev-snapshot`
  following a main push; that is not either new candidate release tag.
- Main advances only by fast-forward from the freshly fetched remote head.
  No unrelated upstream commit is discarded.

The initial remote audit found DS4Windows main at `4d41a61`, with the local
feature branch 44 commits ahead and none behind. VIIPER main was `f5d097b`,
with the local feature branch 21 ahead and none behind. The newest public
release listings were DS4Windows RC4.3 and VIIPER v0.1.2. These are audit
snapshots, not a substitute for rechecking ancestry immediately before pushing.

## Publication gates

The existing DS4Windows published-release workflow required unavailable signing
secrets even for release candidates. The user's earlier instruction explicitly
requested a no-certificate release. The new path permits unsigned output only
for a verified prerelease with an exact named VIIPER RC tag; stable and other
release types retain signing and approved-signer verification. Unsigned RCs
must be labeled clearly. No driver signatures or broker identity checks are
removed.

Draft-first builds prevent an incomplete public release from being offered to
updaters. The public artifacts must come from their verified workflow/source
identity and have matching checksums. The VIIPER Windows executable selected
from the release build becomes DS4Windows' bundled, immutable broker input;
runtime, installer, source provenance and both portable copies are repinned
together. A separately rebuilt executable with the same version is not assumed
to have the same hash.

The broker's earlier release workflow classified every semver tag as stable.
RC tags now remain drafts/prereleases and do not trigger publication to client
package registries. The release archives preserve the full GPL and dependency
notices, plus the local tray modification notice and Apache license.

The initial broad broker lint run also identified findings beyond the earlier
scoped command/tray lint check. Publication requires the actual full CI lint
and test gates; the earlier scoped result is not represented as a full pass.

## Evidence status

After adding the release-policy tests, the local complete CI-equivalent suite
passed 4,536 tests with zero failures and 11 existing opt-in skips. The separate
`Name~Alloc` run passed 153 selected cases: the existing 152 allocation-named
cases plus one portable source-wiring case whose name contains `Allocating`.
This is not a claim of 153 measured allocation assertions. No exclusion or
allocation assertion was relaxed.

All eight opt-in DS4Windows/Go process-integration cases also passed using a
new Release-tag test peer built from broker `f8aa588`. Its SHA-256 was
`8BA2AA21AEEC86623699C31256075050B964D0D19D6FC8D038B22B0F764211C2`.
This Desktop-only peer uses a public synthetic key, ephemeral loopback sockets,
and simulated native attachment; it is not the shipping executable and must
never be packaged. No installed broker or controller was used for these cases.

The first real VIIPER main CI run at `f8aa588` exposed two additional build
scopes: nested C++ DTO serialization and a redundant conversion in a CGO-only
test. These were not covered by the earlier CGO-disabled Linux-target lint.
The first run is a failure, not release evidence; publication remains gated on
their correction and a fresh successful run.

The next broker main run at `324349c` passed lint and all four generated-client
builds, then exposed Windows-QPC integration tests being run exclusively on
Linux. The production CFBK v1 clock intentionally returns unavailable outside
Windows; changing it to a different epoch would violate the cross-process
contract. The identical full coverage suite passed locally on Windows. The
correction requires a Windows CI test job for those real-clock integration
cases while retaining Linux's portable tests, CGO coverage, and clock rejection.
It does not raise deadlines or substitute a fake production clock.

DS4Windows main run `34337631663` at `9af6a74` passed 4,536 tests and the
native-free setup checks, and built MSI/Burn with zero warnings or errors. Its
packaging validator still required the old workflow's direct event tag and
unconditional signing tokens. The validator now checks the verified identity
outputs, exact RC exception, retained stable signing, source/hash gates and
no-overwrite publication. All 19 Python package/policy tests pass, including
33 negative workflow mutations. Full validation of the existing packaged tree
against the corrected source policy also passes. Real MSI lifecycle CI still
requires the next complete run; successful compilation alone is not that gate.

The external published DS4Updater v2.0.4 (source
`ae7ac56a3f3496b13aeed3f0ca1897d18f0fe702`) compares the selected RC tag number
with the installed PE version in its final check. That older mismatch already
existed for RC4.3. Its normal auto-launch can reopen the updated app despite
the false failure message; there is no supported separate expected-PE argument.
The full installer is the recommended managed update path, and fresh-folder
ZIP extraction remains the portable path. This operation does not modify or
publish a separate DS4Updater repository.

## Final broker selection and publication

VIIPER main run `34338656190` and tagged run `34339492052` both passed at
`a9111494bad3fe56a509fe094792d089fc506f36`. These include the full Linux and
Windows gates, four generated-client builds, all executable platforms and both
native libraries. The annotated `v0.1.3-rc4.5` tag points to that source; its
tag object is `875dd3ee1a395c1f2c086a086e2b888dcc0f0acf`.

The Windows x64 release ZIP hash is
`527D0EFADE77F96E0CB31D3F74BA8178C77539135C8026E5CA0F8470A4CE22BD`.
Its executable hash is
`F1ECEF158F02D0BDCD1296C8D5097A281169081D0D59C1A8971592FAC78155EF`.
Archive contents, PE versions, embedded Go/source/build metadata, clean VCS
identity and complete license bytes were verified without executing the broker.
The exact-source ZIP hash is
`D20AE4DEECE5F1C75EE8041742DC6D77608660D2DD7476C786EEF046768DD5F1`.
All ten workflow assets and three source/checksum records matched GitHub's
reported digests after upload. Release ID `385419029` was published at
`2026-09-09T10:37:26Z` as a prerelease; stable/latest remains `v0.1.2`.

DS4Windows' bundled executable, runtime/setup identity checks, provenance and
notice pins now bind that same release artifact. The two systray notices are
preserved byte-for-byte from CI; their line-ending-only differences from the
previous local copies do not change license text. The former local candidate
hashes are superseded, not aliases for the newly selected executable.

Intermediate DS4Windows main run `34339061056` passed its complete tests,
package checks, MSI/Burn build, offline layout and hosted-runner MSI install,
repair and uninstall. Its old broker input is not the final release artifact.
After the final broker repin, the local complete suite again passed 4,536 tests
with zero failures and the same 11 opt-in skips.

An actual authenticated draft lookup showed that GitHub's published-by-tag REST
endpoint returns 404 for drafts. Dispatch now resolves an exact, unique tag
through the paginated release list and rechecks the numeric release ID; the
pre-upload check uses that same ID. Published-event verification retains its
published-by-tag checks. The exact resolver passed against the real VIIPER
draft and seven offline missing/duplicate/changed-identity cases. All 19 Python
tests and 37 negative policy mutations pass; stable signing is unchanged.

The final local source run passed 4,539 tests with zero failures and 11 opt-in
skips. Unlike the unchanged CI filter, this run also included the three legacy
profile/settings cases (`CheckSettingsSave`, `CheckWriteProfile`, and
`CheckJaysProfileRead`); these account for the count difference. The separate
153 allocation-named selection passed again. All eight process-integration
cases passed again using a peer rebuilt from the final tagged `a911149` source;
its executable hash remained the same `8BA2AA21...` recorded above. No production
broker was executed or controller touched for these tests.

At this source checkpoint, DS4Windows publication is in progress. Final tag SHAs, build
runs, artifact hashes and public URLs are to be verified from GitHub after
the workflows complete. Earlier local acceptance remains documented in the
[portable startup qualification](2026-09-08-portable-broker-startup.md), but
those local hashes do not identify newly generated GitHub build artifacts.

This operation does not install the candidate, replace the running controller
session, change drivers, or claim new hardware/game acceptance.
