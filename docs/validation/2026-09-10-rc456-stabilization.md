# RC4.5.6 stabilization and publication ledger

Status: published; required pre-publication and post-publication gates passed. This record is an evidence ledger, not a claim that every possible race, controller/game combination, or installation failure has been eliminated.

## Scope and version contract

- DS4Windows release tag `VIIPERRC4.5.6`, Windows/MSI/Burn version `5.0.5.6`.
- New immutable broker release `v0.1.4-rc4.5.6`, PE file version `0.1.4.0`, using the existing VIIPER/USB-IP backend. No new native-driver backend is introduced.
- Keep the established named-RC unsigned prerelease policy, draft-first CI artifacts, complete offline installer and self-contained portable ZIP, exact broker/source/license pins and updater build record.
- Preserve the installed applications, drivers and current private E controller session. Installation lifecycle tests run in isolated fixtures or hosted CI, not against this machine's Program Files installation.

## Calculated release gates

1. Review controller/capture startup, Stop/retirement and ownership paths; fix concrete defects with regression tests. Preserve finite retries, explicit failure states and terminal cleanup.
2. Review installer/repair serialization and portable startup diagnostics. An unrelated conflict or failed dependency must not be hidden behind a false-success result.
3. Validate the matching broker and updater. Publish the verified broker before repinning DS4Windows to its actual tagged CI artifact.
4. Run the final x64 test suite, native-free lifecycle/installer scripts and packaging checks without relaxing allocation assertions or excluding new failures.
5. Fast-forward tested source to main, pass hosted CI including installer layout/MSI install/repair/uninstall, then create the annotated DS4Windows tag and empty prerelease draft.
6. Dispatch the exact-tag release build. Verify complete installer/portable/source/notices/checksums/build-record assets before publishing; verify the post-publication receipt run too.

## Starting evidence

- DS4Windows source `c555444` contains the reviewed audio/source-routing and Windows 10 managed encryption fixes; application checkpoint `2c49288` passed 5,293 tests, zero failures, 11 gated skips. Private candidate F was composed but not launched.
- Current VIIPER `9999f25bfc65dac47bf263cd575e3a572517d37f` contains two commits beyond published `v0.1.3-rc4.5`: bounded native command ordering/admission and a deterministic ISO-generation fixture. Normal and release-tag Go 1.27.0 Windows suites passed with CGO disabled.
- Existing DS4Updater 2.0.6 uses verified `RELEASE-BUILD.json` records for future named RC binary versions. Compatibility is being rechecked; a new updater release is only needed if a concrete compatibility defect is found.

## Review findings closed in this source

- Capture initialization now rolls back the entire attempt, including endpoint/binding failures before StartRecording, independently retiring each resource without hiding the primary diagnostic. Capture start/cleanup logging has separate throttles.
- Unexpected writer failure terminates that producer with an explicit status. Final output admission and terminal cleanup share a gate; copied frames cannot reassert output after Stop. USB queues/renderers are retired independently. Bluetooth cleanup reserves ownership before persistent cache mutation and handles uncertain first writes without neutralizing untouched native sources.
- Installer obsolete-payload and post-uninstall directory cleanup now use the existing nonblocking setup mutex. Competing operations return the existing busy result before mutation; abandoned ownership and retry are covered.
- Portable startup retains only sanitized authenticated-probe phase/type diagnostics, without keys or raw peer messages. An unsuccessful startup retires only its own broker before the modal error, never a borrowed broker.
- VIIPER partial startup closes only listeners owned by that attempt when initial listener, USB/IP or API setup fails. Exact listener ordering, nil dependencies, primary-error preservation and repeated attempts have deterministic regressions.
- Packaging requires the managed Windows 10 encryption dependency and notice. The new broker is pinned byte-for-byte from tagged CI, including preserved license/build-note line endings. Historical portable startup-task recovery retains the old known-binary hash intentionally.

## Final local validation

