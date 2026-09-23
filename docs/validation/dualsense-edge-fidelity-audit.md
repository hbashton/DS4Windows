# DualSense Edge fidelity audit

## Goal

Complete evidence-backed physical DualSense Edge support and VIIPER Edge
emulation without regressing ordinary DualSense, Nintendo, or other virtual
outputs. Work in isolated Git worktrees, add regression tests before fixes,
review integration against the current destination, and preserve unrelated work.

This is not a claim of perfect compatibility or physical hardware validation.
The side conversation cannot use the app's persistent-goal mechanism; this file
is its resumable objective and evidence ledger.

## Source snapshots

- DS4Windows: `ca9bf655f1fbdea3d68df26089ee5de06ff4098b`.
- VIIPER: `85b6d4800d29741103747ea77f654c7e9b8d8b67`.
- Isolated branch in both repositories: `fix/dualsense-edge-fidelity`.
- Destination worktrees have unrelated in-progress changes; do not stage,
  overwrite, discard, or commit those changes.

## Acceptance checklist

- [x] Independently verify USB/BT input offsets, extra-button masks, motion,
      touch, trigger status, Edge profile status, battery and audio status.
- [x] Verify Edge descriptors and feature-report behavior; fix confirmed
      mismatches without advertising unsupported writes as successful.
- [x] Verify native trigger, rumble, LED, audio and haptics output fields and
      transport bounds, including compatibility with existing V5 clients.
- [x] Audit physical Edge capability detection, hardware-profile interaction,
      trigger stops, remapping/Special Actions and reconnect ownership.
- [x] Add and execute targeted regressions and wider relevant test suites.
- [x] Review both diffs, integrate only validated changes, and test integration.
- [x] Document hardware-dependent or unsupported behavior explicitly.

Checked items above mean source/test audit, not a physical validation pass.

## Baseline findings (corrections recorded below)

1. Fn/paddle/mute masks agree with SDL and HIDMaestro in a static exhaustive
   byte-mask comparison. This is not an application or hardware test.
2. VIIPER's Edge descriptor uses base-controller lengths for output 0x02 and
   feature 0xF2. Captured Edge descriptors use 63 and 52 payload bytes.
3. Edge feature reads are not implemented by the advertised generic feature
   handler; generic feature writes currently succeed without applying state.
4. Virtual USB input byte 54 replaces all physical connection/status flags with
   0x08. Audio-present/mute flags need a deliberate virtual-route policy.
5. Physical improved-rumble capability detection differs from SDL; profile
   application subsequently overrides the detected value. Determine actual
   behavior before changing it.
6. Output-selection help text incorrectly says adaptive triggers need future
   work. Edge-only controls currently use the mapping list, not artwork targets.

## Independent references

- SDL: https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c
- HIDMaestro: https://github.com/hifihedgehog/HIDMaestro/blob/master/profiles/sony/dualsense-edge.json
- Captured Edge descriptor: https://github.com/ShadowBlip/InputPlumber/blob/main/src/drivers/dualsense/report_descriptor.rs
- Output/input fields: https://github.com/SpecialKO/XInput_HID/blob/master/dualsense.cpp
- Hardware constraints: https://www.playstation.com/en-au/support/hardware/set-up-edge-pc/

Reference implementations are cross-checks, not unquestioned authorities. Their
field names can disagree. Preserve verified physical wire values and use
source attribution/license review for any copied implementation.

## Implemented corrections (2026-09-23)

- Edge USB report 0x02 now advertises 63 payload bytes, and 0xF2 advertises
  52. Standard DualSense stays at 47 and 15 respectively. Descriptor cloning
  remains isolated between personas and audio/gamepad variants.
- Virtual USB input retains the physical headphone-present, microphone-present
  and microphone-muted flags (byte 54 bits 0..2), while independently declaring
  a USB transport. Physical transport/unknown upper bits are not propagated.
- Unsupported Edge onboard-profile feature reads/writes now receive a real
  USB STALL through the existing transactional EP0 contract. A device-handler
  `handled=false` alone was insufficient: the generic HID fallback returned
  success. Tests verify the wire response and subsequent normal enumeration.
  This does NOT implement profile storage or forward writes to hardware.
- Improved rumble follows the independent SDL capability rule: Edge supported
  independently of its different firmware version series; ordinary DualSense
  needs known firmware >=0x0224, or retains the default if firmware is unknown.
  Applying a profile can no longer bypass a known unsupported capability;
  legacy rumble remains available.
- Physical firmware/calibration reads honor HID success and the requested ID.
  Bluetooth reads also require the Sony 0xA3 CRC and have five bounded attempts.
  The shared existing CRC implementation avoids depending on application-wide
  fast-table initialization. Calibration accepts only nonzero gyro speed and
  all six nonzero axis ranges; failure preserves the last good calibration.
