# RC4.5.7 hotfix publication ledger

## Release identity

- Requested title: `Release Candidate 4.5.7 Hotfix — Sorry, broke Vibration/Triggers`.
- Tag: `VIIPERRC4.5.7`; Windows/MSI/Burn version: `5.0.5.7`.
- Functional fix commit: `16e854bca19bd3738618d33c7c6212469deea0ec`.
- Reuse unchanged published VIIPER `v0.1.4-rc4.5.6`, source `02d93e4`; binary SHA-256 `89808A41610997A6DAD0807579B816C18B34C039E03E96FDC8FD2BD68434FFBB`.
- DS4Updater 2.0.6 remains compatible. No updater or broker source change is part of this hotfix.

## Evidence and boundaries

The regression analysis, original-release failing reproduction and corrected full-suite evidence are recorded in `2026-09-11-dualsense-bt-rc456-regression.md`. The complete private candidate was built from the functional fix and verified before physical testing.

The initial isolated lab launch omitted DualSense support and the new executable's HidHide allowance. Both test-environment omissions were corrected; the log then confirmed Bluetooth DualSense detection, profile `dsm`, virtual DualSense association and direct VIIPER PCM passthrough. The lab handoff checks now enforce both discovery prerequisites. These were isolated lab setup changes, not a change to normal portable startup or a controller protocol regression.

The user reports having tested haptics on the private candidate. Native game-trigger transport and per-trigger Trigger Lab arbitration were reviewed; live acceptance in a trigger-supporting game remains pending. Both lab applications later disappeared without application exception/normal-shutdown records around a host application update; the precise termination mechanism is not established, and no unproven crash fix is claimed.

Publication follows the existing draft-first policy: final source on main and CI green, annotated tag, exact-tag draft release workflow, verified workflow-owned assets, then publish the prerelease. Public run IDs, artifact hashes and publication confirmation will be recorded after verification; this source record alone is not proof of publication.
