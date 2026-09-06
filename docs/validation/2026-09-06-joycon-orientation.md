# Joy-Con orientation UI validation — 2026-09-06

Scope: single left/right Joy-Con diagrams and controller-list icons follow the
effective Switch 2 holding style. A controller's saved override wins over the
profile fallback; a joined pair remains paired. This is the selected holding
style, not automatic orientation inferred from motion samples.

The physical-controller editor selects the appropriate view automatically.
An offline profile still offers manual artwork previews. Face targets follow
the existing Xbox/Nintendo layout projection. Sideways shoulders use SL/SR;
the front L/ZL or R/ZR controls follow the existing mini-controller source
identities. A sideways right Joy-Con uses the logical left stick. No input,
Bluetooth, USB/IP, VIIPER, or haptic code changed.

Shared frozen vector geometry supplies the artwork, hit targets, and hover
masks. Switching views removes old targets and rebuilds the reverse mapping;
hidden generic buttons cannot highlight nonexistent controls. Source-control
LIST ONLY badges update with actual target presence.

## Evidence

- Focused orientation suite: 28 passed, zero failures or skips.
- Final Release/x64 suite: 3,814 passed, zero failures, three opt-in live audio
  tests skipped. The allocation assertions remain enabled and passed.
- Portable publish: 5.0.4.82-JoyCon-Orientation-Preview.
- WPF contact sheet rendered and inspected: both single upright views, both
  sideways views, and joined artwork; all diagrams are 440 by 220.
- Computer-use verification in the stopped portable app: selector labels fit;
  both sideways and upright-right views render; clicking sideways SL opens
  L1; clicking the sideways right stick opens L3. Dialogs closed unchanged
  and profile editing cancelled; before/after profile hashes match.

No physical controller was connected during these UI checks. Override and
notification behavior was exercised with synthetic runtime objects, not
claimed as a live Joy-Con connect/join/gameplay validation. Installed files,
startup tasks, pairing records, and the VIIPER binary were not modified.
