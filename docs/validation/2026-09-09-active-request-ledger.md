# Active user-request ledger — 2026-09-09

This is the working checklist for the current follow-ups. Source implementation,
automated verification, physical acceptance, and public release are separate
milestones. Nothing in this ledger implies that an in-progress build is installed
or that the published RC4.5.1 assets were replaced.

## Requests and acceptance criteria

| Request | Current evidence / remaining work |
| --- | --- |
| Explain dual-Joy-Con gyro activators clearly and concisely | UI copy distinguishes Swap/Pause and the separate aim activation gate. Implemented in the earlier live-preview tranche; no runtime remapping semantics silently changed. |
| Add an optional live input preview during button remapping | Implemented: default-hidden Show live preview button on profile Controls and the binding dialog; bounded snapshot polling, stale/disconnected clearing, themes and small-screen layout. Earlier complete suite: 4,641 passed, 11 opt-in skipped. Not yet in a public release. |
| Continue closing only definitely fixed GitHub issues; explain the fix/evidence | This pass closed #91 (current installer download), #94 (startup registration settings), #69 (VIIPER tray registration recovery), each with an evidence comment. Prior verified closures: #60, #61, #75, #76, #80, #83, #84, #95. Other reviewed issues remain open without exact evidence. |
| Repair portable DS4Updater 2.0.4 so portable automatic updates can be re-enabled | Implemented safe 2.0.5 source and DS4Windows bootstrap: exact release/digest/PE identity, staged manifest, lock preflight, rollback preserving user edits, exact-folder graceful shutdown, isolated worker, long-path version lookup, deadline/redirect and concurrent-file checks. Updater full suite: **245 passed, no skips**, including actual SHA-pinned RC4.5.1 ZIP stage/apply into a temporary fixture. Compressed self-contained x64/x86 CI builds passed. **Updater 2.0.5 is published and its public assets are verified**; delivering the new DS4Windows bootstrap in RC4.5.2 remains required before portable users can use the fixed flow. |
| Audit original Joy-Con linking against Joy-Con 2 UI | Confirmed old split/join + gyro-provider globals were separate and defaulted to automatic Joined. That audit is complete; the user then explicitly requested unification. |
| Make original Joy-Cons use current magnet checkbox and Controllers Link/Unlink; remove old globals | Source implemented. Each original half starts standalone; the same UI dispatches to the appropriate backend. Original linking retains the first-selected profile/pad, remembers explicit pairs, and reconciles magnets. Unlink/secondary removal immediately neutralizes retained virtual/held keyboard/mouse output without needing another report; exact-session checks fence queued output. Malformed pair files are preserved without blocking startup. Cold secondary output work still briefly pauses reports; interruption-free hardware behavior is not claimed. Cross-generation original/2 pairs are not implemented; the dialog explains matching generations. |
| Make original Joy-Cons respect all applicable profile Nintendo settings | Implemented neutral projection, upright/sideways, face layout, mode shift, per-hand motion/DJG, dampening/lock, stick assist/scroll/tap/sensitivity, horizon/deadzone, high-rate lifetime, logical UDP/diagnostics, connection cue and timeout modes. Card artwork follows the profile orientation; gyro calibration targets exact halves. Original HD rumble uses its own 0x10 writer with side/band/peak synthesis, PCM/control reconciliation, tuning and Stop fences. Original packets cannot preserve three-subframe timing exactly; long Xbox delays are bounded by the live feedback lease. Hardware acceptance remains open; see the detailed parity note. |
| Rename Switch 2 Controls to Nintendo Options | Section, navigation and title renamed. Old JoyCon Global Options controls removed; unrelated per-device Home LED control retained. |
| Keep profiles universal; do not hide unsupported controls | Profile Nintendo settings remain visible and editable for every controller. Unsupported optical mouse, magnetometer, extra physical buttons or headset mechanics have no runtime effect; saved values are not discarded. Ordinary enable/disable dependencies between settings remain. |
| Confirm whether #68 was fixed | Matching original Switch Pro Bluetooth SPI calibration failure repaired by ed0e983; additional startup/removal tests in 9557b3b. Both are ancestors of RC4.5.1. 54 documented targeted checks passed. Exact reporter failure is not established because its log has no stack; separate VIIPER refusal remains unconfirmed. Keep #68 open, not claimed fully resolved. |
| Keep track of all current asks | This ledger is maintained while implementation and verification continue. |
| Keep Joy-Con Horizontal/Vertical selection manual; confirm the Controllers button rotates stick axes | Confirmed in both Joy-Con 2 and the new original Joy-Con path. The card button changes runtime holding style, not just artwork: horizontal rotates the physical axes into the logical left stick; vertical restores the physical side's upright stick. Joined pairs retain the two-stick layout. No automatic orientation detection was added. Joy-Con 2 remembers the controller override; original Joy-Cons save the current profile setting. |
| Expose all Switch 2, DualSense and Edge special buttons as selectable Special Actions triggers | Implemented one 60-entry catalog for activation/unload: C distinct from Mute, GL/GR, Capture, Edge Fn/back paddles, individual Joy-Con rails and existing extras. Canonical saved tokens are unchanged. Fixed Capture/SideL/SideR being discarded on reopen; unknown tokens are retained and fail closed; Macro flags are not mistaken for unload buttons. **13 focused checks passed**, including real checkbox clicks, Save/Actions.xml/reload, runtime evaluation, and dark/light 720/1000px renders at 96/144 DPI. Not installed or published. |
| Make Xbox impulse translation independent of Trigger Lab, with an Advanced checkbox for every controller | Implemented. A universal master setting reuses the existing Nintendo XML field, preserving saved values. It controls only Xbox impulse contributions; ordinary body rumble and unrelated Trigger Lab effects remain independent. Targeted runtime/UI batch: **69 passed**, including real production callbacks with unopened test HID objects. |
| Keep the Nintendo impulse checkbox synchronized with Advanced | Implemented. Both controls bind to the same profile property; they are two views of one setting, not separate gates. Actual WPF click tests confirm synchronization. Nintendo strength/frequency tuning remains under Nintendo Options. |
| Let DualSense/Edge route Xbox impulses to either adaptive triggers or body rumble | Implemented. A subordinate profile checkbox selects adaptive triggers when on, body when off. A session-owned per-side overlay works with Trigger Lab disabled, preserves current underlying profile/Trigger Lab effects, and releases its previous destination on live edits and teardown. USB/BT, Edge, body-only devices, profile reset/XML round trips, and identical-frame policy races have regression coverage. The option stays visible with a clear disabled state on unsupported connected hardware, and remains editable offline. Physical feel is not established by these synthetic checks. |

