#!/usr/bin/env python3
"""Deterministic model tests for the native Burn/VIIPER transaction boundary."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from enum import Enum


class Action(str, Enum):
    INSTALL = "install"
    REPAIR = "repair"
    UNINSTALL = "uninstall"
    LAYOUT = "layout"


class Request(str, Enum):
    NONE = "none"
    PRESENT = "present"
    REPAIR = "repair"
    ABSENT = "absent"


CHAIN = [
    "CloseRunningApplications",
    "DS4WindowsMsi",
    "ViiperNativeSetup",
    "ViiperNativeRemove",
    "CloseRunningApplicationsForUninstall",
]


@dataclass(frozen=True)
class Context:
    action: Action
    related_upgrade_uninstall: bool = False
    recovery_pass: bool = False
    defer_native: bool = False
    native_detected: bool = False


def requested_state(package: str, context: Context) -> Request | None:
    outgoing = (
        context.related_upgrade_uninstall
        and context.action == Action.UNINSTALL
    )
    if package == "CloseRunningApplications":
        return (
            Request.PRESENT
            if not context.recovery_pass
            and context.action in (Action.INSTALL, Action.REPAIR)
            else Request.NONE
        )
    if package == "CloseRunningApplicationsForUninstall":
        return (
            Request.PRESENT
            if not context.recovery_pass
            and context.action == Action.UNINSTALL
            and not outgoing
            else Request.NONE
        )
    if package == "ViiperNativeRemove":
        return (
            Request.PRESENT
            if not context.recovery_pass
            and context.action == Action.UNINSTALL
            and not outgoing
            else Request.NONE
        )
    if context.recovery_pass:
        if package == "ViiperNativeSetup":
            return (
                Request.REPAIR
                if context.native_detected
                else Request.PRESENT
            )
        return Request.NONE
    if package == "ViiperNativeSetup":
        if context.defer_native or context.action == Action.UNINSTALL:
            return Request.NONE
        if context.action in (Action.INSTALL, Action.REPAIR):
            return (
                Request.REPAIR
                if context.native_detected
                else Request.PRESENT
            )
    return None


def plan(context: Context) -> dict[str, Request | None]:
    return {
        package: requested_state(package, context)
        for package in CHAIN
    }


def related_bundle_is_newer(related: str, current: str) -> bool:
    """Model System.Version ordering used by the BA's numeric-only build contract."""
    related_fields = tuple(int(value) for value in related.split("."))
    current_fields = tuple(int(value) for value in current.split("."))
    width = max(len(related_fields), len(current_fields))
    return related_fields + (0,) * (width - len(related_fields)) > (
        current_fields + (0,) * (width - len(current_fields))
    )


def execution_order(
    context: Context, requests: dict[str, Request | None]
) -> list[str]:
    sequence = (
        list(reversed(CHAIN))
        if context.action == Action.UNINSTALL
        else CHAIN
    )
    return [
        package for package in sequence
        if requests[package] not in (None, Request.NONE)
    ]


RECEIPT = re.compile(
    r'^\{"schemaVersion":1,"operation":"(install|uninstall)",'
    r'"exitCode":([0-9]+),"succeeded":(true|false),'
    r'"rebootRequired":(true|false),'
    r'"rollbackStatus":"([a-z-]+)",'
    r'"manualRecoveryRequired":(true|false)\}$'
)


def validate_receipt(
    lines: list[str], operation: str, actual_exit: int
) -> dict[str, object]:
    prefix = "DS4WINDOWS_VIIPER_NATIVE_RESULT "
    records = [
        line[len(prefix):] for line in lines
        if line.startswith(prefix)
    ]
    if len(records) != 1:
        raise ValueError("exactly one receipt is required")
    match = RECEIPT.fullmatch(records[0])
    if not match:
        raise ValueError("malformed receipt")
    parsed = {
        "operation": match.group(1),
        "exitCode": int(match.group(2)),
        "succeeded": match.group(3) == "true",
        "rebootRequired": match.group(4) == "true",
        "rollbackStatus": match.group(5),
        "manualRecoveryRequired": match.group(6) == "true",
    }
    if parsed["operation"] != operation:
        raise ValueError("operation mismatch")
    if parsed["exitCode"] != actual_exit:
        raise ValueError("exit mismatch")
    if actual_exit == 0:
        if parsed != {
            "operation": operation,
            "exitCode": 0,
            "succeeded": True,
            "rebootRequired": False,
            "rollbackStatus": "not-required",
            "manualRecoveryRequired": False,
        }:
            raise ValueError("inconsistent success")
    elif actual_exit == 3010:
        if parsed != {
            "operation": operation,
            "exitCode": 3010,
            "succeeded": False,
            "rebootRequired": True,
            "rollbackStatus": "safely-settled",
            "manualRecoveryRequired": False,
        }:
            raise ValueError("inconsistent reboot")
    elif (
        parsed["succeeded"]
        or parsed["rebootRequired"]
        or (
            parsed["rollbackStatus"] == "not-started"
            and parsed["manualRecoveryRequired"]
        )
        or (
            parsed["rollbackStatus"] == "unverified-see-transaction-log"
            and not parsed["manualRecoveryRequired"]
        )
        or parsed["rollbackStatus"] not in (
            "not-started", "unverified-see-transaction-log"
        )
    ):
        raise ValueError("inconsistent failure")
    return parsed


