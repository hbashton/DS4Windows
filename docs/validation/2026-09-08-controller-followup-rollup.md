# Controller follow-up rollup, 2026-09-08

This covers the user's requested roughly two-hour change window beginning with
the 17:40 local checkpoints, plus the Joy-Con disconnect investigation added
during final validation. Source/test evidence is distinguished from acceptance
in the running portable app. Final integration results are appended below.

## Current requested changes

| Request | Source result and limits |
| --- | --- |
| Trigger Lab profile saving (#74) | Verified existing-profile XML persistence. Main page autosaves; profile editor requires Save; Preview is temporary. User explicitly accepted this behavior, so it was not changed. |
| Restore triggers after Preview | Restores the correct active profile's ordinary L2/R2 effects when Trigger Lab is disabled/inactive, instead of leaving the triggers Off. Active Lab override semantics remain intact. |
| Switch 2 Pro C remapping highlight | Added the missing artwork-aligned hit/highlight target and canonical mapping-row lookup; physical press and remapping selection refer to the same control. |
| Sideways Joy-Con L Capture tooltip | Uses the physical Capture name while retaining the intended default Guide mapping. No silent remap. |
| Unreadable tooltips | Shared theme-owned tooltip background/text/template, including profile editor inheritance and dynamic theme changes. |
| Missing Controller Readings values | Switch 2 uses a nonblocking owned raw/mapped snapshot instead of waiting for a legacy HID event it never signals. Selected-device context and timer overlap/stop races are guarded. |
| Pro gyro mouse left/right | Corrected the Pro USB/Bluetooth sensor basis before the existing SixAxis conversion. Other controller bases and saved inversion settings remain unchanged. An earlier user inversion workaround may need undoing. |
| Switch 2 rumble stops early | Added held-local lease renewal and output-only maintenance of finite HD-rumble packets. Preserves rich payloads, stop ordering and external expiry; completed PCM/native streaming slices are not replayed. Cold shutdown now handles brief maintenance contention within the existing deadline, with real timeout/uncertain ownership still failing safely. |
| Switch 2 IDs all zero / Link Profile-ID | Uses existing full persistent peer IDs for display and profile linking, separately from the legacy MAC. Pair IDs include the exact left/right members. Pair/unpair preserves output/current profile but recomputes the successor's link checkbox. IDs are installation/Windows-identity and transport scoped, not Nintendo factory serials; no guessed USB/Bluetooth matching or blank-link migration. |
| Multiple Switch 2 entries in options | Removed an unused duplicate-zero-MAC dictionary and guarded absent legacy options stores/cleared selection. No unsupported options store is invented. |
| Left Joy-Con and joined pair disconnects | Authorized local dump proves valid same-lifetime reports reset their device counter; old Joy-Con admission treated this as fatal. Both left standalone and right-in-pair incidents retained matching evidence. Correction extends arrival-order handling while retaining identity, framing and host-time checks. |
| Prevent similar future failures | Cross-mode/side reset regressions and retained one-shot failure evidence are implemented; no hot-path report logging or arbitrary relaxation of lifecycle safety. |

## Counter-reset acceptance rule

For the supported Switch 2 Common05 input paths (Pro USB/Bluetooth and left/right
Joy-Con 2 Bluetooth), the raw device counter is diagnostic, not an admission
requirement. Valid reports from the exact current connection are accepted across
arbitrary resets, backward jumps, duplicate counter values and wraparound while
host completion time remains nondecreasing. The next report compares against
the new counter baseline. No guessed reset range or firmware modulus is used.
Other controller families and unsupported report/transport identities are not
given this exception. Framing, calibration, generation and host-clock checks
remain mandatory; stale reports are not made safe merely by having a plausible
counter.

The focused counter suite passed **147/147**, including 17 new regression cases
and a 20,000-report joined-runtime discontinuity loop with zero managed
allocations. Twelve first-failure diagnostic cases also passed in the broader
targeted run. Full integration acceptance is recorded separately below.

## Additional integration defect

The first complete run (`controller-followup-final.trx`) passed 4,412 tests,
failed one, and retained 11 existing skips. The failure was
`DelayedCfbkRefreshesTtlAtPresentationAndZeroDelayStaysDirect`, at physical
feedback retirement. Independent source review confirmed this was not merely
a test-cleanup mistake: standalone/joined runtime owners retire feedback before
stopping input, and their one-shot failure path makes quarantine sticky.
Transient contention with the new output worker could therefore quarantine a
healthy controller slot. A cold, deadline-aware retirement boundary now handles
this on Bluetooth standalone/joined owners and the actual USB composite
participant. The immediate/nonblocking API remains separate; its hidden
blocking impulse-cleanup lock was also corrected. Input processing does not
wait on this cold boundary. Existing authority, terminal-neutral, native-release
and disposal proof checks were not relaxed.

The integrated targeted run passed **328/328**. Three later survivor-budget
boundary tests passed on the final source: two physically released Joy-Cons need
no output writes; one survivor requires a full write budget and gets exactly one
neutral. Real deadline expiry retains quarantine even after a late write ends.
The first failed complete run is not counted as final acceptance. Bluetooth
terminal-attempt admission uses the existing 100 ms per-target Windows write
cancellation contract; this is not a guarantee that managed code can forcibly
preempt an uncooperative native API.

## Earlier checkpoints in the same requested window

DS4Windows `50c45e0` and VIIPER `4f27999`, at 17:40 local:

- #94: startup-preference ownership, disabled-task preservation, and verified settings changes.
- #80: renamed executable setup/staging and alias sidecars.
- #69: VIIPER tray initialization when Explorer is not yet ready.
- Calibration persistence: atomic replacement prevents our own held reader from exhausting save attempts.

The [previous follow-up ledger](2026-09-08-reported-issues-follow-up.md)
contains the detailed tests and remaining live acceptance for those checkpoints.
The earlier 14:38 fixes (#46, #60, #75, #76, #83, #84) precede this time window
and are not newly claimed here.

## Audited without claiming a new complete fix

- #68: the matching Switch Pro Bluetooth startup/calibration failure class was
  already guarded in `ed0e983`; new Bluetooth startup regression cases verify
  safe handling. The reporter's exact controller/stack was not reproduced.
- #32: all six supplied text logs reviewed. The latest slow run spends about
  19 seconds before Found Controller, then about two seconds to virtual output.
  The exact pre-discovery cause remains unproven; no speculative fix.
- #82: diagnostic tooling and source-backed leads, not a reproduced/fixed slowdown.
- #74: accepted save semantics and the separate preview restoration correction
  do not establish that every reported save-error scenario is fixed.

## Final integrated validation

All source and test edits were frozen before these runs:

- `controller-followup-complete.trx`: Release/x64 rebuilt source, **4,426 passed,
  zero failed, 11 existing skips** (4,437 total).
- `controller-followup-repeat.trx`: separate repeat against the same compiled
  source, **4,426 passed, zero failed, 11 existing skips**.
- `controller-followup-allocation.trx`: all **152 allocation-named tests passed**,
  zero failed/skipped, selected with `Name~Allocat`.
- Both complete runs use the existing CI exclusions for `CheckSettingsSave`,
  `CheckWriteProfile` and `CheckJaysProfileRead`; no new exclusions or skipped
  tests were introduced. The skipped-test names match the earlier checkpoint
  suite. No failing allocation assertion was ignored or disabled.
- Independent reviews covered counter admission, input snapshots, identity and
  profile handoffs, presentation preservation, cold owner retirement and USB
  proof/disposal ordering. `git diff --check` passed on the final staged changes.
- VIIPER source remains clean at `4f27999`; its earlier checkpoint validation is
  linked above. It was not changed or rebuilt by this controller follow-up.

These results supersede the failed intermediate full run described above, not
the remaining physical-controller and live-UI acceptance boundaries.

## Deployment and evidence boundaries

The current fixes are source changes and offline tests. The live portable b93
app was not replaced. A local heap dump was captured to diagnose the two
disconnects; it is kept on the Desktop, not committed or uploaded. Dump capture
paused the app and caused a USB Pro timeout at 19:38; it automatically reattached
after capture. This diagnostic side effect was disclosed to the user. No installed
Program Files content, driver, user profile, association store, startup task,
or HID-hiding policy was changed in this follow-up. No GitHub issue was closed
or commented on, source pushed, installer built, or release published.

## Detailed evidence

- [Joy-Con disconnect proof and counter policy](2026-09-08-joycon-counter-reset.md)
- [Held rumble, packet maintenance and shutdown regressions](2026-09-08-switch2-held-rumble-maintenance.md)
- [Stable local identities and profile links](2026-09-08-switch2-profile-identity.md)
- [Controller Readings](2026-09-08-controller-readings.md) and [Pro motion basis](2026-09-08-switch2-pro-motion-basis.md)
- [C/Capture remapping](2026-09-08-controller-diagram-remapping.md) and [tooltip themes](2026-09-08-tooltip-theme-readability.md)
- [Trigger Lab restoration](2026-09-08-issue74-trigger-restoration.md)
- [#68 Bluetooth startup validation](2026-09-08-issue68-bluetooth-startup.md) and [#32 log diagnosis](2026-09-08-issue32-startup-log-diagnosis.md)
