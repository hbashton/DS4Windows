# Issue #82: legacy joystick exposure investigation, 2026-09-08

Status: diagnostic lead and read-only tooling, **not a confirmed fix**. No game was launched, controller disconnected, app replaced, or HidHide/GameInput setting changed in this pass.

## Evidence and limits

- [DS4Windows #82](https://github.com/hbashton/DS4Windows/issues/82) reports Sonic UltraSaturn slowdown with several virtual pad types. The September 2 comment points to PadForge's newer investigation.
- [PadForge's corrected report](https://github.com/hifihedgehog/PadForge/discussions/394#discussioncomment-18243691) attributes its bench slowdown to repeated `joyGetPosEx` calls for absent joystick IDs and reports registry-handle growth when two exposed devices share VID/PID. This is upstream reported measurement, not a reproduced result in DS4Windows.
- [PadForge commit fe1df085](https://github.com/hifihedgehog/PadForge/commit/fe1df085058f17f2f41898baee71eb51b502d86e) implements a once-per-wired-path, exact-MAC stale Bluetooth disconnect when building routed Sony USB audio. [Issue #387](https://github.com/hifihedgehog/PadForge/issues/387) describes the separate DualSense dual-link audio motivation. It is not a general GameInput, virtual-controller latency, or game-runtime fix.
- DS4Windows `DS4Devices.findControllers` sorts Bluetooth first, then suppresses an already-admitted serial before constructing a second `DS4Device`. Only the Quick Charge branch explicitly disconnects wireless. Internal shared-mode deduplication alone does not prove the second OS HID path is hidden from a game.
- `ControlService.EnsureHidHideSessionForDevice` manages the admitted device's identity. `HidHideDeviceIdentity.Resolve` expands only within that PnP container and deliberately avoids mixed USB controller/audio bases. It does not independently find another USB/Bluetooth container with the same serial. Existing user rules may already cover it; exclusive handles may also affect access. This is an exposure precondition worth checking, not proof of the reported cause.
- Read-only local inventory found no Sonic UltraSaturn/GameMaker process and no matching game executable/archive name in Desktop, Documents, or Downloads. No present Sony USB HID collection was observed. Present Bluetooth service records cannot establish an active radio link. The current portable lab bypasses automatic HidHide mutation intentionally, so it cannot establish normal installed-mode containment.

## Bounded collection

`utils/Measure-LegacyJoystickExposure.ps1` requires PowerShell 7 and outputs JSON only. It queries present HID PnP metadata; only joystick/gamepad/multi-axis-controller usages qualify for model grouping. It does not open HID report streams or query/change HidHide policy. Duplicate collections and known container counts remain separate; equal VID/PID does not itself mean one duplicated physical pad or a leak. The read-only inventory run in this pass completed with no metadata failures and found one qualifying collection each for `045E:028E` and `057E:2069`, with no repeated model; no game was sampled.

With the affected game already running, select its exact PID in Task Manager, then run from the repository:

```powershell
pwsh -NoProfile -File utils/Measure-LegacyJoystickExposure.ps1 -TargetProcessId 12345 -DurationSeconds 30 -SampleIntervalMs 1000
```

Omit `-TargetProcessId` for inventory only. Replace `12345`; the script never selects or launches a game automatically. PID plus process start time is checked on every sample; exit/reuse/access failure ends capture explicitly. Sampling is capped at 60 seconds and 121 samples, inventory metadata at 256 HID collection nodes, and query error details at eight entries. OS queries may add time. Review device identities before sharing JSON.

`HandleCount` means **all process handles**, not registry handles. Rising counts establish a symptom only. To attribute #82, obtain an affected-game API/handle-type trace showing `joyGetPosEx` call cadence, absent-ID results, registry-key handle growth, and accompanying frame times. Record exact game/Windows/virtual-pad versions and the game's actual HidHide visibility (including application exceptions and session policy). PnP inventory does not supply that visibility proof.

Compare stable configurations only when separately authorized: one physical transport versus USB+Bluetooth, and same-model versus different-model virtual output, with normal controller hiding recorded. Do not automate radio disconnects, device resets, virtual-pad cycling, or service disabling as a guessed remedy.

Pure fixture validation (no PnP, process lookup, or actual waiting):

```powershell
pwsh -NoProfile -File utils/test-legacy-joystick-exposure.ps1
```
