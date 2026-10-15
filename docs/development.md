# Development

[Documentation index](README.md) · [Contributing](../contributing.md)

## Build on Windows

Use Windows x64, Git and the .NET 8 SDK. Python 3.10 or newer is used by the
packaging scripts. The current VIIPER distribution targets Windows x64 even
though legacy x86 project configurations remain in the solution.

From the repository root:

```powershell
dotnet restore .\DS4WindowsWPF.sln
dotnet build .\DS4Windows\DS4WinWPF.csproj -c Release -p:Platform=x64
dotnet test .\DS4WindowsTests\DS4WindowsTests.csproj -c Release -p:Platform=x64
```

The WPF application project is `DS4Windows/DS4WinWPF.csproj`. Open
`DS4WindowsWPF.sln` for Visual Studio development. Use a compatible Windows SDK
for the project's Windows target. Building does not install drivers or establish
hardware compatibility; use a supported matching package for controller testing.

## Publish and package

```powershell
$packageVersion = "VIIPERRC4.6.6" # Match the intended InformationalVersion
dotnet publish .\DS4Windows\DS4WinWPF.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:InformationalVersion=$packageVersion -o .\bin\x64\Release\output
python .\utils\post-build.py .\bin\x64\Release\output . $packageVersion
```

The final argument is the package version; choose the intended version for your
build. The script requires the complete offline payload, adds the portable
backend and marker, and places satellite assemblies under `Lang/<culture>`.
Do not distribute a partial build directory or manually repack selected files.
See [Translations](dev/translations.md) for resource-loading checks.

Installer authoring and validation are documented in [installer/README.md](../installer/README.md).
Official releases use the [release workflow](../.github/workflows/release.yml), not a local
development ZIP. App/file and installer versions advance together for each release.

## Code and evidence

| Location | Contents |
| --- | --- |
| `DS4Windows/DS4Forms` | WPF views and handlers |
| `DS4Windows/DS4Control` | Controller, mapping and settings services |
| `DS4WindowsTests` | Automated regression tests |
| `utils` | Packaging, validation and diagnostic tools |
| `installer` | Installer projects and scripts |
| `docs/protocols` | Runtime and transport contracts |
| `docs/validation` | Dated investigations and test records |

CI runs the full x64 suite and packaging/installer checks. Run focused tests
while developing, then the checks relevant to your change before proposing it.
Document opt-in hardware tests and skipped checks explicitly. Avoid rewriting
older evidence to make a newer result look like it was tested historically.

## Documentation changes

Keep README concise, user instructions in `docs`, and release history in
CHANGELOG.md. Update links after moves and remove redundant pointer files.
Preserve substantive historical records, third-party notices and contributor attribution.
