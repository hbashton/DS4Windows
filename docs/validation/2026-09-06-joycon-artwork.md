# Joy-Con 2 artwork and thumbnail bounds — 2026-09-06

Scope: presentation only. No controller transport, input mapping semantics,
pairing, haptics, VIIPER, profile values, or driver changes.

The original WPF vector drawing now uses an asymmetric rounded shell, layered
edges, shaded key caps, recessed stick caps, blue/coral inner rails, and Capture,
Home, and C legends. Nintendo's front-view support illustration was a visual
reference, not an asset copied into the application:
https://support.nintendo.com/ph/switch2/mastery/other-player/onehard/index.html

All five orientations remain cached and frozen. Icons have zero-origin bounds
with eight drawing units of transparent padding around their complete content,
including bevels and shadows. Mapping diagrams retain the fixed 440×220 canvas
and canonical control coordinates; their canvas must not be cropped to icon
bounds.

The Controllers card previously requested a 108×66 image plus 8 units of margin
inside a 116×72 border: the child exceeded the border's available space. Its
image now sizes from the padded parent. The overview and both sidebar contexts
also use padded parent viewports and centered Uniform images without competing
fixed child dimensions.

Button painting, hover masks, and clipped hit targets share the same physical
geometry, transformed with the selected orientation. Capture and the small
system/rail buttons no longer inherit a generic circular mask. Stick centers,
direction sectors, and logical button identities are unchanged. Joy-Con artwork
does not use a raster highlight atlas; existing non-Joy-Con atlas assets are
unchanged. The editor already clears previous raster masks and dynamic buttons
when changing diagrams.

## Validation

- Release/x64 full suite: **3,852 passed, 0 failed, 3 opt-in live audio tests
  skipped**. Allocation assertions remain enabled and passed.
- Five orientations in four production XAML image hosts at 96, 144, and 192 DPI:
  **60 rendered combinations**, each nonblank with clear pixels on all four
  edges. Layout bounds also remain inside each host.
- WPF-rendered contact sheets inspected for thumbnails and button masks:
  `joycon-thumbnail-hosts.png` and `joycon-artwork-and-masks.png` (TRX attachments).
- Existing orientation, physical holding-style override, canonical target, and
  direct Switch 2 Pro raster-loading regressions pass.
- New mask checks cover frozen geometry, transformed bounds, filled centers,
  and square Capture versus circular Home.

These are offline WPF renders of the production image hosts, not a claim of
interactive verification in the running controller session. No hardware was
opened or interrupted by the artwork tests. Portable preview: b85 / 5.0.4.85.