- Full rebuilt Release x64 suite against the new broker: **5,327 passed, zero failed, 11 explicitly gated skips** (5,338 total), `isolated_results/rc456-stabilization/final-full/rc456-final-full.trx`. Allocation assertions and XML round trips remain enabled; obsolete hosted-CI exclusions were removed.
- Final focused lifecycle/startup/release checks: 183 passed, zero failed. Earlier combined focused lane: 241 passed, three live-capture skips. One intermediate test-fixture failure was corrected by faulting the actual cleanup dependency rather than assuming an idempotent Dispose would throw; the assertion was retained and the full suite rerun.
- All 22 localization/package composition cases passed, including fail-before-replacement checks for a missing encryption DLL or notice. Installer state simulation passed clean/update/repair/uninstall/downgrade/cancel/concurrency/core failure/optional failure/reboot-resume.
- Renamed executable guards, exact-SID startup registration/rollback, durable task backups, 22 infrastructure diagnostic cases and read-only legacy joystick checks passed without touching real scheduled tasks or installed applications.
- DS4Updater 2.0.6 full suite: 313 passed, zero failed/skipped, including the immutable prior release fixture. Its verified build-record protocol accepts this release; no updater change or republish is needed.
- VIIPER normal and release-tag suites passed under Go 1.27.0 with CGO disabled; seven new partial-startup cases passed 20 repetitions. Main run `34543672440` and tagged run `34544136045` passed on exact source `02d93e403ffde7ad24c1b373c01c6eb04ce02f2e`.

## Hosted installer gate

`utils/test-previous-msi-upgrade.ps1` is restricted to a disposable GitHub-hosted Windows VM and refuses a pre-existing installation or running mapper/broker. It pins the published RC4.5.5 installer, uses layout only to obtain the old payload-only MSI, verifies MSI identity/no custom actions, installs old then new, and checks one product registration, the new binary version, preservation of installed/roaming test profiles, repair and uninstall. It never runs the application or driver installer. Exact fixture files are cleaned individually; no local installation test is authorized by the script.

## Publication evidence

- VIIPER `v0.1.4-rc4.5.6` published as an unsigned prerelease on 2026-09-11 at 00:02:23 UTC: release ID `386698830`, 11 assets including the exact source ZIP. Windows Actions artifact `10178464223`, release asset `556070364`.
- Broker archive SHA-256 `7A08CAE2E5A8829BC0C7CE7CCCF6EDD72AAB3DAE1FA31DC431FEA0A942BED779`; executable `89808A41610997A6DAD0807579B816C18B34C039E03E96FDC8FD2BD68434FFBB`; source ZIP `2F73D30A60ECC134215844EB81DA4FD58BD8F2B85FD6C92CCFF46DC0CF5984C8`. Independent review checked all ZIP files, notices, actual PE/embedded Go provenance and exact source ZIP commit comment without running the broker.
- DS4Windows hosted CI, exact-tag assets, actual updater transaction and post-publication verification completed successfully; final identifiers are recorded below. Public notes explicitly retain the subjective haptics/game acceptance and unsupported Bluetooth Switch 2 headset-audio boundaries.

### Pre-publication CI correction

Main CI `34545049986` at `73fc07e` passed the complete Windows test job and built both MSI and bundle, then failed the installer source validator: the old Program.cs mutex-literal assertion remained after moving the mutex into `SetupMutationOwnership.cs`. No DS4Windows tag or public assets were created from this failed run.

The validator now checks the exact mutex/acquisition/busy/abandonment/release rules in the production helper and the delegation/cleanup gates in Program.cs through one tested function. No runtime safety check was removed. Six additional real-source regressions cover this layout and reject wrong mutex identity, missing delegation, gate bypass, missing release and forbidden RunOnce registration; all **28** packaging cases pass. The application binaries are unchanged from the full green local/hosted regression suites.

A fresh local self-contained publish, portable composition, MSI and bundle build, full installer validator, USB-IP downgrade/reboot simulation, startup-task simulations and installer state simulation then passed. Evidence is under `isolated_results/rc456-stabilization/publish-console.log` and `installer-console.log`; local composition is under `isolated_results/rc456-composition`. These files were not installed, launched, or substituted for the required tagged CI public artifacts. The hosted rerun remains required before tagging.

