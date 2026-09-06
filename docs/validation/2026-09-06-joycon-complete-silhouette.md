# Complete Joy-Con thumbnails — b86 follow-up, 2026-09-06

The user's b85 screenshot showed a flat, truncated-looking upper controller.
Live inspection of b85's overview/sidebar confirmed the same silhouette. The
thumbnail factory still guarded painting ZL/ZR with `if (diagram)`, although
the mapping view painted them. b85's padding/nonblank tests did not detect
missing artwork. Its earlier completion claim was therefore too broad.

The thumbnail and diagram now both paint the upper triggers, shoulder legends,
and sideways SL/SR legends. A shaded rear-trigger housing joins the upper cap
to the shoulder section. No control coordinates, hover masks, profile values,
physical controller transport, or virtual backend code changed.

Validation:

- A new completeness regression failed on b85: "Icon 0 is missing the actual
  painted ZL shape." It now passes, checking both top triggers and the lowest
  Capture/Home/C controls in all five icon orientations.
- Added 120 and 168 DPI: **100 host/orientation/DPI combinations** (four hosts,
  five orientations, five scales from 100% to 200%) remain nonblank and inside
  all four edges.
- Focused artwork/orientation suite: **37 passed**.
- Full Release/x64 suite: **3,855 passed, 0 failed, 3 opt-in audio skipped**;
  allocation checks remain enabled and pass.
- Inspected durable WPF contact sheets at native 125% pixel size plus the
  updated mapping/button-mask sheet. Successful-run MSTest deployment folders
  can be removed by the runner; the render test now supports optional
  `DS4W_ARTWORK_EVIDENCE_DIRECTORY` for retaining images during visual review.
- After this evidence-output-only harness change, all eight presentation
  checks were rerun and passed.

Computer-use inspection of b85 required bringing its elevated window forward;
its accessibility tree was limited. No profile controls were changed. The b86
live-view check remains pending until the existing portable session is closed
and replaced safely. Do not equate the offline render with that live check.
