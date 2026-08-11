#!/usr/bin/env python3
"""Synthetic positive and fail-closed tests for the native bundle contract."""

from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

import viiper_native_bundle as bundle


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
CONTRACT_PATH = (
    REPOSITORY_ROOT
    / "DS4Windows"
    / "DS4Control"
    / "Viiper"
    / "ViiperNativePackageContract.cs"
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class NativeBundleFixture:
    def __init__(self, parent: Path) -> None:
        self.root = parent / bundle.BUNDLE_DIRECTORY_NAME
        self.lock = parent / bundle.LOCK_FILE_NAME
        self.root.mkdir()
        (self.root / "driver").mkdir()
        contract = bundle.read_native_contract(CONTRACT_PATH)
        payloads = {
            "viiper.exe": b"MZ\x90\x00synthetic signed broker",
            "ViiperUdeCtl.exe": b"MZ\x90\x00synthetic signed helper",
            "driver/ViiperUde.sys": b"MZ\x90\x00synthetic Microsoft driver",
            "driver/ViiperUde.cat": b"synthetic Microsoft catalog",
        }
        for relative, contents in payloads.items():
            path = self.root / Path(*relative.split("/"))
            path.write_bytes(contents)
        inf = (
            "[Version]\r\n"
            'Signature="$WINDOWS NT$"\r\n'
            "Class=USBDevice\r\n"
            "CatalogFile=ViiperUde.cat\r\n"
            f"DriverVer=08/11/2026,{contract.driver_package_version}\r\n"
            "[ViiperUde_Install.NT.Wdf]\r\n"
            "KmdfLibraryVersion=1.27\r\n"
        )
        inf_path = self.root / "driver" / "ViiperUde.inf"
        inf_path.write_text(inf, encoding="ascii", newline="")
        submission_files = []
        for name in bundle.SUBMISSION_NAMES:
            if name == "ViiperUde.inf":
                length = inf_path.stat().st_size
                digest = sha256(inf_path).upper()
            else:
                synthetic = ("submission-" + name).encode("ascii")
                length = len(synthetic)
                digest = hashlib.sha256(synthetic).hexdigest().upper()
            submission_files.append(
                {"name": name, "length": length, "sha256": digest}
            )
        manifest = {
            "schema": 2,
            "purpose": "synthetic production evidence",
            "releaseEligible": True,
            "signingRoute": "HLK/WHCP",
            "requiredProductionRoute": "HLK/WHCP dashboard signing",
            "sourceRevision": contract.source_revision,
            "driverPackageVersion": contract.driver_package_version,
            "driverABIMajor": contract.abi_major,
            "driverABIMinor": contract.abi_minor,
            "driverCapabilities": f"0x{contract.capabilities:08x}",
            "driverBuildIdentity": contract.driver_build_identity,
            "cabinet": "ViiperUde-production.cab",
            "cabinetSha256": "C" * 64,
            "packageFolder": "ViiperUde",
            "files": submission_files,
        }
        self.write_manifest(manifest)
        self.archive = parent / bundle.RELEASE_ASSET_NAME
        self.write_archive()
        archive_hash = sha256(self.archive)
        bundle.write_lock(
            self.root,
            self.lock,
            CONTRACT_PATH,
            101,
            "v0.1.1-native-test",
            202,
            self.archive,
            "sha256:" + archive_hash,
            "b" * 64,
        )

    def write_archive(
        self,
        *,
        extra: tuple[str, bytes] | None = None,
        replace: tuple[str, bytes] | None = None,
        rename: tuple[str, str] | None = None,
        omit: str | None = None,
        symlink: str | None = None,
    ) -> None:
        with zipfile.ZipFile(
            self.archive, "w", compression=zipfile.ZIP_DEFLATED
        ) as archive:
            for archive_name, runtime_path in bundle.ARCHIVE_TO_RUNTIME:
                if archive_name == omit:
                    continue
                data = (
                    self.root / Path(*runtime_path.split("/"))
                ).read_bytes()
                if replace is not None and archive_name == replace[0]:
                    data = replace[1]
                target_name = (
                    rename[1]
                    if rename is not None and archive_name == rename[0]
                    else archive_name
                )
                if archive_name == symlink:
                    entry = zipfile.ZipInfo(target_name)
                    entry.create_system = 3
                    entry.external_attr = (0o120777 << 16)
                    archive.writestr(entry, b"viiper.exe")
                else:
                    archive.writestr(target_name, data)
            if extra is not None:
                archive.writestr(extra[0], extra[1])

    def read_manifest(self) -> dict:
        return json.loads(
            (self.root / "submission-manifest.json").read_text(encoding="utf-8")
        )

    def write_manifest(self, value: dict) -> None:
        (self.root / "submission-manifest.json").write_text(
            json.dumps(value, indent=2) + "\n", encoding="utf-8", newline="\n"
        )

    def read_lock(self) -> dict:
        return json.loads(self.lock.read_text(encoding="ascii"))

    def write_lock(self, value: dict) -> None:
        self.lock.write_text(
            json.dumps(value, indent=2) + "\n", encoding="ascii", newline="\n"
        )


class ViiperNativeBundleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.base = Path(self.temporary.name)
        self.fixture = NativeBundleFixture(self.base)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def assert_rejected(self) -> None:
        with self.assertRaises(bundle.BundleContractError):
            bundle.validate_bundle(
                self.fixture.root, self.fixture.lock, CONTRACT_PATH
            )

    def test_exact_fixture_is_accepted(self) -> None:
        value = bundle.validate_bundle(
            self.fixture.root, self.fixture.lock, CONTRACT_PATH
        )
        self.assertEqual(1, value["schema"])

    def test_exact_upstream_archive_is_accepted(self) -> None:
        bundle.validate_release_archive(
            self.fixture.archive,
            self.fixture.root,
            self.fixture.lock,
            CONTRACT_PATH,
        )

    def test_archive_with_extra_or_private_pdb_is_rejected(self) -> None:
        for name in ("extra.bin", "ViiperUde.pdb", "../escape.bin"):
            with self.subTest(name=name):
                self.fixture.write_archive(extra=(name, b"unexpected"))
                value = self.fixture.read_lock()
                digest = sha256(self.fixture.archive)
                value["provenance"]["releaseAssetSha256"] = digest
                value["provenance"]["releaseAssetApiDigest"] = "sha256:" + digest
                self.fixture.write_lock(value)
                self.assert_rejected_archive()

    def test_archive_member_byte_drift_is_rejected(self) -> None:
        original = (self.fixture.root / "viiper.exe").read_bytes()
        self.fixture.write_archive(
            replace=("viiper.exe", original[:-1] + b"!")
        )
        value = self.fixture.read_lock()
        digest = sha256(self.fixture.archive)
        value["provenance"]["releaseAssetSha256"] = digest
        value["provenance"]["releaseAssetApiDigest"] = "sha256:" + digest
        self.fixture.write_lock(value)
        self.assert_rejected_archive()

    def test_archive_case_drift_or_missing_member_is_rejected(self) -> None:
        mutations = (
            {"rename": ("ViiperUde.inf", "viiperude.inf")},
            {"omit": "ViiperUde.cat"},
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                self.fixture.write_archive(**mutation)
                value = self.fixture.read_lock()
                digest = sha256(self.fixture.archive)
                value["provenance"]["releaseAssetSha256"] = digest
                value["provenance"]["releaseAssetApiDigest"] = "sha256:" + digest
                self.fixture.write_lock(value)
                self.assert_rejected_archive()

    def test_archive_link_member_is_rejected_before_extraction(self) -> None:
        self.fixture.write_archive(symlink="viiper.exe")
        value = self.fixture.read_lock()
        digest = sha256(self.fixture.archive)
        value["provenance"]["releaseAssetSha256"] = digest
        value["provenance"]["releaseAssetApiDigest"] = "sha256:" + digest
        self.fixture.write_lock(value)
        self.assert_rejected_archive()

    def assert_rejected_archive(self) -> None:
        with self.assertRaises(bundle.BundleContractError):
            bundle.validate_release_archive(
                self.fixture.archive,
                self.fixture.root,
                self.fixture.lock,
                CONTRACT_PATH,
            )

    def test_contract_identity_is_independently_derived(self) -> None:
        contract = bundle.read_native_contract(CONTRACT_PATH)
        self.assertEqual(
            contract.driver_build_identity,
            bundle.derive_build_identity(contract),
        )

    def test_missing_runtime_file_is_rejected(self) -> None:
        (self.fixture.root / "driver" / "ViiperUde.cat").unlink()
        self.assert_rejected()

    def test_unexpected_runtime_file_is_rejected(self) -> None:
        (self.fixture.root / "driver" / "debug.pdb").write_bytes(b"private")
        self.assert_rejected()

    def test_case_drift_is_rejected(self) -> None:
        source = self.fixture.root / "ViiperUdeCtl.exe"
        source.rename(self.fixture.root / "viiperudectl.exe")
        self.assert_rejected()

    def test_empty_runtime_file_is_rejected(self) -> None:
        (self.fixture.root / "driver" / "ViiperUde.cat").write_bytes(b"")
        self.assert_rejected()

    def test_runtime_hash_drift_is_rejected(self) -> None:
        with (self.fixture.root / "viiper.exe").open("ab") as stream:
            stream.write(b"tamper")
        self.assert_rejected()

    def test_lock_must_be_exact_sibling(self) -> None:
        with self.assertRaises(bundle.BundleContractError):
            bundle.validate_bundle(
                self.fixture.root,
                self.base / "another.lock.json",
                CONTRACT_PATH,
            )

    def test_lock_duplicate_json_property_is_rejected(self) -> None:
        source = self.fixture.lock.read_text(encoding="ascii")
        self.fixture.lock.write_text(
            source.replace('"schema": 1,', '"schema": 1,\n  "schema": 1,'),
            encoding="ascii",
        )
        self.assert_rejected()

    def test_lock_unknown_property_is_rejected(self) -> None:
        value = self.fixture.read_lock()
        value["helpfulButUnreviewed"] = True
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_wrong_abi_is_rejected(self) -> None:
        value = self.fixture.read_lock()
        value["driverABIMinor"] += 1
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_wrong_loaded_identity_is_rejected(self) -> None:
        value = self.fixture.read_lock()
        value["driverBuildIdentity"] = "c" * 64
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_release_id_requires_positive_integer(self) -> None:
        for invalid in (0, -1, bundle.MAX_GITHUB_OBJECT_ID + 1, "101", True):
            with self.subTest(value=invalid):
                value = self.fixture.read_lock()
                value["provenance"]["releaseId"] = invalid
                self.fixture.write_lock(value)
                self.assert_rejected()

    def test_lock_release_asset_id_requires_positive_integer(self) -> None:
        value = self.fixture.read_lock()
        value["provenance"]["releaseAssetId"] = 0
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_noncanonical_release_tag_is_rejected(self) -> None:
        value = self.fixture.read_lock()
        value["provenance"]["releaseTag"] = "release/0.1.1"
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_github_api_digest_drift_is_rejected(self) -> None:
        value = self.fixture.read_lock()
        value["provenance"]["releaseAssetApiDigest"] = "sha256:" + "c" * 64
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_file_order_is_deterministic(self) -> None:
        value = self.fixture.read_lock()
        value["files"] = list(reversed(value["files"]))
        self.fixture.write_lock(value)
        self.assert_rejected()

    def test_lock_nested_schemas_are_closed(self) -> None:
        original = self.fixture.read_lock()
        mutations = (
            ("provenance", lambda value: value["provenance"].update({"url": "x"})),
            ("signing", lambda value: value["signing"].update({"thumbprint": "x"})),
            ("file", lambda value: value["files"][0].update({"source": "x"})),
        )
        for target, mutate in mutations:
            with self.subTest(target=target):
                value = copy.deepcopy(original)
                mutate(value)
                self.fixture.write_lock(value)
                self.assert_rejected()

    def test_controlled_test_manifest_is_rejected(self) -> None:
        value = self.fixture.read_manifest()
        value["releaseEligible"] = False
        value["signingRoute"] = "ControlledTestAttestation"
        self.fixture.write_manifest(value)
        self.assert_rejected()

    def test_manifest_source_revision_drift_is_rejected(self) -> None:
        value = self.fixture.read_manifest()
        value["sourceRevision"] = "0" * 40
        self.fixture.write_manifest(value)
        self.assert_rejected()

    def test_manifest_duplicate_file_is_rejected(self) -> None:
        value = self.fixture.read_manifest()
        value["files"][-1] = copy.deepcopy(value["files"][0])
        self.fixture.write_manifest(value)
        self.assert_rejected()

    def test_manifest_file_order_is_deterministic(self) -> None:
        value = self.fixture.read_manifest()
        value["files"] = list(reversed(value["files"]))
        self.fixture.write_manifest(value)
        self.assert_rejected()

    def test_manifest_hashes_use_upstream_canonical_case(self) -> None:
        original = self.fixture.read_manifest()
        for target in ("cabinet", "file"):
            with self.subTest(target=target):
                value = copy.deepcopy(original)
                if target == "cabinet":
                    value["cabinetSha256"] = value["cabinetSha256"].lower()
                else:
                    value["files"][0]["sha256"] = value["files"][0]["sha256"].lower()
                self.fixture.write_manifest(value)
                self.assert_rejected()

    def test_manifest_purpose_is_canonical(self) -> None:
        original = self.fixture.read_manifest()
        for purpose in ("", " production evidence ", "x" * 513, 7):
            with self.subTest(purpose=purpose):
                value = copy.deepcopy(original)
                value["purpose"] = purpose
                self.fixture.write_manifest(value)
                self.assert_rejected()

    def test_manifest_unknown_root_or_file_property_is_rejected(self) -> None:
        original = self.fixture.read_manifest()
        for target in ("root", "file"):
            with self.subTest(target=target):
                value = copy.deepcopy(original)
                if target == "root":
                    value["unreviewedPolicy"] = "accept-anything"
                else:
                    value["files"][0]["downloadUrl"] = "https://example.invalid"
                self.fixture.write_manifest(value)
                self.assert_rejected()

    def test_manifest_duplicate_json_property_is_rejected(self) -> None:
        path = self.fixture.root / "submission-manifest.json"
        source = path.read_text(encoding="utf-8")
        path.write_text(
            source.replace('"schema": 2,', '"schema": 2,\n  "schema": 2,'),
            encoding="utf-8",
        )
        self.assert_rejected()

    def test_runtime_inf_must_match_submission_evidence(self) -> None:
        path = self.fixture.root / "driver" / "ViiperUde.inf"
        path.write_text(
            path.read_text(encoding="ascii").replace("KMDF 1.27", "KMDF 1.27"),
            encoding="ascii",
        )
        with path.open("ab") as stream:
            stream.write(b";tamper")
        self.assert_rejected()

    def test_staging_copies_only_locked_bytes_and_revalidates(self) -> None:
        publish_extras = self.base / "publish" / "extras"
        destination_root, destination_lock = bundle.stage_bundle(
            self.fixture.root,
            self.fixture.lock,
            publish_extras,
            CONTRACT_PATH,
        )
        bundle.validate_bundle(destination_root, destination_lock, CONTRACT_PATH)
        self.assertEqual(
            sorted(bundle.RUNTIME_PATHS),
            sorted(
                path.relative_to(destination_root).as_posix()
                for path in destination_root.rglob("*")
                if path.is_file()
            ),
        )
        self.assertEqual(
            self.fixture.lock.read_bytes(), destination_lock.read_bytes()
        )

    def test_staging_replaces_stale_generated_bundle(self) -> None:
        publish_extras = self.base / "publish" / "extras"
        destination_root, destination_lock = bundle.stage_bundle(
            self.fixture.root,
            self.fixture.lock,
            publish_extras,
            CONTRACT_PATH,
        )
        (destination_root / "stale.bin").write_bytes(b"stale")
        destination_lock.write_text("stale", encoding="ascii")
        destination_root, destination_lock = bundle.stage_bundle(
            self.fixture.root,
            self.fixture.lock,
            publish_extras,
            CONTRACT_PATH,
        )
        bundle.validate_bundle(destination_root, destination_lock, CONTRACT_PATH)
        self.assertFalse((destination_root / "stale.bin").exists())

    def test_native_publish_removes_only_known_legacy_payload(self) -> None:
        publish = self.base / "publish-tree"
        (publish / "extras").mkdir(parents=True)
        keep = publish / "extras" / "keep.txt"
        keep.write_text("owned", encoding="ascii")
        for relative in bundle.LEGACY_PUBLISH_PATHS:
            path = publish / Path(*relative.split("/"))
            path.write_bytes(b"legacy")
        bundle.remove_legacy_publish_payload(publish)
        bundle.assert_no_legacy_publish_payload(publish)
        self.assertEqual("owned", keep.read_text(encoding="ascii"))

    def test_native_publish_rejects_any_mixed_legacy_payload(self) -> None:
        publish = self.base / "publish-tree"
        (publish / "extras").mkdir(parents=True)
        path = publish / Path(*bundle.LEGACY_PUBLISH_PATHS[0].split("/"))
        path.write_bytes(b"legacy")
        with self.assertRaises(bundle.BundleContractError):
            bundle.assert_no_legacy_publish_payload(publish)

    def test_native_publish_rejects_unanticipated_legacy_version(self) -> None:
        publish = self.base / "publish-tree"
        (publish / "extras").mkdir(parents=True)
        (publish / "extras" / "VIIPER-9.9.9-x64.exe").write_bytes(b"legacy")
        with self.assertRaises(bundle.BundleContractError):
            bundle.assert_no_legacy_publish_payload(publish)

    def test_native_publish_rejects_nested_legacy_payload(self) -> None:
        publish = self.base / "publish-tree"
        nested = publish / "unrelated" / "stale"
        nested.mkdir(parents=True)
        (nested / "USBip-9.9.9-x64.exe").write_bytes(b"legacy")
        with self.assertRaises(bundle.BundleContractError):
            bundle.assert_no_legacy_publish_payload(publish)

    def test_native_publish_rejects_recursive_audit_barrier(self) -> None:
        publish = self.base / "publish-tree"
        barrier = publish / "unrelated" / "linked-tree"
        barrier.mkdir(parents=True)
        with mock.patch.object(
            bundle,
            "_is_reparse_point",
            side_effect=lambda path: path == barrier,
        ):
            with self.assertRaises(bundle.BundleContractError):
                bundle.assert_no_legacy_publish_payload(publish)

    def test_lock_writer_requires_archive_bytes_to_match_bundle(self) -> None:
        self.fixture.lock.unlink()
        original = (self.fixture.root / "viiper.exe").read_bytes()
        self.fixture.write_archive(
            replace=("viiper.exe", original[:-1] + b"!")
        )
        archive_hash = sha256(self.fixture.archive)
        with self.assertRaises(bundle.BundleContractError):
            bundle.write_lock(
                self.fixture.root,
                self.fixture.lock,
                CONTRACT_PATH,
                101,
                "v0.1.1-native-test",
                202,
                self.fixture.archive,
                "sha256:" + archive_hash,
                "b" * 64,
            )
        self.assertFalse(self.fixture.lock.exists())

    def test_immutable_lock_writer_refuses_overwrite(self) -> None:
        with self.assertRaises(bundle.BundleContractError):
            bundle.write_lock(
                self.fixture.root,
                self.fixture.lock,
                CONTRACT_PATH,
                101,
                "v0.1.1-native-test",
                202,
                self.fixture.archive,
                "sha256:" + sha256(self.fixture.archive),
                "b" * 64,
            )

    def test_signature_gate_resolves_release_tag_to_source_revision(self) -> None:
        source = (
            REPOSITORY_ROOT
            / "utils"
            / "validate-viiper-native-bundle-signatures.ps1"
        ).read_text(encoding="utf-8")
        self.assertIn("/git/ref/tags/$encodedTag", source)
        self.assertIn("/git/tags/$([string]$tagObject.sha)", source)
        self.assertIn(
            "[string]$tagObject.sha -cne [string]$lock.provenance.sourceRevision",
            source,
        )
        self.assertIn("validate-archive", source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