Main CI `34546189223` at `830991a` passed the complete test job, package/installer validation, offline layout, install, repair and uninstall. The added previous-version upgrade fixture then failed before attempting an upgrade: MSI COM View.Execute/Close calls emitted nulls into PowerShell's function output, producing arrays around otherwise correct metadata. Read-only inspection of the actual new MSI reproduced the harness failure and confirmed its correct name, version and UpgradeCode. The fixture must explicitly discard those COM outputs; the identity/side-effect restrictions remain mandatory.

The metadata functions now explicitly discard COM Execute/Close results, return only scalar properties/one identity object, and include observed metadata when identity verification fails. The read-only `test-msi-upgrade-metadata.ps1` exercises the exact extracted functions against disposable non-installable MSI databases: correct identity, wrong name/version/ProductCode/UpgradeCode and CustomAction rejection. Six synthetic cases and actual RC4.5.6 MSI inspection passed; the hosted workflow runs this regression both early and against the built MSI. No installer identity or side-effect guard was bypassed, and no application code changed.

The unchanged DS4Updater 2.0.6 assembly also passed its actual Prepare/Apply transaction against the complete local candidate ZIP in a fresh synthetic Desktop fixture: all 553 files matched, eight user-data sentinels were preserved, the obsolete owned sentinel was removed and no staged transaction remained. This is local-composition evidence only; repeat against the final tagged CI archive/receipt before publication. No existing portable or installed application was updated by this test.

### Deterministic audio-boundary coverage

Main CI `34547255181` at `322cdef` reported one failure in the first fresh-frame positive assertion of `ActualNintendoAdmissionRejectsCopiedFrameAfterBoundaryAndRevokesOldProducer(source)` (33 ms test duration; 5,326 passed, 11 skipped). That result does not identify the exact internal rejection edge or prove a measured capture age. Source review established that the assertion took its real capture timestamp before reflective invocation, cold JIT, binding and translation, while the production path correctly applies a strict 24 ms age limit. Source/mode/lease ownership assertions should not depend on host scheduling completing within that limit.

The boundary fixture now uses one controlled clock through the actual runtime, existing sink, actual Bluetooth framer and recording transport lease, with a bound invocation delegate and exact integer-microsecond advancement. The minimal internal/private clock seams default to the unchanged host-wide QPC clock; no public setting, input-path change, lifetime check or freshness inequality is changed. An intermediate forced-delay test correctly exposed the final framer's independent real-clock check; it was not bypassed or replaced with a fake framer.

New regressions exercise an intentional 50 ms setup pause without advancing logical sample age, acceptance at 23,999 microseconds and rejection at 24,000, future timestamps, unchanged real-clock stale rejection, direct Bluetooth physical-writer expiry/future rejection, failed-clock rejection and terminal-neutral behavior. No retry-until-green or relaxed age/allocation assertion is used. Final full-suite and hosted evidence follows after these checks pass.

The final coherent-clock full Release x64 suite passed: **5,332 passed, zero failed, 11 gated skips** (5,343 total), `isolated_results/rc456-stabilization/coherent-clock-full/rc456-coherent-clock-full.trx`. The five additional cases cover all three clock stages while retaining the actual physical framer/lease checks. The earlier real-time fixture failure and intermediate forced-delay failure remain recorded separately; neither is silently reclassified as a pass.

The 58 focused audio initialization, source-boundary, Nintendo output and physical-writer tests then passed five consecutive serial repetitions (290 passes, zero failures). Hosted main CI `34549051490` at `6c0cb75` passed the complete 5,343-case suite, MSI metadata checks, complete packaging, bundle layout, and MSI install/repair/uninstall. Its added upgrade fixture failed before installing the previous version: Burn `/layout` copied the self-contained RC4.5.5 bundle, not an extracted MSI. The archived `layout.log` shows `execute: None`, `cache: No` for the MSI and only a bundle-copy operation. The fixture must use the already-pinned WiX tool's passive `burn extract` operation for the attached MSI, retaining exact package identity and no-custom-action checks. No release tag or DS4Windows public assets were created from this run.