## Final automated verification for this source batch

Release publication is authorized as **VIIPERRC4.5.2 — Joy-Con 1 & Nintendo Options**. The final source corrections are implemented and verified below; complete package/hardware acceptance and the exact-source GitHub release workflow remain separate delivery gates.

- Local DS4Windows Release x64 complete suite: **4,797 passed, 0 failed, 11 opt-in skipped** (4,808 total). Result: `DS4WindowsTests/TestResults/2026-09-09-nintendo-updater-actions-full-final.trx`.
- Final suite after the rumble fixes, universal impulse routing, and disabled-state styling: **4,877 passed, 0 failed, 11 opt-in skipped** (4,888 total). Result: `DS4WindowsTests/TestResults/2026-09-09-rc452-release-source-final.trx`. The immediately preceding full run also passed 4,877 tests. Actual dark/light WPF renders were inspected; the route option fits narrow layouts and visibly dims when unavailable. All allocation assertions remained enabled.
- The preceding run found one stale upright-right stick-assist test expectation. The producer routes upright-right to RX/RY and sideways-right to LX/LY; the fixture now tests both orientations, fractional precision, unused-axis isolation, and orientation-change baselining. No production change was made to satisfy that stale expectation.
- The 11 opt-in cases require live process audio capture or real Go/DS4 retirement end-to-end setup. Allocation assertions remained enabled and passed.
- Updater's full 245-test result and actual release ZIP fixture are documented separately in `DS4Updater/docs/portable-update-2.0.5-validation.md` in the sibling repository. Updater publication is recorded below. The DS4Windows batch has not been installed into Program Files or publicly released; the later portable handoff is recorded below.

## Additional release gates