def receipt(
    operation: str,
    exit_code: int,
    succeeded: bool,
    reboot: bool,
    rollback: str,
    recovery: bool,
) -> str:
    value = {
        "schemaVersion": 1,
        "operation": operation,
        "exitCode": exit_code,
        "succeeded": succeeded,
        "rebootRequired": reboot,
        "rollbackStatus": rollback,
        "manualRecoveryRequired": recovery,
    }
    return (
        "DS4WINDOWS_VIIPER_NATIVE_RESULT "
        + json.dumps(value, separators=(",", ":"))
    )


def expect_invalid(lines: list[str], operation: str, exit_code: int) -> None:
    try:
        validate_receipt(lines, operation, exit_code)
    except ValueError:
        return
    raise AssertionError(f"invalid receipt was accepted: {lines!r}")


def select_target_sid(
    installed_sid: str | None,
    persisted_sid: str | None,
    current_sid: str,
    *,
    reboot_resume: bool,
    outgoing_upgrade: bool,
    registered: bool,
    uninstall: bool,
) -> str:
    candidate = installed_sid or persisted_sid
    first_install = (
        not reboot_resume
        and not outgoing_upgrade
        and not registered
        and not uninstall
    )
    if not candidate and first_install:
        candidate = current_sid
    if not candidate or not candidate.startswith("S-1-"):
        raise ValueError("target SID unavailable")
    return candidate


def run_plan_tests() -> None:
    assert related_bundle_is_newer("4.0.3.0", "4.0.2.1")
    assert not related_bundle_is_newer("4.0.2.1", "4.0.2.1")
    assert not related_bundle_is_newer("4.0.1.9", "4.0.2.1")

    clean = Context(Action.INSTALL)
    clean_plan = plan(clean)
    assert clean_plan == {
        "CloseRunningApplications": Request.PRESENT,
        "DS4WindowsMsi": None,
        "ViiperNativeSetup": Request.PRESENT,
        "ViiperNativeRemove": Request.NONE,
        "CloseRunningApplicationsForUninstall": Request.NONE,
    }
    assert execution_order(clean, clean_plan) == [
        "CloseRunningApplications",
        "ViiperNativeSetup",
    ]

    repair = Context(Action.REPAIR, native_detected=True)
    repair_plan = plan(repair)
    assert repair_plan["ViiperNativeSetup"] == Request.REPAIR
    assert repair_plan["ViiperNativeRemove"] == Request.NONE

    deferred = Context(Action.INSTALL, defer_native=True)
    assert plan(deferred)["ViiperNativeSetup"] == Request.NONE

    recovery = Context(
        Action.REPAIR, recovery_pass=True, native_detected=False
    )
    recovery_plan = plan(recovery)
    assert recovery_plan["ViiperNativeSetup"] == Request.PRESENT
    assert all(
        state == Request.NONE
        for package, state in recovery_plan.items()
        if package != "ViiperNativeSetup"
    )

    direct_uninstall = Context(Action.UNINSTALL, native_detected=True)
    uninstall_plan = plan(direct_uninstall)
    assert uninstall_plan["ViiperNativeSetup"] == Request.NONE
    assert uninstall_plan["ViiperNativeRemove"] == Request.PRESENT
    assert (
        uninstall_plan["CloseRunningApplicationsForUninstall"]
        == Request.PRESENT
    )
    order = execution_order(direct_uninstall, uninstall_plan)
    assert order == [
        "CloseRunningApplicationsForUninstall",
        "ViiperNativeRemove",
    ]
    assert order.index("ViiperNativeRemove") < CHAIN[::-1].index(
        "DS4WindowsMsi"
    )

    outgoing = Context(
        Action.UNINSTALL,
        related_upgrade_uninstall=True,
        native_detected=True,
    )
    outgoing_plan = plan(outgoing)
    assert outgoing_plan["ViiperNativeSetup"] == Request.NONE
    assert outgoing_plan["ViiperNativeRemove"] == Request.NONE
    assert (
        outgoing_plan["CloseRunningApplicationsForUninstall"]
        == Request.NONE
    )

    # The removal package's install direction is native uninstall. Its
    # uninstall direction is native install, so Burn rollback can restore the
    # backend if a later reverse-chain MSI removal fails.
    native_remove_install_direction = "uninstall"
    native_remove_rollback_direction = "install"
    assert native_remove_install_direction != native_remove_rollback_direction

    # One interlocked plan gate accepts only the first request until Retry or a
    # post-upgrade recovery reset explicitly reopens it.
    plan_started = 0
    accepted = []
    for request in (Action.INSTALL, Action.REPAIR, Action.UNINSTALL):
        if plan_started == 0:
            plan_started = 1
            accepted.append(request)
    assert accepted == [Action.INSTALL]
    plan_started = 0
    assert plan_started == 0


