# Isolated satellite-resource regression probe

This child process reads actual DS4Windows UI resources and TaskScheduler
satellites. It never invokes DS4Windows' entry point, creates an application or
controller service, or contacts hardware. No running installation is modified.

```powershell
dotnet build utils/SatelliteResourceProbe/SatelliteResourceProbe.csproj -c Release
./utils/SatelliteResourceProbe/Run-Probe.ps1 -SourceBuild ./DS4Windows/bin/x64/Release/net8.0-windows10.0.19041.0
./utils/SatelliteResourceProbe/Run-Probe.ps1 -SourceBuild ./DS4Windows/bin/x64/Release/net8.0-windows10.0.19041.0 -StandardLayout
python utils/test-localization-package.py
```

The first run reconstructs the old `Lang/<culture>` package using the actual
published dependency/resource identities, and verifies that launching outside
the package loses German translations. The second keeps the standard
`<culture>` folders and verifies both launch directories succeed without
additional probing. Each child also checks parent-culture fallback, neutral
fallback for an unavailable language, and rejection of an unrelated assembly.
The fixture copies are retained under `artifacts/issue60-*` for inspection.

The Python regression separately runs the real `post-build.py` and WiX file
generator against an isolated synthetic payload. It checks satellite bytes,
unchanged dependency metadata, ZIP contents, the updater-owned file list, the
installer manifest, and recursive WiX harvesting. Its dummy installer files
are test data only and are never installed or executed.
