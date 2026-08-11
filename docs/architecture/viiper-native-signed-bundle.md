# VIIPER native signed-bundle boundary

DS4Windows accepts one production VIIPER native UdeCx package. It is an
immutable release input, not something the DS4Windows build downloads or
reconstructs. This commit defines and tests that intake boundary; it does not
contain a production bundle or lock.

The required source layout is exact and case-sensitive:

```text
extras/
|-- viiper-native-udecx.lock.json
`-- viiper-native-udecx/
    |-- viiper.exe
    |-- ViiperUdeCtl.exe
    |-- submission-manifest.json
    `-- driver/
        |-- ViiperUde.inf
        |-- ViiperUde.sys
        `-- ViiperUde.cat
```

No PDB, test certificate, nested archive, alternate executable, or additional
file is a runtime member. The PDB remains evidence in VIIPER's Hardware Dev
Center submission manifest, but is deliberately excluded from the public
runtime package.

`ViiperNativePackageContract.cs` is the single DS4Windows repin point. It binds
the exact VIIPER source revision, server version, four-part driver package
version, ABI, capability mask, and loaded-driver build identity. The live ping
gate aliases those constants, while `viiper_native_bundle.py` independently
derives the SHA-256 build identity and rejects disagreement.

The eventual sibling lock has a closed schema. It records the upstream
repository, source revision, exact GitHub release and asset numeric IDs, exact
tag, original release-asset name, the GitHub API `sha256:` digest, an
independently recomputed archive SHA-256, schema-2 submission manifest SHA-256,
user-mode signer-certificate SHA-256, and the path, length, and SHA-256 of every
one of the six runtime files. The API digest and local archive digest must
agree. Unknown properties, duplicate JSON properties, noncanonical hashes,
path/case drift, links, reparse points, empty files, and additional files all
fail closed.

The schema-2 manifest must independently prove a release-eligible `HLK/WHCP`
route and the same revision/package/ABI/capability/build-identity tuple. The
unchanged stamped INF must still match its submission hash, DriverVer, KMDF
1.27 target, and catalog name. The release signature gate additionally
requires:

- timestamped Authenticode signatures on `viiper.exe` and
  `ViiperUdeCtl.exe` from the certificate fingerprint in the immutable lock;
- Microsoft production hardware signatures on the SYS and CAT, including the
  hardware-verification EKU and excluding the attestation EKU;
- kernel-policy verification and exact INF/SYS membership in the locked CAT.

## Production intake

Only after VIIPER has published its validated Microsoft-returned production
asset:

1. Record and independently verify the downloaded archive SHA-256.
2. Extract its exact six runtime members into the layout above.
3. Determine the SHA-256 of the DER-encoded signer certificate shared by the
   broker and helper.
4. Create the review lock once. The lock writer hashes the fresh archive
   itself, requires the GitHub API digest to agree, rejects any archive other
   than the exact six flat canonical members, and byte-compares those members
   to the reshaped local bundle before creating the lock:

   ```powershell
   python .\utils\viiper_native_bundle.py create-lock `
     --bundle-root .\extras\viiper-native-udecx `
     --lock .\extras\viiper-native-udecx.lock.json `
     --release-id <github-release-id> `
     --release-tag <github-release-tag> `
     --release-asset-id <github-release-asset-id> `
     --release-archive <fresh-downloaded-archive> `
     --release-asset-api-digest sha256:<github-api-digest> `
     --signer-certificate-sha256 <certificate-sha256>
   ```

5. Run `validate-viiper-native-bundle-signatures.ps1` and review the lock and
   payload as one commit. The signature validator resolves the numeric GitHub
   release and asset IDs, peels its exact lightweight or annotated tag and
   requires that tag to resolve to `SourceRevision`, checks the asset
   name/API digest, downloads that asset again, and independently verifies its
   archive hash. It then rejects every extra, missing, duplicated, nested,
   traversing, linked, private-PDB, or oversized ZIP member; extracts only the
   six allowlisted flat upstream members into a new temporary directory; and
   byte-compares each member to the corresponding local locked file before
   inspecting signatures on those byte-identical local files. The lock writer
   refuses to overwrite an existing lock; a repin is an explicit replacement
   review. An existing release tag at another revision is never a valid native
   bundle provenance record, even if someone later attaches a same-named file.

## DS4Windows integration

Native release mode is mutually exclusive with the legacy USB/IP package. The
post-build step removes and recursively rejects the legacy VIIPER executable,
USB/IP payloads, checksum sidecar, and PowerShell backend installer before it
stages the exact locked native bundle. Missing native media has no download or
legacy fallback.

The standard Burn chain carries the exact six files and lock as attached,
content-addressed payloads. Its final vital forward action invokes only the
locked `viiper.exe native-package-install` transaction, passing the protected
driver directory, schema-2 manifest, source revision, all six compiled hashes,
helper path, and validated interactive-user SID. VIIPER alone owns the
package/service mutex order, SetupAPI/SCM mutation, legacy-owner migration,
loaded-driver identity proof, broker health proof, and rollback. Burn never
pre-stops the broker and never kills a mutating child. Exit 3010 is preserved
as a reboot-and-retry boundary.

Direct uninstall is a separate permanent Burn action that runs the exact
cached broker/helper before MSI removal. Related-bundle uninstall never tears
down the shared native package. A scheduling-only HKLM receipt is written last
after exit 0 and cannot turn a committed VIIPER operation into a Burn failure.

The in-app Install / Repair path follows the same contract. Its elevated host
creates an unguessable, protected Program Files staging directory, copies and
flushes only the exact locked files, revalidates hashes/link counts/reparse
boundaries, retains read handles across the child lifetime, and invokes the
same authoritative command. It does not alter the PlayStation report, audio,
haptics, resampling, clock, or state engines.

A production-native release remains intentionally blocked until the final
source/tag-bound Microsoft HLK/WHCP package and timestamped Authenticode
broker/helper asset are published and installed into this exact layout. The
remaining release proof is an elevated, reboot-capable signed-driver run with
Driver Verifier and real DualSense/DualSense Edge/DualShock 4 input,
speaker/haptics, and microphone soak. Ordinary source builds and synthetic
contract tests remain available; synthetic fixtures are never production
bytes.
