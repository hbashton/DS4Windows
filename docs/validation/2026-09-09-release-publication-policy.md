# Draft-first release publication, 2026-09-09

The user explicitly authorized an unsigned release candidate. This exception
requires both the same-repository GitHub release's actual prerelease flag and
an exact named `VIIPERRC` ordinal tag (for example `VIIPERRC4.5`). Stable
releases, numeric prerelease tags, beta/unknown tags and malformed names retain
the approved-certificate, signer-thumbprint and timestamp requirements. No
release-body text or workflow-dispatch flag can grant the exception.
The new dispatch interface accepts only these named-RC prerelease drafts.
Stable and other releases retain their signed published-event build path;
dispatching one of their drafts fails before any build or upload.

## Authoritative publication order

1. Preserve upstream history, finish the VIIPER source/CI/tagged draft, then
   publish its verified prerelease with the exact corresponding source ZIP.
2. Repin DS4Windows to that actual CI broker and its notices/source hashes,
   complete validation, and push the final DS4Windows source to `main`. Wait
   for that CI run. The subsequent release build checks the broker's published
   tag commit and source archive hash against the new pinned build notes.
3. Create an annotated DS4Windows tag at the final source commit and a draft
   prerelease. Leave workflow-owned asset names empty.
4. Dispatch `.NET Release` using the tag as both the workflow ref and input
   (for example `--ref VIIPERRC4.5 -f tag=VIIPERRC4.5`). The
   workflow verifies the actual GitHub draft identity, builds, validates, and
   uploads the ZIP, installer, matching sources, notices, fresh checksums and
   `RELEASE-BUILD.json`. It does not publish the draft or overwrite assets.
5. Verify the successful run and complete assets, then publish the prerelease.
   The published named-RC event verifies the successful exact-source dispatch
   run, Actions uploader, source identity and downloaded asset hashes. It does
   not rebuild or replace the already verified files.

The unsigned mode is stated in the workflow log/summary and generated source
record. Checksums describe the actual workflow-produced files, not the earlier
local tester build. Manually prepared assets are not accepted as workflow
receipts. A duplicate name stops uploading; a partial failed upload requires
explicit operator review rather than automatic deletion or replacement.

`ReleaseSigningPolicyTests` exercises the actual workflow's tag pattern and
source wiring, including stable/unknown rejection, draft/API identity, signing
gates, matching source, fresh checksums, no overwrite, and published-RC
verification. These are native-free policy regressions, not proof of a completed
GitHub Actions build, successful code signing, or physical controller acceptance.
