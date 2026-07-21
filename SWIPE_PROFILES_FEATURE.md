# Swipe Profile Allow-List — Change Summary

This fork adds the ability to restrict which profiles the **two-finger touchpad
swipe** is allowed to switch between, instead of always cycling through every
profile.

## Behaviour

- Open **Settings → "Config Swipe Profiles"** (button next to the
  "Swipe touchpad to switch profiles" checkbox).
- Tick the profiles the swipe may switch between, then click **OK**.
- During use, a two-finger left/right swipe now only moves through the ticked
  profiles, skipping all others.
- If **nothing** is ticked, the swipe cycles through every profile — the
  original (legacy) behaviour. This keeps existing configs working unchanged.

## Files touched

- `DS4Windows/DS4Control/ScpUtil.cs`
  - `BackingStore.swipeProfileList` field and `Global.SwipeProfileList` accessor.
- `DS4Windows/DS4Control/DTOXml/AppSettingsDTO.cs`
  - `SwipeProfileList` XML element (`<SwipeProfileList><Profile>…</Profile></SwipeProfileList>`)
    wired into `MapFrom`/`MapTo`. Backward compatible (absent element = empty list).
- `DS4Windows/DS4Forms/MainWindow.xaml` / `.xaml.cs`
  - "Config Swipe Profiles" button + handler.
  - `ComputeSwipeProfileIndex(...)` replaces the raw index increment/decrement in
    `HotkeysTimer_Elapsed`, honouring the allow-list and falling back to the
    legacy cycle when empty.
- `DS4Windows/DS4Forms/SwipeProfilesEditor.xaml` / `.xaml.cs` (new)
  - Dialog listing all profiles as checkboxes, with Select All / Clear All.

## Build / CI

- The three local DLL references (`FakerInputWrapper`, `SharpOSC`,
  required runtime libraries are committed under `DS4Windows/libs/{x64,x86}/` and
  are explicitly un-ignored in `.gitignore`, so a fresh clone builds.
- `.github/workflows/ci-build.yml` (new) builds on every push / PR / manual
  dispatch: runs the test project on x64, then publishes and packages x64 and
  x86 builds (via the repo's existing `utils/post-build.py`) as downloadable
  artifacts. Tag pushes remain handled by the existing `release.yml`.