- Output-selection help now describes supported feedback accurately and calls
  out the lack of virtual onboard-profile editing.

## Verified report contracts

Offsets here include the report ID. Bluetooth 0x31 adds one byte relative to
the normalized USB 0x01 input body.

| Contract | Code/reference result |
| --- | --- |
| USB byte 10 / BT byte 11 | PS 0x01, touch 0x02, mute 0x04, reserved 0x08, FnL 0x10, FnR 0x20, left paddle 0x40, right paddle 0x80 |
| Extra-button publication | Exhaustive decode -> canonical mapper tests; independent V5 decode -> USB encode test; reserved bit not published |
| Motion and touch | Little-endian signed motion at USB 16..27; two 12-bit coordinate records at 33..40; active flag is inverse tracking bit 7 |
| Trigger feedback | USB input bytes 42/43 and mode nibbles at 48 preserved; same-report status travels with mapped input |
| Edge-specific status | Raw USB 49..52 retained only for matching Edge layout, not mistaken for a base-controller timestamp |
| Game trigger effects | Output validity 0x04 right / 0x08 left; complete 11-byte blocks at USB 11..21 and 22..32 preserved |
| Output compatibility | The existing 48-byte native-effect prefix and V5 combined offset 76 remain unchanged; ordinary 64-byte Edge USB game output uses this prefix. Edge configuration commands are rejected, not truncated |
| Remapping / Special Actions | FnL/FnR/BLP/BRP already reach both mappings and Special Actions; extra controls remain list-based rather than artwork hotspots |
| Audio and reconnect | PID 0x0DF2 is accepted by existing physical audio/native-output paths; lifecycle and backlog regression suites retained |

## Test evidence

- Final full .NET suite: 6,854 passed, 12 skipped, 6,866 total (Release x64).
  Skipped tests are not counted as proof.
- Targeted new physical capability/button/feature/calibration cases: 29 passed.
- VIIPER `go test ./... -count=1`: passed all packages, including ordinary
  DualSense, DualShock 4, Nintendo, Xbox, USB and API transport tests.
- VIIPER `go test -race ./device/dualsense ./internal/server/usb -count=1`:
  passed. This checks concurrent claim lifetime and existing streaming behavior.
- New Edge transaction tests cover forged/stale/duplicate claims, cancellation,
  delivery failure, bounded exhaustion/recovery and concurrent completion.
- No apps restarted, driver changes, physical profile writes, firmware updates,
  or hardware haptics tests were performed by this audit.

## Remaining acceptance gates — goal not complete

1. Windows read-only PnP enumeration found no present physical Edge. USB and
   Bluetooth hardware checks are still required: input, both Fn/paddle pairs,
   touch/motion, both trigger-stop positions, native/adaptive feedback, PCM,
   headset status/audio/microphone, multiple stored profiles and reconnects.
