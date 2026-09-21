# DualSense rear-channel underrun — 2026-09-21

## Evidence and limits

External USB/IP source PCM and physical Bluetooth HID captures were compared
against a frozen, independent public WDL sinc64 oracle. The comparison fixes one
global block offset, preserves authored zero samples, and does not remove quiet
carriers to obtain a passing result. Host USB submission/completion is not proof
of over-air receipt or actuator behavior.

The same pre-gate candidate produced different outcomes across controlled runs:

- Capture `candidate-clock-02` inserted one all-zero carrier between source blocks
  203 and 204. Every nonzero block still appeared, but the strict first-anchor
  comparison failed: 31,129 scalar mismatches. Matching only nonzero hashes would
  have hidden this sequence error. The source-data timestamp preceded the quiet
  carrier, but the corresponding ISO completion followed it; neither timestamp
  identifies actual helper availability.
- Capture `candidate-clock-03` passed all 66,176 scalars in 1,034 complete source
  blocks at one fixed offset. Its full accounting is 249 leading zero carriers,
  1,034 source blocks, and 1,063 trailing zero carriers. The remaining 30 oracle
  stereo frames are zero and do not form an emitted complete block. All source-
  window reports have successful matched host USB completions; only the final
  idle-tail carrier's completion is outside the capture.

Both runs used the scheduler-corrected broker, SHA-256
`44EEBCDA8672E32EA9CF5B386F3E5AFFA1044194074A83F32D239762644C4786`,
but **neither used the new rear underrun gate**. Capture 03's pass therefore does
not validate this change or erase capture 02's failure. Its captured source-data
complete → host USB submission median was 29.340 ms; this is not a latency-parity
claim.

A concurrent read-only ring observer collected 1,097 valid retained slots with
no rejected or overwritten slots. Its actual polling was approximately 15.5 ms,
not the requested 2 ms sleep. The producer-supplied enqueue timestamp marks the
start of publication, potentially before capacity waiting; only observed write-
sequence changes bound committed publication. Sampling cannot establish the
helper's instantaneous empty decision, and invalid observations must not be
interpolated.

## Bounded correction

Only steady V5/native speaker presentation is eligible. Before claiming the
front packet, the presenter probes for an owned current-generation rear block.
If the ring is empty after a recently committed source block, it may defer once
for at most one 32-frame / 3 kHz block interval (approximately 10.667 ms), also
bounded by the prior publication-attempt timestamp's approximately 32 ms recent
window. Notifications cannot renew that absolute deadline. Real end-of-source
therefore resumes ordinary silence after one bounded wait, without replaying the
last waveform. Authored zero blocks remain ordinary owned PCM.

The tradeoff is a possible bounded delay to the unclaimed front-audio carrier;
there is no extra startup buffer, gain change, resampling change, packet removal,
or fabricated waveform. Compact/paired transports and startup priming are not
gated. Eligible native commands and microphone transitions keep their existing
priority; a Busy native credit is rechecked at the existing 1 ms retry scale
during deferral. No physical I/O runs under the state lock.

Ring protocol version 2 adds a separate data-available event, not reuse of the
producer's space notification. Preparation does not release a slot: only an
accepted media write commits it. Lifecycle changes reset both prepared ownership
and the gate. Existing ordered helper Stop semantics remain unchanged. The
null-by-default internal test hook has no CLI, IPC, profile, or settings route.

## Software validation and remaining gate

The actual-helper regression first failed with one extra rear interval between
authored A and B while front audio and the native trigger still progressed.
The same test then passed. The initial expanded six-test run also passed, covering
authored silence, finite end-of-source waiting, Busy physical ownership,
generation clear, microphone control, and front/trigger order.

Receipts under `isolated_results/haptics-ab-20260920/` are:

- `rear-underrun-red-01/rear-underrun-red.trx`: 1 failed, preserved.
- `rear-underrun-green-01/rear-underrun-first-green.trx`: 1 passed.
- `rear-underrun-expanded-01/rear-underrun-expanded.trx`: 6 passed.
- `candidate-clock-wdl-timing-02/` and `candidate-clock-wdl-timing-03/`:
  immutable external-capture sequence/timing audits and input hashes.
- `realtime-ring-observation-01/`: bounded read-only observation and quality
  metadata, separate from the external capture clock.

The first combined 36-test run found a further real failure: a Busy native write
granted itself one front-media fairness debt while that same media was held by
the rear gate. The corrected guard grants new debt only when media can actually
progress; it preserves existing microphone/native debt. The same 36 tests then
passed unchanged. Both receipts are retained, not replaced.

Final receipts under `DS4WindowsTests/TestResults/` cover ordered Stop
preservation, native Busy→ready wake and fairness, prepared-age accounting, and
the null internal test seam:

- `sony-timing-gate-focused.trx`: 35 passed, 1 failed, preserved.
- `sony-timing-gate-focused-02.trx`: 36 passed, 0 failed, including zero-allocation
  checks.
- `sony-timing-expanded-final.trx`: 1,780 passed, 0 failed, 3 opt-in skips.
- `sony-timing-full-final.trx`: 6,766 passed, 0 failed, 12 opt-in skips; SHA-256
  `831C4EAB536725E525C273C754E6CD8B7860DA2D243088A6A8B9128DBC869BC8`.

Independent review found no remaining source blocker in the bounded gate.
**The new gate has not yet been validated on hardware.** Fresh paired capture
must verify the complete fixed sequence, all zero carriers, front/control
behavior, and timing; no release or user-visible improvement is claimed here.

## Portable deployment checkpoint

Build07 was independently verified as a complete 554-file portable package.
Its mapper DLL SHA-256 is
`1A2F8462F653F62CFA4CCA78CEDB9EC24BE3F7EB38C04F3A85B3210C07D8497A`.
The lab directory explicitly substitutes the already-tested clock/WDL broker
`44EEBCDA8672E32EA9CF5B386F3E5AFFA1044194074A83F32D239762644C4786`
and its license notice; the unchanged ZIP retains the public broker and is not
being offered as the private timing candidate.

At 05:36:10 UTC the reviewed same-path Desktop replacement completed. The full
old tree is retained separately. All private data matched byte-for-byte before
launch, authentication and HidHide were unchanged, and the new mapper logged
backend-ready and controller-search startup. Installed files, drivers, and the
game were not changed. Evidence is `sony-timing-deployment-01/result.json`.

Hardware verification remains pending. The old, pre-gate session had already
lost the Bluetooth controller at 05:03:46 UTC with read error 1167 and a
charge-battery message after reporting 0%. It removed its virtual import and
helper. Thus the new build was validated for idle startup only; no connected
controller, helper, converter echo, or new-gate physical quality is claimed.
