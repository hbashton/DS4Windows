# Switch 2 playstyle UI and Pro profile crash — b83

## Confirmed failure

The b82 WPF dispatcher failure at 2026-09-06 14:41:21 UTC was a
DirectoryNotFoundException for the Switch 2 Pro controller image. The stack
went through CompositeDeviceModel.ControllerImageSource,
MainWindowsViewModel.SetEditingControllerContext and ShowProfileEditor.
The relative component URI was resolved as a filesystem path when the getter
was called directly, outside a XAML base-URI context.

ControllerArtwork now loads absolute application-pack URIs, eagerly decodes
and freezes images, and caches them. Both controller-list/profile context
and the profile diagram use the shared resource loader. The regression test
reads the real CompositeDeviceModel getter using a synthetic Bluetooth Pro
runtime and verifies decoded dimensions, freezing and reuse.

## User-facing changes

- Original scalable illustrations and task-based sections for desk mouse,
  motion aiming, two-hand aiming, feedback, second actions, sticks, layout
  and calibration. Existing Switch 2 setting bindings are retained.
- Actual gyro output and activation settings are exposed beside motion-aiming
  instructions. These edit the existing canonical fields, not a second mapper.
  Visiting the page and changing output do not overwrite custom triggers.
- An always-visible **Aim with both Joy-Cons (DJG)** checkbox controls the
  existing dual-gyro feature. Its detailed modes have plain descriptions.
  Enabling DJG does not pair devices or turn on gyro mouse.
- Second actions have a three-step explanation, a control picker, direct
  Shift Modifier editing and a shortcut to Button Mapping. Existing shift
  triggers are retained. Closing an untouched new-action shortcut does not
  create an empty Mode Shift binding. The dialog is centered on its owner.
- Automatic second-action options describe when they apply, including the
  hold/toggle reversal while auto-application is active. They do not enable
  aiming.
- The mapper-admission cache now includes surface mouse and enabled mouse
  stick assist, even when a profile has no unrelated button or special actions.

## Verification

- Full final suite: **3,830 passed, zero failed, three opt-in audio tests
  skipped** (3,833 total), full-b83-verified.trx.
- Focused layout, playstyle, dual-gyro and Mode Shift checks passed. Tests
  cover custom activation preservation, shared mode updates, mapping
  admission/release, untouched shortcut close, and chosen-action commit.
- Portable stopped UI inspected at normal desktop size: feature imagery,
  desk/aim separation, live output/activation visibility, exposed DJG
  checkbox, mode guidance and second-action shortcut. The shortcut opened
  the canonical editor with Switch 2 Mode Shift selected for a new action.
- The UI pass identified the gyro navigation-header mismatch, then the
  untouched-shortcut commit edge; both were corrected before final publish.
  Final centering/no-op-close changes were compiled and covered by the final
  suite; the full UI walkthrough preceded these last two small changes.
- The two inspected saved profile files retained their pre-test SHA256 values.
  No installed files, pairing keys, startup tasks or driver configuration were
  changed. VIIPER is the unchanged b82 binary.

## Remaining hardware question

This does not establish that the reported physical Joy-Con desk-mouse failure
is resolved. The profile inspected already had a special action, so its mapper
was admitted even before the cache fix. It stored gyro output Controls rather
than Mouse; the old automatic-layer checkbox was easy to mistake for enabling
gyro mouse.

The pinned Switch2Connect controller.py startup explicitly selects/enables
motion, mouse and magnetometer with the 0x94 Bluetooth feature mask. DS4Windows'
Bluetooth startup feature activation still needs a targeted comparison and
real sensor-sample validation. Do not claim the admission fix alone explains
the user's failed desk-mouse attempt or that physical optical movement was
verified by these UI tests.

Published portable version: 5.0.4.83-Switch2-Playstyle-Preview.
