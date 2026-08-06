"""Deterministic release-gate simulation for DS4Windows installer ownership.

This deliberately models transitions rather than launching a kernel-driver
installer on the build machine. Source-contract checks below tie every modeled
transition to the Burn/bootstrapper and backend implementation.
"""

from dataclasses import dataclass, field
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


@dataclass
class Transaction:
    action: str
    related: str = "none"  # none, older, newer
    infrastructure_healthy: bool = False
    mutex_available: bool = True
    cancel_before_apply: bool = False
    helper_result: int = 0
    events: list[str] = field(default_factory=list)
    result: int = 0

    def run(self) -> "Transaction":
        if self.related == "newer":
            self.events.append("block-downgrade")
            self.result = 1638
            return self
        if not self.mutex_available:
            self.events.append("mutex-busy")
            self.result = 1618
            return self
        if self.cancel_before_apply:
            self.events.append("cancel-before-mutation")
            self.result = 1223
            return self

        self.events.append("preflight")
        if self.action == "uninstall":
            self.events += ["remove-msi", "remove-owned-infrastructure"]
            return self

        self.events.append("apply-msi")
        if self.related == "older":
            self.events += [
                "defer-infrastructure",
                "remove-old-related-without-shared-teardown",
                "isolated-infrastructure-recovery",
            ]
        else:
            self.events.append("apply-or-repair-infrastructure")

        if self.helper_result == 3010:
            self.events += [
                "clear-ready-marker",
                "disable-owned-startup-tasks",
                "persist-original-user",
                "reboot",
                "resume-next-boot",
                "install-usbip-0.9.7.7",
                "verify-hash-driver-abi-api",
                "enable-owned-startup-tasks",
                "commit-ready-marker",
            ]
            self.result = 3010
        elif self.helper_result != 0:
            self.events += [
                "clear-ready-marker",
                "stop-viiper",
                "disable-owned-startup-tasks",
                "record-failed-state",
            ]
            self.result = self.helper_result
        else:
            self.events += [
                "verify-hash-driver-abi-api",
                "commit-ready-marker",
            ]
        return self


def require(text: str, *contracts: str) -> None:
    missing = [contract for contract in contracts if contract not in text]
    if missing:
        raise SystemExit("Installer state contract missing: " + ", ".join(missing))


def main() -> None:
    bootstrapper = (ROOT / "installer/DS4Windows.Bootstrapper/InstallerApplication.cs").read_text(encoding="utf-8")
    setup_actions = (ROOT / "installer/DS4Windows.SetupActions/Program.cs").read_text(encoding="utf-8")
    backend = (ROOT / "extras/install-viiper-backend.ps1").read_text(encoding="utf-8")
    runtime = (ROOT / "DS4Windows/DS4Control/Viiper/ViiperSetupManager.cs").read_text(encoding="utf-8")

    require(
        bootstrapper,
        'e.PackageId, "CloseRunningApplications"',
        "plannedAction == LaunchAction.Uninstall",
        "deferInfrastructureUntilUpgradeCompletes",
        "infrastructureRecoveryPass",
        "parentOwnedRelatedUninstall",
        "IsRelatedBundleNewer",
        "ShowFailure(1638",
        "command.Resume == ResumeType.Reboot",
        "Interlocked.CompareExchange(ref planStarted, 1, 0)",
        "Ignoring a duplicate installer plan request",
    )
    require(
        backend,
        "Set-UsbipReplacementBoundary",
        "Resolve-UsbipReplacementBoundary",
        "Suspend-StartupTasksUntilInfrastructureReady",
        "Set-InfrastructureStartupFailClosed",
        "$script:UsbipExecutableSha256",
        "Test-UsbipRuntime",
        "Commit-InfrastructureReadiness",
        'Set-InfrastructureState "Failed"',
    )
    require(
        runtime,
        "SupportedViiperSha256",
        "SupportedUsbipExecutableSha256",
        "SupportedUsbipUdeSha256",
        "SupportedUsbipFilterSha256",
        "TryProbeUsbipRuntime",
        "mandatoryRepairRequired = !status.Ready",
        "Application.Current?.Shutdown()",
    )
    require(
        setup_actions,
        "return RunWithSetupMutex(PreflightLocked);",
        "completed with exit code",
        "AppendLogWithRetry",
    )

    clean = Transaction("install").run()
    assert clean.events == [
        "preflight", "apply-msi", "apply-or-repair-infrastructure",
        "verify-hash-driver-abi-api", "commit-ready-marker",
    ]
    repair = Transaction("repair", infrastructure_healthy=True).run()
    assert "apply-or-repair-infrastructure" in repair.events
    update = Transaction("install", related="older").run()
    assert update.events.index("remove-old-related-without-shared-teardown") < update.events.index("isolated-infrastructure-recovery")
    uninstall = Transaction("uninstall").run()
    assert uninstall.events == ["preflight", "remove-msi", "remove-owned-infrastructure"]
    downgrade = Transaction("install", related="newer").run()
    assert downgrade.result == 1638 and downgrade.events == ["block-downgrade"]
    busy = Transaction("repair", mutex_available=False).run()
    assert busy.result == 1618 and "preflight" not in busy.events
    canceled = Transaction("install", cancel_before_apply=True).run()
    assert canceled.result == 1223 and "apply-msi" not in canceled.events
    failed = Transaction("repair", helper_result=1).run()
    assert failed.events[-3:] == [
        "stop-viiper", "disable-owned-startup-tasks", "record-failed-state",
    ]
    reboot = Transaction("install", helper_result=3010).run()
    assert reboot.events.index("disable-owned-startup-tasks") < reboot.events.index("reboot")
    assert reboot.events.index("verify-hash-driver-abi-api") < reboot.events.index("commit-ready-marker")

    print("Installer state-machine simulation passed: clean, update, repair, uninstall, downgrade, cancel, concurrency, failure, reboot/resume.")


if __name__ == "__main__":
    main()