- Original Joy-Con logical Disconnect now claims both exact paired halves without recursively disconnecting unrelated/reconnected controllers. Unexpected single-half physical loss still retains the survivor. Session-only pairs can unlink despite a damaged pair store; actual saved-pair removal remains persistence-checked. Focused validation: **58 passed**, `DS4WindowsTests/TestResults/2026-09-09-original-joycon-disconnect-release-gate.trx`.
- Updater now recognizes `VIIPERRC4.5.2` as binary `5.0.5.2`. Full updater validation: **245 passed, no skips**, including the immutable released ZIP. Both compressed x64/x86 publish checks passed. **Version 2.0.5 is published** at `https://github.com/hbashton/DS4Updater/releases/tag/v2.0.5`; the latest-release API returns `v2.0.5`.
- Updater source is committed and pushed to `master` at `d2e9b3ae316c52b8320055e2acc063da42d05905`. Exact-source GitHub Actions run `34374372375` passed the full test job and both platform builds. This is CI evidence, not publication or launched-worker end-to-end acceptance.
- Updater release run `34376111898` succeeded from that exact tagged commit. Both public assets were downloaded and matched the pre-release CI artifacts, PE version `2.0.5`, and ProductVersion commit. SHA-256: x64 `F1A38ED7C958A7837259812094B9E816B6478EC76E8FEC2B1D998756A44093DB`; x86 `B8369CDB38519845A94E89B6B56C1F361EFA1278C5605CEF85AD9CAA836D806D`. The DS4Windows safe bootstrap still needs delivery in RC4.5.2; this does not retroactively enable old portable callers.
- Hades II hands-off pulsing, Switch 2 Pro Bluetooth / virtual DualSense: the installed RC4.5.1 binary running at dump capture matched the published release. User-authorized heap dump captured at 10:56:55 local time shows retained compact motors `[43,0]`, native report ID `0x02`, flags `0x0c/0x57/0x00`, zero native motors, both physical trigger gates false, and an active canonical BodyLow value `11051` with sustained refresh. The full dump shows **no PCM carrier or media callbacks**, not an active silent PCM stream. Four consumed ordered-control slots retained the same stale compact motor value. No backlog, write failure, or observed write past TTL was found. This is direct evidence of stale compact rumble being admitted by the Nintendo translation. Fresh native source-selector admission now suppresses that value and prevents accumulated media snapshots from restoring it; **48 focused tests passed**. The later 86- and 126-test integration batches below supersede that initial verification. Dump and private diagnostic logs remain local on Desktop and must not be included in release/source assets.
- The bounded loopback packet-monitor attempt produced **zero packets**, so it is not evidence about sender behavior or rumble contents. Its trace/filter were stopped and removed; the memory dump supplied the useful evidence instead.
- Follow-up review identified two additional replay cases before release: stale media must not restore a nonzero motor after a fresh zero-motor stop that retains compatibility mode; mixed PCM/adaptive output must not repeat old PCM samples as a sustained adaptive effect. Both production fixes passed **126 focused tests**, including the actual original Joy-Con handler and physical maintenance writer. Original Joy-Con source selection integration previously passed **86 focused checks**. Existing compatibility hold semantics remain documented and unchanged: media can maintain source liveness, but cannot create new motor intent or undo a stop.

## Independent CI and portable handoff

- Source checkpoint `554914381794c467afcf8ea63a52a8ce78317100` was pushed to
  `main`. Independent CI run `34378088514` found three failing variants of
  `RealOutputWorkerDrainsNeutralBeforeDeviceStopReturns`. Publication was held.
  Their stop-delivered assertion passed, but the expected second packet did not:
  the fixture had no current Joy-Con registration, so the production authority
  guard had already substituted a neutral first packet. A new explicit
  first-packet-active assertion reproduced the invalid precondition for all four
  Joy-Con variants. The fixture now uses a real current coordinator registration
  and scoped output settings; the original active-drain assertions remain.
  Four separate tests cover unregistered-output neutralization. No production
  code was changed for this correction. Initial active publication is prepared
  before starting the worker, excluding the legitimate takeover-neutral setup
  from the blocked-active-write race. The already-idle test uses deterministic
  manual scheduling, since shutdown may legitimately enqueue another neutral.
  Focused lifecycle/feedback checks: **128 passed**. The real active-drain test
  passed **100 repeated cases** (20 runs across five transport variants).
  Final reviewed, unfiltered local full suite: **4,881 passed, 0 failed,
  11 existing opt-in skips** (4,892 total), including allocation assertions.
  Result: `isolated_results/rc452-ci-rumble-fixture/rc452-ci-rumble-fixture-reviewed-full.trx`.
  The final source commit still requires its own successful GitHub CI before
  tagging; these local results are not a substitute for that gate.
- The complete portable candidate built from that checkpoint was verified before
  launch: 551 files, 296 dependency assets, 23 language satellites, matching
  VIIPER aliases and the authorized Xbox persona. It started from its Desktop
  folder with separate lab data at 11:46 local time. The backend became ready;
  this alone does not establish controller or Hades II acceptance. Installed
  files and released RC4.5.1 assets were not replaced.
