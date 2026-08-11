import os
from pathlib import Path
import sys
import shutil
import subprocess
import hashlib
import re
import stat

from viiper_native_bundle import (
    BUNDLE_DIRECTORY_NAME,
    LOCK_FILE_NAME,
    RUNTIME_PATHS,
    remove_legacy_publish_payload,
    stage_bundle,
    validate_bundle,
)


def is_reparse_point(path: Path) -> bool:
    attributes = getattr(path.lstat(), "st_file_attributes", 0)
    return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))


if len(sys.argv) not in (4, 5) or (
    len(sys.argv) == 5 and sys.argv[4] != "--require-native-bundle"
):
    raise SystemExit(
        "Usage: post-build.py <publish-dir> <project-dir> <version> "
        "[--require-native-bundle]"
    )

target_dir = Path(sys.argv[1]).absolute()
project_dir = Path(sys.argv[2]).absolute()
version = sys.argv[3].strip()
require_native_bundle = len(sys.argv) == 5
if not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z._-]{0,79}", version):
    raise SystemExit(
        "Version must be a filename-safe release identifier using only "
        "letters, numbers, periods, underscores, and hyphens."
    )
if not target_dir.is_dir() or is_reparse_point(target_dir):
    raise SystemExit(f"Publish directory is missing or unsafe: {target_dir}")


if require_native_bundle:
    source_bundle = project_dir / "extras" / BUNDLE_DIRECTORY_NAME
    source_lock = project_dir / "extras" / LOCK_FILE_NAME
    native_contract = (
        project_dir
        / "DS4Windows"
        / "DS4Control"
        / "Viiper"
        / "ViiperNativePackageContract.cs"
    )
    validate_bundle(source_bundle, source_lock, native_contract)
    powershell = (
        Path(os.environ["SystemRoot"])
        / "System32"
        / "WindowsPowerShell"
        / "v1.0"
        / "powershell.exe"
    )
    subprocess.run(
        [
            str(powershell),
            "-NoLogo",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(project_dir / "utils" / "validate-viiper-native-bundle-signatures.ps1"),
            "-BundleRoot",
            str(source_bundle),
            "-LockPath",
            str(source_lock),
            "-ContractPath",
            str(native_contract),
        ],
        check=True,
    )
    staged_bundle, staged_lock = stage_bundle(
        source_bundle,
        source_lock,
        target_dir / "extras",
        native_contract,
    )
    validate_bundle(staged_bundle, staged_lock, native_contract)
    remove_legacy_publish_payload(target_dir)


# A published DS4Windows build is an offline installer. Fail package
# composition if any required runtime or installer payload is absent instead
# of producing an archive that later needs a network recovery path.
required_offline_files = (
    "DS4Windows.exe",
    "coreclr.dll",
    "hostfxr.dll",
    "extras/HidHide_1.5.230_x64.exe",
    "extras/FakerInput_0.1.0_x64.msi",
)
if require_native_bundle:
    required_offline_files += tuple(
        f"extras/{BUNDLE_DIRECTORY_NAME}/{relative}" for relative in RUNTIME_PATHS
    ) + (f"extras/{LOCK_FILE_NAME}",)
else:
    required_offline_files += (
        "extras/install-viiper-backend.ps1",
        "extras/VIIPER-0.1.0-x64.exe",
        "extras/USBip-0.9.7.7-x64.exe",
    )
missing_offline_files = [
    relative_path
    for relative_path in required_offline_files
    if not (target_dir / relative_path).is_file()
]
if missing_offline_files:
    missing = ", ".join(missing_offline_files)
    raise FileNotFoundError(
        f"Cannot compose the offline DS4Windows package; missing: {missing}"
    )


# Bind setup to the exact VIIPER executable copied by this publish. This
# sidecar is regenerated for every artifact, so no hand-maintained hash can
# drift when the bundled executable changes.
if not require_native_bundle:
    viiper_name = "VIIPER-0.1.0-x64.exe"
    viiper_path = target_dir / "extras" / viiper_name
    viiper_hasher = hashlib.sha256()
    with viiper_path.open("rb") as viiper_stream:
        for chunk in iter(lambda: viiper_stream.read(1024 * 1024), b""):
            viiper_hasher.update(chunk)
    viiper_hash_path = viiper_path.with_name(viiper_name + ".sha256")
    viiper_hash_path.write_text(
        f"{viiper_hasher.hexdigest()} *{viiper_name}\n",
        encoding="ascii",
    )

