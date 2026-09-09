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

At this source checkpoint, publication is in progress. Final tag SHAs, build
runs, artifact hashes and public URLs are to be verified from GitHub after
the workflows complete. Earlier local acceptance remains documented in the
[portable startup qualification](2026-09-08-portable-broker-startup.md), but
those local hashes do not identify newly generated GitHub build artifacts.

This operation does not install the candidate, replace the running controller
session, change drivers, or claim new hardware/game acceptance.
