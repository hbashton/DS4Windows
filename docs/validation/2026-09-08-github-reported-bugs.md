# GitHub reported-bug audit, 2026-09-08

Scope: the 39 open issues returned by the authenticated GitHub API on this date.
Prioritize owner-acknowledged reports and reproducible crashes, stalls, or
unwanted behavior whose mechanism is present in the current source. This is not
a feature-request implementation pass. Starting source: `4b7d3db` on
`feature/native-udecx-landing-zone`. GitHub comments, issue states and releases
are not modified by this audit.

## Confirmed defects addressed in this pass

| Report | Evidence and change | Validation boundary |
| --- | --- | --- |
| [#46](https://github.com/hbashton/DS4Windows/issues/46) | Legacy green/blue profile fields incorrectly read the red field. The fixed-format update-check timestamp was parsed and formatted using the current culture. Read channels independently; use invariant canonical dates first, with the existing localized legacy-date fallback. | Actual XML deserialize/map and serialize tests: 12 failures and 5 passes before the change; all 17 pass afterward. Already-correct shift extras/wrapper paths were not rewritten. |
| [#76](https://github.com/hbashton/DS4Windows/issues/76) | The three uninstall-only Burn packages were requested as `None` during install/repair. Request `Cache` without executing them while the attached payload is available. Preserve direct uninstall, outgoing-upgrade and isolated-recovery behavior. | Seven production-policy tests and installer state-machine simulation pass; bootstrapper builds with zero warnings/errors. No machine uninstall was performed. Full install/delete-download/uninstall verification still requires an isolated installer environment. This does not retroactively fill an old installation's empty cache. |
| [#83](https://github.com/hbashton/DS4Windows/issues/83) | Reported Bluetooth reclaim restarts clear their own cooldown on removal, enabling the next restart. Extend the existing active-USB no-restart guard to Bluetooth, preserving the HidHide cloak and requiring reconnect/Steam restart if Steam already holds the controller. | Eligibility, repeated generation/reconnect and call-site guard tests pass. This prevents the reported restart-loop trigger; it is not proof that every independent virtual-device cleanup/leak path is fixed. |
| [#84](https://github.com/hbashton/DS4Windows/issues/84) | The reporter's two-controller A/B test and commit history isolate ordinary Bluetooth DS4 effects incorrectly using the shared-overlapped writer. Both effect entry points now share control-pipe routing for normal BT effects; CopyCat/non-BT retain interrupt output. | Four transport-policy/wiring tests pass. Separate Bluetooth audio writer and output serialization lock remain intact. Reporter hardware result is supporting evidence, not a new local physical DS4 verification. |
| [#60](https://github.com/hbashton/DS4Windows/issues/60) | Package composition moved satellites under `Lang` and depended on a working-directory-relative probe path established before managed startup. Preserve standard `<culture>` folders and original dependency metadata; remove extra probing. Repair staging accepts standard culture satellites while retaining old managed `Lang` manifests. | Actual-assembly isolated child reproduces failure outside old package layout and success from both directories with standard layout. Four package regressions validate real composition, ZIP contents, managed manifests and WiX harvesting; now part of CI. No custom assembly resolver or application-startup hook. |
| [#75](https://github.com/hbashton/DS4Windows/issues/75) | Original Switch Pro calibration dereferenced null/mismatched replies; startup writes requested zero-timeout cancellation. Give the USB handshake and subcommands bounded waits, validate received byte count/ACK/subcommand/SPI metadata, retry real factory calibration when user data is unavailable/invalid, and reject unavailable calibration through the existing removal path. Normalize expected closed/disposed transport failures at individual I/O calls, preserving inner exceptions. Do not republish a removed controller after preparation. | 34 fake-transport/calibration/startup tests plus three actual hotplug-publication tests pass. Covers failed/short/unrelated replies, bounded floods/timeouts, factory/user ordering and ranges, invalid IMU denominators, USB setup order, closed/disposed owners and clean removal without workers. No physical legacy Switch Pro was exercised. |

All six entries above have completed implementation and automated verification.
These are source fixes, not new hardware certifications or deployed releases.

## Other reports: disposition and limits

| Reports | Finding / reason not to make a speculative change |
| --- | --- |
| [#68](https://github.com/hbashton/DS4Windows/issues/68) | Switch Pro null-reference symptom is consistent with #75, but lacks that report's matching stack. Correlation is probable, not established for every device in its comments. |
| [#95](https://github.com/hbashton/DS4Windows/issues/95) | Posted installer log packages VIIPER 0.1.0 and fails task registration at `UserId`. Current source already uses exact-SID XML, a neutral trigger and ownership/failure-containment verification. Current extracted-production PowerShell simulations pass without touching Task Scheduler. Reporter must use a build containing that existing change; no new cause is established by this old-package log. |
| [#77](https://github.com/hbashton/DS4Windows/issues/77) | Current all-in-one installer has reboot resume and verified infrastructure recovery absent from the report's path. The app-launched standalone helper still explicitly instructs repair after reboot. Do not equate temporary disabled task state with evidence that all current resume flows fail. |
| [#61](https://github.com/hbashton/DS4Windows/issues/61) | Reported `RestartDs4Windows` start-before-shutdown implementation is no longer present. Current successful setup refreshes readiness and returns without a replacement-app race. |
| [#69](https://github.com/hbashton/DS4Windows/issues/69) | Local VIIPER source already pins the tray/message-pump goroutine to an OS thread. Later report of a blank icon merits a startup trace; it does not establish another specific cause to patch. No VIIPER source changed in this pass. |
| [#31](https://github.com/hbashton/DS4Windows/issues/31) | Hibernate report predates current suspend/resume lifecycle hardening and lacks a usable crash stack. Existing hardening is not proof that this reporter's exact failure is resolved. |
| [#94](https://github.com/hbashton/DS4Windows/issues/94), [#62](https://github.com/hbashton/DS4Windows/issues/62), [#80](https://github.com/hbashton/DS4Windows/issues/80) | Startup enable/disable privilege and task-state behavior, legacy `task.bat` cleanup, and renamed-executable setup need targeted reproduction. Current code no longer generates `task.bat`; blindly deleting an executable in a user directory is not an acceptable cleanup fix. |
| [#67](https://github.com/hbashton/DS4Windows/issues/67) | Current mapper handles legacy/current touch modes and action names. Need original imported XML and the selected action/profile to establish the claimed lost mapping. |
| [#73](https://github.com/hbashton/DS4Windows/issues/73) | Attached log identifies RC4.3; discussion reports RC4.4 restores mute LED, followed by updater rollback. No new current-source LED defect established. |
| [#74](https://github.com/hbashton/DS4Windows/issues/74) | Current profile setup republishes both trigger effects; no profile/transport/output capture establishes a remaining failure. Native game ownership can intentionally supersede profile effects. |
| [#81](https://github.com/hbashton/DS4Windows/issues/81) | Owner says addressed by a later update. A current reproduction/capture is needed before changing authored-haptics ownership or timing again. |
| [#82](https://github.com/hbashton/DS4Windows/issues/82), [#32](https://github.com/hbashton/DS4Windows/issues/32), [#34](https://github.com/hbashton/DS4Windows/issues/34), [#57](https://github.com/hbashton/DS4Windows/issues/57) | Real symptoms/logs, but current exact root causes remain unproven: game enumeration slowdown, old output startup stall, game-specific rumble, and multiple VHCI interfaces. Do not disable system services, remove devices or rewrite feedback speculatively. |
| [#58](https://github.com/hbashton/DS4Windows/issues/58) | Screenshot-only report; image retrieval failed. No readable diagnostic/reproduction to support a code change. |
| [#90](https://github.com/hbashton/DS4Windows/issues/90) | Empty body, no reproduction or measurable expected/actual behavior. |
| [#91](https://github.com/hbashton/DS4Windows/issues/91) | Missing release-download asset, not an established application crash. Publishing/replacing releases is outside this pass. |
| [#37](https://github.com/hbashton/DS4Windows/issues/37), [#38](https://github.com/hbashton/DS4Windows/issues/38) | Coverage/dead-code audits do not independently justify restoring commented hooks. In particular, old exclusive-mode warnings may falsely reject modern working HidHide setups. |
| #6, #14, #18, #22, #29, #35, #63, #71, #79, #85, #87 | Feature, policy, support or broad localization requests rather than a currently demonstrated defect in this pass. No implementation inferred from the request to fix reported bugs. |

## Verification record

- `issue46-before.trx`: 12 failed, 5 passed (before DTO fixes).
- `reported-issues-targeted.trx`: 46 passed, 0 failed (DTO, HidHide lifecycle,
  DS4 effect routing and uninstall planning).
- `test-installer-state-machine.py`: passed.
- `test-startup-task-registration.ps1`: exact-SID schema, ownership, collision,
  rollback, containment and absence simulations passed; no real task mutations.
- `DS4Windows.Bootstrapper.csproj` Release build: 0 warnings, 0 errors.
- `test-localization-package.py`: four passed after two failures before the fix.
- First integrated pass: 4,167 passed, 11 opt-in skips, one failed source-contract
  test (`PhysicalHidTransfersReuseCompletionEvents`) still locating the old
  public reader signature. Its event-reuse assertions were retained and
  bound to the new shared implementation, not disabled. This was not an
  allocation-assertion failure.

## Final verification and review

- `github-issues-final.trx`: **4,185 passed, 0 failed, 11 opt-in skips**.
  The command retains CI's existing three excluded legacy exact-XML snapshot
  tests (`CheckSettingsSave`, `CheckWriteProfile`, `CheckJaysProfileRead`);
  no new exclusions or ignored failures were introduced.
- `github-issues-allocation-and-startup.trx`: **185 passed, 0 failed** after
  improving the existing startup-failure log with its diagnostic reason.
  This includes **all 148 allocation-named tests** plus 34 original Switch Pro
  and three prepared-hotplug publication tests.
- Actual current build satellite metadata/path validation passed. The standard
  layout resource probe passed from both its package folder and an unrelated
  working directory, including parent/neutral fallback and unrelated-assembly
  rejection (`artifacts/issue60-83263e8671c24dee8172dcd20ec6f36c`).
- Four package composition/manifest/ZIP/WiX regressions passed again.
- Independent source review found no material blockers in installer planning,
  DS4 effect routing, Steam-reclaim containment, DTO parsing, native read-count
  handling, calibration transactions or failed-hotplug publication. Requested
  native waits are finite, but safe cancellation drain can still exceed that
  duration if a kernel driver itself hangs; no hard wall-clock claim is made.
- Existing application compiler warnings were unchanged; bootstrapper build
  was warning-free. `git diff --check` passed.

No live DS4Windows/VIIPER process, Task Scheduler entry, controller connection,
installed driver, Program Files content or GitHub issue/release was modified.
The user authorized restarts, but none was necessary for these isolated tests.
Physical legacy Pro/DS4 A/B verification and a disposable-environment Burn
install/delete-downloaded-setup/uninstall cycle remain release acceptance checks,
not evidence claimed by this pass.

Parallel audio research is recorded in
[the public-source audio ledger](2026-09-08-switch2-public-audio-research.md).
It adds genuine historical console capture evidence and an emulator logger lead,
not a verified Bluetooth headphone implementation.
