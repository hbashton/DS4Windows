# RC4.5.1 — Switch 2 Rumble Hotfix

This hotfix addresses Switch 2 rumble regressions introduced in RC4.5 and
completes the Xbox One/Series configuration in both download packages.

> I highly recommend using an Emulated Dualsense controller with a Switch 2 Pro controller: it provides the best haptic feedback and playing experience

## Fixes

- **Gentler connection and identification cues:** finite effects no longer
  repeat as sustained rumble. The original effect strengths and frequencies
  are preserved.
- **More consistent sustained rumble:** recover promptly from brief output
  contention without sending duplicate packets too quickly or replaying a
  backlog. This targets the avoidable gaps affecting Test Heavy, Test Light,
  and held game rumble over USB and Bluetooth.
- **Safer retries:** a queued Bluetooth one-shot is retried as the same effect
  instead of being enqueued again. Temporary timing or worker failures no
  longer permanently stop held-output maintenance.
- **Complete Xbox output configuration:** both the installer and portable ZIP
  include the selected Windows Xbox One/Series identity configuration, fixing
  the missing-file output-validation failure that could reject Joy-Con
  activation when its profile selected Xbox output.

DualSense PCM haptics, adaptive-trigger translation, Xbox impulse feedback,
left/right routing, and immediate Stop handling retain their existing paths.
This is a timing and effect-lifetime correction, not a new rumble intensity
curve or codec.

## Downloads and upgrading

- **Installer:** `DS4Windows_5.0.5.1_Setup_x64.exe` — complete offline setup.
- **Portable:** `DS4Windows_VIIPER_x64.zip` — extract the entire folder and run
  `DS4Windows.exe`; the included VIIPER starts automatically. Close other
  DS4Windows/VIIPER copies before launching a different portable folder.

Windows build **5.0.5.1**, release **VIIPERRC4.5.1**. VIIPER remains the
hash-pinned **0.1.3-rc4.5** build, and USB/IP remains **0.9.7.7**.
Use the standard installer EXE for managed upgrades rather than installing
the embedded MSI directly.

This is an **unsigned release candidate**, not a signed stable release.
Xbox emulation targets Windows; it does not provide authentication for a
physical Xbox console. The included synthetic emulation identity is not a
claim of hardware certification or universal game compatibility.

Automated tests and artifact checks validate the covered code and package
paths. Please report remaining stutter with the controller model, USB or
Bluetooth connection, virtual output type, and whether it occurs in the
Heavy/Light tests or a specific game. Physical feel can vary and remains part
of release-candidate testing.
