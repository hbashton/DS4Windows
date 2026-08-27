#!/usr/bin/env python3
"""Fail CI if a standard installer composition is incomplete or unsafe."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath


REQUIRED_PUBLISH_FILES = {
    "DS4Windows.exe",
    "DS4Windows.release",
    "extras/install-viiper-backend.ps1",
    "extras/VIIPER-0.1.1-x64.exe",
    "extras/VIIPER-0.1.1-x64.exe.sha256",
    "extras/VIIPER-0.1.1-LICENSES.txt",
    "extras/VIIPER-0.1.1-PROVENANCE.txt",
    "extras/USBip-0.9.7.7-x64.exe",
    "extras/HidHide_1.5.230_x64.exe",
    "extras/FakerInput_0.1.0_x64.msi",
}

VIIPER_RELEASE = {
    "Version": "v0.1.1",
    "Source": "https://github.com/hbashton/VIIPER",
    "Source commit": "2854156075187093a286af6b39d8323425c9bfcb",
    "Toolchain": "Go 1.26.2 windows/amd64",
    "Build": "GOOS=windows GOARCH=amd64 CGO_ENABLED=0 BUILD_TYPE=Release",
    "Embedded build date": "2026-08-26T22:12:59Z",
    "Binary": "VIIPER-0.1.1-x64.exe",
    "Binary SHA-256": "3847E8669BBFAA5C08FC13B83231439AE203D6C46B67A3F1E5874A3D82D05E2F",
    "Authenticode status at packaging": "NotSigned",
    "License notice": "VIIPER-0.1.1-LICENSES.txt",
    "License notice SHA-256": "5B33F8E13DD9417015CC9968B552D590AFEFAB314E3A6EB1AF4EF39C7CE5904E",
}
VIIPER_INFRASTRUCTURE_MARKER = "VIIPER-0.1.1+USBIP-0.9.7.7"


def parse_unique_provenance(source: str) -> dict[str, str]:
    fields: dict[str, str] = {}
    for line in source.splitlines():
        line = line.strip()
        if not line or line == "VIIPER bundled artifact provenance":
            continue
        key, separator, value = line.partition(":")
        if not separator or not key.strip() or not value.strip():
            raise SystemExit("Packaged VIIPER provenance contains a malformed field.")
        key = key.strip()
        if key in fields:
            raise SystemExit("Packaged VIIPER provenance contains a duplicate field: " + key)
        fields[key] = value.strip()
    return fields


def sha256(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest().upper()


def validate_named_xaml_resources(source_root: Path) -> None:
    """Reject unresolved named WPF resource references before packaging.

    WPF can defer some StaticResource failures until a view is constructed,
    which previously allowed a clean install to succeed and then crash on the
    first MainWindow load. The application uses named resources globally, so
    every named reference must have a source declaration somewhere in the
    compiled XAML tree. Type-key expressions are resolved by WPF and are not
    named-resource references.
    """
    declared: set[str] = set()
    referenced: dict[str, set[str]] = {}
    xaml_files = sorted((source_root / "DS4Windows").rglob("*.xaml"))
    for path in xaml_files:
        source = path.read_text(encoding="utf-8-sig")
        declared.update(re.findall(r'x:Key\s*=\s*["\']([^"\']+)', source))
        for resource_kind in ("StaticResource", "DynamicResource"):
            for key in re.findall(
                rf"\{{{resource_kind}\s+([^}}\s,]+)", source
            ):
                if key.startswith("{"):
                    continue
                referenced.setdefault(key, set()).add(
                    path.relative_to(source_root).as_posix()
                )

    missing = sorted(set(referenced) - declared)
    if missing:
        details = "; ".join(
            f"{key} ({', '.join(sorted(referenced[key]))})"
            for key in missing
        )
        raise SystemExit("Unresolved named WPF resource reference(s): " + details)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--installer", type=Path, required=True)
    parser.add_argument("--bundle-source", type=Path, required=True)
    args = parser.parse_args()

    source_root = Path(__file__).resolve().parent.parent
    validate_named_xaml_resources(source_root)

    missing = sorted(path for path in REQUIRED_PUBLISH_FILES if not (args.publish_root / path).is_file())
    if missing:
        raise SystemExit("Installer payload is missing: " + ", ".join(missing))
    if not args.installer.is_file() or args.installer.stat().st_size < 1024 * 1024:
        raise SystemExit(f"Installer EXE was not produced correctly: {args.installer}")

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    if (
        manifest.get("schema") != 1
        or manifest.get("product") != "DS4Windows"
        or manifest.get("architecture") != "x64"
        or not isinstance(manifest.get("files"), list)
    ):
        raise SystemExit("Installer package manifest metadata is invalid.")
    release_id = (args.publish_root / "DS4Windows.release").read_text(
        encoding="utf-8-sig"
    ).strip()
    if manifest.get("version") != release_id:
        raise SystemExit("Package manifest version does not match DS4Windows.release.")

    manifest_paths = [entry.get("path") for entry in manifest["files"]]
    if any(not isinstance(path, str) or not path for path in manifest_paths):
        raise SystemExit("Package manifest contains an invalid path.")
    if len({path.casefold() for path in manifest_paths}) != len(manifest_paths):
        raise SystemExit("Package manifest contains duplicate Windows paths.")
    publish_resolved = args.publish_root.resolve()
    for relative in manifest_paths:
        parsed = PurePosixPath(relative)
        resolved = (args.publish_root / Path(*parsed.parts)).resolve()
        invalid_component = any(
            re.search(r'[<>:"\\|?*\x00-\x1F]', part)
            or part.endswith((" ", "."))
            or re.fullmatch(
                r"(?i)(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?",
                part,
            )
            for part in parsed.parts
        )
        if (
            parsed.is_absolute()
            or any(part in {"", ".", ".."} for part in parsed.parts)
            or "\\" in relative
            or invalid_component
            or not resolved.is_relative_to(publish_resolved)
        ):
            raise SystemExit(f"Package manifest contains an unsafe path: {relative}")
    manifest_resolved = args.manifest.resolve()
    actual_paths = {
        path.relative_to(args.publish_root).as_posix().casefold()
        for path in args.publish_root.rglob("*")
        if path.is_file() and path.resolve() != manifest_resolved
    }
    if {path.casefold() for path in manifest_paths} != actual_paths:
        raise SystemExit("Package manifest does not exactly own the publish tree.")

    generated_wix_path = (
        args.bundle_source.parent.parent
        / "DS4Windows.Package"
        / "GeneratedFiles.wxs"
    )
    generated_wix_xml = ET.fromstring(
        generated_wix_path.read_text(encoding="utf-8")
    )
    wix_namespace = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
    publish_prefix = "$(var.PublishRoot)\\"
    generated_paths = set()
    for file_element in generated_wix_xml.findall(".//w:File", wix_namespace):
        source = file_element.get("Source", "")
        if not source.startswith(publish_prefix):
            raise SystemExit("Generated MSI contains a non-publish-root file.")
        generated_paths.add(
            source[len(publish_prefix):].replace("\\", "/").casefold()
        )
    expected_generated_paths = actual_paths | {
        args.manifest.relative_to(args.publish_root).as_posix().casefold()
    }
    if generated_paths != expected_generated_paths:
        missing_from_msi = sorted(expected_generated_paths - generated_paths)
        extra_in_msi = sorted(generated_paths - expected_generated_paths)
        details = []
        if missing_from_msi:
            details.append("missing=" + ", ".join(missing_from_msi[:5]))
        if extra_in_msi:
            details.append("extra=" + ", ".join(extra_in_msi[:5]))
        raise SystemExit(
            "Generated MSI does not exactly own the publish tree (" +
            "; ".join(details) + ")."
        )

    for entry in manifest["files"]:
        path = args.publish_root / entry["path"]
        if not path.is_file() or path.stat().st_size != entry["size"] or sha256(path) != entry["sha256"]:
            raise SystemExit(f"Payload hash mismatch: {entry['path']}")

    bundle = args.bundle_source.read_text(encoding="utf-8")
    required_contracts = [
        'Name="CreateDesktopShortcut"',
        'Name="InstallHidHide"',
        'Name="InstallFakerInput"',
        'Id="ViiperUsbipSetup"',
        'Id="DS4WindowsMsi"',
        'Id="PostUninstallCleanup"',
        'Id="CloseRunningApplications"',
        'Id="CloseRunningApplicationsForUninstall"',
        'RepairArguments="repair ',
        'UninstallArguments="uninstall ',
        'InstallCondition="InstallHidHide"',
        'InstallCondition="InstallFakerInput"',
        'Name="TargetUserSid"',
        '--target-roaming-appdata',
        'Variable="ManagedInstallRegistered"',
        'Variable="ManagedViiperPresent"',
        'CacheId="DS4WindowsSetupActionsPreflight-$(var.SetupActionsHash)"',
        'CacheId="DS4WindowsSetupActionsUninstallPreflight-$(var.SetupActionsHash)"',
        'CacheId="DS4WindowsSetupActionsInfrastructure-$(var.SetupActionsHash)"',
        'Id="HidHide"',
        'Id="FakerInput"',
        'Vital="yes"',
    ]
    for contract in required_contracts:
        if contract not in bundle:
            raise SystemExit("Bundle contract missing: " + contract)

    bundle_xml = ET.fromstring(bundle)
    chain = bundle_xml.find(".//w:Chain", wix_namespace)
    if chain is None or chain.get("DisableSystemRestore") != "yes":
        raise SystemExit(
            "Burn system restore must stay disabled so elevation cannot block "
            "before package execution."
        )
    packages = {
        element.get("Id"): element
        for element in bundle_xml.findall(".//w:Chain/*", wix_namespace)
    }
    for package_id in (
        "PostUninstallCleanup",
        "CloseRunningApplications",
        "CloseRunningApplicationsForUninstall",
        "DS4WindowsMsi",
        "ViiperUsbipSetup",
    ):
        if package_id not in packages:
            raise SystemExit(f"Bundle package is missing: {package_id}")
        if packages[package_id].get("Vital") != "yes":
            raise SystemExit(f"Bundle package must be vital: {package_id}")
    for package_id in ("HidHide", "FakerInput"):
        if package_id not in packages:
            raise SystemExit(f"Bundle package is missing: {package_id}")
        if packages[package_id].get("Vital") != "no":
            raise SystemExit(
                f"Optional package must not roll back the core install: {package_id}"
            )
    if packages["HidHide"].get("InstallCondition") != "InstallHidHide":
        raise SystemExit("HidHide optional-install condition is invalid.")
    if packages["FakerInput"].get("InstallCondition") != "InstallFakerInput":
        raise SystemExit("FakerInput optional-install condition is invalid.")
    cache_ids = {
        packages["PostUninstallCleanup"].get("CacheId"),
        packages["CloseRunningApplications"].get("CacheId"),
        packages["CloseRunningApplicationsForUninstall"].get("CacheId"),
        packages["ViiperUsbipSetup"].get("CacheId"),
    }
    if None in cache_ids or len(cache_ids) != 4 or any(
        "$(var.SetupActionsHash)" not in cache_id for cache_id in cache_ids
    ):
        raise SystemExit("Setup helper cache identities are not content-addressed.")
    chain_ids = [
        element.get("Id")
        for element in bundle_xml.findall(".//w:Chain/*", wix_namespace)
    ]
    if (chain_ids[:2] != ["PostUninstallCleanup", "CloseRunningApplications"] or
            chain_ids[-1] != "CloseRunningApplicationsForUninstall"):
        raise SystemExit(
            "Post-uninstall cleanup and process preflights must bracket the "
            "forward/reverse Burn chain."
        )

    build_script = (args.bundle_source.parent.parent / "build-installer.ps1").read_text(encoding="utf-8")
    for contract in [
        'Get-FileHash -LiteralPath $setupActions -Algorithm SHA256',
        '-p:SetupActionsHash=$setupActionsHash',
        'Global\\DS4Windows-Installer-Build',
        'previous orphaned WiX/MSI build',
        'Publish-InstallerFileAtomically',
        '$pendingInstaller',
        '[IO.File]::Replace',
        'does not match the completed',
        '-p:Version=$ProductVersion -p:InformationalVersion=$DisplayVersion',
        'test-viiper-reboot-boundary.ps1',
        'test-installer-state-machine.py',
        '[switch]$RequireSigning',
        'Unsigned public installers are intentionally blocked',
        'DS4W_SIGN_EXPECTED_THUMBPRINT',
        '$signature.SignerCertificate.Thumbprint',
        '$signature.TimeStamperCertificate',
        'else { "http://timestamp.digicert.com" }',
        'Invoke-SignAndVerify $msiPath',
        'Invoke-SignAndVerify $pendingInstaller',
    ]:
        if contract not in build_script:
            raise SystemExit("Installer build identity contract missing: " + contract)
    manifest_commit = build_script.rfind(
        "Publish-InstallerFileAtomically $pendingManifest $finalManifest"
    )
    installer_commit = build_script.rfind(
        "Publish-InstallerFileAtomically $pendingInstaller $finalInstaller"
    )
    if manifest_commit < 0 or installer_commit < manifest_commit:
        raise SystemExit(
            "The verified manifest must publish before the installer commit point."
        )
    release_workflow = (
        args.bundle_source.parent.parent.parent
        / ".github"
        / "workflows"
        / "release.yml"
    ).read_text(encoding="utf-8")
    for contract in [
        'DS4W_SIGN_CERT_BASE64: ${{ secrets.DS4W_SIGN_CERT_BASE64 }}',
        'DS4W_SIGN_EXPECTED_THUMBPRINT: ${{ secrets.DS4W_SIGN_EXPECTED_THUMBPRINT }}',
        'RELEASE_TAG: ${{ github.event.release.tag_name }}',
        'TAG="$RELEASE_TAG"',
        "Public release signing material or approved signer identity is missing.",
        '$firstPartyBinaries = @(".\\bin\\x64\\Release\\output\\DS4Windows.exe")',
        "First-party release signing failed for $path.",
        'RequireSigning = $true',
        '$signature.SignerCertificate.Thumbprint -ne $approvedThumbprint',
        '-not $signature.TimeStamperCertificate',
        'if: always()',
        '$certificatePath = Join-Path $env:RUNNER_TEMP "ds4windows-release.pfx"',
        'Remove-Item -LiteralPath $certificatePath -Force',
        "VIIPER is an immutable release input",
    ]:
        if contract not in release_workflow:
            raise SystemExit(
                "First-party release signing contract missing: " + contract
            )
    forbidden_release_interpolation = [
        "TAG=${{ github.event.release.tag_name }}",
        "/p:AssemblyVersion=${{ env.BINARY_VERSION }}",
        "post-build.py .\\bin\\x64\\Release\\output . ${{env.VERSION}}",
        "gh release upload ${{github.event.release.tag_name}}",
        "DS4W_SIGNING_ENABLED=false",
    ]
    for contract in forbidden_release_interpolation:
        if contract in release_workflow:
            raise SystemExit(
                "Release workflow embeds untrusted context or permits unsigned publication: "
                + contract
            )
    if "Portable" in bundle or "InstallFolder" in bundle or "destination" in bundle.lower():
        raise SystemExit("The standard installer must not expose a portable or destination-selection path.")
    for contract in [
        'Name="SetupCorrelationId"',
        'Persisted="yes"',
        '--correlation-id &quot;[SetupCorrelationId]&quot;',
    ]:
        if contract not in bundle:
            raise SystemExit(
                "Burn-to-infrastructure correlation contract missing: " +
                contract
            )

    product = (args.bundle_source.parent.parent / "DS4Windows.Package" / "Product.wxs").read_text(encoding="utf-8")
    for contract in [
        '<MajorUpgrade',
        'Scope="perMachine"',
        'Root="HKLM" Key="Software\\DS4Windows"',
    ]:
        if contract not in product:
            raise SystemExit("MSI upgrade contract missing: " + contract)

    installer_root = args.bundle_source.parent.parent
    bootstrapper = (
        installer_root
        / "DS4Windows.Bootstrapper"
        / "InstallerApplication.cs"
    ).read_text(encoding="utf-8")
    for contract in [
        'e.PackageId, "PostUninstallCleanup"',
        'e.PackageId, "CloseRunningApplications"',
        '"CloseRunningApplicationsForUninstall"',
        "plannedAction == LaunchAction.Install",
        "plannedAction == LaunchAction.Repair",
        "plannedAction == LaunchAction.Uninstall",
        "? RequestState.Present",
        "IsRelatedBundleNewer",
        "ShowFailure(1638",
        "Interlocked.CompareExchange(ref planStarted, 1, 0)",
        'engine.SetVariableString("SetupCorrelationId"',
        'Path.Combine(Environment.SystemDirectory, "schtasks.exe")',
    ]:
        if contract not in bootstrapper:
            raise SystemExit(
                "Bootstrapper process-quiescence contract missing: " +
                contract
            )

    setup_actions = (installer_root / "DS4Windows.SetupActions" / "Program.cs").read_text(encoding="utf-8")
    for contract in [
        r'@"Global\DS4Windows-VIIPER-Setup"',
        'SetupResumeShortcut',
        'KillProcessTree(process)',
        'IsInfrastructureCommitted()',
        'ValidateSuppliedInteractiveUser',
        'RegistryView.Registry64',
        'return 1618;',
        'ValidateManagedInstallRoot(installRoot)',
        'FileAttributes.ReparsePoint',
        'EnsureDirectoryPathHasNoReparsePoints(resumeRoot)',
        'ProtectResumeDirectory(resumeRoot, targetUser.Sid)',
        'HashesEqual(bundleSource, stagedBundle)',
        '=== DS4Windows setup invocation ',
        'IsRecognizedProductProcess(process, processName',
        'FileVersionInfo.GetVersionInfo(executablePath)',
        'EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot)',
        'return RunWithSetupMutex(PreflightLocked);',
        'completed with exit code',
        'AppendLogWithRetry',
        'SystemTool("WindowsPowerShell", "v1.0",',
        'arguments.Append(" -CorrelationId ")',
        'ConfigureCommonShortcuts(ds4Path, desktopShortcut)',
        'ReadArgument(args, "--correlation-id")',
        'NormalizeCorrelationId(',
        'Environment.SpecialFolder.CommonPrograms',
        'Environment.SpecialFolder.CommonDesktopDirectory',
        'RemoveCommonShortcuts()',
    ]:
        if contract not in setup_actions:
            raise SystemExit("Setup action safety contract missing: " + contract)
    if 'SetValue("DS4WindowsSetupResume"' in setup_actions:
        raise SystemExit("Setup must not create a custom HKLM RunOnce entry.")

    backend_script = (
        args.bundle_source.parent.parent.parent
        / "extras"
        / "install-viiper-backend.ps1"
    ).read_text(encoding="utf-8")
    for contract in [
        '"Global\\DS4Windows-VIIPER-Setup"',
        "Test-SafePackageRelativePath",
        "Assert-SafeManagedDirectory",
        "Install-ViiperAtomically",
        "Resolve-UsbipReplacementBoundary",
        "Commit-InfrastructureReadiness",
        "Test-RecognizedProductExecutable",
        '$script:InstallerLogRoot = Assert-SafeManagedDirectory',
        '"VIIPER-0.1.1-x64.exe"',
        '[Version]"0.9.7.7"',
        '"USBip-0.9.7.7-x64.exe"',
        'Start-AndVerifyViiper',
        'Start-AndVerifyViiperDirectly',
        'Suspend-StartupTasksUntilInfrastructureReady',
        'Set-InfrastructureStartupFailClosed',
        '$script:UsbipExecutableSha256',
        'pinned USB-IP package and runtime ABI pass after reboot',
        'registration attempt " +',
        'retrying the same packaged executable directly',
        'New-ScheduledTaskTrigger -AtLogOn',
        'infrastructure-actions.log',
        '[string]::IsNullOrWhiteSpace($triggerUser)',
        "sourceInfo.Length -eq $destinationInfo.Length",
        "destinationHash = (Get-FileHash",
        '$script:InstallDir = Join-Path $script:ManagedRoot "VIIPER"',
        '$script:KeepDs4WindowsPortable',
        '$script:CorrelationId',
        "$CorrelationId.Trim() -notmatch '^[0-9A-Fa-f]{32}$'",
        'Protect-ElevatedTaskTargetDirectory $script:InstallDir "VIIPER"',
    ]:
        if contract not in backend_script:
            raise SystemExit(
                "Backend installer safety contract missing: " + contract
            )
    legacy_network_contracts = [
        "api.github.com/repos/hbashton/VIIPER",
        "viiper-windows-amd64.zip",
        "Invoke-WebRequest",
        "DownloadFile(",
        "Start-BitsTransfer",
    ]
    for contract in legacy_network_contracts:
        if contract.casefold() in backend_script.casefold():
            raise SystemExit(
                "Backend installer must use only verified offline payloads; "
                "legacy network acquisition found: " + contract
            )

    bootstrapper = (installer_root / "DS4Windows.Bootstrapper" / "InstallerApplication.cs").read_text(encoding="utf-8")
    for contract in [
        'command.Resume == ResumeType.Reboot',
        r'@"Global\DS4Windows-Installer-Transaction"',
        'if (command.Resume != ResumeType.Reboot)',
        'result = 3010;',
        'CloseWithCurrentResult()',
        'packageStates.Clear()',
        'InfrastructureFailureSummary()',
        'SetupActionsLogPath',
        'deferInfrastructureUntilUpgradeCompletes',
        'infrastructureRecoveryPass',
        'PlanRelatedBundle +=',
        'e.State = RequestState.None;',
        'parentOwnedRelatedUninstall',
        'command.Relation == RelationType.Upgrade &&',
        'action == LaunchAction.Uninstall',
        'command.Relation == RelationType.Upgrade',
        'plannedAction == LaunchAction.Uninstall',
        'Close(result);',
        'IsRelatedBundleNewer',
        'IsInstallerBusyStatus',
        'BOOTSTRAPPER_EXECUTEPACKAGECOMPLETE_ACTION.Retry',
        'ShowInstallerBusyRetry',
        '0x80070652u',
    ]:
        if contract not in bootstrapper:
            raise SystemExit("Bootstrapper lifecycle contract missing: " + contract)

    probe = (installer_root / "DS4Windows.Bootstrapper" / "InfrastructureProbe.cs").read_text(encoding="utf-8")
    for contract in [
        '"InfrastructureState"',
        'BeginOutputReadLine()',
        'BeginErrorReadLine()',
        'RegistryView.Registry64',
        'ViiperApiReady()',
        'ExpectedUsbipHash',
        'IsCompatibleUsbipProbe',
    ]:
        if contract not in probe:
            raise SystemExit("Infrastructure probe contract missing: " + contract)
    expected_hash = re.search(
        r'ExpectedViiperHash\s*=\s*"([0-9A-F]{64})"', probe
    )
    actual_viiper_hash = sha256(
        args.publish_root / "extras" / "VIIPER-0.1.1-x64.exe"
    )
    sidecar_hash = (
        args.publish_root / "extras" / "VIIPER-0.1.1-x64.exe.sha256"
    ).read_text(encoding="utf-8").split()[0].upper()
    if sidecar_hash != actual_viiper_hash:
        raise SystemExit("Packaged VIIPER hash sidecar is stale.")
    if not expected_hash or expected_hash.group(1) != actual_viiper_hash:
        raise SystemExit("Bootstrapper VIIPER identity does not match its packaged binary.")

    viiper_provenance = (
        args.publish_root / "extras" / "VIIPER-0.1.1-PROVENANCE.txt"
    ).read_text(encoding="utf-8")
    viiper_license_hash = sha256(
        args.publish_root / "extras" / "VIIPER-0.1.1-LICENSES.txt"
    )
    provenance_fields = parse_unique_provenance(viiper_provenance)
    if provenance_fields != VIIPER_RELEASE:
        mismatches = sorted(
            key for key in set(provenance_fields) | set(VIIPER_RELEASE)
            if provenance_fields.get(key) != VIIPER_RELEASE.get(key)
        )
        raise SystemExit(
            "Packaged VIIPER provenance does not match the pinned release tuple: "
            + ", ".join(mismatches)
        )
    if actual_viiper_hash != VIIPER_RELEASE["Binary SHA-256"]:
        raise SystemExit("Packaged VIIPER binary does not match the pinned release hash.")
    if viiper_license_hash != VIIPER_RELEASE["License notice SHA-256"]:
        raise SystemExit("Packaged VIIPER license does not match the pinned release hash.")

    functional_version_contracts = [
        (probe, r'ExpectedViiperVersion\s*=\s*"([^"]+)"', "0.1.1", "probe version"),
        (probe, r'ExpectedMarker\s*=\s*"([^"]+)"', VIIPER_INFRASTRUCTURE_MARKER, "probe marker"),
        (backend_script, r'\$script:InfrastructureVersion\s*=\s*"([^"]+)"', VIIPER_INFRASTRUCTURE_MARKER, "backend marker"),
        (setup_actions, r'InfrastructureVersion\s*=\s*\r?\n?\s*"([^"]+)"', VIIPER_INFRASTRUCTURE_MARKER, "setup marker"),
        (bundle, r'DetectCondition="InfrastructureInstalled = &quot;([^&]+)&quot;', VIIPER_INFRASTRUCTURE_MARKER, "bundle marker"),
    ]
    for source, pattern, expected, label in functional_version_contracts:
        matches = re.findall(pattern, source)
        if matches != [expected]:
            raise SystemExit(
                f"VIIPER {label} does not uniquely match the pinned release: {matches}"
            )

    setup_manager = (
        args.bundle_source.parent.parent.parent
        / "DS4Windows"
        / "DS4Control"
        / "Viiper"
        / "ViiperSetupManager.cs"
    ).read_text(encoding="utf-8")
    manager_hash = re.search(
        r'SupportedViiperSha256\s*=\s*\n?\s*"([0-9A-F]{64})"',
        setup_manager,
    )
    if not manager_hash or manager_hash.group(1) != actual_viiper_hash:
        raise SystemExit(
            "Built-in setup VIIPER identity does not match its packaged binary."
        )

    usbip_hashes = []
    for pattern, source, label in [
        (
            r'ExpectedUsbipHash\s*=\s*"([0-9A-Fa-f]{64})"',
            probe,
            "bootstrapper",
        ),
        (
            r'\$script:UsbipExecutableSha256\s*=\s*\r?\n?\s*"([0-9A-Fa-f]{64})"',
            backend_script,
            "backend",
        ),
        (
            r'SupportedUsbipExecutableSha256\s*=\s*\r?\n?\s*"([0-9A-Fa-f]{64})"',
            setup_manager,
            "runtime",
        ),
    ]:
        match = re.search(pattern, source)
        if not match:
            raise SystemExit(
                f"{label} USB-IP executable identity is missing."
            )
        usbip_hashes.append(match.group(1).upper())
    if len(set(usbip_hashes)) != 1:
        raise SystemExit(
            "USB-IP executable identity differs between installer and runtime gates."
        )
    for contract in [
        "EnsurePathDoesNotTraverseReparsePoints(sourceRoot",
        "EnsurePathDoesNotTraverseReparsePoints(sourcePath",
        "IsSafeRelativePackagePath(relativePath)",
        "FileShare.Read",
        "IsOptionalSatelliteResourcePath(relativePath)",
        "GetInfrastructureActionsLogPath()",
        "viiper-setup-host.log",
        'startInfo.ArgumentList.Add("-Yes")',
        "definition.Triggers.Add(new LogonTrigger())",
        "progress = new DS4WinWPF.DS4Forms.ViiperSetupProgress(",
        "progress.WaitForProcess(process)",
        'startInfo.ArgumentList.Add("-CorrelationId")',
    ]:
        if contract not in setup_manager:
            raise SystemExit(
                "Built-in installer staging contract missing: " + contract
            )

    post_build = (
        args.bundle_source.parent.parent.parent
        / "utils"
        / "post-build.py"
    ).read_text(encoding="utf-8")
    for contract in [
        "is_reparse_point",
        "Refusing to replace output containing a reparse point",
        ".ds4windows-managed-files.txt",
        'Path(__file__).resolve().with_name("inject_deps_path.py")',
    ]:
        if contract not in post_build:
            raise SystemExit(
                "Portable package ownership contract missing: " + contract
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
