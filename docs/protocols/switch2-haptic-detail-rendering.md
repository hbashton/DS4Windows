# Switch 2 haptic detail rendering — September 5, 2026

## Follow-up: PCM peak and stereo-band correlation

After playing b68, the user confirmed the five-second input stall did not recur,
but reported that DualSense PCM -> Switch 2 HD rumble was insufficiently tuned:
peaks and left/right LF/HF assignment did not correlate well with the source.
This follow-up changes only the pure synthesis/composition path, not input,
transport cadence, the virtual backend, controller ownership or profile UI.
The running portable session has not been replaced by this change.

Source audit found no global left/right swap: VIIPER extracts USB audio channels
2/3 (zero-based) into even/odd stereo samples; DS4Windows maps those to independent
left/right groups; both USB and BLE encode left first and right second. Each
physical side can carry both LF and HF; LF is not synonymous with left, nor HF
with right. Tests independently extract the four wire fields on both transports.
Actual actuator perception still needs a physical A/B check.

Three concrete synthesis weaknesses are addressed:

1. The old packet-wide band proportions were reused in every time slice, so a
   later HF burst could color an earlier LF event. The same audited 32-sample
   window now reconstructs LF from DC/bins 1/2, takes the complementary HF signal,
   and measures each band's RMS and peak separately inside each of the three
   slices. The existing dominant-frequency range/remapping remains unchanged.
2. RMS alone diluted short attacks, while its 1.45 gain clipped larger sustained
   levels early. Each band's envelope now has a 95% reconstructed-peak floor,
   bounded by its reconstructed peak and the source slice peak. Local band energy
   is normalized to that slice's source energy before this envelope mapping.
   Exactly silent slices remain silent despite finite-window reconstruction
   ringing. This is a deliberate bounded peak-oriented synthesis policy, not an
   energy-preserving or lossless physical-waveform reconstruction.
3. Any nonzero compatibility rumble previously replaced the PCM carrier frequency.
   PCM/body composition now chooses the stronger component independently for
   each side and band, with PCM winning ties. Both amplitudes are still composed
   with the existing headroom policy. Supported, pressed trigger overlays retain
   their explicit priority. Quiet PCM cannot retune a stronger compatibility
   effect; tiny compatibility rumble cannot retune a stronger authored PCM effect.

The short single-sample peak fixture (96/128, middle slice, otherwise silent)
went from HF code136 to276, with LF code53 and a full source-peak reference code339.
These are encoded amplitude codes, **not measured actuator acceleration**. The
opening/closing slices and opposite side stay silent. Sustained source values
88/96/112/127 no longer collapse onto one maximum amplitude code. A test covering
the former shared-ratio behavior was updated to require LF to remain in the LF
opening instead of increasing alongside the HF middle. Policy-composition tests
were updated to use the new PCM/body carrier rule; trigger priority is unchanged.

References were rechecked: Switch2Connect's pinned two-band stereo analysis and
source-aware composition, and PadForge's `HapticToneReducer` (windowed RMS plus
autocorrelation). Their existing RMS/window approaches do not themselves solve
this specific peak complaint. The added packet-local reconstruction, peak policy
and dominant PCM/body carrier rule are local code. No new foreign code or binary
was copied. Existing Switch2Connect GPL attribution remains in the analyzer.

Validation artifacts are in the Desktop lab evidence directory:

- `pcm-correlation-red-20260905.trx`: five new failures reproduced on b68 source,
  five other new tests passed, including stereo/wire invariants.
- `Switch2PcmCorrelationTests`: 13 test cases, including both carrier layouts,
  both physical wire envelopes, 128 bin/phase combinations, every peak position
  with signed extremes, and a 100-packet stereo-swap/gain-scaling corpus.
- Intermediate runs exposed old expected composition/envelope semantics and a
  too-tight one-code quantization tolerance; these were corrected explicitly.
  Canonical rounding plus the SDL quantizer may differ by two codes at a boundary.
- Warm conversion still measures zero managed allocations; one focused run
  averaged2.185us/report. This is offline CPU cost under that run's conditions,
  not an end-to-end latency claim or a controlled before/after speed comparison.
- `pcm-correlation-full-20260905.trx`:3649passed,11opt-in skips,0failed in54s.
  Skips: three live process-loopback audio tests and eight separately hash-pinned
  Go-peer process integration cases not enabled in this run. Final warm converter
  sample2.204us/report, zero managed allocations. Detailed state is in the central
  dated platform ledger; no deployment or physical listening test in this turn.

No extra packet, history, queue, lock, timer or transport wait was added. Remaining
limitations: 3kHz/8-bit input has already lost detail from the original USB audio;
dominant frequencies still have packet-wide93.75Hz resolution; off-bin leakage
and within-active-slice reconstruction ringing remain approximations. Two
oscillators cannot preserve all overlapping waveforms, peak timing finer than
a wire subframe, or adaptive-trigger mechanics. User boosting can still clip
after synthesis. Physical tuning acceptance is pending.

