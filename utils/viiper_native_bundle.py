#!/usr/bin/env python3
"""Validate and stage the exact production VIIPER native UdeCx bundle.

Once production bytes exist, their checked-in lock is the review boundary.
This tool never downloads a payload and never manufactures trust at release
time: it proves that the six bytes-on-disk artifacts, VIIPER's schema-2
submission evidence, and the native runtime contract compiled into DS4Windows
are one immutable package.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import tempfile
from typing import Any
import zipfile


BUNDLE_DIRECTORY_NAME = "viiper-native-udecx"
LOCK_FILE_NAME = "viiper-native-udecx.lock.json"
PACKAGE_NAME = "native-udecx-windows-amd64"
RELEASE_ASSET_NAME = "viiper-native-udecx-windows-amd64.zip"

RUNTIME_PATHS = (
    "viiper.exe",
    "ViiperUdeCtl.exe",
    "submission-manifest.json",
    "driver/ViiperUde.inf",
    "driver/ViiperUde.sys",
    "driver/ViiperUde.cat",
)
ARCHIVE_TO_RUNTIME = (
    ("viiper.exe", "viiper.exe"),
    ("ViiperUdeCtl.exe", "ViiperUdeCtl.exe"),
    ("ViiperUde.inf", "driver/ViiperUde.inf"),
    ("ViiperUde.sys", "driver/ViiperUde.sys"),
    ("ViiperUde.cat", "driver/ViiperUde.cat"),
    ("submission-manifest.json", "submission-manifest.json"),
)
LEGACY_PUBLISH_PATHS = (
    "extras/VIIPER-0.1.0-x64.exe",
    "extras/VIIPER-0.1.0-x64.exe.sha256",
    "extras/USBip-0.9.7.7-x64.exe",
    "extras/USBip-0.9.7.7-LICENSE.txt",
)
LEGACY_PUBLISH_BASENAME = re.compile(
    r"(?i)^(?:VIIPER-.+-x64\.exe(?:\.sha256)?|USBip-.+(?:-x64\.exe|-LICENSE\.txt))$"
)
SUBMISSION_NAMES = (
    "ViiperUde.inf",
    "ViiperUde.sys",
    "ViiperUde.pdb",
    "ViiperUde.cat",
)
SUBMISSION_ROOT_KEYS = {
    "schema",
    "purpose",
    "releaseEligible",
    "signingRoute",
    "requiredProductionRoute",
    "sourceRevision",
    "driverPackageVersion",
    "driverABIMajor",
    "driverABIMinor",
    "driverCapabilities",
    "driverBuildIdentity",
    "cabinet",
    "cabinetSha256",
    "packageFolder",
    "files",
}
SUBMISSION_FILE_KEYS = {"name", "length", "sha256"}

LOCK_KEYS = {
    "schema",
    "product",
    "package",
    "architecture",
    "serverVersion",
    "driverPackageVersion",
    "driverABIMajor",
    "driverABIMinor",
    "driverCapabilities",
    "driverBuildIdentity",
    "provenance",
    "signing",
    "files",
}
PROVENANCE_KEYS = {
    "repository",
    "sourceRevision",
    "releaseId",
    "releaseTag",
    "releaseAssetId",
    "releaseAssetName",
    "releaseAssetApiDigest",
    "releaseAssetSha256",
    "submissionManifestSha256",
}
SIGNING_KEYS = {
    "driverRoute",
    "userModeSignerCertificateSha256",
}
LOCK_FILE_KEYS = {"path", "length", "sha256"}
MAX_GITHUB_OBJECT_ID = (1 << 63) - 1


class BundleContractError(ValueError):
    """The bundle is ambiguous, incomplete, or does not match its lock."""


@dataclass(frozen=True)
class NativeContract:
    architecture: str
    upstream_repository: str
    source_revision: str
    server_version: str
    abi_major: int
    abi_minor: int
    capabilities: int
    driver_package_version: str
    driver_build_identity: str


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _files_equal(left: Path, right: Path) -> bool:
    if left.stat().st_size != right.stat().st_size:
        return False
    with left.open("rb") as left_stream, right.open("rb") as right_stream:
        while True:
            left_block = left_stream.read(1024 * 1024)
            right_block = right_stream.read(1024 * 1024)
            if left_block != right_block:
                return False
            if not left_block:
                return True


def _is_reparse_point(path: Path) -> bool:
    attributes = getattr(path.lstat(), "st_file_attributes", 0)
    return bool(
        attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    )


def _assert_plain_path(path: Path, description: str, *, directory: bool) -> None:
    if not path.exists():
        raise BundleContractError(f"Missing {description}: {path}")
    if path.is_symlink() or _is_reparse_point(path):
        raise BundleContractError(f"{description} cannot be a link/reparse point: {path}")
    if directory and not path.is_dir():
        raise BundleContractError(f"{description} is not a directory: {path}")
    if not directory and not path.is_file():
        raise BundleContractError(f"{description} is not a regular file: {path}")


def _json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise BundleContractError(f"JSON contains duplicate property {key!r}")
        value[key] = item
    return value


def _read_json(path: Path, description: str) -> dict[str, Any]:
    _assert_plain_path(path, description, directory=False)
    if path.stat().st_size <= 0:
        raise BundleContractError(f"{description} is empty: {path}")
    try:
        value = json.loads(
            path.read_text(encoding="utf-8-sig"),
            object_pairs_hook=_json_object,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise BundleContractError(f"{description} is not valid UTF-8 JSON: {error}") from error
    if not isinstance(value, dict):
        raise BundleContractError(f"{description} must contain one JSON object")
    return value


def _require_exact_keys(value: dict[str, Any], expected: set[str], description: str) -> None:
    if set(value) != expected:
        missing = sorted(expected - set(value))
        extra = sorted(set(value) - expected)
        details: list[str] = []
        if missing:
            details.append("missing=" + ", ".join(missing))
        if extra:
            details.append("unexpected=" + ", ".join(extra))
        raise BundleContractError(
            f"{description} has an ambiguous schema ({'; '.join(details)})"
        )


def _is_exact_int(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _require_lower_sha256(value: Any, description: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
        raise BundleContractError(f"{description} must be 64 lowercase hexadecimal digits")
    return value


def _require_positive_int64(value: Any, description: str) -> int:
    if (
        not _is_exact_int(value)
        or value <= 0
        or value > MAX_GITHUB_OBJECT_ID
    ):
        raise BundleContractError(
            f"{description} must be one positive signed 64-bit integer"
        )
    return value


def _read_string_constant(source: str, name: str) -> str:
    matches = re.findall(
        rf"\binternal\s+const\s+string\s+{re.escape(name)}\s*=\s*\"([^\"]+)\"\s*;",
        source,
        flags=re.DOTALL,
    )
    if len(matches) != 1:
        raise BundleContractError(f"Native contract must define exactly one string {name}")
    return matches[0]


def _read_integer_constant(source: str, type_name: str, name: str) -> int:
    matches = re.findall(
        rf"\binternal\s+const\s+{re.escape(type_name)}\s+"
        rf"{re.escape(name)}\s*=\s*(0x[0-9A-Fa-f]+|[0-9]+)\s*;",
        source,
    )
    if len(matches) != 1:
        raise BundleContractError(f"Native contract must define exactly one integer {name}")
    return int(matches[0], 0)


def read_native_contract(contract_path: Path) -> NativeContract:
    _assert_plain_path(contract_path, "DS4Windows native package contract", directory=False)
    source = contract_path.read_text(encoding="utf-8-sig")
    contract = NativeContract(
        architecture=_read_string_constant(source, "Architecture"),
        upstream_repository=_read_string_constant(source, "UpstreamRepository"),
        source_revision=_read_string_constant(source, "SourceRevision"),
        server_version=_read_string_constant(source, "ServerVersion"),
        abi_major=_read_integer_constant(source, "ushort", "DriverAbiMajor"),
        abi_minor=_read_integer_constant(source, "ushort", "DriverAbiMinor"),
        capabilities=_read_integer_constant(source, "uint", "DriverCapabilities"),
        driver_package_version=_read_string_constant(source, "DriverPackageVersion"),
        driver_build_identity=_read_string_constant(source, "DriverBuildIdentity"),
    )
    if contract.architecture != "x64":
        raise BundleContractError("The first native bundle contract must be x64")
    if not re.fullmatch(r"[0-9a-f]{40}", contract.source_revision):
        raise BundleContractError("Native source revision must be one exact lowercase Git SHA")
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){3}", contract.driver_package_version):
        raise BundleContractError("Native driver package version must have four numeric parts")
    if contract.abi_major <= 0 or contract.capabilities <= 0:
        raise BundleContractError("Native ABI major and capabilities must be nonzero")
    _require_lower_sha256(contract.driver_build_identity, "Native driver build identity")
    expected_identity = derive_build_identity(contract)
    if contract.driver_build_identity != expected_identity:
        raise BundleContractError(
            "Native contract build identity is not derived from its source/package/ABI tuple"
        )
    return contract


def derive_build_identity(contract: NativeContract) -> str:
    preimage = (
        "VIIPER-UDE-BUILD-IDENTITY/v1\n"
        f"sourceRevision={contract.source_revision}\n"
        f"driverPackageVersion={contract.driver_package_version}\n"
        f"abi={contract.abi_major}.{contract.abi_minor}\n"
        f"capabilities=0x{contract.capabilities:08x}\n"
    )
    return hashlib.sha256(preimage.encode("utf-8")).hexdigest()


def _enumerate_runtime(bundle_root: Path) -> dict[str, Path]:
    if bundle_root.name != BUNDLE_DIRECTORY_NAME:
        raise BundleContractError(
            f"Native bundle directory must be named case-exact {BUNDLE_DIRECTORY_NAME!r}"
        )
    _assert_plain_path(bundle_root, "native bundle directory", directory=True)
    driver_root = bundle_root / "driver"
    _assert_plain_path(driver_root, "native driver directory", directory=True)

    directories: set[str] = set()
    files: dict[str, Path] = {}
    for entry in bundle_root.rglob("*"):
        relative = entry.relative_to(bundle_root).as_posix()
        if entry.is_symlink() or _is_reparse_point(entry):
            raise BundleContractError(f"Native bundle contains a link/reparse point: {relative}")
        if entry.is_dir():
            directories.add(relative)
        elif entry.is_file():
            files[relative] = entry
        else:
            raise BundleContractError(f"Native bundle contains a non-file entry: {relative}")

    if directories != {"driver"}:
        raise BundleContractError(
            "Native bundle directories are not exact: " + ", ".join(sorted(directories))
        )
    expected = set(RUNTIME_PATHS)
    if set(files) != expected:
        missing = sorted(expected - set(files))
        extra = sorted(set(files) - expected)
        details = []
        if missing:
            details.append("missing=" + ", ".join(missing))
        if extra:
            details.append("unexpected=" + ", ".join(extra))
        raise BundleContractError(
            "Native bundle files are not the exact six-file runtime (" + "; ".join(details) + ")"
        )
    if len({name.casefold() for name in files}) != len(files):
        raise BundleContractError("Native bundle contains case-insensitive duplicate paths")
    for relative, path in files.items():
        if path.stat().st_size <= 0:
            raise BundleContractError(f"Native runtime file is empty: {relative}")
    for relative in ("viiper.exe", "ViiperUdeCtl.exe", "driver/ViiperUde.sys"):
        with files[relative].open("rb") as stream:
            if stream.read(2) != b"MZ":
                raise BundleContractError(f"Native PE image has no MZ header: {relative}")
    return files


def _validate_submission_manifest(
    manifest_path: Path,
    runtime_files: dict[str, Path],
    contract: NativeContract,
) -> dict[str, Any]:
    manifest = _read_json(manifest_path, "VIIPER schema-2 submission manifest")
    _require_exact_keys(
        manifest, SUBMISSION_ROOT_KEYS, "VIIPER schema-2 submission manifest"
    )
    required = {
        "schema": 2,
        "releaseEligible": True,
        "signingRoute": "HLK/WHCP",
        "sourceRevision": contract.source_revision,
        "driverPackageVersion": contract.driver_package_version,
        "driverABIMajor": contract.abi_major,
        "driverABIMinor": contract.abi_minor,
        "driverCapabilities": f"0x{contract.capabilities:08x}",
        "driverBuildIdentity": contract.driver_build_identity,
    }
    for name, expected in required.items():
        actual = manifest.get(name)
        if type(actual) is not type(expected) or actual != expected:
            raise BundleContractError(
                f"Submission manifest {name} does not match the reviewed native contract"
            )
    purpose = manifest["purpose"]
    if (
        not isinstance(purpose, str)
        or not purpose
        or purpose != purpose.strip()
        or len(purpose) > 512
    ):
        raise BundleContractError(
            "Submission manifest purpose must be a canonical nonempty string"
        )
    if manifest["requiredProductionRoute"] != "HLK/WHCP dashboard signing":
        raise BundleContractError("Submission manifest required production route is invalid")
    if manifest["packageFolder"] != "ViiperUde":
        raise BundleContractError("Submission manifest package folder is invalid")
    if (
        not isinstance(manifest["cabinet"], str)
        or not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z._-]{0,127}\.cab", manifest["cabinet"])
    ):
        raise BundleContractError("Submission manifest cabinet name is invalid")
    if (
        not isinstance(manifest["cabinetSha256"], str)
        or not re.fullmatch(r"[0-9A-F]{64}", manifest["cabinetSha256"])
    ):
        raise BundleContractError("Submission manifest cabinet SHA-256 is invalid")

    entries = manifest.get("files")
    if not isinstance(entries, list) or len(entries) != len(SUBMISSION_NAMES):
        raise BundleContractError(
            "Submission manifest must describe exactly INF, SYS, PDB, and CAT"
        )
    by_name: dict[str, dict[str, Any]] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            raise BundleContractError("Submission manifest file entry is not an object")
        _require_exact_keys(
            entry, SUBMISSION_FILE_KEYS, "Submission manifest file entry"
        )
        name = entry.get("name")
        if name not in SUBMISSION_NAMES or name in by_name:
            raise BundleContractError(
                f"Submission manifest has unexpected or duplicate file {name!r}"
            )
        length = entry.get("length")
        sha256 = entry.get("sha256")
        if not _is_exact_int(length) or length <= 0:
            raise BundleContractError(f"Submission manifest has invalid length for {name}")
        if not isinstance(sha256, str) or not re.fullmatch(r"[0-9A-F]{64}", sha256):
            raise BundleContractError(f"Submission manifest has invalid SHA-256 for {name}")
        by_name[name] = entry
    if set(by_name) != set(SUBMISSION_NAMES):
        raise BundleContractError("Submission manifest file names are incomplete")
    if [entry["name"] for entry in entries] != list(SUBMISSION_NAMES):
        raise BundleContractError(
            "Submission manifest files must use canonical deterministic order"
        )

    # Microsoft signing can change SYS/CAT. The stamped INF is the unchanged
    # source-bound member retained in the public runtime package.
    inf_path = runtime_files["driver/ViiperUde.inf"]
    inf_entry = by_name["ViiperUde.inf"]
    if (
        inf_path.stat().st_size != inf_entry["length"]
        or _sha256(inf_path) != str(inf_entry["sha256"]).lower()
    ):
        raise BundleContractError(
            "Runtime INF does not match the source-bound submission manifest"
        )
    inf_source = inf_path.read_text(encoding="utf-8-sig")
    driver_ver = re.findall(
        r"(?im)^\s*DriverVer\s*=\s*[^,\r\n]+\s*,\s*([^\s;]+)\s*$",
        inf_source,
    )
    if driver_ver != [contract.driver_package_version]:
        raise BundleContractError("Runtime INF DriverVer does not match the native contract")
    if not re.search(r"(?im)^\s*KmdfLibraryVersion\s*=\s*1\.27\s*$", inf_source):
        raise BundleContractError("Runtime INF does not preserve the reviewed KMDF 1.27 target")
    if not re.search(r"(?im)^\s*CatalogFile\s*=\s*ViiperUde\.cat\s*$", inf_source):
        raise BundleContractError("Runtime INF does not name the locked VIIPER catalog")
    return manifest


def _validate_lock(
    lock: dict[str, Any],
    runtime_files: dict[str, Path],
    contract: NativeContract,
) -> None:
    _require_exact_keys(lock, LOCK_KEYS, "Native bundle lock")
    expected_values: dict[str, Any] = {
        "schema": 1,
        "product": "VIIPER",
        "package": PACKAGE_NAME,
        "architecture": contract.architecture,
        "serverVersion": contract.server_version,
        "driverPackageVersion": contract.driver_package_version,
        "driverABIMajor": contract.abi_major,
        "driverABIMinor": contract.abi_minor,
        "driverCapabilities": f"0x{contract.capabilities:08x}",
        "driverBuildIdentity": contract.driver_build_identity,
    }
    for name, expected in expected_values.items():
        actual = lock.get(name)
        if type(actual) is not type(expected) or actual != expected:
            raise BundleContractError(f"Native bundle lock {name} is not the reviewed value")

    provenance = lock.get("provenance")
    if not isinstance(provenance, dict):
        raise BundleContractError("Native bundle lock provenance must be an object")
    _require_exact_keys(provenance, PROVENANCE_KEYS, "Native bundle provenance")
    if provenance["repository"] != contract.upstream_repository:
        raise BundleContractError("Native bundle provenance repository is not VIIPER")
    if provenance["sourceRevision"] != contract.source_revision:
        raise BundleContractError("Native bundle provenance revision is not the reviewed source")
    for name in ("releaseId", "releaseAssetId"):
        _require_positive_int64(
            provenance[name], f"Native bundle provenance {name}"
        )
    if not isinstance(provenance["releaseTag"], str) or not re.fullmatch(
        r"v[0-9A-Za-z][0-9A-Za-z._-]{0,79}", provenance["releaseTag"]
    ):
        raise BundleContractError("Native bundle provenance tag is not a canonical release tag")
    if provenance["releaseAssetName"] != RELEASE_ASSET_NAME:
        raise BundleContractError("Native bundle provenance names an unexpected release asset")
    release_hash = _require_lower_sha256(
        provenance["releaseAssetSha256"], "Locally recomputed release asset SHA-256"
    )
    api_digest = provenance["releaseAssetApiDigest"]
    if not isinstance(api_digest, str) or not re.fullmatch(
        r"sha256:[0-9a-f]{64}", api_digest
    ):
        raise BundleContractError(
            "GitHub release asset API digest must be canonical sha256:<64 lowercase hex>"
        )
    if api_digest != "sha256:" + release_hash:
        raise BundleContractError(
            "GitHub API asset digest does not match the independently downloaded archive"
        )
    manifest_hash = _require_lower_sha256(
        provenance["submissionManifestSha256"], "Submission manifest SHA-256"
    )
    if manifest_hash != _sha256(runtime_files["submission-manifest.json"]):
        raise BundleContractError("Submission manifest does not match immutable provenance")

    signing = lock.get("signing")
    if not isinstance(signing, dict):
        raise BundleContractError("Native bundle lock signing evidence must be an object")
    _require_exact_keys(signing, SIGNING_KEYS, "Native bundle signing evidence")
    if signing["driverRoute"] != "HLK/WHCP":
        raise BundleContractError("Native driver lock must require the HLK/WHCP route")
    _require_lower_sha256(
        signing["userModeSignerCertificateSha256"],
        "User-mode signer certificate SHA-256",
    )

    entries = lock.get("files")
    if not isinstance(entries, list) or len(entries) != len(RUNTIME_PATHS):
        raise BundleContractError("Native bundle lock must own exactly six runtime files")
    by_path: dict[str, dict[str, Any]] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            raise BundleContractError("Native bundle lock file entry is not an object")
        _require_exact_keys(entry, LOCK_FILE_KEYS, "Native bundle lock file entry")
        relative = entry["path"]
        if not isinstance(relative, str) or relative not in RUNTIME_PATHS or relative in by_path:
            raise BundleContractError(
                f"Native bundle lock has unexpected or duplicate path {relative!r}"
            )
        parsed = PurePosixPath(relative)
        if parsed.is_absolute() or any(part in {"", ".", ".."} for part in parsed.parts):
            raise BundleContractError(f"Native bundle lock contains unsafe path {relative!r}")
        if not _is_exact_int(entry["length"]) or entry["length"] <= 0:
            raise BundleContractError(f"Native bundle lock has invalid length for {relative}")
        _require_lower_sha256(entry["sha256"], f"Native bundle lock SHA-256 for {relative}")
        by_path[relative] = entry
    if set(by_path) != set(RUNTIME_PATHS):
        raise BundleContractError("Native bundle lock path set is incomplete")
    if [entry["path"] for entry in entries] != list(RUNTIME_PATHS):
        raise BundleContractError("Native bundle lock files must use canonical deterministic order")
    for relative, path in runtime_files.items():
        entry = by_path[relative]
        if path.stat().st_size != entry["length"] or _sha256(path) != entry["sha256"]:
            raise BundleContractError(f"Native runtime payload hash mismatch: {relative}")


def validate_bundle(
    bundle_root: Path,
    lock_path: Path,
    contract_path: Path,
) -> dict[str, Any]:
    bundle_root = bundle_root.absolute()
    lock_path = lock_path.absolute()
    expected_lock = bundle_root.parent / LOCK_FILE_NAME
    if lock_path != expected_lock:
        raise BundleContractError(
            f"Native lock must be the case-exact sibling {expected_lock}"
        )
    contract = read_native_contract(contract_path.absolute())
    runtime_files = _enumerate_runtime(bundle_root)
    _validate_submission_manifest(
        runtime_files["submission-manifest.json"], runtime_files, contract
    )
    lock = _read_json(lock_path, "immutable native bundle lock")
    _validate_lock(lock, runtime_files, contract)
    return lock


def validate_release_archive(
    archive_path: Path,
    bundle_root: Path,
    lock_path: Path,
    contract_path: Path,
) -> None:
    """Prove the downloaded upstream ZIP is exactly the local locked bundle."""
    lock = validate_bundle(bundle_root, lock_path, contract_path)
    _validate_release_archive_bytes(archive_path, bundle_root, lock)


def _validate_release_archive_bytes(
    archive_path: Path,
    bundle_root: Path,
    lock: dict[str, Any],
) -> None:
    """Validate a flat upstream ZIP against an already validated local bundle."""
    archive_path = archive_path.absolute()
    bundle_root = bundle_root.absolute()
    _assert_plain_path(archive_path, "VIIPER GitHub release archive", directory=False)
    if _sha256(archive_path) != lock["provenance"]["releaseAssetSha256"]:
        raise BundleContractError(
            "VIIPER release archive does not match its independently recorded SHA-256"
        )
    expected_names = {archive_name for archive_name, _ in ARCHIVE_TO_RUNTIME}
    lock_by_path = {entry["path"]: entry for entry in lock["files"]}
    try:
        with zipfile.ZipFile(archive_path, "r") as archive:
            entries = archive.infolist()
            actual_names = [entry.filename for entry in entries]
            if len(entries) != len(expected_names) or set(actual_names) != expected_names:
                raise BundleContractError(
                    "Upstream archive must contain exactly the six flat canonical runtime files"
                )
            if len(actual_names) != len(set(actual_names)):
                raise BundleContractError("Upstream archive contains duplicate member names")
            by_name = {entry.filename: entry for entry in entries}
            total_size = 0
            for archive_name, runtime_path in ARCHIVE_TO_RUNTIME:
                entry = by_name[archive_name]
                parsed = PurePosixPath(entry.filename)
                unix_mode = (entry.external_attr >> 16) & 0xFFFF
                unix_file_type = stat.S_IFMT(unix_mode)
                dos_attributes = entry.external_attr & 0xFFFF
                if (
                    parsed.is_absolute()
                    or len(parsed.parts) != 1
                    or any(part in {"", ".", ".."} for part in parsed.parts)
                    or entry.is_dir()
                    or unix_file_type not in (0, stat.S_IFREG)
                    or bool(
                        dos_attributes
                        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
                    )
                    or bool(entry.flag_bits & 0x1)
                    or entry.file_size <= 0
                ):
                    raise BundleContractError(
                        f"Upstream archive contains an unsafe member: {entry.filename!r}"
                    )
                expected_length = lock_by_path[runtime_path]["length"]
                if entry.file_size != expected_length:
                    raise BundleContractError(
                        f"Upstream archive length differs from local lock: {entry.filename}"
                    )
                total_size += entry.file_size
            if total_size > 256 * 1024 * 1024:
                raise BundleContractError("Upstream archive expands beyond the runtime size limit")

            # Extract only after the central-directory allowlist succeeds. Each
            # destination is selected by us, never by an archive path.
            with tempfile.TemporaryDirectory(prefix="viiper-native-archive-") as temporary:
                extracted = Path(temporary)
                for archive_name, runtime_path in ARCHIVE_TO_RUNTIME:
                    destination = extracted / archive_name
                    entry = by_name[archive_name]
                    with archive.open(entry, "r") as source:
                        with destination.open("xb") as output:
                            remaining = entry.file_size
                            while remaining:
                                block = source.read(min(1024 * 1024, remaining))
                                if not block:
                                    raise BundleContractError(
                                        "Upstream archive member ended before its declared size: "
                                        + archive_name
                                    )
                                output.write(block)
                                remaining -= len(block)
                            if source.read(1):
                                raise BundleContractError(
                                    "Upstream archive member exceeds its declared size: "
                                    + archive_name
                                )
                    local_path = bundle_root / Path(*PurePosixPath(runtime_path).parts)
                    if not _files_equal(destination, local_path):
                        raise BundleContractError(
                            "Upstream archive bytes differ from local locked runtime: "
                            + archive_name
                        )
    except (zipfile.BadZipFile, NotImplementedError, RuntimeError, EOFError) as error:
        raise BundleContractError(f"VIIPER release asset is not a valid ZIP: {error}") from error


def _unlocked_bundle_metadata(
    bundle_root: Path,
    contract_path: Path,
    release_id: int,
    release_tag: str,
    release_asset_id: int,
    release_archive_path: Path,
    release_asset_api_digest: str,
    signer_certificate_sha256: str,
) -> dict[str, Any]:
    contract = read_native_contract(contract_path.absolute())
    runtime_files = _enumerate_runtime(bundle_root.absolute())
    _validate_submission_manifest(
        runtime_files["submission-manifest.json"], runtime_files, contract
    )
    release_archive_path = release_archive_path.absolute()
    _assert_plain_path(
        release_archive_path, "VIIPER GitHub release archive", directory=False
    )
    release_hash = _sha256(release_archive_path)
    _require_positive_int64(release_id, "GitHub release ID")
    if not isinstance(release_tag, str) or not re.fullmatch(
        r"v[0-9A-Za-z][0-9A-Za-z._-]{0,79}", release_tag
    ):
        raise BundleContractError("GitHub release tag must be a canonical v-prefixed tag")
    _require_positive_int64(release_asset_id, "GitHub release asset ID")
    if release_asset_api_digest != "sha256:" + release_hash:
        raise BundleContractError(
            "GitHub API asset digest must exactly match the independently "
            "recomputed archive SHA-256"
        )
    signer_hash = _require_lower_sha256(
        signer_certificate_sha256, "User-mode signer certificate SHA-256"
    )
    return {
        "schema": 1,
        "product": "VIIPER",
        "package": PACKAGE_NAME,
        "architecture": contract.architecture,
        "serverVersion": contract.server_version,
        "driverPackageVersion": contract.driver_package_version,
        "driverABIMajor": contract.abi_major,
        "driverABIMinor": contract.abi_minor,
        "driverCapabilities": f"0x{contract.capabilities:08x}",
        "driverBuildIdentity": contract.driver_build_identity,
        "provenance": {
            "repository": contract.upstream_repository,
            "sourceRevision": contract.source_revision,
            "releaseId": release_id,
            "releaseTag": release_tag,
            "releaseAssetId": release_asset_id,
            "releaseAssetName": RELEASE_ASSET_NAME,
            "releaseAssetApiDigest": release_asset_api_digest,
            "releaseAssetSha256": release_hash,
            "submissionManifestSha256": _sha256(
                runtime_files["submission-manifest.json"]
            ),
        },
        "signing": {
            "driverRoute": "HLK/WHCP",
            "userModeSignerCertificateSha256": signer_hash,
        },
        "files": [
            {
                "path": relative,
                "length": runtime_files[relative].stat().st_size,
                "sha256": _sha256(runtime_files[relative]),
            }
            for relative in RUNTIME_PATHS
        ],
    }


def write_lock(
    bundle_root: Path,
    lock_path: Path,
    contract_path: Path,
    release_id: int,
    release_tag: str,
    release_asset_id: int,
    release_archive_path: Path,
    release_asset_api_digest: str,
    signer_certificate_sha256: str,
) -> None:
    lock_path = lock_path.absolute()
    expected_lock = bundle_root.absolute().parent / LOCK_FILE_NAME
    if lock_path != expected_lock:
        raise BundleContractError(f"Native lock must be written as {expected_lock}")
    if lock_path.exists():
        raise BundleContractError(
            "Refusing to overwrite an immutable native bundle lock; review a new lock explicitly"
        )
    lock = _unlocked_bundle_metadata(
        bundle_root,
        contract_path,
        release_id,
        release_tag,
        release_asset_id,
        release_archive_path,
        release_asset_api_digest,
        signer_certificate_sha256,
    )
    _validate_release_archive_bytes(release_archive_path, bundle_root, lock)
    with lock_path.open("x", encoding="ascii", newline="\n") as stream:
        stream.write(json.dumps(lock, indent=2, ensure_ascii=True) + "\n")
    validate_bundle(bundle_root, lock_path, contract_path)


def stage_bundle(
    source_bundle_root: Path,
    source_lock_path: Path,
    publish_extras_root: Path,
    contract_path: Path,
) -> tuple[Path, Path]:
    validate_bundle(source_bundle_root, source_lock_path, contract_path)
    publish_extras_root = publish_extras_root.absolute()
    publish_extras_root.mkdir(parents=True, exist_ok=True)
    _assert_plain_path(publish_extras_root, "publish extras directory", directory=True)
    destination_bundle = publish_extras_root / BUNDLE_DIRECTORY_NAME
    destination_lock = publish_extras_root / LOCK_FILE_NAME
    if source_bundle_root.absolute() == destination_bundle:
        raise BundleContractError("Source and staged native bundle paths must differ")

    staging = Path(tempfile.mkdtemp(prefix=".viiper-native-stage-", dir=publish_extras_root))
    try:
        staged_bundle = staging / BUNDLE_DIRECTORY_NAME
        staged_lock = staging / LOCK_FILE_NAME
        (staged_bundle / "driver").mkdir(parents=True)
        for relative in RUNTIME_PATHS:
            destination = staged_bundle / Path(*PurePosixPath(relative).parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(
                source_bundle_root.absolute() / Path(*PurePosixPath(relative).parts),
                destination,
            )
        shutil.copyfile(source_lock_path.absolute(), staged_lock)
        validate_bundle(staged_bundle, staged_lock, contract_path)

        for destination in (destination_bundle, destination_lock):
            if destination.exists() or destination.is_symlink():
                if destination.is_symlink() or _is_reparse_point(destination):
                    raise BundleContractError(
                        f"Refusing to replace staged output link/reparse point: {destination}"
                    )
                if destination.is_dir():
                    shutil.rmtree(destination)
                else:
                    destination.unlink()
        os.replace(staged_bundle, destination_bundle)
        os.replace(staged_lock, destination_lock)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    validate_bundle(destination_bundle, destination_lock, contract_path)
    return destination_bundle, destination_lock


def remove_legacy_publish_payload(publish_root: Path) -> None:
    """Remove only known generated legacy payloads before native composition."""
    publish_root = publish_root.absolute()
    _assert_plain_path(publish_root, "publish root", directory=True)
    for relative in LEGACY_PUBLISH_PATHS:
        path = publish_root / Path(*PurePosixPath(relative).parts)
        if not path.exists() and not path.is_symlink():
            continue
        if path.is_symlink() or _is_reparse_point(path) or not path.is_file():
            raise BundleContractError(
                f"Refusing to remove unsafe legacy publish payload: {relative}"
            )
        path.unlink()
    assert_no_legacy_publish_payload(publish_root)


def assert_no_legacy_publish_payload(publish_root: Path) -> None:
    _assert_plain_path(publish_root, "publish root", directory=True)
    present: list[str] = []
    pending = [publish_root]
    while pending:
        directory = pending.pop()
        for path in directory.iterdir():
            relative = path.relative_to(publish_root).as_posix()
            if LEGACY_PUBLISH_BASENAME.fullmatch(path.name):
                present.append(relative)
            if path.is_symlink() or _is_reparse_point(path):
                raise BundleContractError(
                    "Cannot recursively audit native publish tree through a "
                    f"link/reparse point: {relative}"
                )
            if path.is_dir():
                pending.append(path)
    if present:
        raise BundleContractError(
            "Native production package contains legacy VIIPER/USB-IP payload: "
            + ", ".join(sorted(present))
        )


def _default_contract() -> Path:
    return (
        Path(__file__).resolve().parent.parent
        / "DS4Windows"
        / "DS4Control"
        / "Viiper"
        / "ViiperNativePackageContract.cs"
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate_parser = subparsers.add_parser("validate")
    validate_parser.add_argument("--bundle-root", type=Path, required=True)
    validate_parser.add_argument("--lock", type=Path, required=True)
    validate_parser.add_argument("--contract", type=Path, default=_default_contract())

    lock_parser = subparsers.add_parser("create-lock")
    lock_parser.add_argument("--bundle-root", type=Path, required=True)
    lock_parser.add_argument("--lock", type=Path, required=True)
    lock_parser.add_argument("--contract", type=Path, default=_default_contract())
    lock_parser.add_argument("--release-id", type=int, required=True)
    lock_parser.add_argument("--release-tag", required=True)
    lock_parser.add_argument("--release-asset-id", type=int, required=True)
    lock_parser.add_argument("--release-archive", type=Path, required=True)
    lock_parser.add_argument("--release-asset-api-digest", required=True)
    lock_parser.add_argument("--signer-certificate-sha256", required=True)

    stage_parser = subparsers.add_parser("stage")
    stage_parser.add_argument("--bundle-root", type=Path, required=True)
    stage_parser.add_argument("--lock", type=Path, required=True)
    stage_parser.add_argument("--publish-extras-root", type=Path, required=True)
    stage_parser.add_argument("--contract", type=Path, default=_default_contract())

    archive_parser = subparsers.add_parser("validate-archive")
    archive_parser.add_argument("--archive", type=Path, required=True)
    archive_parser.add_argument("--bundle-root", type=Path, required=True)
    archive_parser.add_argument("--lock", type=Path, required=True)
    archive_parser.add_argument("--contract", type=Path, default=_default_contract())

    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "validate":
            validate_bundle(arguments.bundle_root, arguments.lock, arguments.contract)
        elif arguments.command == "create-lock":
            write_lock(
                arguments.bundle_root,
                arguments.lock,
                arguments.contract,
                arguments.release_id,
                arguments.release_tag,
                arguments.release_asset_id,
                arguments.release_archive,
                arguments.release_asset_api_digest,
                arguments.signer_certificate_sha256,
            )
        elif arguments.command == "stage":
            stage_bundle(
                arguments.bundle_root,
                arguments.lock,
                arguments.publish_extras_root,
                arguments.contract,
            )
        elif arguments.command == "validate-archive":
            validate_release_archive(
                arguments.archive,
                arguments.bundle_root,
                arguments.lock,
                arguments.contract,
            )
        else:  # pragma: no cover - argparse makes this unreachable.
            raise AssertionError(arguments.command)
    except (BundleContractError, OSError) as error:
        print(f"VIIPER native bundle rejected: {error}", file=sys.stderr)
        return 2
    print(f"VIIPER native bundle {arguments.command} succeeded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