- The candidate's compiled updater bootstrap accepted live `v2.0.5` metadata and
  the verified public updater executable, then formed the safe RC4.5.2 launch
  request. Its injected process boundary did not start a worker. This extends
  bootstrap verification, not launched-worker end-to-end update acceptance.
- A separate initially updater-free Desktop fixture also exercised the
  production bootstrap's actual fresh download of public 2.0.5. Origin, size,
  hash, PE identity, staging cleanup and safe launch arguments passed. An extra
  PowerShell post-check initially needed a typed-string correction; it was
  completed against the same downloaded bytes without a second asset download.
  No updater worker or controller app was launched by this check.
- The next exact-source CI run `34380417498` at
  `6e99c44bef78dddd0c1ba440e62a1e9066f71643` passed shutdown tests but failed
  the first registered Joy-Con allocation case: 4,752 bytes versus zero.
  Publication remained held. A new native-profiler capture of the same warmed
  writer reproduced counter-only jumps during background-GC preparation with
  zero objects and a valid deliberate-allocation control. The test-only
  measurement correction and limits of attribution are documented in
  [Nintendo allocation release gate](2026-09-09-nintendo-allocation-gate.md).
  No production GC policy, writer code or zero-allocation tolerance was changed.
- After that correction, the ordinary no-profiler/no-diagnostic run passed
  **144 focused tests** and **4,886 unfiltered full-suite tests**, with zero
  failures and the same 11 opt-in skips (4,897 total). Same-writer positive
  controls prove real allocations still fail the zero-byte gate; the separate
  native pressure control covered 1,280 strictly zero measurement windows.
  Final source must still pass its independent CI before release tagging.

## Post-release follow-ups

- Reddit RC4.5 Stop/read-failure-995 hang: a matching fallback-worker/service-lock
  deadlock was reproduced with four failing-first tests. Source fix implemented;
  **152 focused and 4,915 full-suite tests passed**, zero failures and 11 existing
  full-suite opt-in skips. Added 29 regressions covering removal/cancellation,
  reconnect ownership and hotplug publication. Not installed or published;
  reporter hardware confirmation remains open. See
  [Stop/removal regression](2026-09-09-stop-removal-deadlock.md).
- Consistent small Joy-Con L vibration-test stutters remain open. Earlier
  traces did not establish an active sustained-rumble window; they are not
  evidence that the stutter is fixed. The Stop report takes priority in this
  pass; running apps and controllers are left untouched.

## RC4.5.3 publication and corrected report scope

This update supersedes the earlier pending-publication status snapshots above.

