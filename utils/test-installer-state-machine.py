"""Deterministic native-VIIPER Burn ownership simulation.

This models package ordering without mutating SCM, SetupAPI, or a driver on the
build machine. Source contracts bind the model to the Burn/bootstrapper and to
the authoritative VIIPER child invocation.
"""

from dataclasses import dataclass, field
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


@dataclass
class Transaction:
    action: str
    related: str = "none"  # none, older, newer
    managed_related: bool = True
    invoked_by_upgrade: bool = False
    mutex_available: bool = True
    cancel_before_apply: bool = False
    native_result: int = 0
    optional_result: int = 0
    events: list[str] = field(default_factory=list)
    result: int = 0

    def run(self) -> "Transaction":
        if self.related == "newer" and not (
            self.invoked_by_upgrade and self.action == "uninstall"
        ):
            self.events.append("block-downgrade")
            self.result = 1638
            return self
        if not self.mutex_available:
            self.events.append("bundle-mutex-busy")
            self.result = 1618
            return self
        if self.cancel_before_apply:
            self.events.append("cancel-before-mutation")
            self.result = 1223
            return self

        self.events.append("native-preflight-ds4-only")
        if self.action == "uninstall":
            if self.invoked_by_upgrade:
                self.events += ["preserve-native-viiper", "remove-msi"]
                return self
            self.events.append("authoritative-native-uninstall")
            if self.native_result not in (0, 3010):
                self.events.append("retain-msi-and-cached-recovery-media")
                self.result = self.native_result
                return self
            self.events += ["delete-native-receipt", "remove-msi"]
            if self.native_result == 3010:
                self.events.append("schedule-reboot")
                self.result = 3010
            return self

        self.events += ["apply-msi", "apply-optional-packages"]
        if self.optional_result:
            self.events.append("report-nonvital-optional-failure")
        if self.related == "older" and self.managed_related:
            self.events += [
                "defer-native-viiper",
                "remove-old-related-without-native-teardown",
                "isolated-native-recovery",
            ]
        elif self.related == "older":
            self.events.append("ignore-unmanaged-legacy-registration")

        self.events.append("authoritative-native-install-repair")
        if self.native_result == 3010:
            self.events += [
                "do-not-commit-receipt",
                "schedule-reboot",
                "resume-full-native-transaction",
            ]
            self.result = 3010
        elif self.native_result != 0:
            self.events += [
                "do-not-commit-receipt",
                "viiper-owned-rollback",
                "burn-rolls-back-msi",
            ]
            self.result = self.native_result
        else:
            self.events.append("commit-native-receipt-last")
        return self


def require(text: str, *contracts: str) -> None:
    missing = [contract for contract in contracts if contract not in text]
    if missing:
        raise SystemExit("Installer state contract missing: " + ", ".join(missing))


