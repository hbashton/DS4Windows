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


class ReleaseWorkflowValidationTests(unittest.TestCase):
    @staticmethod
    def workflow():
        return (REPOSITORY / ".github/workflows/release.yml").read_text(encoding="utf-8")

    def assert_policy_mutations_rejected(self, replacements):
        source = self.workflow()
        for original, replacement in replacements:
            with self.subTest(contract=original):
                self.assertIn(original, source)
                mutated = source.replace(original, replacement)
                self.assertNotEqual(source, mutated)
                with self.assertRaises(SystemExit):
                    VALIDATOR.validate_release_workflow(mutated)

    def test_actual_verified_release_policy_passes_installer_validation(self):
        VALIDATOR.validate_release_workflow(self.workflow())

    def test_installer_uses_the_validated_policy_entrypoint(self):
        source = (REPOSITORY / "utils/validate-installer.py").read_text(encoding="utf-8")
        self.assertIn("    validate_release_workflow(release_workflow)", source[source.index("def main() -> int:"):])

    def test_release_identity_cannot_bypass_the_verified_job(self):
        self.assert_policy_mutations_rejected([
            ('RELEASE_TAG: ${{ needs.identity.outputs.tag }}', 'RELEASE_TAG: ${{ github.event.release.tag_name }}'),
            ('UNSIGNED_RC_RELEASE: ${{ needs.identity.outputs.unsigned_rc }}', 'UNSIGNED_RC_RELEASE: true'),
            ('$release.tag_name -cne $tag -or $release.id -le 0', '$false'),
            ('$dispatch -and -not $release.draft', '$false'),
        ])

    def test_unsigned_exception_cannot_expand_to_stable_or_unknown_tags(self):
        self.assert_policy_mutations_rejected([
            ("$unsignedRc = $isPrerelease -ceq 'true' -and", '$unsignedRc = $true -and'),
            ("$tag -cmatch '^VIIPERRC[0-9]+(\\.[0-9]+){0,3}\\z'", "$tag -match '^VIIPER'"),
            ('if ($dispatch -and -not $unsignedRc)', 'if ($false)'),
            ("RequireSigning = $env:UNSIGNED_RC_RELEASE -ne 'true'", 'RequireSigning = $false'),
        ])

    def test_signed_releases_keep_all_certificate_gates(self):
        self.assert_policy_mutations_rejected([
            ("if: env.UNSIGNED_RC_RELEASE != 'true'", 'if: false'),
            ('DS4W_SIGN_CERT_PASSWORD: ${{ secrets.DS4W_SIGN_CERT_PASSWORD }}', 'DS4W_SIGN_CERT_PASSWORD: ignored'),
            ("'^[0-9A-Fa-f]{40}$'", "'.*'"),
            ('$signature.Status -ne "Valid"', '$false'),
            ('$signature.SignerCertificate.Thumbprint -ne $approvedThumbprint', '$false'),
            ('-not $signature.TimeStamperCertificate', '$false'),
        ])

    def test_actual_asset_and_corresponding_source_checks_are_required(self):
        self.assert_policy_mutations_rejected([
            ('if ($sourceCommit -cne $tagCommit)', 'if ($false)'),
            ('$brokerTagCommit.Trim() -cne $brokerCommit', '$false'),
            ('Hash -cne $brokerSourceHash', 'Hash -cne $ignored'),
            ('Hash -cne $brokerHash', 'Hash -cne $ignored'),
            ('Hash -cne $expected[$name]', 'Hash -cne $ignored'),
            ('Hash -cne $env:RELEASE_RECEIPT_SHA256', 'Hash -cne $ignored'),
            ('Hash -cne $record.sha256', 'Hash -cne $ignored'),
        ])

    def test_upload_cannot_precede_hash_checks_or_overwrite_assets(self):
        command = '        gh release upload $env:RELEASE_TAG @assets --repo $env:GITHUB_REPOSITORY\n'
        source = self.workflow()
        self.assertIn(command, source)
        source = source.replace(command, '').replace('        $existing = @($release.assets.name)', command + '        $existing = @($release.assets.name)')
        with self.assertRaisesRegex(SystemExit, 'before upload'):
            VALIDATOR.validate_release_workflow(source)
        self.assert_policy_mutations_rejected([
            ('Refusing to overwrite existing release asset:', 'Overwrite existing asset:'),
            ('gh release upload $env:RELEASE_TAG @assets', 'gh release upload $env:RELEASE_TAG @assets --clobber'),
        ])

    def test_published_receipt_requires_successful_exact_run_and_bytes(self):
        self.assert_policy_mutations_rejected([
            ('$receipt.sourceCommit -cne $tagCommit', '$false'),
            ("$run.event -cne 'workflow_dispatch'", '$false'),
            ("$run.conclusion -cne 'success'", '$false'),
            ('$run.head_sha -cne $tagCommit', '$false'),
            ("$published[0].uploader.login -cne 'github-actions[bot]'", '$false'),
            ('Hash -cne $asset.sha256', 'Hash -cne $ignored'),
        ])

    def test_published_verification_cannot_rebuild_or_read_release_body_policy(self):
        for injected in ('dotnet publish', 'gh release upload', '${{ github.event.release.body }}'):
            with self.subTest(injected=injected), self.assertRaises(SystemExit):
                VALIDATOR.validate_release_workflow(self.workflow() + '\n        ' + injected + '\n')


if __name__ == "__main__":
    unittest.main()
