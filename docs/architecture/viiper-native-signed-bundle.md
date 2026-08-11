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

The next packaging-integration change must make portable staging, Burn
composition, and in-app repair require this contract explicitly. It must use
the deterministic staging copy, revalidate the staged bytes, recursively reject
every legacy VIIPER/USB-IP payload, and provide no production-native fallback
when the signed bundle or lock is absent.

At this commit boundary, portable packaging, Burn, and in-app repair still use
the legacy payload path. A production-native release is intentionally blocked
until that integration is complete and the final source/tag-bound Microsoft
HLK/WHCP plus Authenticode release asset exists. Ordinary source builds and the
synthetic contract tests remain available; synthetic fixtures are never
accepted as production bytes.
