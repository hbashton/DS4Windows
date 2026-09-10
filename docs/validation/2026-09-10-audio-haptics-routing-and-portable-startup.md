# Audio Haptics routing, Nintendo output, and portable startup

Status: source implemented, independently reviewed, automated validation passed, and complete private candidate F packaged and verified. Not launched; this is not a release or hardware acceptance record.

## Requested work

1. Audio Haptics must discover Chrome/apps routed to nondefault Sonar outputs, just as the Overview quick-settings picker does. Explain that System audio captures the default output, not all render endpoints.
2. Audio Haptics must support Switch 2 Pro and Joy-Con 2, using the existing stereo PCM-to-HD-rumble conversion and sole physical writer. Joy-Con 1 is not included in this request.
3. Fix portable VIIPER readiness on the Windows 10 installation described in the reporter's follow-up, without reducing authentication or encryption.

## Observed evidence

- Running private E build, source `3d45895593de6608769982a469bd25ad8c636dd6`: Audio Haptics used only `GetDefaultAudioEndpoint(Render, Multimedia)` for its app list. Overview enumerated all active render endpoints. The user confirmed Sonar's separate output routing was the condition behind Chrome being missing.
- E log at 2026-09-10 17:41:31 onward repeatedly selected the same SteelSeries Sonar Gaming route, reported the app had moved, and restarted about once per second. A borrowed/cached NAudio session manager was disposed during route queries. Its owning MMDevice retained that disposed manager.
- [Reporter follow-up](https://www.reddit.com/r/DS4Windows/comments/1wbl3ly/comment/p902c8x/): Windows 10 22H2 build 19045.7725; both loopback broker ports listening under the same PID; portable key file exists. These observations rule out missing listeners/key at the time of the snapshot, not every possible startup failure.
- `ViiperEncryptedStream` unconditionally constructed .NET `ChaCha20Poly1305`. [Microsoft's platform matrix](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography#authenticated-encryption) requires Windows build 20142 or newer for that platform implementation. This is a concrete incompatibility with the reported build, even though the reporter did not supply the inner exception.

## Validation gates

- All-active-endpoint discovery, bad/removed endpoint isolation, stable saved app identity, and default-output source help.
- Route query ownership: failing-first reproduction, same-route stability, verified different-route debounce, format-stable recovery without accidentally recording unrelated apps.
- Nintendo: independent stereo/three-slice conversion; Mix/Replace; native neutral/active game coexistence; finite original capture expiry; exact-owner withdrawal; no repeated stale PCM; output-disable/profile/disconnect handling; USB/Bluetooth use the existing owner.
- VIIPER: same authenticated v2 protocol, RFC known-answer vectors, native/managed interoperability, tamper rejection, native allocation regression, measured managed overhead, complete portable dependency closure.
- No installed app, driver, security setting, or user profile changes are authorized by this record. Hardware acceptance remains distinct from simulated transport/test results.

## Interim runs (not final acceptance)

- Two borrowed-NAudio-manager ownership regressions failed before the route fix: `isolated_results/processed-app-route/recovery-red/processed-route-red.trx`.
- First combined x64 Release focused run: 124 passed, 0 failed, 3 explicitly gated live-audio tests skipped. Evidence: `isolated_results/audio-routing-nintendo-startup/focused/audio-routing-crypto-focused.trx`.
- Preliminary full suite against that first compilation: 5,255 passed, 0 failed, 11 explicitly gated hardware tests skipped. Evidence: `isolated_results/audio-routing-nintendo-startup/regression-first/audio-routing-regression-first.trx`.
- Nintendo admission/composition review, deterministic lifecycle tests, and capture-source-generation fencing continued after that compilation. These counts do not certify the final patch or tactile hardware behavior.

## Presentation semantics

- The Nintendo Audio Haptics path reuses the native 3 kHz stereo PCM analyzer and its three chronological slices, not a conventional two-motor magnitude downmix. Pro USB/Bluetooth and standalone/paired Joy-Con 2 retain existing physical routing and sole-writer ownership.
- Nintendo Mix keeps a fresh game contribution and adds the audio contribution. The two available bands cannot reproduce arbitrary overlapping spectra exactly: each band's stronger carrier is selected, native wins equal-amplitude ties, and the existing soft-saturating amplitude mixer is reused independently per side/slice.
- Nintendo Replace uses the selected audio for vibration while fresh audio is available. Existing physical DualSense trigger controls are unchanged. Source stop/disable withdraws only the local audio producer and must not resurrect consumed native PCM.
- Windows 10 fallback remains authenticated/encrypted VIIPER v2 ChaCha20-Poly1305. BouncyCastle.Cryptography 2.7.0 supplies the managed implementation. The native implementation is still selected when available. Managed public APIs allocate bounded nonce/MAC bookkeeping; this is not advertised as zero-allocation. MIT notice is included in output/publish.

## Independent review and focused final checks

- Peer review found and corrected a capture-stop/profile-update format race, a possible capture-stopped callback/lifecycle-lock inversion, and stale process-lease PCM crossing a queued source switch. The final-admission fence carries both source generations, resets partial frames, and only withdraws the exact audio producer.
- Nintendo review preserved default native feedback priority outside explicitly opted-in composition, CFBK wire sources remain unchanged, and consumed-stream suppression still passes the existing delivery epoch/uncertainty checks. The physical transport lifetime is immutable per device instance; profile/virtual-pad changes reuse it.
- Final reviewed x64 Release focused run: **378 passed, 0 failed, 3 gated live-capture tests skipped**. Evidence: `isolated_results/audio-routing-nintendo-startup/reviewed-focused/audio-routing-reviewed-focused.trx`.
- The preceding full run passed 5,292 tests with 11 gated skips; it preceded the final capture-format race correction and is not the final source acceptance run.
- Final reviewed x64 Release full suite: **5,293 passed, 0 failed, 11 gated live/external-integration tests skipped**, 5,304 total. Evidence: `isolated_results/audio-routing-nintendo-startup/reviewed-regression/audio-routing-reviewed-regression.trx`. This includes the final capture-format correction and regression test.
- Forced managed cipher allocation diagnostics measured 232 bytes/write and 312 bytes/read per warmed 24-byte record (1,000 records each). In-process elapsed times were 3.330 ms and 5.464 ms respectively; these are not controller-latency or cross-machine benchmarks. Native warmed zero-allocation and Nintendo composed-writer zero-allocation assertions remain strict and pass.
- No tactile hardware result or reporter-machine Windows 10 result has been claimed. The current E lab process and user profiles have not been replaced by these source edits.

## Complete private candidate F

- Tested source checkpoint: `2c49288a5343f5e6c31abfd7faa85b82d93f0b90` (clean at composition).
- Final application directory: `C:\Users\hbash\Desktop\DS4Windows-Haptics-Lab-20260910-F\portable\DS4Windows`.
- 553 files verified against the final lab manifest, including all 297 dependency assets, 23 application language satellites, the authorized Xbox persona, bundled VIIPER, self-contained .NET, native libraries and notices. BouncyCastle.Cryptography 2.7.0 is present in the dependency manifest; its DLL and notice were explicitly checked after composition.
- Application SHA-256: `7649E980ABB4AB29301C2B9203A34D1F1A1EB1ACE469EC8AB58621E7A6EC90E1`.
- Candidate root VIIPER SHA-256: `DC2D47B49F94FA827903FD24F18AE0289EBE682F09D6EF67A09EE7A6A8005EB7` (unchanged broker from E).
- Final `PRIVATE-LAB-MANIFEST.json` SHA-256: `39FBA5D923498E4AE7E0C20E11416C0C85D7F20C9277FDECD06B8D8B2DD2F23B`.
- The base ZIP under `x64\Release` is an intermediate with the released broker, **not the final lab or a release handoff artifact**. F has not been launched or installed. Preparing the isolated lab data and ownership preflight remains necessary before launch; preserve E's current profile edits and keys when migrating, never substitute a fresh profile template for them.
- Runtime read-only check after composition still showed E's DS4Windows PID 37588 and VIIPER PID 7192. No installed files, drivers, routing settings, or user profiles were changed; no public tag, upload, push, or Reddit comment was made.
