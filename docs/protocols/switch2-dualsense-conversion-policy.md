# Switch 2 DualSense feedback conversion policy

## Source and scope

Behavioral source: [Switch2Connect](https://github.com/TommyWabg/Switch2Connect/tree/61ac6642ce12fe7217e38a860b14863b18ca7e28), pinned commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`.
The authored `src/config.py` header grants GPL-3.0-or-later; `LICENSE.md`
contains GPL version 3. The relevant source symbols are:

- `Config.audio_haptics_enabled` and `Config.adaptive_triggers_enabled`;
- `gui.py:open_audio_haptics_settings`;
- `virtual_controller.py:_setup_vg_controller`,
  `get_current_adaptive_trigger_frames`, and its native trigger-output handler.

These establish separate default-on preferences. DS4Windows retains its own
existing translators, canonical ownership and sole physical writers. No
Switch2Connect transport architecture, bundled binary, or PadForge code is
copied by this change.

## User controls and persistence

The profile editor's Switch 2 Controls section exposes:

- **Convert DualSense audio haptics to HD rumble**;
- **Convert DualSense adaptive-trigger effects to HD rumble**.

XML keys are `Switch2DualSenseAudioHapticsEnabled` and
`Switch2DualSenseAdaptiveTriggersEnabled`. Both default to true in backing
storage, profile DTOs, absent-key migration and reset. They are independent of
the master output-data setting, which overrides both and ordinary body rumble.
They do not change virtual audio endpoint descriptors or physical Sony trigger
settings.

Audio conversion retains the existing stereo/chronological HD-rumble synthesis.
Adaptive-trigger conversion remains a side-local approximation, only for
supported effects and a pressed physical trigger. Switch 2 has no resistance
actuator. Neither setting makes this transformation lossless or hardware-proven.

## Live changes and safety boundaries

`Switch2DualSenseFeedbackPolicyLane` retains at most one fixed-size compact
feedback packet. It is used only on feedback/control paths, never on input.
The existing physical session, canonical ingress, state-lane pump and writer
remain authoritative.

- Disabled audio is not allowed back through the generic PCM-to-body fallback.
  Only the compact compatibility motor bytes remain in that fallback.
- With zero configured delay, removing a component preserves compatibility body
  rumble and the other enabled component, bounded by the original absolute
  CFBK expiry. The owner computes remaining TTL at publication; an expired
  refresh is Neutral. This cannot extend the event's lifetime.
- A re-enable alone never restores a removed cached component. Fresh game
  feedback is required. Physical trigger activity can only narrow on refresh.
- A change while delayed media is queued immediately requests Neutral and
  discards the queue. It does not advance a future media event to the present.
  Fresh events again use the configured delay.
- Every admitted session publication attempt advances an exact local watermark before
  owner dispatch. A cached refresh compares that watermark under the same
  session gate. Newer Neutral, Stop, failed-but-admitted publication, retirement
  or a replacement owner cannot be overwritten with an older cached event.
- A failed clock or failed-but-admitted source/refresh discards replayable
  bytes while retaining only its exact cleanup receipt. A later refresh can
  compare-and-neutral that receipt, including with unchanged preferences;
  it cannot inherit another producer's watermark or reconstruct old media.
- Cached data is bound to the stream generation as well as the physical
  session/profile slot. Recovery drains admitted callbacks, requests an exact
  release and invalidates the cache before starting the replacement reader.
- UI refresh uses the existing feedback callback admission lease. It does not
  acquire the policy/session gate while holding the callback-admission lock.
- Profile-option application may execute on the physical input queue; it only
  signals the existing feedback-control worker. A struct-only final guard on
  this lane's delayed events checks current profile revision, enable flags and
  captured stream generation before admission, so a queued old event cannot
  evade a load/disable or recovery while cleanup is waking. The stream reader
  is one cold method-group delegate per output device and performs only a
  `Volatile.Read`; no per-frame closure or input-path lock/I/O is introduced.
  The delayed queue also treats bound policy/stream changes as a selection
  boundary: fresh post-edit media replaces the obsolete queue instead of
  being discarded behind a stale predecessor.
  This does not cancel an already-admitted transport
  write; the existing neutral/retry and teardown contracts remain in force.

An accepted policy publication is canonical/physical-owner acceptance, not a
claim that the device's actuator has already stopped or that a HID/USB/GATT
flush completed. Existing transport retry and lifecycle evidence must be used
for those claims.

## Test coverage and remaining validation

`Switch2DualSenseFeedbackPolicyTests` covers all four conversion combinations,
compatibility preservation, PCM-only suppression, immutable packet retention,
master disable, delayed admission/queue clearing, no re-enable resurrection,
absolute expiry, newer same-session Neutral including failed delivery, stream
replacement, trigger-release narrowing and default-on XML round trips.
`Switch2BluetoothFeedbackLifetimeTests` and
`Switch2ProUsbOwnedFeedbackActivationLifetimeTests` include actual owner/pump
paths proving expired refresh requests produce neutral simulated wire output.
The existing `ViiperSwitch2DualSenseHdRumbleTests` retain composition and
allocation coverage.

These are source/replay/simulation tests, not hardware certification. No app,
controller test, pairing operation, driver installation or installed-binary
replacement was performed for this change. Record central test results in the
main validation ledger; USB/BLE actuator, game, latency and full product gates
remain separately open until measured.
