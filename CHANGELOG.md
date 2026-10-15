# Changelog

Historical upstream release records, consolidated without discarding their detail. See [Releases](https://github.com/hbashton/DS4Windows/releases) for current downloads.

## Inherited release records

The following records preserve the original release descriptions and their
limitations. They are historical context, not current setup instructions.

<a id="inherited-release-candidate-4-1"></a>

### RELEASE_CANDIDATE_4_1

Original record: `docs/RELEASE_CANDIDATE_4_1.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.1 — Bugfixes and Lower-Latency Audio/Vibration

RC4.1 is a reliability-focused update for DS4Windows 5. It tightens the
real-time controller path, fixes several first-run and profile-switching
regressions, and makes the new offline installer safer and easier to recover.

#### Faster, more consistent controller feedback

- DualSense game haptics, Audio Haptics, speaker audio, and controller state
  now share a preallocated real-time path with fewer queue handoffs. Feedback
  is no longer discarded merely because a scheduling deadline was missed.
- The virtual DualSense input ceiling is now 1,000 Hz. DS4Windows submits only
  fresh mapped input, allowing each physical controller to run at its observed
  rate without manufacturing duplicate reports or forcing a 200/250 Hz cap.
- Native game haptics, lightbar/player LEDs, adaptive triggers, rumble, and
  DS4Windows overrides are merged atomically so state is not replayed, lost,
  or applied to the wrong media frame.
- Game ownership now begins only after meaningful output, survives brief idle
  periods, and ends with the owning game process. Closing a game immediately
  restores the profile lightbar, player LEDs, triggers, and rumble.
- Rumble preview and game-rumble delivery are synchronized across the new
  worker threads, fixing missing beats, stuck vibration, and stop/start races.
- Per-app audio and Audio Haptics capture recover after source changes, app
  restarts, speaker/headset changes, and stalled loopback sessions. The source
  list also has an explicit Refresh action.

#### User-facing bug fixes

- Rapid profile changes are latest-wins. Stale virtual-device transitions and
  UI callbacks are discarded instead of replaying slowly, keeping Quick
  Settings, audio routing, and the emulated controller synchronized.
- Two-finger profile swipes track both contacts and issue one switch per
  gesture, while Auto Profiles recover from transient evaluation failures and
  safely ignore disconnected slots.
- Fresh light/default-theme installations no longer crash on launch because
  of a missing `InverseBoolConverter` resource.
- VIIPER microphone status monitoring owns its timeout for the entire request,
  preventing profile transitions from disposing a wait handle still used by a
  background callback.
- Closed dropdowns no longer change profile, audio, or haptics settings when
  the mouse wheel is used to scroll the page.
- The automatic audio source is labeled **Game Audio** and identifies the
  exact virtual playback endpoint currently assigned to the controller.

#### Safer all-in-one installation and updates

- `DS4Windows_5.0.1.0_Setup_x64.exe` is a self-contained offline installer for
  DS4Windows, .NET, VIIPER 0.0.8, and usbip-win2 0.9.7.7. HidHide and
  FakerInput remain optional checkboxes. The portable ZIP remains separate.
- Unsupported USB-IP versions, mismatched driver files, ABI failures, unsafe
  Citrix USB filters, and an incorrect VIIPER binary always open mandatory
  guided repair; a saved “do not show again” choice cannot suppress a broken
  backend.
- The 0.9.7.8 to 0.9.7.7 USB-IP correction is split safely across a required
  reboot. Startup tasks remain disabled until the next boot verifies driver
  removal, exact hashes, ABI compatibility, and the VIIPER API.
- Install, update, repair, uninstall, and in-app infrastructure repair are
  serialized. Duplicate button clicks and duplicate Burn Plan requests are
  ignored, while a competing transaction returns Windows Installer busy
  instead of mutating the same files or processes.
- Process preflight now owns the same infrastructure lock as VIIPER repair, so
  one setup cannot terminate DS4Windows or VIIPER while another setup is
  replacing or validating them.
- Helper logs include a unique start record and matching completion/exit-code
  record. Concurrent append attempts are retried, and failure diagnostics stay
  available under `%ProgramData%\DS4Windows\Installer`.
- Package composition rejects a stale or mixed `DS4Windows.release` identity
  before running WiX. The verified manifest is published before the installer
  EXE, making the EXE the final atomic release commit point.
- Updates replace only manifest-owned files. Profiles, settings, logs, and
  unrecognized user files are preserved, while partial success never receives
  the Ready marker or enabled startup tasks.

#### Validation completed

- 738 automated regression tests passed; two live audio-capture tests were
  skipped because they require an interactive application session.
- The clean self-contained x64 publish, setup helper, custom bootstrapper,
  WiX MSI ICE checks, and Burn bundle all built successfully.
- Payload hashes, package ownership, unsafe path checks, optional dependency
  conditions, offline layout, clean/update/repair/uninstall ordering,
  cancellation, downgrade blocking, collision handling, failure rollback, and
  reboot/resume were validated.
- The 0.9.7.8 to 0.9.7.7 reboot-boundary simulation passed, and deliberate
  overlapping installer/build attempts failed closed without changing the
  completed package manifest.

#### Downloads

- **Recommended:** `DS4Windows_5.0.1.0_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip`

The installer requests a restart only when USB-IP must be safely installed or
replaced.

<a id="inherited-release-candidate-4-2"></a>

### RELEASE_CANDIDATE_4_2

Original record: `docs/RELEASE_CANDIDATE_4_2.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.2 — Reduce CPU Usage

RC4.2 cuts VIIPER's controller-emulation overhead while preserving responsive
input, Game Bar navigation, Bluetooth ownership, and clean application-audio
routing.

#### Highlights

- **Much lower VIIPER CPU usage.** Virtual Xbox 360, DualShock 4, DualSense,
  and Switch 2 Pro input writers use adaptive, thread-safe latest-state
  delivery instead of busy-looping redundant reports. Fresh input retains a
  1,000 Hz ceiling without manufacturing duplicate traffic.
- **Stable Xbox 360 and Game Bar input.** Profile changes retire temporary Game
  Bar companions before a native Xbox pad appears, so the overlay cannot bind
  a stale controller that is about to be removed.
- **Lower-latency input without stale queues.** Mapped controller state uses a
  bounded latest-wins writer instead of replaying obsolete reports under load.
- **Reliable DualShock 4 Bluetooth ownership.** A temporary HID read drought
  no longer tears down a controller that Windows still reports as present.
- **Correct per-app DS4 audio.** Process loopback now consumes exactly one
  8 ms source quantum per encoder tick; PCM is no longer silently discarded,
  malformed, or replayed at the wrong effective rate.
- **Game-safe app speaker overrides.** A selected application remains the
  controller speaker source when a native PS5 game takes feedback ownership.
  Game triggers, LEDs, lightbar, rumble, and haptics are merged atomically
  without replacing the app's PCM generation. Audio Haptics Replace remains
  authoritative; Mix combines the captured and native game haptics.
- **Stronger capture recovery.** Selected-app speaker audio and Audio Haptics
  recover cleanly after source changes, process restarts, and headset/speaker
  transitions.
- **Prerelease updates that follow the installed channel.** Release-candidate
  users automatically receive newer prereleases; stable users remain on stable
  builds, and a newer stable release still takes priority.
- **Cleaner layouts.** Scrollable Audio Haptics, Trigger Lab, Auto Profiles,
  and log views no longer place their scrollbar over controls or text.

#### Included software

- DS4Windows **5.0.2.0** (self-contained x64)
- VIIPER **0.0.9**
- usbip-win2 **0.9.7.7**
- Optional offline HidHide and FakerInput installers

The standard installer preserves profiles and settings while upgrading only
package-owned files. Portable users can continue using the release ZIP.

<a id="inherited-release-candidate-4-3"></a>

### RELEASE_CANDIDATE_4_3

Original record: `docs/RELEASE_CANDIDATE_4_3.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.3 - Installer Reliability

RC4.3 pairs DS4Windows 5.0.3.0 with VIIPER 0.1.0 and tightens every standard,
portable, update, repair, and uninstall path around one verified offline
package.

#### Highlights

- **VIIPER 0.1.0 everywhere.** The tray, Windows executable metadata, API
  version, release package, DS4Windows compatibility gate, and both installer
  paths all identify the same production backend build.
- **Exact portable compatibility.** A portable VIIPER can run from any
  location when its executable matches this release's pinned SHA-256. Users
  are not forced into Program Files merely because DS4Windows is portable.
- **Deterministic updates.** Stale installer registrations are retired rather
  than relaunched or allowed to block the current package.
- **Fail-closed backend checks.** Missing, outdated, or modified VIIPER
  binaries cannot silently start. Repair uses the exact bundled offline
  payload and verifies it before virtual controllers are enabled.
- **Safer shared infrastructure.** Updates preserve healthy VIIPER and USB-IP
  infrastructure. Uninstall removes only files and tasks owned by the managed
  installation.
- **Broader release gates.** Clean install, upgrade, repair, internal repair,
  USB-IP removal and recovery, uninstall, reinstall, payload integrity, and
  startup behavior are validated before publication.

#### Included software

- DS4Windows **5.0.3.0** (self-contained x64)
- VIIPER **0.1.0**
- usbip-win2 **0.9.7.7**
- Optional offline HidHide and FakerInput installers

The standard installer preserves profiles and settings while upgrading only
package-owned files. Portable users can continue using the release ZIP.

<a id="inherited-release-candidate-4-4"></a>

### RELEASE_CANDIDATE_4_4

Original record: `docs/RELEASE_CANDIDATE_4_4.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.4 - Input Fidelity, Audio Efficiency, and Mute Controls

RC4.4 pairs DS4Windows 5.0.4.0 with VIIPER 0.1.2. This candidate focuses on
faithful DualSense input transport, lower handoff latency, bounded DualShock 4
Bluetooth audio ownership, explicit mute behavior, and safer controller and
installer lifecycle handling.

#### Highlights

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
- **Fenced profile-lightbar restoration after game exit.** When the exact
  retained foreground lifecycle candidate is confirmed to have exited,
  DS4Windows can restore only the configured profile lightbar and player LEDs,
  even when the game's final native report was neutral. The candidate must
  first have been positively associated with a visual claim while its exact
  Process was still running; its physical target, VIIPER stream, feedback
  admission, final nonvisual report, and newest native revision must all still
  match. A newer or unverified visual writer wins. Game Bar and shell hosts are
  negative capture filters, not proof of who wrote a report: if an overlay owns
  the foreground window while a new visual claim arrives, DS4Windows safely
  leaves that visual state alone. Triggers, rumble, haptics, audio,
  microphone/mute, and pacer state remain untouched. The separate exact SDL
  bootstrap signature may expire only before any real feedback epoch; neither
  policy is sender-PID attribution.
- **Quieter routine logging.** Native-session, idle-report hex, foreground PID
  association, and normal process-exit messages no longer crowd the GUI log.
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

#### Included software

- DS4Windows **5.0.4.0** (self-contained x64)
- VIIPER **0.1.2**
- usbip-win2 **0.9.7.7**
- Optional offline HidHide and FakerInput installers

The standard installer preserves profiles and settings while upgrading only
package-owned files. Portable users can continue using the release ZIP.

#### Validation

Release coverage exercises ordered trigger epochs, raw-alias negotiation,
failed-write retry, USB and Bluetooth input parsing, the read-ahead lifecycle,
audio start/stop and reconnect races, Sonar endpoint selection and PCM format,
mute persistence and runtime policy, confirmed-exit and unknown-liveness LED
fences, same-process stream/target rebinding, first-command and failed-write USB
ordering, Bluetooth combined-admission ordering, absence of routine lifecycle
logging, HidHide generation changes, and startup-task ownership. Publication
also gates on the Release build and regression suite, installer state-machine
and restart simulations, package manifests, pinned payload hashes, and public
signing requirements.

#### Downloads

- **Recommended:** `DS4Windows_5.0.4.0_Setup_x64.exe` is the self-contained
  offline x64 installer for DS4Windows, VIIPER, and usbip-win2, with optional
  HidHide and FakerInput selections.
- **Portable:** `DS4Windows_VIIPER_x64.zip` contains the same DS4Windows and
  pinned VIIPER versions without installing DS4Windows itself.

Download either asset from the
[DS4Windows Releases page](https://github.com/hbashton/DS4Windows/releases).

<a id="inherited-release-candidate-4-5"></a>

### RELEASE_CANDIDATE_4_5

Original record: `docs/RELEASE_CANDIDATE_4_5.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5 — More Ways to Play

#### The short version

Switch 2 controllers meet Xbox One / Series emulation, Joy-Cons become more
flexible, and DS4Windows gets a substantial round of everyday reliability and
usability improvements.

This is the cumulative change from **RC4.3**, including the work developed for
RC4.4. The highlights are Switch 2 Pro support over USB and Bluetooth, wireless
Joy-Con 2 support on their own or as a pair, more expressive feedback conversion,
and a dedicated Switch 2 settings page built around how you actually play.

It is a **tester release candidate**. There is real Windows and physical-controller
evidence behind important paths, alongside extensive automated coverage; that
does not mean every controller, game and connection combination is certified.
Bluetooth headset audio is not a supported feature of this candidate.

#### Your controller, more choices

**Xbox One / Series is a virtual-output choice, not a Switch 2-only feature.**
The existing supported physical-controller families can select it through the
normal profile system. Switch 2 input also uses that same mapper and the existing
virtual-output choices, including Xbox 360, DualShock 4, DualSense and Switch 2
Pro. Existing button assignments and profile tools remain the place to customize
your controls.

The new Xbox path carries separate body-rumble and left/right impulse-trigger
feedback. Supported DualSense targets can use the existing opt-in trigger
controls; Switch 2 targets convert that extra detail into HD rumble. Controllers
with conventional motors receive a capability-appropriate approximation rather
than being assumed to have hardware they do not contain.

Windows testing has demonstrated changing USB Pro input, mapped GL/GR rear
buttons, and independently exercised all four Xbox feedback channels. Separate
Windows tests also demonstrated two distinct virtual Xbox controllers at once,
with removal of one leaving the other usable. That is meaningful progress beyond
an output selector or a successful connection message, but not a claim of
universal game compatibility or Xbox-console authentication.

#### Joy-Cons that fit the way you play

- **One half is a controller.** A remembered Joy-Con can activate on its own;
  you do not have to wait for its partner or manually activate it each time.
  First-time Bluetooth association remains explicit.
- **Automatic or manual joining.** Automatic pairing combines compatible left
  and right halves. With it disabled, use the labeled **Link** buttons: select
  one half, then its opposite. Click again to cancel. **Unlink** separates a
  pair without forgetting its Bluetooth association.
- **Keep the pad you already had.** Joining retains the selected half's virtual
  pad and active profile instead of rebuilding both outputs. Unlinking retains
  the pair's output for one half. Physical reconnection still takes time; this
  is not advertised as a seamless or instantaneous transition.
- **Upright or sideways.** A single Joy-Con's artwork, mapping targets and
  shoulder-button presentation follow its selected holding style. The app does
  not pretend to infer that preference from how you happen to move it.
- **Mouse and motion options with context.** Desk-mouse controls are separate
  from motion aiming. Gyro output and activation settings sit beside their
  explanations, and **Aim with both Joy-Cons (DJG)** is an easy-to-find toggle.
  Bluetooth startup now enables the real optical and motion sensors; both
  halves have supplied changing sensor data through the profile mapper.

A newly diagnosed disconnect bug also gets a concrete fix: valid Joy-Con reports
could reset their internal counter and be mistaken for invalid input, tearing
down an otherwise connected half or pair. The corrected handling preserves
connection and timing checks while accepting those legitimate counter changes.

#### Feedback with more of the detail left in

Switch 2 HD-rumble conversion now pays closer attention to **which side**, **which
frequency band**, and **when an effect peaks**.

DualSense audio-haptic conversion keeps left and right separate, measures short
attacks within each part of the received audio window, and reduces the chance
that a small ordinary-rumble contribution overrides a stronger authored effect.
Xbox body and impulse effects have more room to overlap without immediately
collapsing to the same maximum strength. Independent controls let you choose
whether to convert DualSense audio haptics, supported adaptive-trigger effects,
and Xbox impulse-trigger detail.

Held profile and preview rumble now stays serviced instead of expiring after a
brief pulse. Sustained effects receive the refreshes their physical packets need;
finished audio slices are not looped into an unwanted buzz. Stop, profile changes
and replacement effects retain their ordering, including when a write is already
in progress. Shutdown handles a short overlap without falsely abandoning a
healthy controller, while a real timeout still blocks unsafe cleanup.

These are better-defined, tested conversions—not lossless waveform reproduction.
Switch 2 controllers cannot reproduce a DualSense trigger's mechanical resistance,
and tactile tuning remains something to assess with real games and controllers.

#### Less guesswork in the interface

Switch 2 controls have their own illustrated, task-based section: desk mouse,
motion aiming, two-hand aiming, feedback, button layers, sticks and calibration.
**Give buttons a second job** includes a control picker and shortcuts into the
existing mapping editor instead of leaving you to locate the next setting.

Joy-Con artwork now has a fuller, more dimensional silhouette, with matching
hover/highlight areas and uncropped thumbnails. Switch 2 Pro images no longer
resolve as missing files when opening a profile. The C button has its missing
remapping target, and sideways Capture is labeled as Capture without silently
changing its default assignment.

Readability improvements include themed tooltips that remain legible, clearer
physical-button names, and explicit Link/Unlink labels. Controller Readings now
uses the selected Switch 2 device's live snapshot; Pro gyro-axis handling is
corrected without changing your saved inversion preferences. If you previously
inverted an axis to work around the old behavior, check that preference again.

Switch 2 controllers also receive distinct, persistent local IDs for display and
**Link Profile/ID**, replacing indistinguishable zero-address labels. A joined
pair has an identity based on its members. These are local installation/Windows
identities—not fabricated Nintendo serial numbers—and USB and Bluetooth remain
separate where a trustworthy cross-transport match is unavailable.

#### The fixes that make the features easier to live with

- **Connecting, switching and disconnecting:** corrected slot collisions,
  stale controller rows after virtual-output replacement, and cross-controller
  blocking during another pad's setup. Unnecessary repeated startup-recovery
  waits and retired USB/IP reconnect attempts have been reduced or cleaned up.
  Real Windows enumeration still sets a floor on switch time.
- **Sony feedback and mute controls:** the cumulative update adds independent
  microphone/speaker mute targets, conventional mute-LED behavior, and mutual
  exclusion with mute-button profile switching. It also corrects a reproduced
  RC4.4 native-media fallback that could unintentionally select ordinary rumble,
  plus normal Bluetooth DS4 effect routing. The reported Hades II advanced-haptic
  regression still needs its specific game/transport acceptance check.
- **Profile effects after preview:** ending Trigger Lab preview restores the
  active profile's ordinary trigger settings when no Lab override is active.
- **Lightbar and audio housekeeping:** guarded profile-lightbar restoration
  after a verified game exit, quieter routine logs, more efficient DS4 Bluetooth
  audio ownership, and the targeted SteelSeries Sonar capture correction.
- **Safer startup and setup:** startup choices are preserved, renamed application
  setup is handled consistently, uninstall-only helpers are cached for future
  removal, and VIIPER's tray can recover when Explorer is not ready at login.
- **Updates that move forward:** a newer release candidate is no longer offered
  an older RC as an update. Release-candidate ordering is checked separately
  from the application's Windows version number.
- **Profiles and persistence:** corrected legacy color-channel loading, more
  reliable date/localization handling, calibration-save contention, original
  Switch Pro startup/calibration failures, and options-dialog failures involving
  multiple Switch 2 controllers. Steam/HidHide reclaim no longer uses the
  identified restart-loop path.

#### The portable ZIP brings its own broker

The portable ZIP now includes `viiper.exe` beside DS4Windows. Opening that
portable package starts its matching broker automatically when needed. A
verified, compatible instance already running from the same portable folder
can be reused; simply being already open is not an error.

A different or incompatible broker produces a clear close-and-relaunch
message. DS4Windows does not terminate that other instance. It stops only a
broker it started itself when its own session ends, after controller cleanup;
a reused broker remains running. The portable session uses local broker data
and leaves the installed broker and its startup task alone. USB/IP drivers
still need to be installed and pass the existing checks. If Windows denies
driver access, the portable app may need to be run as administrator.

This behavior belongs to the normal portable ZIP, not a special lab launch.
The managed installer retains its normal backend layout.

Portable updates currently use a fresh ZIP extracted into a new folder after
closing both apps. The older standalone updater can kill DS4Windows before
broker cleanup and cannot safely replace the bundled running broker in place;
the app explains the safe manual update path instead of launching it. Normal
managed-install update behavior is preserved, including recognition of an
installed folder if an older ZIP updater copied a portable marker into it.

For managed installs, prefer the full installer. The existing standalone
DS4Updater v2.0.4 has a pre-existing final verification mismatch: it compares
the RC tag number with the Windows file version, so `4.5` and `5.0.5.0` can
produce a false replacement-failure message after a successful copy. Its normal
auto-launch path can still reopen the updated application. This external updater
limitation is not fixed by RC4.5; it does not justify changing either version
scheme or pointing an exact-tag download at a nonexistent numeric release.

#### USB headset output: verified, with clear limits

The Switch 2 Pro's native Windows USB headphone output was physically verified
through its 3.5 mm jack, including left/right channel assignment and start/stop.
The Switch 2 page explains how to select that endpoint and provides a Windows
Sound settings shortcut. It does not silently replace your default audio device.

This is the physical controller's USB audio endpoint, not an emulated Xbox
headset. **Headset microphone capture has not been verified. Bluetooth headphone
and microphone audio remain unsupported:** successful Bluetooth command replies
and experimental packet writes did not establish audible output.

#### What the testing does—and does not—say

The earlier local portable RC4.5 checkpoint passed **4,516 tests twice**, with zero failures and
11 existing opt-in skips each time. A separate run passed all **152
allocation-named tests**. Existing CI exclusions remained unchanged; failed
intermediate runs led to investigated fixes, not relaxed assertions. The
[portable startup qualification](docs/validation/2026-09-08-portable-broker-startup.md)
records these runs and replacement packages. The earlier
[qualification record](docs/validation/2026-09-08-rc45-qualification.md) preserves
the initial 4,463-test RC4.5 checkpoint, distinct from the older 4,426-test
controller checkpoint.

The later publication checkpoint, with release-policy coverage and the verified
GitHub-built VIIPER artifact, passed **4,536 CI-filtered tests** and then **4,539
unfiltered local tests**, with zero failures and the same 11 opt-in skips. The
three extra cases are legacy profile/settings tests. The [publication record](docs/validation/2026-09-09-github-rc45-publication.md)
distinguishes these final broker pins from the earlier local kit and records
the complete broker CI and installer lifecycle gates.

All eight opt-in Xbox client/broker process-integration cases also passed
separately at the recorded broker source checkpoints. Those use isolated loopback
connections and simulated native attachment; they do not count as additional
physical-controller tests.

Physical evidence includes USB Pro input and feedback, Bluetooth controller and
sensor activity, native USB headphone output, and Windows virtual-pad lifecycle
checks. Newest UI, disconnect, sustained-rumble and combined-package changes
still need fresh tester acceptance. Test results are not a measurement of
controller-to-game latency.

The shipping path remains **VIIPER plus USB/IP**. This candidate does not claim
a completed native-driver backend, 0.125 ms input latency, a measured
multi-controller latency cure, or zero-time virtual-pad switching. Joy-Con 2 USB
support and the complete source/target/game matrix are not certified here.

For the most useful feedback, include the physical controller, USB or Bluetooth,
selected virtual pad, profile, game, and what happened immediately before the
problem. Try single and joined Joy-Cons, profile/output switching, held rumble
followed by Stop, and power-off/reconnect. Those everyday transitions are where
this candidate most needs to earn its release status.

#### Evidence behind this assessment

This assessment covers `VIIPERRC4.3` through the RC4.5 source preparation,
including the [RC4.4 feature set](CHANGELOG.md#inherited-release-candidate-4-4). Detailed records:

- [Current controller fixes, repeated full-suite results and remaining acceptance](docs/validation/2026-09-08-controller-followup-rollup.md)
- [Reported-bug fixes](docs/validation/2026-09-08-github-reported-bugs.md) and [startup/setup/tray follow-up](docs/validation/2026-09-08-reported-issues-follow-up.md)
- [Installer registration regression and existing-machine cleanup](docs/validation/2026-09-08-installer-duplicate-arp-entries.md)
- [Joy-Con standalone activation](docs/protocols/switch2-joycon-automatic-activation.md), [Link/Unlink handoff](docs/validation/2026-09-06-joycon-link-handoff.md), and [Windows multi-pad evidence](docs/protocols/switch2-cross-slot-and-pad-switch-recovery.md)
- [Switch 2 playstyle interface](docs/validation/2026-09-06-switch2-playstyle.md) and [physical Bluetooth sensor observations](docs/validation/2026-09-06-joycon-bluetooth-sensors.md)
- [Haptic detail conversion](docs/protocols/switch2-haptic-detail-rendering.md), [held-rumble and shutdown validation](docs/validation/2026-09-08-switch2-held-rumble-maintenance.md), and [Xbox feedback capabilities](docs/protocols/xbox-one-physical-output-policy.md)
- [Physical USB headphone evidence](docs/validation/2026-09-06-switch2-pro-usb-audio.md), [Bluetooth audio limits](docs/validation/2026-09-08-switch2-bt-receiver-plans.md), and [DualSense regression scope](docs/dualsense-rc43-rc44-native-haptics-regression.md)

Reference implementations and protocol research—including Switch2Connect, SDL,
PadForge/HIDMaestro, and Nintendo/Microsoft protocol work—were used where
relevant. Their demonstrated behavior informed the implementation; a feature
working upstream was not counted as a DS4Windows hardware test.

<a id="inherited-release-candidate-4-5-1"></a>

### RELEASE_CANDIDATE_4_5_1

Original record: `docs/RELEASE_CANDIDATE_4_5_1.md`. Instructions and validation claims apply to that release.

### RC4.5.1 — Switch 2 Rumble Hotfix

This hotfix addresses Switch 2 rumble regressions introduced in RC4.5 and
completes the Xbox One/Series configuration in both download packages.

> I highly recommend using an Emulated Dualsense controller with a Switch 2 Pro controller: it provides the best haptic feedback and playing experience

#### Fixes

- **Gentler connection and identification cues:** finite effects no longer
  repeat as sustained rumble. The original effect strengths and frequencies
  are preserved.
- **More consistent sustained rumble:** recover promptly from brief output
  contention without sending duplicate packets too quickly or replaying a
  backlog. This targets the avoidable gaps affecting Test Heavy, Test Light,
  and held game rumble over USB and Bluetooth.
- **Safer retries:** a queued Bluetooth one-shot is retried as the same effect
  instead of being enqueued again. Temporary timing or worker failures no
  longer permanently stop held-output maintenance.
- **Complete Xbox output configuration:** both the installer and portable ZIP
  include the selected Windows Xbox One/Series identity configuration, fixing
  the missing-file output-validation failure that could reject Joy-Con
  activation when its profile selected Xbox output.

DualSense PCM haptics, adaptive-trigger translation, Xbox impulse feedback,
left/right routing, and immediate Stop handling retain their existing paths.
This is a timing and effect-lifetime correction, not a new rumble intensity
curve or codec.

#### Downloads and upgrading

- **Installer:** `DS4Windows_5.0.5.1_Setup_x64.exe` — complete offline setup.
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire folder and run
  `DS4Windows.exe`; the included VIIPER starts automatically. Close other
  DS4Windows/VIIPER copies before launching a different portable folder.

Windows build **5.0.5.1**, release **VIIPERRC4.5.1**. VIIPER remains the
hash-pinned **0.1.3-rc4.5** build, and USB/IP remains **0.9.7.7**.
Use the standard installer EXE for managed upgrades rather than installing
the embedded MSI directly.

This is an **unsigned release candidate**, not a signed stable release.
Xbox emulation targets Windows; it does not provide authentication for a
physical Xbox console. The included synthetic emulation identity is not a
claim of hardware certification or universal game compatibility.

Automated tests and artifact checks validate the covered code and package
paths. Please report remaining stutter with the controller model, USB or
Bluetooth connection, virtual output type, and whether it occurs in the
Heavy/Light tests or a specific game. Physical feel can vary and remains part
of release-candidate testing.

<a id="inherited-release-notes-rc456"></a>

### release-notes-rc4.5.6

Original record: `docs/release-notes-rc4.5.6.md`. Instructions and validation claims apply to that release.

### RC4.5.6 — Steadier Feedback & Safer Startup

This stabilization update brings together the feedback-delivery work since 4.5.5 with fixes for initialization, recovery and setup ownership.

#### What's improved

- **More dependable startup.** Windows 10 can use the encrypted VIIPER connection without requiring Windows 11's native encryption provider. Authentication stays enabled. Portable startup now reports the failed connection stage and releases its own unsuccessful broker before showing the error.
- **Safer installation and recovery.** Installer cleanup and repair share the same setup lock. VIIPER releases listeners it opened if initialization fails partway through, so an unsuccessful attempt does not leave its ports occupied.
- **Better feedback delivery.** Native DualSense commands retain their order through busy USB and Bluetooth writers, with bounded retries instead of prematurely acknowledging undelivered state. Sustained Switch 2 rumble uses the referenced first-active-subframe framing.
- **Audio Haptics for Switch 2 Pro and Joy-Con 2.** Turn application or system audio into stereo HD rumble, using Mix or Replace alongside game feedback. Both application pickers find audio sessions across active outputs, including separate Sonar routes. System audio follows the Windows default output.
- **Clean audio recovery and stopping.** Capture failures roll back fully so they can recover. Source changes discard stale audio. Writer failures stop the affected runtime, retire its resources and clear its owned feedback without overwriting untouched native game feedback.
- **Complete matching downloads.** Both packages include VIIPER 0.1.4-rc4.5.6, the Windows 10 encryption dependency, Xbox emulation identity, runtime files and notices. DS4Updater 2.0.6 remains compatible; no separate updater replacement is needed.

#### Downloads and updating

Use **DS4Windows_5.0.5.6_Setup_x64.exe** for the standard installation, or extract the **entire portable ZIP** into its own folder. Do not copy only DS4Windows.exe. Close DS4Windows and VIIPER before replacing an existing installation's files; keep your profiles.

This is an **unsigned release candidate**, published under the existing VIIPERRC policy. Source archives, license notices, SHA-256 checksums and the CI build record accompany the downloads.

#### Validation and remaining limits

Release checks cover the full automated x64 suite, startup/Stop ownership, installer failure simulations, complete offline packaging, and hosted MSI install/repair/uninstall plus an upgrade from the published 4.5.5 package with profile-preservation checks. Matching broker tests run on Windows and Linux; the updater suite is also checked.

These checks do not guarantee every controller/game combination. Final subjective Joy-Con smoothness and GTA V Enhanced rapid-fire feedback still need tester confirmation on USB and Bluetooth. Bluetooth Switch 2 headset audio remains unsupported. No native-driver backend or new end-to-end latency guarantee is claimed.

<a id="inherited-release-notes-rc457"></a>

### release-notes-rc4.5.7

Original record: `docs/release-notes-rc4.5.7.md`. Instructions and validation claims apply to that release.

### RC4.5.7 Hotfix — Sorry, broke Vibration/Triggers

4.5.6 introduced a Bluetooth DualSense feedback-delivery regression. Sorry about that. This hotfix addresses the command backlog and startup stalls behind delayed or incorrect vibration and trigger effects.

#### What's fixed

- **Bluetooth DualSense game feedback:** queued game commands can now progress between audio packets while preserving their order, rather than falling behind the audio clock.
- **Feedback startup:** a partially filled audio buffer can no longer prevent the commands and audio needed to finish starting the stream.
- **Trigger Lab delivery:** live trigger changes now reach the Bluetooth output while speaker/audio passthrough is active. Unrelated profile edits do not clear the game's trigger effects.
- **Per-trigger control stays intact:** enabled Trigger Lab effects override the corresponding trigger; the other trigger continues to receive native game effects.

This is a targeted transport fix, not a gain boost. PCM encoding, audio cadence and input buffering are unchanged. It does not introduce a new controller backend or change Nintendo rumble tuning.

#### Downloads

- **Installer:** `DS4Windows_5.0.5.7_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire ZIP, not just the EXE.

Both include the complete matching **VIIPER 0.1.4-rc4.5.6** package. No new VIIPER release is required for this fix. **DS4Updater 2.0.6 remains compatible.** Close DS4Windows and VIIPER before replacing their files, and keep your profiles.

#### Validation

The fix passed the full automated x64 suite: **5,394 passed, zero failed**, with 11 hardware/environment-gated skips. Regression tests verify exact trigger command ordering, retry under a busy writer, startup progress, and unchanged PCM content through the output-writing code using simulated device I/O.

The private candidate was tested for haptics on a physical Bluetooth DualSense. Adaptive triggers still need confirmation in a trigger-supporting game; automated transport coverage is not a claim that every game/controller combination has been physically tested.

Published as an **unsigned release candidate** under the existing `VIIPERRC` policy. Matching source archives, notices, checksums and the GitHub Actions build record accompany the downloads.

<a id="inherited-release-notes-rc458"></a>

### release-notes-rc4.5.8

Original record: `docs/release-notes-rc4.5.8.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5.8 Hotfix — Bluetooth Haptics & Disconnect Recovery

This update fixes a native DualSense rumble regression traced to RC4.4 and a separate queued-disconnect shutdown deadlock. It keeps the subsequent feedback, Trigger Lab, Nintendo, audio, input, and installer improvements intact.

#### What's fixed

- **Native Bluetooth rumble:** continuing audio packets no longer clear the game's legacy or improved rumble mode immediately after a valid command. The mode and motor values remain active until an explicit change or stop.
- **Feedback ownership:** local trigger/LED updates cannot accidentally cancel native rumble, and stale media templates cannot overwrite a newer accepted motor command. Adaptive-trigger and LED one-shot updates are still consumed once.
- **Disconnect recovery:** a disconnect queued on the controller worker now hands off to the lifecycle owner without a circular wait. Final output retirement and completion waits for external callers remain enforced.

This is not a gain boost or a rollback. PCM sample content, audio cadence, native command ordering and retry, per-trigger Trigger Lab priority, and Nintendo feedback tuning remain unchanged.

#### Downloads

- **Installer:** `DS4Windows_5.0.5.8_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire ZIP, not just the EXE.

Both include the unchanged, complete **VIIPER 0.1.4-rc4.5.6** package. No broker code changed and no new VIIPER release is required. **DS4Updater 2.0.6 remains compatible.** Close DS4Windows and VIIPER before replacing their files, and keep your profiles.

#### Validation and limits

The rebuilt x64 suite passed **5,413 tests with zero failures** and 11 hardware/environment-gated skips, including **2,646 Nintendo tests**. Allocation checks remained enabled and passed. Real-helper tests with simulated device I/O cover continuous rumble, explicit stops, command ordering, busy retry, microphone boundaries, unchanged PCM/gain, and disconnect completion.

The private candidate connected a physical Bluetooth DualSense, associated virtual DualSense output, and started speaker passthrough. These are startup observations, **not confirmation of in-game haptics or adaptive-trigger feel**. Physical game acceptance remains pending; no complete Hades II, Expedition 33, GTA, or all-game compatibility claim is made.

Published as an **unsigned release candidate** under the existing draft-first `VIIPERRC` policy. Matching sources, notices, checksums, and the verified GitHub Actions build record accompany the downloads.

<a id="inherited-release-notes-rc459"></a>

### release-notes-rc4.5.9

Original record: `docs/release-notes-rc4.5.9.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5.9 Hotfix — Faster Feedback, Same Detail

Rapid feedback should stay responsive, not build up a delay during sustained action. This hotfix targets the DualSense Bluetooth feedback backlog reported in #81 while retaining the RC4.5.8 haptics and disconnect fixes.

#### What's improved

- **Faster native feedback dispatch:** game feedback now has a separate 500 Hz / 2 ms software pacing ceiling instead of sharing the 200 Hz / 5 ms local-settings cadence. Distinct effects and stops remain ordered; audio and microphone scheduling retain their existing boundaries.
- **No unnecessary repeat backlog:** consecutive byte-identical, known state-setting commands may share an unclaimed pending queue entry. Changed effects, A-to-B-to-A transitions, stops, unknown commands, extra trigger bytes, and ownership changes are preserved.
- **Adaptive-trigger commands use the faster path too.** Automated tests verify trigger changes, complete trigger payloads, and release commands remain intact. Live validation covered body rumble, not in-game adaptive triggers; this addresses the shared queue-delay cause, not every possible cause of trigger skipping.

This is not a gain boost or a rollback. PCM samples, codec settings, audio cadence, native rumble-mode ownership, per-trigger Trigger Lab priority, and Nintendo HD-rumble tuning are unchanged. Existing Switch 2 and Joy-Con functionality remains included.

#### Measured on a Bluetooth DualSense

In a controlled one-second 250 Hz body-rumble burst, all **251 changes, including the final stop**, reached the physical Bluetooth writer in order. Against RC4.5.8, the final-stop submission delay fell from **252 ms to about 0.18 ms**. Two additional candidate runs preserved all 251 changes, with final-stop submission delays of **0.15–0.18 ms**.

These are **host-side source-write-to-physical-writer measurements**, not radio acknowledgments, actuator latency, guaranteed 500 Hz hardware throughput, or proof of every game's behavior. GTA rapid-fire gameplay and adaptive-trigger feel still need in-game confirmation. Short input checks showed no consistent report-interval worsening during these bounded bursts.

#### Downloads

- **Installer:** `DS4Windows_5.0.5.9_Setup_x64.exe`
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire ZIP, not just the EXE.

Both include the complete, unchanged **VIIPER 0.1.4-rc4.5.6** package and Xbox output identity. No new broker release is required. **DS4Updater 2.0.6 remains compatible**; the portable bootstrap obtains the published updater rather than bundling an outdated copy. Close DS4Windows and VIIPER before replacing files, and keep your profiles.

#### Validation

The rebuilt x64 suite passed **5,539 tests with zero failures** and 11 existing hardware/environment-gated skips. Allocation assertions remained enabled, including a new intentional-allocation control. Coverage includes ordered native feedback, trigger payload preservation, media/PCM continuity, transport retry and completion handling, and existing Nintendo behavior.

Published as an **unsigned release candidate** under the existing draft-first `VIIPERRC` policy. Matching sources, notices, checksums, and the GitHub Actions build record accompany the downloads. Detailed evidence is recorded in [the feedback cadence validation](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.5.9/docs/validation/2026-09-12-issue81-native-rate.md).

<a id="inherited-release-notes-rc461"></a>

### release-notes-rc4.6.1

Original record: `docs/release-notes-rc4.6.1.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.6.1 Hotfix — Dial In Your Aim, Safer HidHide

RC4.6.1 adds a simple way to dial in flick-stick aiming, cleans up trigger settings, prevents the startup-task failures reported in #98 and #103 from aborting setup, and protects against the confirmed HidHide configuration-client crash caused by Windows app-execution aliases.

#### Dial in a full 360° turn

- Open **Axis Config → LS or RS → Output Mode: Flick Stick** and choose a **360° test button** beside Real World Calibration.
- Save the profile, face a recognizable landmark in-game, and tap your chosen button. Increase Real World Calibration if the turn falls short; decrease it if it goes too far.
- Each tap requests one approximately one-second turn. Holding the button does not repeat, and extra taps during a turn do not build a queue.
- The test button's normal mapping is reserved while configured. Choose **Not assigned** when finished to restore it; your original binding is never deleted. A trigger assignment reserves both its soft and full-pull outputs.
- Changes to the profile, stick mode, assigned button, or controller connection cancel unfinished turns. Flick Stick also safely handles a controller disappearing during mapping.

This is a manual calibration aid for games that accept **mouse look**, not automatic camera tracking. Use a spare button and keep other aiming inputs still for a repeatable result. The setting is in **Axis Config**, not button remapping or Special Actions.

#### Cleaner trigger settings

- Removed the legacy **Trigger Effect**, **Trigger Start**, and **Trigger Strength** controls from Axis Config. **Trigger Lab** remains the place to configure adaptive effects.
- Normal trigger dead zones, curves and sensitivity remain available. Existing saved trigger effects, Trigger Lab restoration and native game-provided feedback are preserved.

#### Safer HidHide configuration

- Prevent DS4Windows from registering Windows app-execution aliases that can crash HidHide's Applications list.
- Check the configuration before opening HidHide through DS4Windows, with clear feedback when it cannot be read safely.
- If unsafe aliases are found, offer a **confirmation-based, backed-up repair** that removes only those entries. Other applications, hidden-device rules and hiding settings are preserved. Unsafe or changed configurations block the repair instead of guessing.

This addresses the confirmed alias-related crash, not every possible HidHide failure. An external tool can still add an unsafe entry; launch HidHide through DS4Windows to use the guard.

#### Startup-task recovery (#98 and #103)

- Setup keeps the original **RunVIIPER** and **RunDS4Windows** names. Existing definitions at those two reserved root names are backed up before replacement, including manually created or incomplete tasks. No alternate task names are introduced.
- Registration, verification or Task Scheduler access failures produce a startup warning instead of canceling installation. Setup can start the verified executables directly; automatic logon startup may need a later Repair.
- Verification diagnostics identify the mismatched fields instead of reporting only a generic failure. The #98 log proves verification failed, but does not contain the task definition needed to establish which field failed on that PC.
- Launch and uninstall still check the task's contents. An unsuccessful repair never authorizes running an arbitrary task just because its name matches.

Backups are stored under **%ProgramData%\\DS4Windows\\Installer\\task-backups**. Tasks outside the two reserved root names are untouched. Windows can still refuse registration; this release makes that failure non-fatal, not invisible. Driver integrity, pending-reboot, USB/IP compatibility and VIIPER API checks remain required.

#### Packages and updates

- Release/update tag: **VIIPERRC4.6.1**. Windows application, MSI and installer version: **5.0.7.0**, advanced for reliable upgrade ordering.
- Complete offline x64 installer and portable ZIP, including **VIIPER 0.1.5-rc4.6**, the required Xbox output identity, and matching source/notices. Portable users should extract the entire folder.
- **DS4Updater 2.0.6** remains compatible; no separate updater or broker upgrade is required for this hotfix. USB/IP remains **0.9.7.7**.
- Audio, haptics, adaptive-trigger transport, and Nintendo rumble tuning are unchanged from RC4.6.

#### Validation

This is an **unsigned release candidate**. The complete local x64 suite passed **5,991 tests with zero failures**, with 11 explicitly gated tests skipped. Calibration uses the canonical mapper and recording test outputs; no input is injected into the desktop by the tests. Calibration light/dark UI layouts were inspected at normal and 150% scale. Existing profile, trigger restoration, native feedback and Nintendo regression checks remain enabled.

In-game calibration still depends on mouse sensitivity and acceleration. Hardware/gameplay acceptance is not replaced by automated tests, and this release makes no new claim about the previously unattributed rare movement interruption or issue #81 gameplay acceptance.

Source evidence: [calibration and trigger UI](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/validation/2026-09-14-axis-calibration-and-trigger-ui.md), [calibration guide](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/flick-stick-calibration.md), [startup-task collision](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/validation/2026-09-14-issue103-startup-task-collision.md), and [HidHide safeguard](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/validation/2026-09-14-hidhide-client-alias-guard.md).

<a id="inherited-release-notes-rc462"></a>

### release-notes-rc4.6.2

Original record: `docs/release-notes-rc4.6.2.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.6.2 — Game Mods, Safer Updates & Startup

RC4.6.2 adds optional DSX game-mod support for DualSense triggers and lights, fixes custom-named portable updates, and improves startup recovery and shifted profile settings.

#### Let supported game mods control triggers and lights

- New **Settings → Game mod support (DSX)** option for physical **DualSense and DualSense Edge** controllers. It starts **off**; enable it only when using a compatible mod.
- Local mods can control adaptive triggers, lightbar, player lights and the microphone light through the DSX UDP protocol. The default address is **127.0.0.1:6969**; connection details and **Apply / Retry** make a busy port easier to resolve.
- **Trigger Lab takes priority on each enabled trigger.** A mod can control one trigger without blocking the other, and temporary mod settings do not overwrite your profile.
- Resetting the mod, disabling support, stopping DS4Windows or removing the controller releases its overrides and restores the underlying state.
- Native trigger commands retain their order and exact effect data. Repeated trigger-command flags, stale duplicate tracking after a reset, and old callbacks affecting replacement controllers are covered by new regression tests.

This is **local trigger/light compatibility**, not full DSX replacement or audio/PCM haptics support. Some legacy effects and input-threshold instructions remain unsupported rather than sending guessed commands. Mods should send reset when exiting; disabling the option also releases a held override. See the [setup and compatibility guide](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/DSX-game-mod-support.md).

#### Portable updates keep your custom name (#99)

- **DS4Updater 2.0.7** preserves the selected application EXE name without adding another canonical `DS4Windows.exe` to a compatible target installation.
- Required DLLs and runtime files retain their correct names. An old default EXE is removed only when the package owns it; unrelated files and profiles are preserved.
- Renaming, clearing a saved name, rollback, and protected setup staging use matching application identities. Unsafe collisions stop before file replacement.
- The new custom-only layout requires **RC4.6.2 / Windows version 5.0.8.0 or newer**. The updater checks the verified target version; it is not locked to one release tag.
- RC4.6.2 requests updater **2.0.7 or newer**, including future verified versions. Portable users should always extract the complete ZIP when updating manually.

#### Clearer startup recovery (#77)

- Keep your requested startup preference separate from tasks temporarily disabled while setup needs repair or a reboot.
- Show clearer requested-versus-active startup status and the appropriate next action.
- Stage a protected, narrowly scoped continuation for the next logon after a required reboot, with account, boot, package and hash checks before continuing setup.
- Preserve the original **RunDS4Windows** and **RunVIIPER** task names and explicit startup opt-outs. Driver and broker readiness checks remain required.

Windows can still deny permissions or require manual repair. Automated recovery tests pass; a complete physical reboot/logon/UAC acceptance test for this change remains outstanding.

#### Profile reliability and regression coverage (#46)

- Extras-only shifted bindings keep their activation button through save/load and repeated saves.
- Shift extras can activate without replacing the button's normal action, and release correctly when the source or modifier is released.
- Add non-default settings, culture-independent date, macro value-band and historical profile-migration tests.
- Thanks to **Gabarsolon** for the DSX feature contribution and **anagnorisis2peripeteia / Cameron Beeley** for settings, macro and migration contributions.

#### Packages and validation

- Tag: **VIIPERRC4.6.2**. Windows application/MSI/installer version: **5.0.8.0**, advanced for correct upgrade ordering.
- Complete offline x64 installer and portable ZIP, including unchanged **VIIPER 0.1.5-rc4.6**, **USB/IP 0.9.7.7**, the required Xbox output identity, matching sources and notices. No separate broker upgrade is needed.
- Companion updater: **DS4Updater 2.0.7**. Earlier release assets remain unchanged.
- The release candidate passed **6,367 tests with zero failures**, with 11 explicitly gated skips. The warmed native-shadow check measures **zero allocations**; the allocation assertion was not relaxed.
- This is an **unsigned release candidate**. Automated tests, hosted MSI lifecycle checks and package verification do not replace physical controller/game-mod acceptance. Nintendo rumble gain/cadence and PCM audio encoding are not retuned in this release.

Technical evidence: [PR review](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/validation/2026-09-14-pull-request-review.md) and [startup, updater and profile fixes](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/validation/2026-09-14-issues-99-77-46.md).

<a id="inherited-release-notes-rc463"></a>

### release-notes-rc4.6.3

Original record: `docs/release-notes-rc4.6.3.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.6.3 — Smooth Profile Switching & Mouse Clicks

RC4.6.3 fixes temporary-profile switching regressions and overlapping mouse-button bindings and macros. It is built from published main, without the earlier unshipped haptics experiments.

#### What changed

- Fix repeated profile loads while holding a temporary-profile switch. Releasing before activation finishes now cancels that activation instead of applying it late.
- Keep the current mapping reporting during profile preparation, and publish the prepared mapping at a guarded boundary. Backend and audio refresh work no longer runs on the physical input queue.
- Preserve held keys through automatic profile press/release transitions. A switch no longer directly releases a key that another held binding still owns.
- Preserve the existing virtual pad for same-output profile changes. There is no fixed activation delay; changing the virtual controller type still requires a real device transition.
- Fence delayed switches and refreshes against disconnects, replacement connections and newer profile selections.
- Keep mouse-toggle state separate for each binding, so unrelated toggled actions cannot block mouse-button presses or releases.
- Keep a mouse button held while any mapped binding or macro still owns it. Ending one macro no longer releases another owner's click.
- Correct FakerInput's X1/X2 side-button event translation.
- Preserve held mouse buttons when switching output backends, and retire old macro input safely on disconnect or Stop.

#### Validation and limitations

Targeted regression checks cover temporary-profile press/release with held movement and clicks, early release, rapid re-press, nested temporary profiles, stale connection work, overlapping mouse holds, independent toggles, macro completion and side-button translation. Ordinary **R2 → Left Mouse** combined with cursor movement or keyboard input already passes the baseline tests; it is not newly enabled by this release.

Local validation: **6,681 tests passed, 0 failed**, with 12 opt-in hardware cases skipped. The profile-switch simulations use the real mapper, serialized profile worker and registered report admission with recording output sinks. They do not claim hardware/game acceptance. See the [profile-switch validation ledger](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.3/docs/validation/2026-09-21-temporary-profile-input-continuity.md).

The reporter's machine and game have **not** been validated. These fixes do not establish the cause of that report or guarantee that every game accepts simultaneous controller and mouse/keyboard input. See [Troubleshooting mapped mouse clicks](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.3/docs/troubleshooting-mouse-clicks.md) for conditional Windows touchpad guidance and game-specific checks. DS4Windows does not automatically change Windows touchpad settings.

#### Packages

- Tag: **VIIPERRC4.6.3**. Windows application/MSI/installer version: **5.0.9.0**, advanced for correct upgrade ordering.
- Complete offline x64 installer and portable ZIP, with unchanged **VIIPER 0.1.5-rc4.6**, **USB/IP 0.9.7.7** and Xbox output identity. No separate broker update is required.
- **DS4Updater 2.0.7** remains compatible; its minimum-version requirement is unchanged. Extract the complete ZIP for manual portable updates.
- This is an **unsigned release candidate**. Publication is gated on exact-source CI, installer lifecycle and downloaded-package checks; automated validation does not replace acceptance on the reporter's hardware and game.

<a id="inherited-release-notes-rc464"></a>

### release-notes-rc4.6.4

Original record: `docs/release-notes-rc4.6.4.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.6.4 — Smoother DualSense Haptics

Enjoy more detailed, consistent feedback from your DualSense over Bluetooth. This update focuses on keeping both advanced haptics and traditional rumble smooth through busy action scenes.

#### Improvements and fixes

- **More faithful advanced haptics:** improved Bluetooth haptic conversion helps preserve the detail and character of supported games' effects.
- **Fewer interrupted effects:** fixes brief gaps that could make closely spaced haptic effects feel incomplete or skippy.
- **Rumble that lasts as intended:** fixes a settings update that could cut an active rumble effect short. Genuine stop commands still stop the effect normally.
- **Adaptive triggers stay responsive:** trigger and lighting updates continue alongside rumble without unnecessarily interrupting it.
- **Matching VIIPER included:** the installer and portable ZIP include the updated backend required for these improvements.

These improvements apply to DualSense and DualSense Edge over Bluetooth when emulating a DualSense. Advanced haptics require a game that supports them.

#### Install or update

##### Recommended: all-in-one installer

1. Close your games, exit DS4Windows, and quit VIIPER from its tray icon.
2. Download and run **DS4Windows_5.0.10.0_Setup_x64.exe** from the assets below.
3. Follow setup, then reopen DS4Windows and reconnect your controller. Existing profiles are retained.

##### Portable version

1. Close your games, exit DS4Windows, and quit VIIPER.
2. Download **DS4Windows_VIIPER_x64.zip** and extract the **entire** ZIP into a new folder. Do not replace just the EXE.
3. Keep a backup of your existing profiles and settings. If you store them beside DS4Windows, copy them into the new folder before launching.
4. Start DS4Windows from the extracted folder. It will start the bundled VIIPER when no other copy is running. On a new PC, complete the driver setup when prompted.

**For the best experience:** select **DualSense** as your emulated controller, enable controller audio/speaker support for advanced haptics, and restart the game after changing controller emulation. Game support and individual effects vary.

This is an **unsigned Windows x64 release candidate**. Download only from this repository's release page. No new USB/IP version is required; the supported version remains **0.9.7.7**.

Thanks for your feedback and patience. Enjoy the update!

<a id="inherited-release-notes-rc465"></a>

### release-notes-rc4.6.5

Original record: `docs/release-notes-rc4.6.5.md`. Instructions and validation claims apply to that release.

A small update to make everyday use smoother. Thanks for helping us catch these issues!

#### What's fixed

- Fixed a freeze when opening **Log** after long sessions.
- Made scrolling, clearing and exporting the Log page more reliable.
- Fixed cases where the **DualSense lightbar** did not return to your profile color after closing a game.
- Fixed an update issue that could leave setup stuck on **another installation finishing**.
- **VIIPER repair now keeps DS4Windows open.** Missing or mismatched versions are restored automatically, with the correct download used when needed.
- **Portable stays portable.** VIIPER is repaired beside DS4Windows, without moving your files or changing another installation.
- Removed VIIPER's separate update pop-ups. The matching version now comes with DS4Windows updates.
- Refreshed the [getting-started guide](https://github.com/hbashton/DS4Windows/blob/main/docs/getting-started.md) to make setup easier to follow.

#### How to update

1. Close your games, DS4Windows and VIIPER.
2. **Recommended:** download and run **DS4Windows_5.0.11.0_Setup_x64.exe**. Your saved profiles are kept.
3. **Portable:** back up your profiles and settings, then extract the whole **DS4Windows_VIIPER_x64.zip** into your existing portable folder, replacing the packaged app files. Keep your profiles, settings and `portable-data` folder.

Both downloads include the matching VIIPER. No separate VIIPER update is needed for this hotfix.

This is an unsigned Windows x64 release candidate, so Windows may show a publisher warning.

Enjoy, and thanks for your feedback!

<a id="inherited-release-notes-rc466"></a>

### release-notes-rc4.6.6

Original record: `docs/release-notes-rc4.6.6.md`. Instructions and validation claims apply to that release.

A smoother start, easier updates, and better DualSense Edge support. Thanks for helping make DS4Windows more dependable!

#### What's improved

- **More reliable VIIPER startup and repair.** Slow startup checks are given time to finish, failed attempts clean up more safely, and retries keep DS4Windows open.
- **Clearer error messages.** If VIIPER cannot start, DS4Windows explains which startup check failed instead of showing only a generic error.
- **Safer app startup.** Opening DS4Windows twice at the same time no longer lets two installed instances compete for your controllers.
- **Fixed the `-autolaunch` update error.** Installed copies use the matching installer; portable copies stay in their own folder. Profiles and settings are preserved.
- **Better DualSense Edge compatibility.** Improved controller identification, motion calibration checks and virtual-controller reports.
- **Native DualSense feedback across both models.** Standard DualSense and DualSense Edge can use the shared advanced haptics and adaptive-trigger path with either emulated model.
- **Safer Edge settings handling.** Unsupported onboard-profile commands are rejected instead of appearing to work or applying incomplete settings. Virtual onboard-profile editing is not included.

The matching VIIPER is included. You do not need to update it separately.

#### How to update

1. Close your games, DS4Windows and VIIPER.
2. **Installer:** download and run **DS4Windows_5.0.12.0_Setup_x64.exe**. Your saved profiles are kept.
3. **Portable:** back up your profiles and settings, then extract the complete **DS4Windows_VIIPER_x64.zip** into your portable folder, replacing the packaged app files. Keep your profiles, settings and `portable-data` folder.

If your older version shows the `-autolaunch` error when checking for updates, use the installer or complete portable ZIP once to get the corrected update path.

This is an unsigned Windows x64 release candidate, so Windows may show a publisher warning. Not every Edge feature has been verified on hardware over both USB and Bluetooth; please report any remaining issues with your controller model, connection type and log.

Enjoy, and thank you for your feedback!

<a id="inherited-release-notes-rc46"></a>

### release-notes-rc4.6

Original record: `docs/release-notes-rc4.6.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.6 Hotfix — Clearer Audio, Reliable Feedback & Disconnects

RC4.6 brings together the fixes since RC4.5.9: more reliable controller audio, safer feedback delivery under load, and cleaner Bluetooth disconnects. The faster DualSense feedback path from RC4.5.9 stays in place.

#### Controller audio that starts and stays on the right source

- **DualSense speaker recovery:** restarting or switching audio sources no longer stalls speaker warmup because an earlier session's counters were reused.
- **Sonar compatibility:** physical DualSense USB and Bluetooth capture use the established Sonar-compatible endpoint capture policy, including audio from an emulated DualShock 4 headset.
- **Keep the source quality:** app and external-endpoint capture for DualSense uses 48 kHz floating-point audio. The virtual DualShock 4 headset endpoint remains at its native 32 kHz; physical DualShock 4 Bluetooth audio keeps its existing codec and rate.
- **Correct USB playback:** different source rates are converted to the physical DualSense output rate instead of being copied frame-for-frame. Large callbacks are no longer truncated, and small callbacks are processed without waiting to fill an extra block.
- **Safer source changes:** old callbacks and buffered samples cannot leak into a replacement source. Capture shutdown and shared-app capture retirement no longer hold the manager lock while waiting on callbacks.

#### Feedback delivery beyond DualSense

- **Physical DualShock 4:** fresh motor changes and explicit stops can pass immediately during Bluetooth audio instead of waiting behind the unchanged-state maintenance interval.
- **Physical DualShock 3:** a failed output write remains pending for retry; a newer stop can replace the failed active effect.
- **Switch 2 Pro and Joy-Con 2:** eligible identical commands at an unclaimed pending queue tail can share an entry. Changed effects and stops, A-to-B-to-A sequences, PCM and timed feedback are not folded together. This is a delivery change, not a rumble gain, encoding or cadence retune.
- **Virtual DualShock 4 / VIIPER:** the V3 speaker-output path reserves room for a motor-stop command, preserves ordered changes, and reports a full distinct-command queue instead of silently accepting and dropping its contents. Reconnecting feedback readers cannot let an old connection retire the new owner's state.

#### Disconnect and profile safeguards

- **No stale controller row after manual Bluetooth disconnect:** the controller-list and tray commands now request complete removal, rather than depending on a later HID read failure after transport cleanup has already finished.
- **Reject stale Game Bar activation:** delayed compatibility activation rechecks the current profile policy before allocating or rerouting output. Switching to Xbox output, disabling compatibility or selecting input-only mode prevents an old activation request from taking over.

#### Packages and updating

- DS4Windows **VIIPERRC4.6**, Windows file/installer version **5.0.6.0**.
- Bundled **VIIPER 0.1.5-rc4.6**, with matching source and notices. USB/IP remains **0.9.7.7**.
- Complete offline installer and portable ZIP, including VIIPER and the required Xbox output identity. Portable users should extract the entire folder.
- The published **DS4Updater 2.0.6** remains compatible; no replacement updater is needed for this version.

#### Validation and limits

This is an **unsigned release candidate**. The rebuilt local x64 suite passed **5,717 tests with zero failures**, including eight isolated DS4Windows-to-VIIPER process tests; three live-audio tests remain explicitly gated. Regression coverage exercises speaker priming, sample-rate continuity and channel placement, source retirement, feedback order and stop admission, reconnect ownership, manual removal, and Game Bar activation policy. Allocation-sensitive assertions remain enabled; a traced GC-counter measurement issue was corrected in the test harness without raising the zero-byte limit or changing production behavior.

Software tests are not a promise of unlimited controller output rates or zero physical latency. Final audio listening, disconnect behavior and gameplay feel still need tester confirmation. The rare once-in-a-day movement interruption remains unattributed; the Game Bar safeguard is not being presented as proof that incident is solved. Genuine disconnect and shutdown neutral-report safety remains intact.

Detailed source evidence: [cross-controller feedback](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-12-cross-controller-feedback.md), [audio and Sonar](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-12-sonar-ds4-dualsense-audio.md), [manual disconnect](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-14-dualsense-manual-disconnect.md), and [rare input investigation](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-14-rare-input-neutral-investigation.md).

<a id="inherited-2026-09-09-rc452-release-notes"></a>

### 2026-09-09-rc452-release-notes

Original record: `docs/validation/2026-09-09-rc452-release-notes.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5.2 Hotfix — Joy-Con 1 & Nintendo Options

Original Joy-Cons join the new Nintendo experience, with easier linking, clearer settings, and live feedback while you remap. This hotfix also corrects stale DualSense compatibility-rumble state associated with the reported Hades II pulsing on a Switch 2 Pro.

#### Original Joy-Cons, familiar controls

- Use one original Joy-Con on its own, or link a left and right through the same Controllers-page Link/Unlink buttons used by Joy-Con 2.
- The magnet checkbox now controls automatic pairing for both generations. The old separate Joy-Con global options have been removed.
- Linking keeps the first-selected controller's profile and virtual pad. Saved pairs are remembered; unlinking or losing a half releases held input safely.
- Choose Horizontal or Vertical manually from the controller card. Stick direction and controller artwork follow your choice.
- Use applicable Nintendo profile features with original Joy-Cons: per-hand gyro aiming, aim activation, mode shifting, stick-to-mouse tools, and translated Xbox/DualSense feedback.

#### Nintendo Options and easier remapping

- **Switch 2 Controls is now Nintendo Options.** Profiles remain universal: settings stay visible and saved even on controllers that cannot use a particular feature.
- A **Show live preview** button adds controller readings while remapping, without permanently filling the screen.
- Clearer dual-Joy-Con aiming labels explain side swapping, pausing, and the separate aim-activation setting.
- Special Actions now expose the missing C, GL/GR, Capture, Mute, Edge function/paddle, and individual Joy-Con rail buttons as selectable triggers.
- Capture and side-button trigger selections survive reopening. Unrecognized saved trigger names are preserved instead of silently weakening an action's trigger combination.

#### Rumble and reliability fixes

- Fixed stale DualSense compatibility rumble being restarted by later non-rumble control reports on Nintendo controllers. This corrects the stale-state path identified in the Hades II diagnostic dump.
- Fresh control commands select the appropriate rumble source; cached audio snapshots cannot bring an old compatibility effect back.
- A fresh zero-motor stop remains stopped even when later audio packets carry an older motor snapshot. Mixed audio/adaptive effects no longer replay old audio samples as sustained vibration.
- **Translate Xbox impulse-trigger vibration** is a separate Advanced option for every controller, independent of Trigger Lab. The Nintendo Options checkbox controls the same saved setting.
- DualSense and DualSense Edge can send Xbox impulses to their adaptive triggers or blend them into body rumble. Turning impulse translation off leaves ordinary body rumble enabled.
- Fixed original Joy-Con paired disconnect behavior and unlinking a session-only pair when its saved-pair file is damaged.
- Fixed upright right-Joy-Con stick-assist input selection while preserving fine axis precision.

#### Portable updates

- Safe portable updating uses **DS4Updater 2.0.5**: checked downloads, staged replacement, preserved profiles and pairing data, and rollback on supported failures.
- The updater waits for the relevant apps to close rather than force-killing unrelated copies. Long portable-folder paths are supported.
- The portable ZIP remains complete, including the matching `viiper.exe`, Xbox emulation identity, required runtime, and offline dependency installers.

#### Before you update

Close DS4Windows before installing or replacing files, and keep a backup of your profiles. Portable users coming from RC4.5.1 or older should use this ZIP for the initial upgrade; the new safe updater entry point is delivered in 4.5.2.

Original Joy-Cons do not gain Joy-Con 2's optical mouse or extra hardware. Pairing is within the same generation; mixed original/Joy-Con 2 pairs are not supported. Original holding-style changes save to the current profile; Joy-Con 2 retains its per-controller choice. Original HD rumble approximates translated effects within its packet format, and the new raw-stick calibration wizard is not supported for original Joy-Cons.

This is an **unsigned release candidate**, not a signed stable release. It uses Windows file version **5.0.5.2**. VIIPER remains the matching **0.1.3-rc4.5** build; a new broker installation is not required solely for this rumble correction.

Automated regression and packet/lifecycle tests cover these changes. Fresh physical Hades II rumble acceptance, original Joy-Con feedback feel, and a launched-worker end-to-end portable update have not yet been confirmed; please report any problems with the connection type, virtual controller type, and logs.

<a id="inherited-2026-09-09-rc453-release-notes"></a>

### 2026-09-09-rc453-release-notes

Original record: `docs/validation/2026-09-09-rc453-release-notes.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5.3 Hotfix — Reliable Stop & Reconnect

This hotfix addresses a shutdown deadlock that could leave **Stop** disabled until DS4Windows restarted, sometimes alongside a `read failure: 995` message. It also protects a newly reconnected controller from delayed cleanup belonging to its previous connection.

#### What's fixed

- Controller-removal callbacks no longer block a worker that Stop is waiting to finish. Cleanup remains serialized and tied to the exact connection.
- DS4 and DualSense recognize error 995 caused by their own deliberate Stop cancellation. Unexpected 995 errors and other read failures still retain their diagnostics and removal handling.
- A worker calling Stop cannot try to join or interrupt itself.
- Delayed removal cannot delete a replacement controller that reused the same device path or address. Hotplug also rejects controllers already being removed.

#### Before you update

Close DS4Windows before installing or replacing files, and back up your profiles. Extract the complete portable ZIP; do not replace only `DS4Windows.exe`. Portable users on RC4.5.1 or older should use the ZIP for the initial upgrade—the safe updater entry point arrived in RC4.5.2.

The portable package remains complete, with the unchanged **VIIPER 0.1.3-rc4.5**, matching `viiper.exe`, Xbox emulation identity, required runtime, and offline dependency installers. No new broker installation is required solely for this hotfix. Portable updating to RC4.5.3 uses **DS4Updater 2.0.6**.

#### Validation and limits

The versioned build passed **4,918 automated tests**, with **zero failures** and **11 existing opt-in skips**. This includes **29 new shutdown/reconnect regression cases** and three release-ordering checks. Tests reproduce the stop/removal race and verify cancellation and reconnect ownership; they do not establish physical confirmation of the reporter's session.

This is an **unsigned prerelease**, not a signed stable release. Release tag: **VIIPERRC4.5.3**. Windows file version: **5.0.5.3**.

<a id="inherited-2026-09-09-rc454-release-notes"></a>

### 2026-09-09-rc454-release-notes

Original record: `docs/validation/2026-09-09-rc454-release-notes.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5.4 Hotfix — Read Failure Recovery

This follow-up makes DS4 Bluetooth recovery safer after an unexpected read failure, including failures that happen **before pressing Stop**.

#### What's fixed

- After a terminal Bluetooth read failure, DS4Windows no longer forces another physical output write before removing the failed controller. A stalled output request must not block that recovery path.
- Failed-read cleanup also avoids publishing a fresh effect into the Bluetooth audio lane.
- Normal rumble, lightbar, CopyCat settings and the working control-pipe behavior from issue #84 are unchanged.
- Includes 4.5.3's Stop/removal deadlock and reconnect-ownership fixes. A new regression test verifies the corrected sequence: unexpected read error first, then Stop.

This fixes application recovery behavior. It does **not** suppress unexpected errors or claim to prevent every Windows read failure. The initiating cause of the reporter's particular 995 remains unconfirmed.

Validation: **4,927 automated tests passed**, zero failures, with 11 existing opt-in tests skipped. The new recovery regressions failed against their previous behavior before passing with the fixes.

#### Updating

Close DS4Windows before updating and back up your profiles. The installer and complete portable ZIP include the required runtime, VIIPER **0.1.3-rc4.5**, Xbox emulation identity and offline dependency installers.

Portable RC4.5.2 and newer use the already-published [DS4Updater 2.0.6](https://github.com/hbashton/DS4Updater/releases/tag/v2.0.6). Its verified-metadata version handling supports this release without another updater code change. For an initial upgrade from RC4.5.1 or older, extract the complete ZIP into a new folder and copy your settings/profiles.

Release tag: **VIIPERRC4.5.4**. Windows file version: **5.0.5.4**. This is an **unsigned prerelease**.

<a id="inherited-2026-09-09-rc455-release-notes"></a>

### 2026-09-09-rc455-release-notes

Original record: `docs/validation/2026-09-09-rc455-release-notes.md`. Instructions and validation claims apply to that release.

### Release Candidate 4.5.5 Hotfix — Reliable Setup & Startup

This hotfix repairs a setup/startup conflict that could stop an upgrade with **VIIPER/USB-IP setup failed (0x80070001)**, even though the USB/IP driver was healthy.

#### What's fixed

- Setup can recover the verified older VIIPER startup registration left behind by the affected build, including a recognized portable-broker target. It saves the original task before changing it.
- DS4Windows no longer redirects the installed startup task to a portable runtime preference or removes the task's ownership marker.
- With installed startup enabled, runtime selection follows the verified installed broker too, so an old portable preference cannot put the next sign-in into a repair loop.
- The app and installer now agree on the broker's startup priority, preventing repeated unnecessary task replacement.
- Owned tasks are updated in place instead of being deleted before replacement. Unrelated startup tasks are preserved.
- When setup cannot safely recover a task, the error identifies the task conflict instead of hiding it behind a generic dependency failure. Retry diagnostics refer to the current attempt.

Includes the earlier 4.5.3/4.5.4 Stop and read-failure recovery fixes. This hotfix does not change controller input, rumble or USB/IP transport behavior.

Validation: **4,958 automated tests passed**, zero failures, with 11 existing opt-in skips. Startup ownership/recovery, durable backup and setup-error regression checks also passed. Allocation assertions remain enabled.

#### Updating

Close DS4Windows before updating and back up your profiles. The installer and complete portable ZIP include the runtime, VIIPER **0.1.3-rc4.5**, Xbox emulation identity and offline dependency installers. No separate broker update is required.

Portable RC4.5.2 and newer can use the already-published [DS4Updater 2.0.6](https://github.com/hbashton/DS4Updater/releases/tag/v2.0.6). For an initial upgrade from RC4.5.1 or older, extract the complete ZIP into a new folder and copy your settings/profiles.

Release tag: **VIIPERRC4.5.5**. Windows file version: **5.0.5.5**. This is an **unsigned prerelease**.