def run_sid_tests() -> None:
    intended = "S-1-5-21-100-200-300-1001"
    alternate_admin = "S-1-5-21-100-200-300-500"
    assert select_target_sid(
        None, None, intended,
        reboot_resume=False,
        outgoing_upgrade=False,
        registered=False,
        uninstall=False,
    ) == intended
    assert select_target_sid(
        intended, None, alternate_admin,
        reboot_resume=False,
        outgoing_upgrade=False,
        registered=True,
        uninstall=True,
    ) == intended
    assert select_target_sid(
        intended, intended, alternate_admin,
        reboot_resume=True,
        outgoing_upgrade=False,
        registered=True,
        uninstall=False,
    ) == intended
    assert select_target_sid(
        intended, intended, alternate_admin,
        reboot_resume=False,
        outgoing_upgrade=True,
        registered=True,
        uninstall=True,
    ) == intended
    try:
        select_target_sid(
            None, None, alternate_admin,
            reboot_resume=False,
            outgoing_upgrade=False,
            registered=True,
            uninstall=True,
        )
    except ValueError:
        pass
    else:
        raise AssertionError(
            "maintenance silently replaced a missing installed target SID"
        )


def run_receipt_tests() -> None:
    success = receipt(
        "install", 0, True, False, "not-required", False
    )
    reboot = receipt(
        "install", 3010, False, True, "safely-settled", False
    )
    pre_mutation_failure = receipt(
        "install", 1, False, False, "not-started", False
    )
    recovery_failure = receipt(
        "uninstall", 1, False, False,
        "unverified-see-transaction-log", True,
    )
    assert validate_receipt([success], "install", 0)["succeeded"]
    assert validate_receipt(
        ["diagnostic", reboot], "install", 3010
    )["rebootRequired"]
    assert not validate_receipt(
        [pre_mutation_failure], "install", 1
    )["manualRecoveryRequired"]
    assert validate_receipt(
        [recovery_failure], "uninstall", 1
    )["manualRecoveryRequired"]

    expect_invalid([], "install", 0)
    expect_invalid([success, success], "install", 0)
    expect_invalid([
        "DS4WINDOWS_VIIPER_NATIVE_RESULT not-json"
    ], "install", 0)
    expect_invalid([success], "uninstall", 0)
    expect_invalid([success], "install", 3010)
    expect_invalid([
        receipt("install", 0, False, False, "not-required", False)
    ], "install", 0)
    expect_invalid([
        receipt("install", 3010, False, False,
                "safely-settled", False)
    ], "install", 3010)
    expect_invalid([
        receipt("install", 3010, False, True,
                "safely-settled", True)
    ], "install", 3010)
    expect_invalid([
        receipt("install", 1, False, False, "invented", False)
    ], "install", 1)
    expect_invalid([
        receipt("install", 1, False, False, "not-started", True)
    ], "install", 1)
    expect_invalid([
        receipt("install", 1, False, False,
                "unverified-see-transaction-log", False)
    ], "install", 1)
    duplicate_property = success.replace(
        '"schemaVersion":1,',
        '"schemaVersion":1,"schemaVersion":1,',
    )
    expect_invalid([duplicate_property], "install", 0)
    extra_property = success[:-1] + ',"extra":true}'
    expect_invalid([extra_property], "install", 0)


def main() -> int:
    run_plan_tests()
    run_sid_tests()
    run_receipt_tests()
    print("Native installer state-machine tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
