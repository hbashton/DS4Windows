#!/usr/bin/env python3
"""Exercise real package composition and WiX harvesting without installing."""

import importlib.util
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


REPOSITORY = Path(__file__).resolve().parent.parent
REQUIRED = (
    "DS4Windows.exe", "coreclr.dll", "hostfxr.dll",
    "extras/install-viiper-backend.ps1", "extras/VIIPER-0.1.3-rc4.5-x64.exe",
    "extras/VIIPER-0.1.3-rc4.5-LICENSES.txt", "extras/VIIPER-0.1.3-rc4.5-PROVENANCE.txt",
    "extras/VIIPER-0.1.3-rc4.5-BUILD-NOTES.txt", "extras/LICENSE.txt",
    "extras/VIIPER-SYSTRAY-NOTICE.md", "extras/VIIPER-SYSTRAY-LICENSE.txt",
    "extras/USBip-0.9.7.7-x64.exe", "extras/HidHide_1.5.230_x64.exe",
    "extras/FakerInput_0.1.0_x64.msi",
)
SATELLITES = (
    "de/DS4Windows.resources.dll", "pt-BR/DS4Windows.resources.dll",
    "de/Microsoft.Win32.TaskScheduler.resources.dll",
)
PORTABLE_ONLY = ("viiper.exe", "viiper.exe.sha256", "DS4Windows.portable")
PORTABLE_MARKER = b"DS4Windows portable package v1\n"
VALIDATOR_SPEC = importlib.util.spec_from_file_location(
    "localization_package_validator", REPOSITORY / "utils" / "validate-installer.py"
)
VALIDATOR = importlib.util.module_from_spec(VALIDATOR_SPEC)
VALIDATOR_SPEC.loader.exec_module(VALIDATOR)


