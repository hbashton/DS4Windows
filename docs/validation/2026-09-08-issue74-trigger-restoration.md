# Issue #74: restore normal trigger effects after Trigger Lab, 2026-09-08

Status: the confirmed restoration inconsistency is fixed and covered by offline regressions. This does not establish that every restart/profile-switch symptom in [issue #74](https://github.com/hbashton/DS4Windows/issues/74) has the same cause, or that the report was user error.

## Defect and bounded change

Normal profile application uses an active Trigger Lab override when present and otherwise applies the profile's legacy L2/R2 effects. Trigger Lab's preview/live-edit restoration instead encoded Lab Off when Lab was disabled or had no persistent effect. That could turn off an existing normal profile effect after preview, pause, or editor restoration.

Both `TriggerLabControl.ApplyPersistentEffects` and `RestorePhysicalProfileEffects` now use `TriggerLabProfileEffectRestoration`. Its decision matches `ControlService.CheckProfileOptions`:

- An active Lab override retains the existing pair policy: apply each Lab design with its own active flag; an inactive side is explicitly off.
- Without a persistent Lab override, apply each side's existing legacy effect and parameters through `PrepareTriggerEffect`.

Live restoration uses the selected profile slot; restoration after editing another profile uses the physical controller's running profile slot. The helper does not normalize or rewrite a stored design, save a profile, change virtual pads, reset output ownership, or run the other side effects of `CheckProfileOptions`. Existing game-rumble feedback handling is unchanged.

Temporary preview duration remains 2,800 ms. Preview still does not raise `SettingsChanged`; shell autosave and the profile editor's Save behavior were not changed. Trigger Lab XML remains profile-local. Full-pull input mapping is separate from physical trigger feedback.

## Regression evidence

Before changing the restoration decision, its previous behavior was mechanically extracted into the production helper and both control entry points were wired to it. The new native-free tests then recorded:

- `issue74-restoration-before.trx`: **8 failed, 3 passed, 11 total**.
- `issue74-restoration-after.trx`: **11 passed, 0 failed** after the fix.
- `issue74-trigger-regressions.trx`: **27 passed, 0 failed** with the broader `FullyQualifiedName~TriggerLab|FullyQualifiedName~TriggerFullPullTests` filter.

Coverage includes disabled/paused Lab with independently configured legacy sides; enabled game-rumble-only/no-persistent-override state; missing Lab settings; active left/right override policy; pause/resume without rewriting armed designs; and source-bound restoration-slot/temporary-preview wiring. The dispatch tests exercise the production decision with callbacks instead of constructing a physical controller. Existing XML, encoding, linked/split-design, and full-pull tests were also retained.

Commands used Release/x64 with `--no-restore`; the broader run also used `--no-build`. Existing application warnings were not suppressed. No live controller, user profile, running application, driver, or installed build was touched. Physical DualSense acceptance remains unperformed. Separate save-failure reporting and native-game ownership behavior were not changed or claimed resolved.