- [VIIPERRC4.5.3](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.3)
  is published as an unsigned prerelease: release ID `385874559`, 13 assets,
  source `d2e743a2d871910d2e4733c6875ab87d47096286`, Windows version `5.0.5.3`.
  The complete portable package retains VIIPER `0.1.3-rc4.5`.
  [Post-publication workflow 34412155943](https://github.com/hbashton/DS4Windows/actions/runs/34412155943)
  completed successfully from that exact source.
- [DS4Updater 2.0.6](https://github.com/hbashton/DS4Updater/releases/tag/v2.0.6)
  is stable and published. The existing compiled RC4.5.2 bootstrap successfully
  downloaded it into an initially updater-free isolated fixture, verified its
  origin, size, SHA-256 and PE identity, and reached a capture-only launch boundary
  with the safe RC4.5.3 arguments. The compiled 2.0.6 resolver also accepted the
  **unmodified public** RC4.5.3 metadata and verified the actual release receipt,
  ZIP, EXE/DLL identity and package marker; no simulated publication envelope was
  used. Inputs were unchanged and no updater worker, application or broker was
  launched. Local proof files in the sibling updater repository are
  `isolated_results/updater-2.0.6/PUBLIC-RELEASE-METADATA.json`,
  `PUBLIC-RC453-UPDATER-VERIFICATION.json` in that same directory, and
  `bootstrap/PUBLIC-BOOTSTRAP-VERIFICATION.json`. This is bootstrap/package
  acceptance, not launched-worker end-to-end or controller hardware acceptance.
- The reporter clarified that **read failure 995 occurred before pressing Stop**
  and that RC4.3 works while RC4.5 fails. The earlier explanation treating the
  initial failure as cancellation caused by Stop was not established and does
  not fit that order. RC4.5.3 fixes the reproduced Stop/removal deadlock and exact
  reconnect-ownership faults; it does **not** establish or claim a fix for the
  reporter's initiating read failure. Controller model/transport and the initial
  cancellation source remain unconfirmed. The user explicitly authorized this
  release while that separate RC4.3-to-RC4.5 investigation continues; a further
  correction belongs in a later release if reproduced. Joy-Con rumble stutter
  also remains open, without a hardware-fix claim.

## RC4.5.4 recovery correction

- The corrected failure-before-Stop chronology now has a failing-first integration
  regression through the actual reader, DualSense lifecycle logger, removal
  handler and public service Stop boundary. The old synchronous handler deadlocks;
  the queued RC4.5.3 handler passes. This does not attribute the initial 995 to Stop.
- A separate DS4 Bluetooth terminal-failure defect was reproduced: a new forced
  output could block removal after the failure log. RC4.5.4 removes that terminal
  send, while retaining ordinary issue #84 control-pipe routing and all unexpected
  error diagnostics. Five new tests include blocked-output and audio-lane cases.
- Versioned Release/x64 validation: **4,927 passed, zero failed, 11 existing
  opt-in skips**, unfiltered and including allocation assertions. Exact-source CI
  and complete release artifact checks are still required before publication.
- Windows binary version is `5.0.5.4`, release tag `VIIPERRC4.5.4`. Published
  RC4.5.3 and updater 2.0.6 remain immutable; no broker change is needed. The
  initiating native cancellation and Joy-Con rumble stutter remain unconfirmed.
  See [corrected chronology and recovery evidence](2026-09-09-read-failure-recovery.md).

## RC4.5.4 publication completed

This supersedes the pending CI/publication gates in the preceding snapshot.
[VIIPERRC4.5.4](https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.5.4)
is published from `f15c3001ccb5ba577a20c9174e252e2835383631`, with all 13 verified
release assets. Exact-source CI `34413872665`, draft build `34414663236` and
post-publication verification `34415309349` all passed. The complete installer
and ZIP are also in `Desktop/DS4Windows-RC4.5.4-Release`; neither was run.

The unchanged updater 2.0.6 resolver accepted untouched public RC4.5.4 metadata
and the real receipt/ZIP/PE/marker identities. Twenty further repetitions of the
six new recovery tests passed (120 cases). Source, test scope, hashes and limits
are recorded in the [published recovery evidence](2026-09-09-read-failure-recovery.md#published-result).
The initiating 995 and Joy-Con rumble stutter are still unconfirmed; publication
does not turn those open questions into hardware-fix claims.

## RC4.5.5 setup and startup correction

- The user's failed RC4.5.3 setup was a stale unmarked `RunVIIPER` task conflict,
  not a demonstrated USB/IP driver failure. The exact old task/settings were
  backed up and repaired, and the approved Retry completed successfully.
- Permanent fixes separate portable runtime preference from installed startup,
  preserve task ownership, align priority, update owned tasks in place, and
  recover narrowly verified legacy registrations only after durable backups.
  Unrelated tasks and unsafe backup paths remain protected.
- Final upgrade review also reproduced and corrected a logon repair loop: with
  installed startup enabled, runtime selection now matches the verified canonical
  startup broker and normalizes the stale portable cache. Four failures reproduced
  the mismatch before 123 focused cases passed with the fix.
- Versioned Release/x64 suite: **4,958 passed, zero failed, 11 opt-in skips**,
  including allocation assertions. Startup recovery, real backup-file and
  22 current-attempt diagnostic cases passed; the bootstrapper build is clean.
  These three script gates are now required in CI.
- Prepared tag `VIIPERRC4.5.5`, Windows version `5.0.5.5`, title **Reliable Setup
  & Startup**. Exact-source CI, release artifacts and updater acceptance remain
  publication gates. VIIPER and updater 2.0.6 are unchanged.
- See [root cause, safety boundaries and evidence](2026-09-09-installer-startup-recovery.md).
  The initial 995 cause and Joy-Con rumble stutter remain open; this installer
  correction does not change input or haptic transport.

## Current execution boundaries

- Preserve the earlier source and live-preview changes, now checkpointed in the
  release source commit; later test corrections do not replace unrelated work.
- Source and fixture tests did not restart the user's apps. The user then
  closed DS4Windows cleanly at 11:33:46 local time and quit VIIPER for the
  separately requested portable hardware handoff. Neither process nor its
  3241/3242 listeners remained at the handoff preflight.
- Do not install tests into Program Files, overwrite released assets, or claim
  physical validation from synthetic tests.
- Original Joy-Con HD rumble has different temporal/packet limits from Joy-Con 2;
  document approximations, preserve sides/bands/peaks where representable, and
  never send guessed Switch 2 commands to original hardware.
- Updater 2.0.5's public publication and the next DS4Windows delivery remain
  distinct from local builds and validation.
