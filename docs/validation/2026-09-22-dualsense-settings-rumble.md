# DualSense Bluetooth settings-refresh rumble continuity

## Reproduction and scope

A matched, fixed HID replay reproduced a Hades II rumble pulse being interrupted
by a settings report before the game's subsequent stop command. Both the current
DS4Windows candidate and PadSense CE 0.2.1 forwarded the mode-clearing settings
command. This
change is a targeted interoperability correction, not a claim that PadSense
already preserves this pulse or that its behavior is being copied here.

The original 48-byte USB report has ID `02`, validity bytes `0C/57/00`, zero
motors, both trigger blocks exactly `05` followed by ten zero bytes, and only
the final player/RGB values variable. Classification must use the original
native report before profile, Trigger Lab, or DSX composition. Changed audio
fields, unknown bytes, other report shapes, explicit motor-zero commands and
the exact all-zero command remain authoritative.

The compatibility rule is limited to the DualSense/Edge unified Bluetooth
writer. It does not change USB passthrough, Nintendo rumble conversion, input
publication, gains, trigger validity, media encoding or output cadence.

## Required ownership contract

- A recognized settings update may preserve only an already successfully
  presented, active continuous rumble tuple. It cannot start rumble from a
  future queued command or resurrect a stopped or disconnected effect.
- The classification is immutable through retained admission, the helper FIFO,
  retries and physical presentation. The private parent/helper protocol must
  reject incompatible or unknown policy encodings.
- Real rear-channel PCM can end this preservation, including authored zero
  PCM. Generated idle silence cannot. PCM must be newer than the publication
  watermark of the accepted explicit rumble owner, so an older queued waveform
  cannot undo newer motor intent.
- An explicit motor command on the same physical carrier takes precedence over
  automatic PCM handoff. Unowned local LED/trigger updates neither create a
  new motor owner nor reset the PCM watermark.
- Mode changes, ring consumption and command acknowledgement commit only at
  the successful physical-write boundary. Busy retries and lifecycle clears
  must preserve this transaction boundary.

This exact byte pattern does not contain an intent bit distinguishing a
settings refresh from an audio-mode request. The narrow compatibility decision
is deliberate: preserve the proven refresh case while honoring real PCM and
explicit stops. It is not a general rule that zero mode selectors are harmless.

## Validation

The implementation and independent review are complete. The private helper
protocol is version 17; its command envelope appends one validated policy byte.
The app and helper must be deployed together. No VIIPER wire-protocol or broker
change is required by this particular fix.

The focused suite passes 97 tests with no failures or skips. Coverage includes
raw and embedded-original ingress, immutable retained admission, unsupported
metadata, strict zero-allocation classification, protocol-version rejection,
legacy/improved motor tuples, authored-zero PCM, older queued PCM, same-carrier
explicit ownership, post-composition Busy retries, reset cancellation, trigger
and LED delivery, and unchanged front/rear bytes and gains.

Earlier stress-fixture failures are retained in the local receipts. Their
corrections complete the synthetic predecessor before inducing a Busy write at
the intended post-composition boundary, synchronize Clear against the rejected
old claim, and wait for the explicitly queued local command before disposing
the fixture. Exact output, ownership, stop and acknowledgement assertions were
not relaxed. The default-null helper observer is set only by tests; normal
presentation never uses it.

Two initial broader runs had one existing rear-deferral timing assertion fail;
three unchanged isolated reruns and the subsequent focused suite passed. That
test coordinated credit across threads inside a real 10.667 ms deadline. The
new refresh policy is inactive before its failing callback. The fixture now
stages Busy and returned credit on the presenter itself, outside locks and
physical I/O, while retaining the original absolute deadline, ownership,
media-count and ordering assertions. QPC diagnostics record the sequence.
Production deadlines and scheduling remain unchanged.

The final focused run passes 97 tests with no failures or skips. The final full
suite passes 6,801 tests with no failures and 12 pre-existing opt-in environment
skips. The skip names match the earlier baseline: three live application-audio
captures, one installed-bundle check and eight real broker process-interoperability
cases. Receipts are `sony-settings-rumble-focused-06.trx` and
`sony-settings-rumble-full-final.trx` in the local test-results directory.

Complete-package verification and a fresh hardware replay are pending at this
source checkpoint. Their hash-pinned receipts are recorded separately so the
built source inventory remains immutable. No public release is implied by this note.

Private paired baseline captures and receipts are retained under
`isolated_results/haptics-ab-20260920/hid-rumble-capture-02`. Host packet timing
is not an over-the-air or exact actuator timestamp; the accompanying raw IMU
proxy is corroborating evidence only, not calibrated haptic strength.
