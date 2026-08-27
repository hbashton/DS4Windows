# Release Candidate 4.4 - Input Fidelity, Audio Efficiency, and Mute Controls

RC4.4 pairs DS4Windows 5.0.4.0 with VIIPER 0.1.2. This candidate focuses on
faithful DualSense input transport, lower handoff latency, bounded DualShock 4
Bluetooth audio ownership, explicit mute behavior, and safer controller and
installer lifecycle handling.

## Highlights

- **Ordered DualSense RAW trigger fidelity.** The VIIPER V5 input scheduler
  preserves complete press, peak, settled-status, release, and failed-write
  retry states instead of collapsing meaningful trigger transitions into one
  latest value. Independently timed L2 and R2 peaks remain chronological; the
  transport does not synthesize a combined state that the physical controller
  never reported.
- **Complete negotiated RAW metadata.** A negotiated raw-input alias receives
  the 53-byte V5 state: the established 33-byte mapped state plus validity and
  controller-layout flags, the same report's sensor timestamp, and normalized
  physical bytes 41 through 55. Legacy aliases keep their established 33-byte
  contract. The physical report's authentication tail is intentionally not
  copied because mapped virtual reports cannot reuse that authentication tag.
- **Lower-latency input handoff.** DualSense input keeps exactly one HID read
  ahead and arms the alternate fixed buffer before parsing, mapping, callbacks,
  or virtual publication. The V5 writer wakes for fresh work and prioritizes
  ordered edges; it does not hold reads for a four-millisecond coalescing tick
  or manufacture duplicate reports. The virtual USB interrupt endpoint remains
  the backend's normal one-millisecond presentation opportunity.
- **More efficient DualShock 4 Bluetooth audio.** Enabling controller-speaker
  audio creates one bounded owner for capture, encoding, reusable timing, and
  HID output. Its 8 ms production clock blocks instead of busy-spinning.
  Disabling audio closes capture, joins the encoder, cancels and drains pending
  output, sends the ordered audio-off state when possible, and disposes the HID
  lane and wait handles before a replacement generation can start.
- **A narrow SteelSeries Sonar capture path.** Ordinary render devices keep the
  standard Windows loopback implementation. Only an endpoint with both
  SteelSeries Sonar product identity and the Sonar `ROOT\MEDIA` software-adapter
  projection selects the short polling loopback path. That path requests a
  4 ms buffer and preserves extensible 32-bit float PCM as float; Windows may
  negotiate a different final buffer size.
- **Profile-controlled mute targets.** The new **Mute Button Mutes
  Input/Output** master option can target the controller microphone and
  built-in speaker independently through their own profile checkboxes. A target
  changes only when both its checkbox and the master option are enabled. The
  mute LED follows the conventional state: on means muted and off means live.
  Enabling this mode disables and greys out mute-button profile switching so
  one button press cannot own both actions.
- **Safe visual-only lightbar recovery.** When SDL/Steam-style native output
  stops refreshing the visual LED lane, DS4Windows can restore the configured
  profile lightbar. This is deliberately limited to visual state. Foreground
  association is a lifecycle heuristic, not sender-PID attribution, so it
  never claims an exact writer and never releases triggers, rumble, advanced
  haptics, or audio. Explicit Sony release and transport boundaries remain
  authoritative for those lanes.
- **Reconnect-safe wired HidHide ownership.** DS4Windows records the live HID
  identity before PnP removal and removes only persistent entries that it
  inserted. A wired controller that returns under a new HID instance can
  enumerate before the new generation is hidden, while user-created rules and
  mixed controller/audio USB container nodes remain untouched.
- **More compatible startup repair.** Elevated startup tasks are registered
  from schema-validated XML that retains the exact target-user SID, avoiding
  ambiguous local-account normalization on affected Windows installations.
  Task discovery enumerates and matches the exact root task, so a task that has
  not been created is normal absence rather than a failed CIM query. A missing
  `VIIPER` Run value is likewise treated as normal first-run state, and foreign
  same-name tasks remain protected from replacement or cleanup.
- **VIIPER 0.1.2 throughout the package.** The standard installer and portable
  archive use the same pinned production backend build and dependency-license
  notice for the supported virtual-controller, microphone, speaker, haptics,
  and adaptive-trigger paths.

## Included software

- DS4Windows **5.0.4.0** (self-contained x64)
- VIIPER **0.1.2**
- usbip-win2 **0.9.7.7**
- Optional offline HidHide and FakerInput installers

The standard installer preserves profiles and settings while upgrading only
package-owned files. Portable users can continue using the release ZIP.

## Validation

Release coverage exercises ordered trigger epochs, raw-alias negotiation,
failed-write retry, USB and Bluetooth input parsing, the read-ahead lifecycle,
audio start/stop and reconnect races, Sonar endpoint selection and PCM format,
mute persistence and runtime policy, visual LED lease recovery, HidHide
generation changes, and startup-task ownership. Publication also gates on the
Release build and regression suite, installer state-machine and restart
simulations, package manifests, pinned payload hashes, and public signing
requirements.

## Downloads

- **Recommended:** `DS4Windows_5.0.4.0_Setup_x64.exe` is the self-contained
  offline x64 installer for DS4Windows, VIIPER, and usbip-win2, with optional
  HidHide and FakerInput selections.
- **Portable:** `DS4Windows_VIIPER_x64.zip` contains the same DS4Windows and
  pinned VIIPER versions without installing DS4Windows itself.

Download either asset from the
[DS4Windows Releases page](https://github.com/hbashton/DS4Windows/releases).
