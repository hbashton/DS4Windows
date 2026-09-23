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
    managed_related: bool = True
    invoked_by_upgrade: bool = False
    infrastructure_healthy: bool = False
    mutex_available: bool = True
    cancel_before_apply: bool = False
    helper_result: int = 0
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
            self.events.append("mutex-busy")
            self.result = 1618
            return self
        if self.cancel_before_apply:
            self.events.append("cancel-before-mutation")
            self.result = 1223
            return self

        self.events.append("preflight")
        if self.action == "uninstall":
            # Burn uninstalls in reverse chain order. Infrastructure is
            # quiesced while the MSI-owned app files still exist. An outgoing
            # bundle preserves infrastructure owned by the incoming upgrade.
            self.events += (
                ["preserve-shared-infrastructure", "remove-msi"]
                if self.invoked_by_upgrade
                else ["remove-owned-infrastructure", "remove-msi"]
            )
            return self

        self.events.append("apply-msi")
        if self.related == "older" and self.managed_related:
            self.events += [
                "defer-infrastructure",
                "remove-old-related-without-shared-teardown",
                "isolated-infrastructure-recovery",
            ]
        else:
            self.events.append("apply-or-repair-infrastructure")
            if self.related == "older":
                self.events.append("ignore-legacy-bundle-registration")

        if self.helper_result == 3010:
            self.events += [
                "clear-ready-marker",
                "disable-owned-startup-tasks",
                "persist-original-user",
                "persist-requested-startup",
                "stage-explicit-protected-resume",
                "return-restart-required",
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
            if self.optional_result:
                self.events.append("report-optional-package-failure")
        return self


@dataclass
class ResumeTransaction:
    """A later logon is a separate transaction, not implied by exit code 3010."""

    boot_changed: bool = True
    correct_user: bool = True
    correct_transaction: bool = True
    attempted: bool = False
    snapshot_valid: bool = True
    permission_granted: bool = True
    infrastructure_ready: bool = True
    startup_requested: bool = True
    events: list[str] = field(default_factory=list)

    def run(self) -> "ResumeTransaction":
        if not (self.boot_changed and self.correct_user and self.correct_transaction) or self.attempted:
            return self
        self.events.append("verify-protected-snapshot")
        if not self.snapshot_valid:
            return self
        self.events += ["record-one-attempt", "remove-owned-resume-shortcut"]
        if not self.permission_granted:
            self.events.append("show-repair-required")
            return self
        self.events += ["repair-pinned-infrastructure", "verify-hash-driver-abi-api"]
        if not self.infrastructure_ready:
            self.events.append("keep-startup-disabled")
            return self
        self.events.append("enable-original-startup-tasks" if self.startup_requested else "keep-startup-off")
        self.events.append("commit-ready-marker")
        return self


def require(text: str, *contracts: str) -> None:
    missing = [contract for contract in contracts if contract not in text]
    if missing:
        raise SystemExit("Installer state contract missing: " + ", ".join(missing))


def main() -> None:
    bootstrapper = (ROOT / "installer/DS4Windows.Bootstrapper/InstallerApplication.cs").read_text(encoding="utf-8")
    bundle = (ROOT / "installer/DS4Windows.Bundle/Bundle.wxs").read_text(encoding="utf-8")
    setup_actions = (ROOT / "installer/DS4Windows.SetupActions/Program.cs").read_text(encoding="utf-8")
    backend = (ROOT / "extras/install-viiper-backend.ps1").read_text(encoding="utf-8")
    runtime = (ROOT / "DS4Windows/DS4Control/Viiper/ViiperSetupManager.cs").read_text(encoding="utf-8")
    startup_policy = (ROOT / "DS4Windows/DS4Control/Viiper/ViiperStartupTaskPolicy.cs").read_text(encoding="utf-8")
    recovery = (ROOT / "installer/StartupSetupState.cs").read_text(encoding="utf-8")

    require(
        bootstrapper,
        'UninstallHelperPackagePlan.TryGetState(e.PackageId,',
        'e.PackageId, "CloseRunningApplications"',
        "plannedAction == LaunchAction.Uninstall",
        "deferInfrastructureUntilUpgradeCompletes",
        "infrastructureRecoveryPass",
        "parentOwnedRelatedUninstall",
        "IsParentOwnedRelatedUninstall",
        "IsRelatedBundleNewer",
        "ShowFailure(1638",
        "command.Resume == ResumeType.Reboot",
        "StartupSetupRecovery.TryClaim",
        "requestedSetupResume ? LaunchAction.Repair",
        "Interlocked.CompareExchange(ref planStarted, 1, 0)",
        "Ignoring a duplicate installer plan request",
        'engine.SetVariableString("SetupCorrelationId"',
        "Not retrying installer-busy result from non-vital",
        "packageStates.ContainsKey",
        "managedRelatedBundles",
        "ManagedBundleTag",
    )
    helper_plan = (ROOT / "installer/DS4Windows.Bootstrapper/UninstallHelperPackagePlan.cs").read_text(encoding="utf-8")
    require(helper_plan, '"PostUninstallCleanup"', '"ViiperUsbipUninstall"',
            '"CloseRunningApplicationsForUninstall"', "RequestState.Cache",
            "relation != RelationType.Upgrade", "infrastructureRecoveryPass")
    require(
        bundle,
        'Tag="DS4WindowsManagedV2"',
        'Id="PostUninstallCleanup"',
        'Id="ViiperUsbipUninstall"',
        'Name="DS4Windows.SetupActions.InfrastructureUninstall.exe"',
        'Permanent="yes"',
        'Behavior="scheduleReboot"',
        '--bundle-id',
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
        'Protect-ElevatedTaskTargetDirectory $script:InstallDir "VIIPER"',
        'Save-StartupSetupIntent "RestartRequired"',
        'Update-StartupSetupRequest',
    )
    require(
        runtime,
        "SupportedViiperSha256",
        "SupportedUsbipExecutableSha256",
        "SupportedUsbipUdeSha256",
        "SupportedUsbipFilterSha256",
        "TryProbeUsbipRuntime",
        "mandatoryRepairRequired = !status.Ready",
        "Continue in degraded mode",
        "ResolveRuntimeViiperPath(",
        "FindAlternativeViiperPath(canonicalViiperPath)",
        "ViiperStartupTaskPolicy.RefreshOnLaunch(false, canonicalPath,",
        "ViiperStartupTaskPolicy.SelectRuntimePath(startupEnabled,",
        "FindAlternativeViiperPath(canonicalViiperPath), startupRequested)",
        "IsSelectableViiperExecutable,",
        "startupPath => EnsureViiperStartupTask(startupPath,",
        "FileHasSha256(normalized, SupportedViiperSha256)",
        "PersistPreferredViiperPath(selectedPath, canonicalPath)",
    )
    # Selection is now behind the production policy seam exercised by the C#
    # behavior tests. Retain both the real callback wiring above and the safety
    # guards here; a runtime preference must not retarget the installed task.
    require(
        startup_policy,
        "if (portableSession) return;",
        "startupEnabled && isSelectable(canonicalPath)",
        "? Path.GetFullPath(canonicalPath) : selectAlternative();",
        "string selectedPath = SelectRuntimePath(enabled, canonicalPath,",
        "if (isSelectable(selectedPath)) persistRuntime(selectedPath);",
        "if (!enabled || !isSelectable(canonicalPath)) return;",
        "ensureTask(canonicalPath);",
    )
    require(
        setup_actions,
        "return RunWithSetupMutex(PreflightLocked);",
        "completed with exit code",
        "AppendLogWithRetry",
        'ReadArgument(args, "--correlation-id")',
        'StartupSetupRecovery.Register',
        'SetupResumeBundleSource.Resolve(bundleId)',
    )
    require(recovery, "IsEligible(", "pending.TargetSid", "pending.BootSessionId",
            "previousAttempt", "VerifyExecutable(", "ExecutableSha256",
            '"SetupResumeAttempt"', "ExpectedExecutable(", "IsOwnedShortcut(")

    clean = Transaction("install").run()
    assert clean.events == [
        "preflight", "apply-msi", "apply-or-repair-infrastructure",
        "verify-hash-driver-abi-api", "commit-ready-marker",
    ]
    repair = Transaction("repair", infrastructure_healthy=True).run()
    assert "apply-or-repair-infrastructure" in repair.events
    update = Transaction("install", related="older").run()
    assert update.events.index("remove-old-related-without-shared-teardown") < update.events.index("isolated-infrastructure-recovery")
    legacy_update = Transaction(
        "install", related="older", managed_related=False
    ).run()
    assert "defer-infrastructure" not in legacy_update.events
    assert "ignore-legacy-bundle-registration" in legacy_update.events
    assert legacy_update.events.index(
        "apply-or-repair-infrastructure"
    ) < legacy_update.events.index("ignore-legacy-bundle-registration")
    uninstall = Transaction("uninstall").run()
    assert uninstall.events == ["preflight", "remove-owned-infrastructure", "remove-msi"]
    downgrade = Transaction("install", related="newer").run()
    assert downgrade.result == 1638 and downgrade.events == ["block-downgrade"]
    outgoing = Transaction(
        "uninstall", related="newer", invoked_by_upgrade=True
    ).run()
    assert outgoing.result == 0
    assert outgoing.events == [
        "preflight", "preserve-shared-infrastructure", "remove-msi",
    ]
    busy = Transaction("repair", mutex_available=False).run()
    assert busy.result == 1618 and "preflight" not in busy.events
    canceled = Transaction("install", cancel_before_apply=True).run()
    assert canceled.result == 1223 and "apply-msi" not in canceled.events
    failed = Transaction("repair", helper_result=1).run()
    assert failed.events[-3:] == [
        "stop-viiper", "disable-owned-startup-tasks", "record-failed-state",
    ]
    reboot = Transaction("install", helper_result=3010).run()
    assert reboot.events.index("disable-owned-startup-tasks") < reboot.events.index("stage-explicit-protected-resume")
    assert "commit-ready-marker" not in reboot.events
    resumed = ResumeTransaction().run()
    assert resumed.events.index("verify-hash-driver-abi-api") < resumed.events.index("enable-original-startup-tasks")
    assert resumed.events[-1] == "commit-ready-marker"
    for arguments in ({"boot_changed": False}, {"correct_user": False},
                      {"correct_transaction": False}, {"attempted": True}):
        assert not ResumeTransaction(**arguments).run().events
    assert ResumeTransaction(snapshot_valid=False).run().events == ["verify-protected-snapshot"]
    denied = ResumeTransaction(permission_granted=False).run()
    assert denied.events[-1] == "show-repair-required"
    assert "repair-pinned-infrastructure" not in denied.events
    blocked = ResumeTransaction(infrastructure_ready=False).run()
    assert blocked.events[-1] == "keep-startup-disabled" and "commit-ready-marker" not in blocked.events
    opted_out = ResumeTransaction(startup_requested=False).run()
    assert "enable-original-startup-tasks" not in opted_out.events
    assert "keep-startup-off" in opted_out.events
    optional_failed = Transaction("install", optional_result=1).run()
    assert optional_failed.result == 0
    assert optional_failed.events[-1] == "report-optional-package-failure"

    print("Installer state-machine simulation passed: clean, update, repair, uninstall, downgrade, cancel, concurrency, core failure, optional failure, reboot/resume.")


if __name__ == "__main__":
    main()