2. Virtual onboard profiles (features 0x60..0x65, 0x68, 0x70..0x7B and
   feature 0x80's profile-snapshot subcommand 0x70) are not
   implemented. They now fail honestly. A software profile store and verified
   transaction semantics would be separate implementation work, not arbitrary
   writes to the user's physical controller.
3. Firmware feature 0x20 remains synthetic; this is not a captured full Edge
   firmware identity. HIDMaestro #47 confirms a difference in firmware series
   but does not supply a fully verified physical response. Do not invent one.
4. Feature 0xF2 payload semantics remain unverified. Known nonzero output
   extensions include Edge configuration previews and profile controls, now
   explicitly rejected; they are not padding and are not implemented by the
   common game-feedback path. Correct descriptor sizes do not prove those
   features work. Do not change the established V5 wire ABI to speculate.
5. USB input bytes 56..63 are a physical authentication tag. They cannot simply
   be copied after altering virtual state. No authentication claim is made.
6. Physical hardware profiles can remap/attenuate before DS4Windows receives
   input or delivers effects. Sony documents that medium/short trigger stops
   disable adaptive effects. These are hardware behavior, not missing bits.
7. Existing UI `Charging` derives from USB link status rather than the complete
   battery charge-state enum. Raw battery status remains intact in emulation;
   a UI policy change needs separate behavior checks before altering it.

## Additional reference snapshots and provenance

- SDL `c71abd08605b8bb7078372307a93274725c99fe0`.
- HIDMaestro `9df50410230c11b410f43909ede0e5fc8b23d15b`.
- https://github.com/daidr/dualsense-tester at
  `f6e6247fd66ada9c8b63f3ba62c6d72945c0da53`: Edge profile/trigger-stop status
  and feature transaction cross-check (MIT).
- https://github.com/Kurobac/edgemap at
  `eb1d96e69c000d3423424b62ccfc4206df4053b3`: USB/BT and firmware/calibration
  report cross-check (GPL-3.0).
- https://github.com/hifihedgehog/HIDMaestro/issues/47: explicit limitations of
  synthetic Edge firmware responses.

No reference source files were copied wholesale. Changes implement verified
protocol facts in the existing mapping/transport stack and reuse this
repository's CRC and transaction contracts. Existing third-party notices stay
unchanged.

## Integration evidence

- Source checkpoints: DS4Windows `0e2d332`, VIIPER `11f0dc1`.
- Integrated only the audited files into `DS4Windows-rc463-mouse` and
  `VIIPER-haptics-reference`. File blob hashes matched the isolated checkpoints.
  Unrelated staged release assets were unchanged by integration.
- Destination DS4Windows full suite: **7,022 passed, 12 skipped, 7,034 total**.
  Evidence: `artifacts/edge-audit-integration/results/edge-integration-final.trx`.
- Destination VIIPER: full `go test ./... -count=1` and the DualSense/USB
  `-race` suites passed after source integration.
- Integration testing initially used output under the other worktree. Five
  source-inspection tests consequently read that older checkout, not the current
  source. Moving isolated output inside the destination checkout resolved all
  five; no product fix or shared build-output deletion was needed.
- The main thread's release commit advanced independently to DS4Windows
  `65d4fde`; VIIPER was at `2ea6829`. Edge changes are separate from that
  release work. This audit does not rebuild bundled release payloads, install
  binaries, push commits, or publish a release.
- Remaining acceptance gates above are still open. A hardware-availability
  question was sent because no physical Edge was detected.

## Final RC4.6.6 independent review

- Rechecked the pinned SDL, edgemap and dualsense-tester sources against the
  integrated physical/virtual input, calibration and shared output paths.
  Existing Fn/paddle masks, motion/touch positions, 11-byte trigger blocks,
  USB/BT normalization, model-specific status bytes and descriptor counts agree
  with those source contracts. This remains source/test evidence, not a new
  physical Edge capture.
- Closed the remaining USB firmware-read validation gap: HID success alone
  could previously accept another report ID as firmware bytes. USB firmware
  and calibration now both require their requested ID; Bluetooth CRC and its
  existing bounded retries are unchanged. No per-frame work or delay is added.
- Cross-persona native game feedback is retained in both directions. Edge
  configuration authorization is rejected when either the virtual source or
  physical recipient is Edge: a base virtual identity does not change how an
  actual Edge interprets USB byte 39/41 bit 7. Rejection precedes native writes
  and compatibility fallback. Base-to-base reports retain their uninterpreted
  high bits. Media-only PCM retains its independent lane and last safe state.
- The pinned DS5Dongle implementation documents feature `0x80`, subcommand
  `0x70/0x01`, preparing Edge profile snapshots. That previously reached the
  generic feature-command success path despite no profile store. It now gets a
  transactional STALL, including incomplete variants of that opcode, without
  changing command response, feedback or media state. Ordinary common queries
  and base-controller behavior are unchanged.
- Removed a remaining base/Edge model restriction from cold Bluetooth haptics
  converter negotiation. Although native feedback accepted the pairing, a
  virtual Edge on a physical standard DualSense still silently requested the
  legacy converter. Both physical Sony models now use the same verified native
  compatibility gate; USB stays native PCM, and an unverified or changed
  recipient cannot acquire the Sony-filtered media stream.
- Corrected virtual gyro calibration to describe the mapped values actually
  sent by DS4Windows: `SixAxis` defines 16 units per degree/second and the V5
  mapper carries those calibrated values unchanged. The virtual feature `0x05`
  endpoints remain +/-8192, but their reference speeds are now +/-512 instead
  of +/-500. SDL's independent calibration equation shows the former values
  multiplied angular speed by 125/128 (2.34375% low) when a game recalibrated
  the already calibrated samples. Accelerometer scale remains 8192 units/g.
  Paired C# and Go tests use the same exact wire vector to cover the actual
  mapper, V5 decoder, USB input encoder and advertised calibration for both
  personas. No physical factory calibration or raw HID sensor format changed.
- Final focused .NET suite: **132 passed, 0 skipped, 0 failed** (Release x64),
  `artifacts/rc466-edge-audit/rc466-final-edge-audit.trx`. It covers capability,
  USB/BT feature validation, native command preservation/rejection, actual cold
  converter negotiation and the canonical motion contract.
- Broker checks: `go test ./device/dualsense ./internal/server/usb -run
  'Edge|DualSense|VirtualSonyCalibration' -count=1` and the same command with
  `-race` both passed after these final corrections. Full-release suites are
  tracked separately by the release owner; no hardware was used by this review.
- Additional primary reference (MIT; protocol facts, not copied source):
  https://github.com/awalol/DS5Dongle/blob/c67c7f685fe8d8cc44f519d27710c5a639a1be7d/src/dse.cpp#L50-L75.
  Its complete physical profile bridge does not establish standalone virtual
  profile/firmware support for VIIPER; the remaining gates above still apply.
