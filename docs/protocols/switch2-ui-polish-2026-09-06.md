# Switch 2 controls and Joy-Con artwork

This change is presentation-only. It does not alter controller admission,
pairing, report scheduling, mapping semantics, feedback translation or VIIPER.

- Layout/connection, HD rumble, motion, Joy-Con mouse/aiming and calibration
  have separate themed cards. Detailed dual-gyro and stick-action tuning is
  collapsed until requested; generic stick settings remain available to Pro.
- Switch 2 tooltips describe behavior and purpose in plain language, without
  protocol or lifetime terminology. Existing profile bindings and action
  handlers are retained.
- Joy-Con-only controls are hidden for Pro and other physical controllers.
  Offline profile editing keeps them available.
- Original frozen vector artwork distinguishes left, right and paired Joy-Cons
  in the controller list and profile header. The mapping selector offers an
  upright Joy-Con front view with matching clickable button and stick targets.
  This is an upright layout preview, not a new horizontal-mode mapper. C/Chat,
  rail and rear controls remain available through the existing mapping list.
- Wireless setup explains automatic standalone activation; redundant manual
  standalone buttons are no longer displayed. No association records change.

Validation: Release x64 build/publish succeeded. Both complete test runs passed
3,799 tests with zero failures and three opt-in audio tests skipped. Coverage
includes model-specific icons, frozen artwork, diagram bounds, capability
guards, existing binding locations and a short-tooltip regression check.

Portable Windows UI inspection confirmed the new Joy-Con selector/artwork and
its A-position target opening the existing Circle mapping dialog. The preview
was cancelled without saving, and the inspected profile's file hash remained
unchanged. No live hardware feedback or transport tests were required for this
UI-only change; the b80 VIIPER backend is reused unchanged.
