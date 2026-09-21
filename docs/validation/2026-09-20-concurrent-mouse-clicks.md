# RC4.6.3 mouse concurrency validation

## Scope and baseline

Started from fetched `origin/main` at
`62f206ab6a7d4e83d50387ed52cc5e967a076e48` in a separate worktree.
Earlier unshipped haptics, Game Bar and Device Options edits are not included.
VIIPER, USB/IP, Xbox persona and updater dependencies are unchanged.

The initial 14 real `MapCustom`/`Commit` simulations produced 10 passes and
4 failures on the unchanged mapper. Ordinary R2/face-button left-click plus
controller movement, W/Space/Ctrl and mouse movement worked in both press
orders. An unrelated toggled Button action suppressed left-down or left-up.
This is a confirmed conditional bug, not proof that all reporters used toggles.

Source inspection also found that macro mouse events bypassed mapped-button
ownership: an overlapping macro could release another binding's held click.
FakerInput used toggle-style events, so its extra down could itself release
the button. Its extended-button path additionally ignored X1/X2 selection.

## Implementation and regression coverage

- Separate toggle state per physical binding/controller; preserve configured
  two-stage trigger toggles and retire removed/shifted bindings.
- Publish edges of the union of mapped, touchpad, toggle and macro owners for
  each of the five mouse buttons. Preserve explicitly retained macro state
  without accumulating unreachable owners.
- Flush short macro mouse edges through buffered output immediately, with no
  timer, sleep, synthetic re-click loop or movement suppression added.
- Capture macro epochs before scheduling; disconnected/stopped slots reject
  both delayed and queued old macro mouse events. Stop retires all slots.
- Serialize backend replacement with mouse publication and its final flush;
  replay held buttons once to the reset/new backend. Release the publication
  lock even when a backend flush throws.
- Select the bundled FakerInput wrapper's actual XButton1/XButton2 masks.

Tests use the real mapper/macro entry points, memory output sinks, real managed
FakerInput report state without connecting the driver, explicit concurrent
barriers, and warmed allocation assertions. They do not inject Windows input,
restart applications, or alter profiles/touchpad settings.

## Local result

`dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64`

**6,642 passed, 0 failed; 12 opt-in hardware cases skipped.** This includes
`ConcurrentMouseMappingTests`, `MacroMouseOwnershipTests` and
`MousePublicationFenceTests`. Existing compile warnings remain; they were not
silenced or treated as new evidence.

Publication additionally requires the final-source GitHub CI and tagged draft
workflow, installer lifecycle/upgrade tests, and final downloaded asset checks.
Their receipts are separate from this local test result.

## Limitations

The reporter's profile, machine and game were not available for reproduction.
No claim is made that these conditional fixes prove the cause of their report
or override games that reject mixed controller/mouse input. Microsoft documents
touchpad suppression after typing, but that is only a conditional troubleshooting
lead; no Windows setting is changed automatically. See
[mouse-click troubleshooting](../troubleshooting-mouse-clicks.md).