The corrected fixture retains the verified bundle-only layout and uses the release composer's WiX 5.0.2 resolver to passively extract its attached container into a fresh, bounded directory. It verifies the exact previous MSI path, SHA-256 `EA28DE830488C8B7783607C38AE97A4B1CAFFB60DA68BE4F1CBD7105F25A4370`, identity, and absence of custom actions. All 12 metadata/payload-selection regressions passed, including missing/wrong-name/wrong-location/duplicate rejection and the actual new MSI. The actual extraction helper and bounded process wrapper passed locally against the pinned old bundle without executing it or installing anything; both old and new MSI identities passed read-only inspection. Extraction diagnostics are retained with hosted evidence. Application/runtime source is unchanged by this fixture correction.

## Completed publication

- Exact release source: `08cbbc7b32a7c87d0867f619af61017f83b8c0d8`. Main CI **34550499566** passed in full. The hosted upgrade proof confirms the hash-pinned 4.5.5 MSI upgraded to 4.5.6, one expected product registration remained after upgrade/repair, two test profiles were preserved, and zero registrations remained after uninstall. No application or driver was launched by that fixture.
- Annotated tag `VIIPERRC4.5.6` identifies that source. Draft-first exact-tag release build **34551429379** passed and uploaded all 13 workflow-owned assets without replacing existing files.
- Published unsigned prerelease: [Release Candidate 4.5.6 — Steadier Feedback & Safer Startup](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.6), release ID **386730703**, publication **2026-09-11T01:49:21Z**. The post-publication verification run **34552197773** passed: public bytes, Actions uploader, exact source and successful draft receipt matched; no asset was rebuilt or overwritten.
- Installer: `DS4Windows_5.0.5.6_Setup_x64.exe`, 202,361,837 bytes, SHA-256 `35FBD0F1621E758D8104FB0238F6B7C2421F5191BC736297F78F67269BF56FFE`.
- Portable: `DS4Windows_VIIPER_x64.zip`, 137,619,364 bytes, SHA-256 `0706581B05CD74F78B5BC6B08664572411E9CD514D54D3526F9E46856C603A44`.
- Release receipt SHA-256: `B35B7BFD83129AFB010296E61FF704624D45AB2E79AF50763152A61FE63ABCB1`. DS4Windows source ZIP SHA-256: `6D0B7BD8D8A38346459DBBD3C817D7BC8D19C94AC89D20188E852C6841FB7E7B`. The corresponding source ZIP comments identify the exact DS4Windows and VIIPER commits.
- Independent package inspection verified all **553 files**, **297 dependency assets**, **23 language satellites**, exact managed-file manifest, self-contained .NET/Desktop 8.0.30, BouncyCastle 2.7.0 DLL/notice, Xbox identity, both matching broker aliases/sidecars, and notices. App EXE/DLL identities are `5.0.5.6` / `VIIPERRC4.5.6`.
- The unchanged updater **2.0.6** actually prepared/applied this final ZIP in a fresh synthetic fixture. All 553 payload files individually matched, eight user-file sentinels were preserved, the obsolete owned sentinel was removed and no staging remained. Proof SHA-256: `13F793E4A55DA5EDA0EC65C153C091FDE4490AE12B274C61E3D84E5C57477920`.
- Passive extraction of the final downloaded installer confirmed its actual embedded MSI's identity and absence of custom actions, without installation. Embedded MSI SHA-256: `FE023D22DF948DE642C54A2DEA455AC1598FE90FEF44555E5504BD2E6416A385`.
- Final downloads and local transaction proof are retained under `C:\Users\hbash\Desktop\DS4Windows-RC4.5.6-Publish-20260910`. No live application, controller session, installed driver or Program Files installation was replaced by publication. This post-publication ledger update does not move the release tag or alter release bytes.