class LocalizationPackageTests(unittest.TestCase):
    def test_actual_composition_preserves_standard_satellites_and_manifests(self):
        with tempfile.TemporaryDirectory(prefix="ds4w-localization-package-") as temporary:
            root = Path(temporary)
            publish = root / "x64" / "Release" / "output"
            publish.mkdir(parents=True)
            # Only the root updater manifest is synthesized. A same-named
            # nested resource remains an ordinary, shared packaged file.
            fixture_paths = REQUIRED + SATELLITES + ("Resources/.ds4windows-managed-files.txt",)
            contents = {
                relative: ("fixture:" + relative).encode("utf-8")
                for relative in fixture_paths
            }
            broker_name = "VIIPER-0.1.3-rc4.5-x64.exe"
            expected_hash = hashlib.sha256(contents["extras/" + broker_name]).hexdigest()
            provenance_name = "extras/VIIPER-0.1.3-rc4.5-PROVENANCE.txt"
            contents[provenance_name] = (
                f"Binary: {broker_name}\nBinary SHA-256: {expected_hash.upper()}\n"
                "Source commit: 0123456789abcdef0123456789abcdef01234567\n"
            ).encode("utf-8")
            for relative in fixture_paths:
                path = publish / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(contents[relative])
            deps_text = json.dumps({
                "runtimeTarget": {"name": "test"},
                "targets": {"test": {
                    "DS4Windows/1.0.0": {"resources": {
                        name: {"locale": name.split("/")[0]}
                        for name in SATELLITES if name.endswith("/DS4Windows.resources.dll")
                    }},
                    "TaskScheduler/2.10.1": {"resources": {
                        "lib/net6.0-windows7.0/de/Microsoft.Win32.TaskScheduler.resources.dll": {"locale": "de"}
                    }},
                }},
                "libraries": {"DS4Windows/1.0.0": {"type": "project"}},
            })
            (publish / "DS4Windows.deps.json").write_text(deps_text, encoding="utf-8")
            (publish / "DS4Windows.runtimeconfig.json").write_text(
                json.dumps({"runtimeOptions": {}}), encoding="utf-8"
            )
            subprocess.run([
                sys.executable, str(REPOSITORY / "utils" / "post-build.py"),
                str(publish), str(REPOSITORY), "issue60-regression",
            ], cwd=root, check=True, capture_output=True, text=True)
            package = publish.parent / "DS4Windows"
            self.assertFalse((package / "Lang").exists())
            self.assertEqual(deps_text, (package / "DS4Windows.deps.json").read_text(encoding="utf-8"))
            VALIDATOR.validate_localization_package(package)
            owned = set((package / ".ds4windows-managed-files.txt").read_text(encoding="utf-8").splitlines())
            for relative in fixture_paths:
                self.assertEqual(contents[relative], (package / relative).read_bytes())
                self.assertIn(relative, owned)

            archive = publish.parent / "DS4Windows_issue60-regression_x64.zip"
            with zipfile.ZipFile(archive) as packaged:
                for relative in fixture_paths:
                    self.assertIn("DS4Windows/" + relative, packaged.namelist())
                    self.assertEqual(contents[relative], packaged.read("DS4Windows/" + relative))
                broker = packaged.read("DS4Windows/extras/VIIPER-0.1.3-rc4.5-x64.exe")
                self.assertEqual(broker, packaged.read("DS4Windows/viiper.exe"))
                broker_hash = hashlib.sha256(broker).hexdigest()
                self.assertEqual(expected_hash, broker_hash)
                self.assertEqual(contents[provenance_name], packaged.read("DS4Windows/" + provenance_name))
                self.assertIn(
                    f"Binary SHA-256: {broker_hash.upper()}\n".encode("utf-8"),
                    packaged.read("DS4Windows/" + provenance_name),
                )
                self.assertEqual(
                    f"{broker_hash} *viiper.exe\n".encode("ascii"),
                    packaged.read("DS4Windows/viiper.exe.sha256"),
                )
                self.assertEqual(
                    f"{broker_hash} *VIIPER-0.1.3-rc4.5-x64.exe\n".encode("ascii"),
                    packaged.read("DS4Windows/extras/VIIPER-0.1.3-rc4.5-x64.exe.sha256"),
                )
                self.assertEqual(PORTABLE_MARKER, packaged.read("DS4Windows/DS4Windows.portable"))
                zip_owned = set(packaged.read("DS4Windows/.ds4windows-managed-files.txt").decode("utf-8").splitlines())
                self.assertEqual(owned | set(PORTABLE_ONLY), zip_owned)
                names = packaged.namelist()
                self.assertEqual(len(names), len({name.casefold() for name in names}))
                for relative in PORTABLE_ONLY:
                    self.assertFalse((package / relative).exists())
                    self.assertNotIn(relative, owned)

            wix = root / "fixture.wxs"
            manifest = root / "fixture-manifest.json"
            subprocess.run([
                sys.executable, str(REPOSITORY / "utils" / "generate-installer-files.py"),
                str(package), str(wix), str(manifest), "--version", "issue60-regression",
            ], cwd=root, check=True, capture_output=True, text=True)
            paths = {entry["path"] for entry in json.loads(manifest.read_text(encoding="utf-8"))["files"]}
            sources = {
                entry.attrib["Source"].removeprefix("$(var.PublishRoot)\\").replace("\\", "/")
                for entry in ET.parse(wix).getroot().iter("{http://wixtoolset.org/schemas/v4/wxs}File")
            }
            for relative in fixture_paths:
                self.assertIn(relative, paths)
                self.assertIn(relative, sources)
            for relative in PORTABLE_ONLY:
                self.assertNotIn(relative, paths)
                self.assertNotIn(relative, sources)

            # Validation examines real published paths, not only source text.
            (package / SATELLITES[0]).unlink()
            with self.assertRaisesRegex(SystemExit, "Missing standard-layout satellite"):
                VALIDATOR.validate_localization_package(package)

    def assert_rejected_publish_preserves_existing_output(self, collision, *, directory=False, reparse=False):
        with tempfile.TemporaryDirectory(prefix="ds4w-portable-collision-") as temporary:
            root = Path(temporary)
            publish = root / "x64" / "Release" / "output"
            publish.mkdir(parents=True)
            for relative in REQUIRED:
                path = publish / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(("fixture:" + relative).encode("utf-8"))
            previous = publish.parent / "DS4Windows"
            previous.mkdir()
            (previous / "preserve.txt").write_bytes(b"previous published output")
            archive = publish.parent / "DS4Windows_portable-regression_x64.zip"
            archive.write_bytes(b"previous archive")
            marker = publish / "DS4Windows.release"
            marker.write_bytes(b"original marker")
            owned = publish / ".ds4windows-managed-files.txt"
            owned.write_bytes(b"original manifest")
            path = publish / collision
            if reparse:
                external = root / "outside"
                external.mkdir()
                (external / "sentinel.txt").write_bytes(b"untouched external data")
                try:
                    path.symlink_to(external, target_is_directory=True)
                except OSError as error:
                    self.skipTest(f"Creating fixture symlink unavailable: {error}")
            elif directory:
                path.mkdir()
                (path / "nested.txt").write_bytes(b"collision")
            else:
                path.write_bytes(b"collision")
            result = subprocess.run([
                sys.executable, str(REPOSITORY / "utils" / "post-build.py"),
                str(publish), str(REPOSITORY), "portable-regression",
            ], cwd=root, capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("reparse point" if reparse else "reserved portable", result.stderr)
            self.assertEqual(b"previous archive", archive.read_bytes())
            self.assertEqual(b"previous published output", (previous / "preserve.txt").read_bytes())
            self.assertEqual(b"original marker", marker.read_bytes())
            self.assertEqual(b"original manifest", owned.read_bytes())
            if reparse:
                self.assertEqual(b"untouched external data", (external / "sentinel.txt").read_bytes())

    def test_portable_alias_collision_is_rejected_before_overwrite(self):
        self.assert_rejected_publish_preserves_existing_output("viiper.exe")

    def test_portable_alias_case_collision_is_rejected_before_overwrite(self):
        self.assert_rejected_publish_preserves_existing_output("VIIPER.EXE")

    def test_portable_sidecar_case_collision_is_rejected_before_overwrite(self):
        self.assert_rejected_publish_preserves_existing_output("Viiper.Exe.SHA256")

    def test_portable_marker_case_collision_is_rejected_before_overwrite(self):
        self.assert_rejected_publish_preserves_existing_output("ds4WINDOWS.portable")

    def test_portable_reserved_directory_is_rejected_before_overwrite(self):
        self.assert_rejected_publish_preserves_existing_output("viiper.exe", directory=True)

    def test_reparse_tree_is_rejected_before_overwrite(self):
        self.assert_rejected_publish_preserves_existing_output("linked", reparse=True)

    def test_runtime_configuration_has_no_working_directory_probing(self):
        configuration = json.loads((REPOSITORY / "DS4Windows" / "runtimeconfig.template.json").read_text(encoding="utf-8"))
        self.assertFalse(configuration.get("additionalProbingPaths"))

    def test_validator_rejects_working_directory_probing_before_loading_metadata(self):
        with tempfile.TemporaryDirectory(prefix="ds4w-localization-validator-") as temporary:
            package = Path(temporary)
            (package / "DS4Windows.runtimeconfig.json").write_text(
                json.dumps({"runtimeOptions": {"additionalProbingPaths": ["./Lang/"]}}),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(SystemExit, "must not depend on additional probing"):
                VALIDATOR.validate_localization_package(package)

    def test_validator_rejects_missing_resource_metadata(self):
        with tempfile.TemporaryDirectory(prefix="ds4w-localization-validator-") as temporary:
            package = Path(temporary)
            (package / "DS4Windows.runtimeconfig.json").write_text(
                json.dumps({"runtimeOptions": {}}), encoding="utf-8"
            )
            (package / "DS4Windows.deps.json").write_text(
                json.dumps({"targets": {}}), encoding="utf-8"
            )
            with self.assertRaisesRegex(SystemExit, "omits application or TaskScheduler"):
                VALIDATOR.validate_localization_package(package)


if __name__ == "__main__":
    unittest.main()