## Earlier b67 implementation and evidence (historical)

The user reported Xbox and DualSense effects becoming drowned out on the
Switch 2 conversion path and authorized improvements plus portable replacement.
This change is synthesis policy, not a new transport or proof of perceptual parity.

## Evidence and changes

Nine new regressions fail on the preceding source and pass with this change:
quiet PCM on both carrier layouts, distinct LF/HF tones, pure high-frequency
energy leaking into low-band rumble, occupied-carrier trigger masking, hard
clipping of overlaps, a quantized-silent Xbox trigger retuning audible body
rumble, and Xbox impulse dynamics disappearing under boosted body rumble.

- HD-only PCM conversion no longer applies the ordinary motor's minimum-8/255
  activation threshold. Wire quantization remains; true silence stays zero.
  The legacy DS4 two-motor converter retains its existing behavior.
- A packet-local Goertzel bank analyzes the 32 already-received 3 kHz samples
  independently on each side. Power is divided between LF bins1/2 (plus the
  existing DC-envelope fallback) and HF bins3..16, not duplicated into both.
  The selected band controls apply to the same three chronological envelopes
  already used by the carrier. No extra packet, timer, queue or history is added.
- Non-silent trigger controls outrank body controls, which outrank PCM controls.
  A zero-amplitude addition cannot change an occupied carrier. This follows
  Switch2Connect's source-aware ordering rather than letting any tiny prior
  oscillator value suppress a trigger's frequency identity.
- Xbox body/impulse and DualSense overlay amplitude codes use
  `a + b - round(a*b/1023)`: neutral leaves a lone source unchanged, overlap
  compresses gradually. This is an amplitude-code policy, not physical energy
  conservation. For example900+300 becomes936 and900+600 becomes972 instead
  of both collapsing to1023. Direct native Switch 2 passthrough is unchanged.
- Xbox impulses that quantize to zero no longer retune an audible body carrier.

## Reference and deliberate differences

Switch2Connect `61ac6642ce12fe7217e38a860b14863b18ca7e28`, GPL-3.0-or-later:
`src/dualsense_haptic.py` supplies the two-band analysis and bounded output
remapping approach; `src/controller.py::_vib_merge_ble_source_aware` supplies
the trigger/base/audio priority. Attribution accompanies the new analyzer.
No PadForge code or binary was copied.

The reference accumulates64 samples from48kHz/16-bit USB audio. This path
already receives32 samples per side in a3kHz/8-bit carrier; it analyzes exactly
that complete window, with93.75Hz bin spacing. It does not zero-pad a partial
64-sample window, claim the reference's resolution, or introduce cross-packet
history that would need new lifetime/reset ownership. LF carrier remapping is
94..234 ->225..281; HF is281..609 ->281..369. Above-reference HF energy is
retained at the bounded carrier ceiling, not discarded. Control codes are not
claimed to be independently calibrated actuator frequencies.

## Verification

`switch2-feedback-detail-red-20260905.trx`:9 failed before correction.
`switch2-feedback-detail-green-20260905.trx`:9 passed after correction.
`switch2-feedback-detail-focused-green-20260905.trx`:165 passed. Includes all
1,048,576 pairs of ten-bit amplitude codes (bounds, monotonicity, symmetry,
neutral identity), an independent direct-Fourier oracle, stereo silence,
chronological envelopes, and warm allocation checks.

Offline warmed stereo conversion averaged2.867us/report in the focused run,
with zero measured managed allocations. This is CPU cost, not Bluetooth,
USB-IP, game or end-to-end latency. Full-suite rerun and deployment results
are recorded in the dated platform ledger. The first full run was3632pass,
3existing audio skips,1failure: unchanged magnetometer yaw-assist allocation
test observed448bytes; its isolated13-test suite subsequently passed. This
intermittent observation is not claimed fixed by the haptics change.
The unchanged full repeat passed 3,633 tests with three existing audio skips.
b67 launched and reached Bluetooth/DualSense/Ready; a subsequent physical
disconnect exposed the separate retirement failure documented in
`switch2-bluetooth-disconnect-retirement.md`. No haptic A/B acceptance yet.

## Earlier b67 remaining limits (superseded where noted above)

Two encoded oscillators cannot reproduce every overlapping Xbox actuator or
an arbitrary DualSense waveform losslessly. Packet-local band proportions are
shared by the three within-packet envelopes; intra-packet changes of spectral
balance, off-bin leakage and above-ceiling tones remain approximations. This
cannot recover detail already lost in the3kHz/8-bit carrier. Full-scale sources
have no remaining headroom; intentional post-mix profile boosting can still
clip. Simultaneous effects may feel less uniformly strong but more distinct.
No physical A/B acceptance, calibrated actuator response, transport-drop audit
or mechanical adaptive-trigger equivalence is claimed by these software tests.
