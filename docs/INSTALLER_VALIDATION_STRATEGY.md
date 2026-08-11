# Native VIIPER installer validation and recovery strategy

The DS4Windows native-backend installer delegates all driver, broker-service,
legacy-owner migration, and rollback mutations to VIIPER's signed transaction.
DS4Windows validates and retains the release media, supplies immutable expected
identities, waits for the exact child process to terminate, and interprets only
the transaction's documented exit status. It does not reproduce SetupAPI, SCM,
Driver Store, or USB-IP cleanup logic.

## Transaction rules

1. Burn and the in-app installer serialize their own setup entry points. VIIPER
   then acquires its Administrators-bound private package and service namespaces
   in package-to-service order.
2. The only authoritative install command is the staged, pinned `viiper.exe
   native-package-install`. The only authoritative removal command is the same
   pinned executable's `uninstall` command. DS4Windows never invokes
   `ViiperUdeCtl.exe` directly.
3. The source bundle contains exactly six direct children: `viiper.exe`,
   `ViiperUdeCtl.exe`, `ViiperUde.inf`, `ViiperUde.sys`, `ViiperUde.cat`, and
   `submission-manifest.json`. Installation reshapes the unchanged driver bytes
   into an exact three-file driver directory because that is the helper's
   fail-closed input contract.
4. Every file, source revision, ABI, capability mask, driver version, loaded
   build identity, release asset, and signing identity is pinned by the compiled
   native-package contract and immutable lock. Case, layout, duplicate JSON
   members, reparse points, extra links, extras, and hash drift are rejected.
5. The original interactive user's SID and profile are captured before
   elevation, validated after elevation, and persisted by Burn across reboot
   resume. They are data for VIIPER's exact legacy-owner migration, never a
   substitute for authorization.
6. DS4Windows may be closed before mutation. The wrapper never pre-stops or
   kills VIIPER, modifies USB-IP, removes tasks, or edits the Driver Store.
   VIIPER owns those operations and their rollback order.
7. After the mutating child starts, the wrapper drains stdout and stderr and
   waits on the exact Windows process object without a timeout or forced kill.
   Source-media handles and transaction scope remain live until termination.
8. Exit 0 means verified commit. Exit 3010 means verified reboot-required
   outcome and causes the complete transaction to be retried after reboot.
   Every other exit, malformed proof, crash, or wait ambiguity fails closed.
9. A registry receipt is scheduling evidence only. It is written last and can
   never override VIIPER's authenticated ABI, capability, package-version, and
   loaded-driver identity proof.
10. Native install/repair is the last vital forward Burn mutation. Direct
    uninstall runs VIIPER's authoritative removal before MSI removes the cached
    helper and bundle media. Related-bundle removal cannot tear down shared
    native state.

## Runtime contract

The current native contract is centralized in `ViiperNativePackageContract`.
DS4Windows must authenticate the local VIIPER broker and require all of the
following before creating a virtual controller:

- server identity `VIIPER` and native transport ready;
- exact UdeCx ABI and capability mask;
- exact driver package version;
- exact 32-byte loaded-kernel build identity, encoded as canonical lowercase
  hexadecimal;
- exact native service identity and protected installed broker provenance.

Missing, malformed, duplicated, differently cased, stale, or conflicting proof
fails before any bus or device creation. No production native path falls back to
USB-IP.

## Preservation boundaries

The native bus changes transport, scheduling, lifecycle, and installation—not
the established DualSense, DualSense Edge, or DualShock 4 media/state engines.
Deterministic parity gates compare the native adapter with the proven USB-IP
behavior for HID input, output state, speaker/haptics, microphone ISO payloads,
packet lengths, endpoint alternate settings, reset boundaries, and reconnect.
Production release additionally requires signed live tests; byte-level unit
parity is not presented as proof of real Windows USB scheduling or audio timing.

Uninstall removes only exact VIIPER-owned devnodes, packages, service, protected
credentials, broker files, and logs. It does not recursively delete application
directories, user profiles, settings, controller data, or unrelated USB-IP
installations. Failed or indeterminate rollback preserves authenticated recovery
media and leaves the broker stopped for reconciliation.

## Release gates

- Production Authenticode verification for the broker and helper, including
  trusted chain, timestamp, and pinned signer certificate.
- Microsoft HLK/WHCP-returned INF/SYS/CAT validation, exact catalog membership,
  schema-2 manifest, source revision, and loaded-build-identity derivation.
- Exact six-file archive validation before and after archive round-trip, then
  build-time reshaping and revalidation against the immutable DS4Windows lock.
- Native-only resource and Burn-chain checks proving there is no legacy VIIPER,
  USB-IP executable, PowerShell installer, network download, or fallback payload.
- SetupActions, bootstrapper, WPF application, WiX, Python contract, and full
  regression-suite builds/tests.
- Deterministic install/repair/uninstall state-machine coverage for success,
  no-op repair, rollback, rollback failure, crash, mutex contention, tampering,
  reboot 3010, resume, direct uninstall, and related-bundle upgrade ordering.
- Source-bound live Windows validation with Driver Verifier, sleep/resume,
  broker crash/reconnect, reset/reconfigure, multiple controllers, and concurrent
  HID plus full-duplex PlayStation speaker/haptics/microphone traffic.
- Source-bound SDL/QPC/WPR latency evidence for Xbox, DualShock 4, and DualSense,
  with causal event fences and comparison to the same-machine USB-IP baseline.

No release is production-ready merely because it compiles. The final gate is a
matching signed bundle installed on a disposable Windows target and exercised
with real controllers without input loss, stale state, media dropout, cadence
drift, lifecycle leaks, or Driver Verifier findings.
