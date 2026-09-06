# RC4.3 to RC4.4 native DualSense haptics regression audit

Evidence checkpoint: September 5, 2026. The user reports that Hades II's
advanced feedback worked with a physical DualSense emulating a DualSense in
RC4.3, but not in the released RC4.4. This audit has reproduced and corrected
a native-media fallback defect in software. It has **not** reproduced the
user's game session or established that this is its only cause. The physical
connection type and whether ordinary rumble/adaptive triggers survived remain
unconfirmed.

## Exact release boundary

- `VIIPERRC4.3`: `3579450dc8f50a74d9532e711249589c732c460b`.
- `VIIPERRC4.4`: `4d41a613c771588e05558a386c2ca5591b6949c3`.
- Bundled VIIPER changed from v0.1.0 to v0.1.2. Its V5 framing, media
  generation, output writer and audio conversion diffs were inspected as well;
  this checkpoint does not exonerate every broker/USB-IP change.

## Reproduced failure

`ApplyAtomicAudioHapticsFeedback` calls `ApplyFeedback` with
`freshNativeOutput: false`: the frame is an audio interval, not a new game HID
command. Its compatibility motor bytes can be zero while its waveform is
nonzero.

1. `TryApplyBluetoothCombinedHapticsOutputReport` can return false. USB targets
   cannot consume this Bluetooth carrier. On Bluetooth, an occupied
   `bluetoothCombinedTemplateUpdateClaimed` flag rejects immediate template
   publication even though the controller identity and media payload are valid.
2. `5fcc5ab864a25536179bda5ca7efef29c101bb5a`, included in RC4.4, changed raw
   native forwarding to require caller-owned scratch storage. The media callback
   supplies none. The old fallback prepared a raw native report itself; the new
   fallback returns false at this point.
3. Execution then reached `SetDevRumble`, treating the media frame's zero motor
   bytes as an explicit local rumble command. This advances the physical local
   rumble generation. `f2807747609962566fd73b85e7874e5ef20e679c`, also in RC4.4,
   introduced the template claim that supplies a deterministic BT rejection
   scenario for this path.
4. The BT compositor explicitly permits a new local rumble generation to
   override native motor state. Its `MergeLocalRumbleIntoV5AudioSnapshot` sets
   common flag0 bits `0x03` and copies the compatibility motor levels. This is
   not an innocuous zero in an audio sample: it selects the classic rumble path.
   The Sony-authored [Linux PlayStation driver](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c)
   independently identifies `HAPTICS_SELECT` as the classic-rumble selection in
   `dualsense_output_worker` (checked September 5, 2026).

The physical consequence is strongest on the BT route above. The USB tests
prove the erroneous local-command publication too, **not** the complete USB
audio/rendering failure. Game-level causality still requires an actual trace
and replay on the user's transport.

## Narrow correction

After native media transport attempts, a compatible physical DualSense's
non-fresh audio frame is consumed without falling through to raw-state replay
or compatibility rumble. Genuine fresh HID/control reports retain their current
route. Other physical controller families retain their existing waveform-to-
rumble translation. No packet format, timer, driver, broker or profile setting
was changed. This prevents a rejected media interval from changing feedback
mode; it does not claim that rejected intervals were physically delivered.

## Validation

`DualSenseNativeMediaFallbackTests` invokes the production media callback using
an unopened HID object, a cached test identity, no physical workers, and no
audio capture. The BT case holds the real template-claim flag to model
contention deterministically. Both primary virtual pads and audio sidecars
are covered. Global test state is restored in `finally`.

- Before correction: all four USB/BT media cases fail, each publishing one
  unintended compatibility rumble command. Genuine compact control passes.
- After correction: all five cases pass.
- Related DualSense/audio/VIIPER regression suite: 546 passed, 3 existing
  audio tests skipped, 0 failed.
- Full DS4Windows suite: 3,610 passed, 3 existing audio tests skipped, 0 failed
  (63 seconds; isolated C#/Go Xbox interop peer enabled). This green run does
  not attribute or resolve the previously recorded intermittent allocation-test
  failure from the earlier Switch 2 work.
- Evidence under workspace `_results`: `dualsense-native-media-fallback-red-20260905.trx`,
  `dualsense-native-media-fallback-green-20260905.trx`, and
  `dualsense-native-media-regression-focused-20260905.trx`, and
  `dualsense-native-media-regression-full-20260905.trx`.

The running Desktop b65 portable session has not been replaced and does not
contain this new source patch. Program Files, installed configuration and
drivers were not modified. No release or installer was published.

## Required game acceptance

1. Confirm physical USB versus Bluetooth, and distinguish waveform haptics,
   ordinary rumble and adaptive triggers.
2. On a separate Desktop portable candidate, compare the same Hades II action
   and profile. Verify nonzero virtual rear-channel media and physical media
   presentation, while checking for unintended compatibility-mode updates.
3. Confirm perceived detailed effects and their stop behavior. If the game
   remains silent, inspect endpoint selection/PCM ingress and native transport
   health; passing unit tests is not grounds to close the reported regression.