def main() -> None:
    bootstrapper = (
        ROOT / "installer/DS4Windows.Bootstrapper/InstallerApplication.cs"
    ).read_text(encoding="utf-8")
    bundle = (ROOT / "installer/DS4Windows.Bundle/Bundle.wxs").read_text(
        encoding="utf-8"
    )
    setup_actions = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (
            ROOT / "installer/DS4Windows.SetupActions/Program.cs",
            ROOT / "installer/DS4Windows.SetupActions/NativeViiperPackage.cs",
        )
    )
    in_app_installer = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (
            ROOT / "DS4Windows/DS4Control/Viiper/ViiperSetupManager.cs",
            ROOT / "DS4Windows/DS4Control/Viiper/ViiperNativePackageInstaller.cs",
        )
    )
    app_project = (ROOT / "DS4Windows/DS4WinWPF.csproj").read_text(
        encoding="utf-8"
    )
    post_build = (ROOT / "utils/post-build.py").read_text(encoding="utf-8")

    require(
        bootstrapper,
        'e.PackageId, "PostUninstallCleanup"',
        'e.PackageId, "CloseRunningApplications"',
        '"NativeViiperInstallRepair"',
        '"NativeViiperUninstallForDirectRemove"',
        "plannedAction == LaunchAction.Uninstall",
        "deferInfrastructureUntilUpgradeCompletes",
        "infrastructureRecoveryPass",
        "parentOwnedRelatedUninstall",
        "IsRelatedBundleNewer",
        "ShowFailure(1638",
        "command.Resume == ResumeType.Reboot",
        "Interlocked.CompareExchange(ref planStarted, 1, 0)",
    )
    require(
        bundle,
        'Tag="DS4WindowsManagedV2"',
        'Id="PostUninstallCleanup"',
        'Id="NativeViiperInstallRepair"',
        'Id="NativeViiperUninstallForDirectRemove"',
        'Permanent="yes"',
        'Vital="yes"',
        'Value="3010" Behavior="scheduleReboot"',
        '[WixBundleExecutePackageCacheFolder]viiper-native-udecx',
        'PayloadGroupRef Id="NativeViiperRuntimePayloads"',
    )
    require(
        setup_actions,
        'case "native-install":',
        'case "native-uninstall":',
        '"native-package-install"',
        '"--expected-broker-sha256"',
        '"--expected-helper-sha256"',
        '"--target-user-sid"',
        "ResolveInteractiveUser(args).Sid",
        "NativeBundleMedia.Open",
        "process.StartInfo.ArgumentList.Add(argument)",
        "process.StandardOutput.ReadToEndAsync()",
        "process.StandardError.ReadToEndAsync()",
        "WaitForNativePackageProcess(process)",
        "WaitForSingleObject(process.SafeHandle, InfiniteWait)",
        "CommitNativeReceipt(pins, targetSid);",
        "MarkNativeReceiptRemoving();",
        "exitCode == 0 || exitCode == 3010",
        "PreflightLocked(includeViiper: false)",
    )
    require(
        in_app_installer,
        "NativeInstallerPins.TryLoad",
        'startInfo.ArgumentList.Add("--native-package")',
        "RunElevatedNativePackageInstall",
        "ValidateNativeInstallerAccount",
        '"native-package-install"',
        '"--expected-broker-sha256"',
        "ProtectNativeSetupDirectory(setupDirectory)",
        "NativeStagedMedia.Create",
        "information.NumberOfLinks != 1",
        "Never time-limit or terminate this process after launch.",
        "process.StandardOutput.ReadToEndAsync()",
        "process.StandardError.ReadToEndAsync()",
        "WaitForSingleObject(process.SafeHandle",
        "DeleteNativeSetupDirectory(setupDirectory)",
        "CommitNativeInstallerReceipt(pins, targetUserSid)",
    )
    require(
        app_project,
        "GenerateViiperPackageChecksum",
        "Condition=\"'$(ViiperNativeBundleEnabled)' != 'true'\"",
        "..\\extras\\install-viiper-backend.ps1",
        "..\\extras\\VIIPER-0.1.0-x64.exe",
        "..\\extras\\USBip-0.9.7.7-x64.exe",
    )
    require(
        post_build,
        "if require_native_bundle:",
        "remove_legacy_publish_payload(target_dir)",
        '"extras/install-viiper-backend.ps1"',
    )

    clean = Transaction("install").run()
    assert clean.events == [
        "native-preflight-ds4-only",
        "apply-msi",
        "apply-optional-packages",
        "authoritative-native-install-repair",
        "commit-native-receipt-last",
    ]
    repair = Transaction("repair").run()
    assert "authoritative-native-install-repair" in repair.events
    update = Transaction("install", related="older").run()
    assert update.events.index(
        "remove-old-related-without-native-teardown"
    ) < update.events.index("isolated-native-recovery")
    legacy_update = Transaction(
        "install", related="older", managed_related=False
    ).run()
    assert "defer-native-viiper" not in legacy_update.events
    assert "ignore-unmanaged-legacy-registration" in legacy_update.events
    uninstall = Transaction("uninstall").run()
    assert uninstall.events == [
        "native-preflight-ds4-only",
        "authoritative-native-uninstall",
        "delete-native-receipt",
        "remove-msi",
    ]
    uninstall_failed = Transaction("uninstall", native_result=3).run()
    assert uninstall_failed.events[-1] == "retain-msi-and-cached-recovery-media"
    uninstall_reboot = Transaction("uninstall", native_result=3010).run()
    assert uninstall_reboot.result == 3010
    assert uninstall_reboot.events.index("delete-native-receipt") < \
        uninstall_reboot.events.index("remove-msi")
    downgrade = Transaction("install", related="newer").run()
    assert downgrade.result == 1638 and downgrade.events == ["block-downgrade"]
    outgoing = Transaction(
        "uninstall", related="newer", invoked_by_upgrade=True
    ).run()
    assert outgoing.events == [
        "native-preflight-ds4-only", "preserve-native-viiper", "remove-msi"
    ]
    failed = Transaction("repair", native_result=1).run()
    assert failed.events[-3:] == [
        "do-not-commit-receipt",
        "viiper-owned-rollback",
        "burn-rolls-back-msi",
    ]
    reboot = Transaction("install", native_result=3010).run()
    assert reboot.events[-3:] == [
        "do-not-commit-receipt",
        "schedule-reboot",
        "resume-full-native-transaction",
    ]
    optional_failed = Transaction("install", optional_result=1).run()
    assert optional_failed.result == 0
    assert optional_failed.events.index(
        "report-nonvital-optional-failure"
    ) < optional_failed.events.index("authoritative-native-install-repair")

    print(
        "Native installer state-machine simulation passed: install, repair, "
        "upgrade, direct/outgoing uninstall, reboot, rollback, and ordering."
    )


if __name__ == "__main__":
    main()