# move l18n assemblies to a separate directory
lang_dir = target_dir / "Lang"
if not lang_dir.exists():
    Path.mkdir(lang_dir)

langs = ["ar", "cs", "de", "el", "es", "fi", "fr", "he", "hu-HU", "idn", "it", "ja", "ms",
         "nl", "pl", "pt", "pt-BR", "ru", "se", "tr", "uk-UA", "vi", "zh-Hans", "zh-Hant", "zh-CN"]
for lang in langs:
    current_lang_dir = target_dir / lang
    target_lang_dir = lang_dir / lang
    if not target_lang_dir.exists():
        target_lang_dir.mkdir()

    if current_lang_dir.exists():
        for file in current_lang_dir.iterdir():
            if file.is_file():
                shutil.move(file, target_lang_dir / file.name)
        current_lang_dir.rmdir()


# Resolve companion tooling from this script, not from the caller's checkout
# layout. CI passes the repository root as project_dir; the historical parent
# lookup escaped that checkout and failed only on a clean runner.
lang_script = Path(__file__).resolve().with_name("inject_deps_path.py")
if not lang_script.is_file():
    raise FileNotFoundError(f"Dependency-path helper is missing: {lang_script}")
deps_json_path = target_dir / "DS4Windows.deps.json"
subprocess.run([sys.executable, str(lang_script), str(deps_json_path)], check=True)

# Preserve the exact GitHub release channel in both portable and managed
# packages. The numeric Windows file version cannot distinguish an RC from a
# stable build, so Settings and the updater use this marker to include the
# installed prerelease notes without exposing prereleases to stable users.
release_marker = target_dir / "DS4Windows.release"
release_marker.write_text(version.strip() + "\n", encoding="utf-8")

# Record every file owned by this package. DS4Updater uses this manifest on the
# next update to remove package files that no longer ship, without touching
# profiles, settings, plugins, or other user-created content.
manifest_name = ".ds4windows-managed-files.txt"
manifest_path = target_dir / manifest_name
package_entries = list(target_dir.rglob("*"))
reparse_entry = next(
    (entry for entry in package_entries if is_reparse_point(entry)), None
)
if reparse_entry is not None:
    raise SystemExit(
        "Published package contains a reparse point: "
        + reparse_entry.relative_to(target_dir).as_posix()
    )
managed_files = sorted(
    file.relative_to(target_dir).as_posix()
    for file in package_entries
    if file.is_file() and file.name != manifest_name
)
if len({path.casefold() for path in managed_files}) != len(managed_files):
    raise SystemExit("Published package contains case-insensitive duplicate paths.")
manifest_path.write_text("\n".join(managed_files) + "\n", encoding="utf-8")


# rename target dir (net8.0-windows) to DS4Windows
renamed_dir = target_dir.parent / "DS4Windows"
if renamed_dir.exists():
    if is_reparse_point(renamed_dir):
        raise SystemExit(f"Refusing to replace reparse-point output: {renamed_dir}")
    prior_entries = list(renamed_dir.rglob("*"))
    prior_reparse = next(
        (entry for entry in prior_entries if is_reparse_point(entry)), None
    )
    if prior_reparse is not None:
        raise SystemExit(
            "Refusing to replace output containing a reparse point: "
            + str(prior_reparse)
        )
    shutil.rmtree(renamed_dir)

os.rename(target_dir, renamed_dir)

# create a zip
arch = target_dir.parents[1].name
zip_name = f"DS4Windows_{version}_{arch}"
target_zip_path = target_dir.parent / f"{zip_name}.zip"
if target_zip_path.exists():
    os.remove(target_zip_path)

# Archive only the newly composed DS4Windows directory. Using the whole
# Release directory could recursively include an older ZIP from a prior local
# build and silently double the artifact size.
zip_dir = shutil.make_archive(
    zip_name,
    "zip",
    root_dir=renamed_dir.parent,
    base_dir=renamed_dir.name,
)

# move the zip to the build directory
shutil.move(zip_dir, target_zip_path)
