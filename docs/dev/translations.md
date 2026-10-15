# Translations and packaging

[Documentation index](../README.md) · [Development](../development.md)

Edit `DS4Windows/Translations/Strings.<culture>.resx` for UI translations;
`Strings.resx` supplies neutral English text. Use the existing resource keys and
preserve format placeholders. Check the translated text in the corresponding UI.

Developer builds can use the standard `<culture>` satellite layout. Release
packaging uses `utils/post-build.py` to collect resource assemblies into
`Lang/<culture>` alongside DS4Windows. Keep this entire folder in installer and
portable distributions, including dependency resources such as TaskScheduler's.

The app's resource resolver handles the packaged layout, parent-culture fallback
and neutral fallback. The former post-build GOTO edits and dependency-file
injection instructions are obsolete; do not patch `DS4Windows.deps.json` to make
translations load.

Run the [satellite resource probe](../../utils/SatelliteResourceProbe/README.md)
and packaging regression checks when changing translation loading or layout.
They check actual UI and dependency resources from both the package directory
and an unrelated working directory.
