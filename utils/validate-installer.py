#!/usr/bin/env python3
"""Fail CI if a standard installer composition is incomplete or unsafe."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


REQUIRED_PUBLISH_FILES = {
    "DS4Windows.exe",
    "extras/install-viiper-backend.ps1",
    "extras/VIIPER-0.0.7-x64.exe",
    "extras/VIIPER-0.0.7-x64.exe.sha256",
    "extras/USBip-0.9.7.7-x64.exe",
    "extras/HidHide_1.5.230_x64.exe",
    "extras/FakerInput_0.1.0_x64.msi",
}


def sha256(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--installer", type=Path, required=True)
    parser.add_argument("--bundle-source", type=Path, required=True)
    args = parser.parse_args()

    missing = sorted(path for path in REQUIRED_PUBLISH_FILES if not (args.publish_root / path).is_file())
    if missing:
        raise SystemExit("Installer payload is missing: " + ", ".join(missing))
    if not args.installer.is_file() or args.installer.stat().st_size < 1024 * 1024:
        raise SystemExit(f"Installer EXE was not produced correctly: {args.installer}")

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
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
        'Id="CloseRunningApplications"',
        'RepairArguments="repair ',
        'UninstallArguments="uninstall ',
        'InstallCondition="InstallHidHide"',
        'InstallCondition="InstallFakerInput"',
        'Name="TargetUserSid"',
        '--target-roaming-appdata',
        'Variable="ManagedInstallRegistered"',
        'Variable="ManagedViiperPresent"',
    ]
    for contract in required_contracts:
        if contract not in bundle:
            raise SystemExit("Bundle contract missing: " + contract)
    if "Portable" in bundle or "InstallFolder" in bundle or "destination" in bundle.lower():
        raise SystemExit("The standard installer must not expose a portable or destination-selection path.")

    product = (args.bundle_source.parent.parent / "DS4Windows.Package" / "Product.wxs").read_text(encoding="utf-8")
    for contract in ['<MajorUpgrade', 'Id="DESKTOP_SHORTCUT"', 'Scope="perMachine"']:
        if contract not in product:
            raise SystemExit("MSI upgrade contract missing: " + contract)

    installer_root = args.bundle_source.parent.parent
    setup_actions = (installer_root / "DS4Windows.SetupActions" / "Program.cs").read_text(encoding="utf-8")
    for contract in [
        r'@"Global\DS4Windows-VIIPER-Setup"',
        'SetupResumeShortcut',
        'KillProcessTree(process)',
        'IsInfrastructureCommitted()',
        'ValidateSuppliedInteractiveUser',
        '=== DS4Windows setup invocation ',
    ]:
        if contract not in setup_actions:
            raise SystemExit("Setup action safety contract missing: " + contract)
    if 'SetValue("DS4WindowsSetupResume"' in setup_actions:
        raise SystemExit("Setup must not create a custom HKLM RunOnce entry.")

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
    ]:
        if contract not in bootstrapper:
            raise SystemExit("Bootstrapper lifecycle contract missing: " + contract)

    probe = (installer_root / "DS4Windows.Bootstrapper" / "InfrastructureProbe.cs").read_text(encoding="utf-8")
    for contract in [
        '"InfrastructureState"',
        'BeginOutputReadLine()',
        'BeginErrorReadLine()',
    ]:
        if contract not in probe:
            raise SystemExit("Infrastructure probe contract missing: " + contract)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
