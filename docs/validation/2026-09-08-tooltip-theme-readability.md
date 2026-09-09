# Tooltip theme readability, 2026-09-08

Status: the source-level theme override defects are fixed, with isolated WPF resource/rendering regressions passing. No running application or controller was touched.

## Cause and scope

The Switch 2 settings' local implicit `ToolTip` style supplied wrapping and width but did not extend the application's named tooltip style. This replaced the dark theme's tooltip setters and left default OS chrome with independently themed child text. The light theme did not define either tooltip style alias. Light theme's general `ForegroundColor` uses caption text, which is not the correct guaranteed pair for its window-colored tooltip surface.

Both theme dictionaries now import `ToolTipStyles.xaml`. It retains `ToolTipStyle` and the implicit `ToolTip` alias, and supplies a small explicit border/content template using dynamic foreground, background, and border resources. Dark tooltip colors match the dark raised surface and normal text. Light tooltip colors pair Windows window text with the window background. The local Switch 2 wrapping style now extends `ToolTipStyle`, preserving its existing 320-pixel maximum width and wrapped content template. Theme changes can update existing tooltip resources rather than leaving static old colors.

Tooltip wording, settings layout, profile persistence, and controller behavior are unchanged. A source scan found no explicit tooltip child foreground/background literals that required replacement; similarly named owner-control colors are not tooltip content colors and were left alone.

## Evidence

`ToolTipThemeTests` loads the actual compiled theme dictionaries and the exact local wrapping style from `ProfileEditor.xaml`. Its controls are laid out on an isolated STA without creating `Application`, `Window`, opening a popup, or launching DS4Windows.

- `tooltip-theme-before.trx`: **5 failed, 1 passed, 6 total** against the old resources. This recorded missing light aliases, default rather than theme-owned chrome, and the local override dropping its base style. An initial test-only pack-URI initialization issue was corrected before this recorded baseline.
- `tooltip-theme-after.trx`: **6 passed, 0 failed** with the production fix.

Coverage verifies named/implicit aliases in both themes, the real border and realized text foreground, normal-text contrast of at least 4.5:1, preservation of local width/wrapping, dynamic dark-to-light palette changes, and explicit tooltip content not hardcoding literal colors. No screenshot of the running application or live hover behavior is claimed.

Command: `dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ToolTipThemeTests" --logger "trx;LogFileName=tooltip-theme-after.trx" --verbosity minimal` (the baseline used the corresponding `before` filename). Existing build warnings were not suppressed.
