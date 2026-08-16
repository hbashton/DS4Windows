#!/usr/bin/env python3
"""Fail-closed static and composed-media validation for the native installer."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Iterable


WIX = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
EXPECTED_CHAIN = [
    "CloseRunningApplications",
    "DS4WindowsMsi",
    "ViiperNativeSetup",
    "ViiperNativeRemove",
    "CloseRunningApplicationsForUninstall",
]
FORBIDDEN_INSTALLER_TEXT = [
    re.compile(value, re.IGNORECASE)
    for value in (
        r"usb[\s_-]*ip",
        r"install-viiper-backend",
        r"\bhidhide\b",
        r"\bfakerinput\b",
        r"RunDS4Windows",
        r"DownloadFile",
        r"DownloadString",
        r"WebClient",
        r"HttpClient",
        r"Start-Process",
        r"\brunas\b",
    )
]
WINDOWS_DEVICE_NAME = re.compile(
    r"(?i)^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$"
)
WINDOWS_INVALID_NAME = re.compile(r'[<>:"\\|?*\x00-\x1f]')


def fail(message: str) -> None:
    raise SystemExit(message)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def is_reparse(path: Path) -> bool:
    attributes = getattr(path.lstat(), "st_file_attributes", 0)
    return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))


def ordinary_file(path: Path) -> bool:
    return path.is_file() and not path.is_symlink() and not is_reparse(path)


def require_no_reparse_tree(root: Path) -> None:
    require(root.is_dir(), f"Missing directory: {root}")
    require(not root.is_symlink() and not is_reparse(root),
            f"Directory is a reparse point: {root}")
    for entry in root.rglob("*"):
        require(not entry.is_symlink() and not is_reparse(entry),
                f"Tree contains a reparse point: {entry}")


def safe_relative(value: object) -> str:
    require(isinstance(value, str) and value,
            "Manifest path must be a nonempty string.")
    require("\\" not in value and ":" not in value,
            f"Manifest path is not canonical: {value!r}")
    candidate = Path(value)
    require(not candidate.is_absolute(),
            f"Manifest path is absolute: {value}")
    require(all(part not in ("", ".", "..") for part in candidate.parts),
            f"Manifest path escapes its root: {value}")
    require(all(not WINDOWS_INVALID_NAME.search(part)
                and not part.endswith((" ", "."))
                and not WINDOWS_DEVICE_NAME.fullmatch(part)
                for part in candidate.parts),
            f"Manifest path is unsafe on Windows: {value}")
    canonical = candidate.as_posix()
    require(canonical == value,
            f"Manifest path is not canonical: {value}")
    return canonical


def validate_package_manifest(root: Path, manifest_path: Path) -> dict:
    require_no_reparse_tree(root)
    require(ordinary_file(manifest_path),
            f"Package manifest is missing or unsafe: {manifest_path}")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f"Package manifest is malformed: {exc}")

    require(isinstance(manifest, dict) and manifest.get("schema") == 1,
            "Package manifest schema must be 1.")
    require(manifest.get("product") == "DS4Windows",
            "Package manifest product is not DS4Windows.")
    require(manifest.get("architecture") == "x64",
            "Package manifest architecture must be x64.")
    require(isinstance(manifest.get("version"), str)
            and manifest["version"].strip(),
            "Package manifest version is empty.")
    records = manifest.get("files")
    require(isinstance(records, list) and records,
            "Package manifest has no file inventory.")

    indexed: dict[str, dict] = {}
    for record in records:
        require(isinstance(record, dict),
                "Package manifest file record is not an object.")
        relative = safe_relative(record.get("path"))
        folded = relative.casefold()
        require(folded not in indexed,
                f"Package manifest duplicates a case-insensitive path: {relative}")
        require(isinstance(record.get("size"), int)
                and record["size"] >= 0,
                f"Package manifest has an invalid size: {relative}")
        require(isinstance(record.get("sha256"), str)
                and re.fullmatch(r"[0-9A-F]{64}", record["sha256"]),
                f"Package manifest has an invalid SHA-256: {relative}")
        indexed[folded] = record

        path = root / Path(relative)
        require(ordinary_file(path),
                f"Manifest-bound file is missing or unsafe: {relative}")
        require(path.stat().st_size == record["size"],
                f"Manifest-bound size differs: {relative}")
        require(sha256(path) == record["sha256"],
                f"Manifest-bound SHA-256 differs: {relative}")

    actual = sorted(
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() and path.resolve() != manifest_path.resolve()
    )
    expected = sorted(
        record["path"] for record in records
    )
    require([value.casefold() for value in actual] ==
            [value.casefold() for value in expected],
            "Package manifest does not exactly cover the publish tree.")

    required = [
        "DS4Windows.exe",
        "DS4Windows.release",
        "ViiperNativeRuntimeMetadata.json",
        "extras/manage-viiper-native-package.ps1",
    ]
    for relative in required:
        require(relative.casefold() in indexed,
                f"Package manifest does not bind {relative}.")
    native_records = [
        record for record in records
        if record["path"].casefold().startswith(
            "extras/viiper-native-package/")
    ]
    require(native_records,
            "Package manifest does not bind a native package tree.")

    metadata = json.loads(
        (root / "ViiperNativeRuntimeMetadata.json").read_text(
            encoding="utf-8"))
    require(metadata.get("schemaVersion") == 1,
            "Native metadata schema must be 1.")
    require(metadata.get("releaseEligibility") == "production",
            "Composed installer media is not production-eligible.")
    require(metadata.get("productionSigningRoute") ==
            "HLK/WHCP dashboard signing",
            "Native metadata does not require HLK/WHCP signing.")
    return manifest


def read_text(path: Path) -> str:
    require(ordinary_file(path), f"Installer source is missing: {path}")
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        fail(f"Could not read installer source {path}: {exc}")


def reject_legacy_or_online_behavior(
    named_sources: Iterable[tuple[str, str]]
) -> None:
    for label, source in named_sources:
        for pattern in FORBIDDEN_INSTALLER_TEXT:
            match = pattern.search(source)
            require(match is None,
                    f"{label} retains forbidden installer behavior: "
                    f"{pattern.pattern}")


def element_by_id(root: ET.Element, element: str, identity: str) -> ET.Element:
    matches = [
        node for node in root.findall(f".//w:{element}", WIX)
        if node.get("Id") == identity
    ]
    require(len(matches) == 1,
            f"Bundle must contain exactly one {element} {identity}.")
    return matches[0]


def validate_bundle_source(path: Path) -> str:
    source = read_text(path)
    try:
        root = ET.fromstring(source)
    except ET.ParseError as exc:
        fail(f"Bundle source is invalid XML: {exc}")

    chain = root.find(".//w:Chain", WIX)
    require(chain is not None, "Bundle has no package chain.")
    ids = [node.get("Id") for node in list(chain)]
    require(ids == EXPECTED_CHAIN,
            f"Bundle chain is not exact: {ids!r}")

    variables = {
        node.get("Name"): node
        for node in root.findall(".//w:Variable", WIX)
    }
    require(set(variables) == {"TargetUserSid"},
            f"Bundle variables are not minimal: {sorted(variables)}")
    target = variables["TargetUserSid"]
    require(target.get("Persisted") == "yes"
            and target.get("Hidden") is None
            and target.get("Value") == "",
            "TargetUserSid must be empty and persisted by Burn.")

    searches = root.findall(
        ".//{http://wixtoolset.org/schemas/v4/wxs/util}RegistrySearch")
    search_map = {node.get("Variable"): node for node in searches}
    require("NativePackageReceipt" in search_map
            and "InstalledTargetUserSid" in search_map,
            "Bundle does not restore protected native receipt/SID state.")
    for variable in ("NativePackageReceipt", "InstalledTargetUserSid"):
        require(search_map[variable].get("Root") == "HKLM"
                and search_map[variable].get("Bitness") == "always64",
                f"{variable} search must use 64-bit HKLM.")

    setup = element_by_id(root, "ExePackage", "ViiperNativeSetup")
    remove = element_by_id(root, "ExePackage", "ViiperNativeRemove")
    expected_setup = {
        "InstallArguments":
            'install --target-user-sid "[TargetUserSid]"',
        "RepairArguments":
            'repair --target-user-sid "[TargetUserSid]"',
        "UninstallArguments":
            'uninstall --target-user-sid "[TargetUserSid]"',
    }
    for attribute, value in expected_setup.items():
        require(setup.get(attribute) == value,
                f"ViiperNativeSetup {attribute} is not exact.")
    require(setup.get("PerMachine") == "yes"
            and setup.get("Vital") == "yes"
            and setup.get("DetectCondition") ==
            'NativePackageReceipt = "Installed"',
            "ViiperNativeSetup lifecycle attributes are unsafe.")

    require(remove.get("InstallArguments") ==
            'uninstall --target-user-sid "[TargetUserSid]"'
            and remove.get("UninstallArguments") ==
            'install --target-user-sid "[TargetUserSid]"'
            and remove.get("DetectCondition") == "0"
            and remove.get("PerMachine") == "yes"
            and remove.get("Vital") == "yes",
            "ViiperNativeRemove rollback contract is unsafe.")

    for identity in (
        "CloseRunningApplications",
        "CloseRunningApplicationsForUninstall",
    ):
        preflight = element_by_id(root, "ExePackage", identity)
        require(preflight.get("InstallArguments") == "preflight"
                and preflight.get("DetectCondition") == "0"
                and preflight.get("Permanent") == "yes"
                and preflight.get("Vital") == "yes",
                f"{identity} contract is unsafe.")

    msi = element_by_id(root, "MsiPackage", "DS4WindowsMsi")
    require(msi.get("Vital") == "yes" and msi.get("Visible") == "no",
            "DS4Windows MSI must be vital and managed by Burn.")
    msi_properties = {
        node.get("Name"): node.get("Value")
        for node in msi.findall("w:MsiProperty", WIX)
    }
    require(msi_properties == {"DS4WINDOWS_BUNDLE_MANAGED": "1"},
            "Burn must pass the exact MSI trust-boundary property.")
    return source


def validate_setup_actions(source: str) -> None:
    required = [
        r'extras\manage-viiper-native-package.ps1',
        "ViiperNativeRuntimeMetadata.json",
        r'extras\viiper-native-package',
        "package-manifest.json",
        "-ExecutionPolicy Bypass -File ",
        " -Operation ",
        " -TargetUserSID ",
        "DS4WINDOWS_VIIPER_NATIVE_RESULT ",
        "ParseAndValidateReceipt",
        "RequireProtectedDirectoryAcl",
        "NativePackageTargetUserSid",
        "Global\\DS4Windows-VIIPER-Native-Setup",
        "process.WaitForExit();",
        "process.WaitForExit();",
    ]
    for token in required:
        require(token in source,
                f"SetupActions lost required contract token: {token}")
    for forbidden in (
        "AllowLocalTestPackage",
        "LocalTestAcknowledgement",
        "pnputil",
        "sc.exe",
        'GetProcessesByName("viiper")',
        "Directory.Delete(",
        "install-viiper",
    ):
        require(forbidden not in source,
                f"SetupActions crosses the VIIPER ownership boundary: {forbidden}")
    receipt_pattern = (
        r'\^\\"schemaVersion\\":1'  # deliberately only a source marker check
    )
    require("exactly one is required" in source
            and "safely-settled" in source
            and "not-required" in source
            and "manualRecoveryRequired" in source,
            "SetupActions receipt validation is incomplete.")
    require(source.count("DS4WINDOWS_VIIPER_NATIVE_RESULT ") == 1,
            "SetupActions must recognize one canonical receipt prefix.")
    require(source.count("AccessControlSections.Owner |") >= 2
            and source.count("AccessControlSections.Access), path)") == 2,
            "SetupActions ACL reads must request both owner and access sections.")
    acl = source[
        source.index("private static void RequireProtectedAcl("):
        source.index("private static VerifiedMedia VerifyProtectedNativeMedia(")
    ]
    for token in (
        "FileSystemRights.WriteData",
        "FileSystemRights.AppendData",
        "FileSystemRights.WriteExtendedAttributes",
        "FileSystemRights.DeleteSubdirectoriesAndFiles",
        "FileSystemRights.WriteAttributes",
        "FileSystemRights.Delete",
        "FileSystemRights.ChangePermissions",
        "FileSystemRights.TakeOwnership",
        "genericWrite",
        "genericAll",
    ):
        require(token in acl,
                f"SetupActions lost atomic ACL-control token: {token}")
    for token in (
        "FileSystemRights.Write |",
        "FileSystemRights.Modify |",
        "FileSystemRights.FullControl |",
    ):
        require(token not in acl,
                f"SetupActions restored composite ACL mask: {token}")
    setup_log = source[
        source.index("private static void InitializeProtectedLog()"):
        source.index("private sealed class PackageManifest")
    ]
    for token in (
        "OpenAndValidateOrdinaryDirectory(programData)",
        "CreateOrLockProtectedDirectory(productDirectory,",
        "CreateOrLockProtectedDirectory(installerDirectory,",
        "Directory.CreateDirectory(path, expectedSecurity)",
        "FileListDirectory",
        "FileFlagOpenReparsePoint",
        "FileFlagBackupSemantics",
        "FileShare.Read | FileShare.Write",
        "FileMode.CreateNew",
        "FileShare.Read, 4096, FileOptions.WriteThrough,",
        "stream.GetAccessControl()",
        "RequireExactLogSecurity(",
        "stream.SetLength(0)",
        "logStream.Write(bytes, 0, bytes.Length)",
        "DisposeProtectedLog()",
        "CreateFileW(",
    ):
        require(token in setup_log,
                f"SetupActions lost protected log token: {token}")
    for token in (
        "Directory.SetAccessControl",
        "File.Delete(logPath)",
        "File.Create(logPath)",
        "File.AppendAllText",
        "FileShare.Delete",
        "private static string logPath",
    ):
        require(token not in setup_log,
                f"SetupActions restored path-racy log behavior: {token}")
    initialization_order = [
        setup_log.index("OpenAndValidateOrdinaryDirectory(programData)"),
        setup_log.index("CreateOrLockProtectedDirectory(productDirectory,"),
        setup_log.index("CreateOrLockProtectedDirectory(installerDirectory,"),
        setup_log.index("OpenOrCreateProtectedLogFile("),
    ]
    require(initialization_order == sorted(initialization_order),
            "SetupActions protected-log hierarchy lock order is unsafe.")
    existing_log_order = [
        setup_log.index("existingHandle = CreateFileW(path,"),
        setup_log.index("stream = new FileStream(existingHandle,"),
        setup_log.index("var attributes = File.GetAttributes(path);"),
        setup_log.index("var actualSecurity = stream.GetAccessControl();"),
        setup_log.index("stream.SetLength(0);"),
    ]
    require(existing_log_order == sorted(existing_log_order),
            "SetupActions must lock, validate, then truncate an existing log.")
    del receipt_pattern


def validate_bootstrapper(source: str) -> None:
    required = [
        "PrepareTargetUserSid",
        '"InstalledTargetUserSid"',
        'GetVariableString("TargetUserSid")',
        'SetVariableString("TargetUserSid", sid, true)',
        "targetUserSidPrepared",
        "command.Resume != ResumeType.Reboot",
        "command.Relation != RelationType.Upgrade",
        "ViiperNativeSetup",
        "ViiperNativeRemove",
        "outgoingRelatedUninstall",
        "infrastructureRecoveryPass",
        "Global\\DS4Windows-Installer-Transaction",
        "Interlocked.CompareExchange(ref planStarted, 1, 0)",
    ]
    for token in required:
        require(token in source,
                f"Bootstrapper lost required state-machine token: {token}")
    for forbidden in (
        "SetInteractiveUserVariables",
        "TargetUserName",
        "TargetLocalAppData",
        "TargetRoamingAppData",
        "schtasks.exe",
    ):
        require(forbidden not in source,
                f"Bootstrapper retains unsafe user/lifecycle behavior: {forbidden}")


def validate_infrastructure_probe(source: str) -> None:
    for token in (
        '"NativePackageReceipt"',
        '"NativePackageTargetUserSid"',
        '"NativePackageMetadataSha256"',
        '"production"',
        '"VIIPERNativeBroker"',
        'key.GetValue("ImagePath")',
        "CommandLineMatches(",
        '"service"',
        '"--transport"',
        '"native-ude"',
        '"--key-file"',
        '"--log.file"',
        "ServiceControllerStatus.Running",
    ):
        require(token in source,
                f"Infrastructure probe lost exact health contract: {token}")
    broker = source[
        source.index("var brokerPath = Path.Combine("):
        source.index("var credentialPath = Path.Combine(")
    ]
    credential = source[
        source.index("var credentialPath = Path.Combine("):
        source.index("var logPath = Path.Combine(")
    ]
    log = source[
        source.index("var logPath = Path.Combine("):
        source.index("if (!IsOrdinaryFile(brokerPath)")
    ]
    require("Environment.SpecialFolder.ProgramFiles" in broker
            and "CommonApplicationData" not in broker
            and '"VIIPER", "viiper.exe"' in broker,
            "Infrastructure broker must be exact Program Files\\VIIPER\\viiper.exe.")
    require(all("Environment.SpecialFolder.CommonApplicationData" in block
                and "Environment.SpecialFolder.ProgramFiles" not in block
                for block in (credential, log))
            and '"VIIPER", "viiper.key.txt"' in credential
            and '"VIIPER", "viiper-native-broker.log"' in log,
            "Infrastructure credential/log paths must remain exact ProgramData paths.")


def validate_manager_security(repo: Path) -> None:
    manager = read_text(repo / "extras" /
                        "manage-viiper-native-package.ps1")
    security_test = read_text(repo / "installer" /
                              "Test-InstallerSecurityContracts.ps1")
    required = (
        "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)",
        "[IO.Directory]::CreateDirectory(",
        "[IO.FileSystemAclExtensions]::Create(",
        "function Assert-ProtectedStage {",
        "function Open-VerifiedStagedBroker {",
        "$sourceAlgorithm.ComputeHash($sourceStream)",
        "$sourceStream.CopyTo($destinationStream)",
        "[IO.FileShare]::None",
        "$stagedAlgorithm.ComputeHash($launchLock)",
        "$stagedBrokerLease.LaunchLock.Dispose()",
        "Refusing protected staging cleanup with unexpected entries",
    )
    for token in required:
        require(token in manager,
                f"Native manager lost protected-stage token: {token}")
    for token in (
        "Copy-Item -LiteralPath $brokerPath",
        "Get-FileHash -LiteralPath $stagedBroker",
        "Remove-Item -LiteralPath $stage -Recurse",
    ):
        require(token not in manager,
                f"Native manager restored pathname-racy staging: {token}")

    open_broker = manager[
        manager.index("function Open-VerifiedStagedBroker {"):
        manager.index("function Remove-ProtectedStage {")
    ]
    open_order = [
        open_broker.index("$sourceAlgorithm.ComputeHash($sourceStream)"),
        open_broker.index("$sourceStream.CopyTo($destinationStream)"),
        open_broker.index("$launchLock = [IO.FileStream]::new("),
        open_broker.index("$stagedAlgorithm.ComputeHash($launchLock)"),
    ]
    require(open_order == sorted(open_order),
            "Native manager source/copy/launch-lock verification order is unsafe.")
    main = manager[manager.rindex("$programDataRoot = "):]
    main_order = [
        main.index("Open-VerifiedStagedBroker"),
        main.index("Invoke-JoinedNativeProcess"),
        main.index("$stagedBrokerLease.LaunchLock.Dispose()"),
        main.index("Remove-ProtectedStage"),
        main.index("Write-StructuredOutcome -RequestedOperation"),
    ]
    require(main_order == sorted(main_order),
            "Native manager must hold the launch lock through join, then clean "
            "the stage before writing its final receipt.")

    for token in (
        "ReadAndExecute incorrectly overlaps",
        "Default Program Files",
        "Assert-SharingViolation",
        "Source lock allowed a concurrent writer",
        "Source lock allowed a concurrent rename/delete open",
        "Launch lock allowed a post-hash writer",
        "Launch lock allowed post-hash rename/delete",
        "$PSVersionTable.PSEdition",
        "preowned parent",
        "Junction",
        "No-share-delete directory handle allowed a path swap",
        "Held protected log allowed a concurrent writer",
        "Held protected log allowed a path swap",
        "Held protected log allowed deletion",
    ):
        require(token in security_test,
                f"Installer security regression lost test token: {token}")


def validate_build_script(source: str) -> None:
    required = [
        "DS4W_SIGN_CERT_PATH",
        "DS4W_SIGN_CERT_PASSWORD",
        "DS4W_SIGN_CERTIFICATE_SHA256",
        "DS4W_SIGN_TIMESTAMP_URL",
        "Set-RequiredAuthenticodeSignature",
        "Assert-AuthenticodeSignature",
        "-RequireProduction -RequirePackage",
        "https",
        "installerManifest",
        "sourceRevision",
        "nativeDriverBuildIdentity",
        "PublishRoot must not already exist",
        "BundleVersion must be numeric so Burn and the custom BA use identical upgrade ordering",
        "Test-InstallerSecurityContracts.ps1",
        "Get-Command pwsh.exe",
        "Windows PowerShell installer security contract failed",
        "PowerShell 7 installer security contract failed",
    ]
    for token in required:
        require(token in source,
                f"Installer build lost required release gate: {token}")
    for forbidden in (
        "if ($env:DS4W_SIGN_CERT_PATH)",
        "http://timestamp",
        "SkipSigning",
        "SkipApplicationPublish",
        "DS4W_SIGNTOOL_PATH",
        "Get-Command signtool.exe",
        "test-viiper-reboot-boundary",
        "install-viiper-backend",
    ):
        require(forbidden not in source,
                f"Installer build retains an unsafe release path: {forbidden}")

    order = [
        source.index("Set-RequiredAuthenticodeSignature -Path $applicationPath"),
        source.index("generate-installer-files.py"),
        source.index("Set-RequiredAuthenticodeSignature -Path $setupActions"),
        source.index("DS4Windows.Package.wixproj"),
        source.index("Set-RequiredAuthenticodeSignature -Path $msiPath"),
        source.index("DS4Windows.Bundle.wixproj"),
        source.index("Set-RequiredAuthenticodeSignature -Path $builtInstaller"),
        source.index("Publish-InstallerFileAtomically $pendingInstaller"),
    ]
    require(order == sorted(order),
            "Installer signing/composition/publication order is unsafe.")


def validate_product_source(path: Path) -> str:
    source = read_text(path)
    root = ET.fromstring(source)
    package = root.find(".//w:Package", WIX)
    require(package is not None
            and package.get("Scope") == "perMachine",
            "MSI must be per-machine.")
    require('<MajorUpgrade' in source
            and 'Id="ProgramFiles64Folder"' in source,
            "MSI must have per-machine upgrade and Program Files ownership.")
    require('Id="DS4WINDOWS_BUNDLE_MANAGED" Secure="yes"' in source
            and 'Condition="DS4WINDOWS_BUNDLE_MANAGED = 1"' in source,
            "MSI must reject direct install/repair/uninstall outside Burn.")
    require('Id="CommonAppDataFolder"' in source
            and 'Name="Start Menu"' in source
            and 'Name="Programs"' in source
            and 'Root="HKLM"' in source,
            "MSI shortcuts must use per-machine shell locations/keypaths.")
    require('Root="HKCU"' not in source,
            "Per-machine MSI must not use elevated-user HKCU keypaths.")
    return source


def validate_import_script(path: Path) -> str:
    source = read_text(path)
    required = [
        "ExpectedProvenanceSha256",
        "viiper-native-udecx-production",
        "releaseEligibility",
        "Assert-NoReparseDirectoryChain",
        "Assert-ProtectedSourceAcl",
        "The production provenance manifest must be an ordinary file directly under SourceRoot.",
        "Production provenance path escapes its root",
        "Production package source contains an unbound file",
        "viiper-native-package.stage-",
        "[IO.Directory]::Move($stage, $destinationPackage)",
        "[IO.File]::Replace(",
        "$packageCommitted = $true",
        "Refusing unsafe failed-import cleanup",
    ]
    for token in required:
        require(token in source,
                f"Production native import lost required gate: {token}")
    for forbidden in (
        "Invoke-WebRequest",
        "Invoke-RestMethod",
        "Start-BitsTransfer",
        "WebClient",
        "HttpClient",
        "Start-Process",
        "runas",
    ):
        require(forbidden.casefold() not in source.casefold(),
                f"Production native import retains online/elevation behavior: {forbidden}")
    verify = source.index("Production package file differs from provenance")
    package_commit = source.index("[IO.Directory]::Move($stage, $destinationPackage)")
    metadata_commit = source.index("[IO.File]::Replace(")
    require(verify < package_commit < metadata_commit,
            "Production native import verification/commit order is unsafe.")
    acl = source[
        source.index("function Assert-ProtectedSourceAcl {"):
        source.index("function Get-SafeRelativePath {")
    ]
    for token in (
        "::WriteData",
        "::AppendData",
        "::WriteExtendedAttributes",
        "::DeleteSubdirectoriesAndFiles",
        "::WriteAttributes",
        "::Delete",
        "::ChangePermissions",
        "::TakeOwnership",
        "[long]0x10000000",
        "[long]0x40000000",
        "$acl.GetAccessRules(",
        "[Security.Principal.SecurityIdentifier]",
    ):
        require(token in acl,
                f"Production import lost atomic ACL-control token: {token}")
    for token in ("::Write -bor", "::Modify -bor", "::FullControl -bor"):
        require(token not in acl,
                f"Production import restored composite ACL mask: {token}")
    return source


def validate_workflows(repo: Path) -> None:
    ci = read_text(repo / ".github" / "workflows" / "ci-build.yml")
    release = read_text(repo / ".github" / "workflows" / "release.yml")
    for label, source in (("CI", ci), ("release", release)):
        require("continue-on-error:" not in source,
                f"{label} workflow may not weaken a required gate.")
        action_refs = re.findall(r"(?m)^\s*uses:\s*[^\s@]+@([^\s#]+)", source)
        require(action_refs and all(re.fullmatch(r"[0-9a-f]{40}", ref)
                                    for ref in action_refs),
                f"{label} workflow actions must be pinned to exact commits.")

    for token in (
        "Validate native package contract (Windows PowerShell 5.1)",
        "Validate native package contract (PowerShell 7)",
        "Validate installer security contracts (Windows PowerShell 5.1)",
        "Validate installer security contracts (PowerShell 7)",
        "test-installer-state-machine.py",
        "validate-installer.py --source-only",
        "Prove production build fails without signing credentials",
        "Test-InstallerComposition.ps1",
        "dotnet test",
        "git status --short",
    ):
        require(token in ci, f"CI workflow lost required gate: {token}")

    for token in (
        'tags:\n      - "v*.*.*"',
        "Verify immutable tag provenance",
        "Re-verify exact release checkout",
        "needs: test",
        "environment: production",
        "Import-ProductionNativePackage.ps1",
        "DS4W_NATIVE_PACKAGE_PROVENANCE_SHA256",
        "-RequireProduction -RequirePackage",
        "DS4W_SIGN_PFX_BASE64",
        "DS4W_SIGN_CERT_PASSWORD",
        "DS4W_SIGN_CERTIFICATE_SHA256",
        "DS4W_SIGN_TIMESTAMP_URL",
        "build-installer.ps1",
        "actions/attest-build-provenance@",
        "actions/upload-artifact@",
        "SHA256SUMS.txt",
        "Release already exists; assets will not be overwritten.",
        "gh release create $tag @assets --draft --verify-tag",
        "gh release edit $tag --draft=false",
        "Remove ephemeral signing credential",
        "Windows PowerShell installer security contract failed",
        ".\\installer\\Test-InstallerSecurityContracts.ps1",
    ):
        require(token in release,
                f"Release workflow lost required fail-closed gate: {token}")
    require(release.index("needs: test") <
            release.index("Import-ProductionNativePackage.ps1") <
            release.index("build-installer.ps1") <
            release.index("Remove ephemeral signing credential") <
            release.index("actions/attest-build-provenance@") <
            release.index("gh release create"),
            "Release test/import/build/attest/publication order is unsafe.")


def assert_valid_authenticode(paths: list[Path]) -> None:
    if os.name != "nt":
        return
    escaped = [str(path).replace("'", "''") for path in paths]
    literals = ",".join(f"'{value}'" for value in escaped)
    command = (
        "$ErrorActionPreference='Stop';"
        f"$paths=@({literals});"
        "foreach($path in $paths){"
        "$signature=Get-AuthenticodeSignature -LiteralPath $path;"
        "if($signature.Status -ne 'Valid' -or "
        "$null -eq $signature.TimeStamperCertificate){"
        "throw \"Invalid or untimestamped Authenticode signature: $path\""
        "}}"
    )
    result = subprocess.run(
        ["powershell.exe", "-NoLogo", "-NoProfile",
         "-NonInteractive", "-Command", command],
        text=True, capture_output=True, check=False,
    )
    require(result.returncode == 0,
            "Authenticode composition check failed: " +
            (result.stderr or result.stdout).strip())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-root", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--installer", type=Path)
    parser.add_argument("--bundle-source", type=Path, required=True)
    parser.add_argument("--setup-actions-source", type=Path, required=True)
    parser.add_argument("--bootstrapper-source", type=Path, required=True)
    parser.add_argument("--product-source", type=Path)
    parser.add_argument("--build-script", type=Path)
    parser.add_argument("--source-only", action="store_true")
    args = parser.parse_args()

    repo = args.bundle_source.resolve().parents[2]
    product_source = args.product_source or (
        repo / "installer" / "DS4Windows.Package" / "Product.wxs")
    build_script = args.build_script or (
        repo / "installer" / "build-installer.ps1")
    import_script = repo / "installer" / "Import-ProductionNativePackage.ps1"
    infrastructure_probe_path = (
        repo / "installer" / "DS4Windows.Bootstrapper" /
        "InfrastructureProbe.cs"
    )

    bundle = validate_bundle_source(args.bundle_source)
    setup = read_text(args.setup_actions_source)
    bootstrapper = read_text(args.bootstrapper_source)
    infrastructure_probe = read_text(infrastructure_probe_path)
    product = validate_product_source(product_source)
    build = read_text(build_script)
    production_import = validate_import_script(import_script)
    reject_legacy_or_online_behavior([
        ("Bundle", bundle),
        ("SetupActions", setup),
        ("Bootstrapper", bootstrapper),
        ("Infrastructure probe", infrastructure_probe),
        ("MSI", product),
        ("build", build),
        ("production native import", production_import),
    ])
    validate_setup_actions(setup)
    validate_bootstrapper(bootstrapper)
    validate_infrastructure_probe(infrastructure_probe)
    validate_manager_security(repo)
    validate_build_script(build)
    validate_workflows(repo)

    if args.source_only:
        require(args.publish_root is None
                and args.manifest is None
                and args.installer is None,
                "--source-only cannot accept composed artifacts.")
    else:
        require(args.publish_root is not None
                and args.manifest is not None
                and args.installer is not None,
                "Composed validation requires publish root, manifest, and installer.")
        publish_root = args.publish_root.resolve()
        manifest_path = args.manifest.resolve()
        installer = args.installer.resolve()
        validate_package_manifest(publish_root, manifest_path)
        require(ordinary_file(installer),
                f"Installer is missing or unsafe: {installer}")
        require(installer.stat().st_size > 256 * 1024,
                "Compiled Burn installer is unexpectedly small.")
        with installer.open("rb") as stream:
            require(stream.read(2) == b"MZ",
                    "Compiled Burn installer is not a Windows PE file.")
        assert_valid_authenticode([
            installer,
            publish_root / "DS4Windows.exe",
            publish_root / "extras" /
            "manage-viiper-native-package.ps1",
        ])

    print("Native installer validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
