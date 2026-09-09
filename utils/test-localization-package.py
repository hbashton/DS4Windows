#!/usr/bin/env python3
"""Exercise real package composition and WiX harvesting without installing."""

import importlib.util
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
            for relative in REQUIRED + SATELLITES:
                path = publish / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(("fixture:" + relative).encode("utf-8"))
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
            for relative in REQUIRED + SATELLITES:
                self.assertEqual(("fixture:" + relative).encode("utf-8"), (package / relative).read_bytes())
                self.assertIn(relative, owned)

            archive = publish.parent / "DS4Windows_issue60-regression_x64.zip"
            with zipfile.ZipFile(archive) as packaged:
                for relative in REQUIRED + SATELLITES:
                    self.assertIn("DS4Windows/" + relative, packaged.namelist())

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
            for relative in REQUIRED + SATELLITES:
                self.assertIn(relative, paths)
                self.assertIn(relative, sources)

            # Validation examines real published paths, not only source text.
            (package / SATELLITES[0]).unlink()
            with self.assertRaisesRegex(SystemExit, "Missing standard-layout satellite"):
                VALIDATOR.validate_localization_package(package)

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
