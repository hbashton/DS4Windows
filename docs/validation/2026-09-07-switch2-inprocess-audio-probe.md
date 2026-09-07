# b90: persistent Switch 2 Pro Bluetooth audio probing

## Approach correction

Do **not** close DS4Windows and try to take the controller with a second GATT
reader. The active DS4Windows input lease owns the Nintendo connection and
service. Closing it lost the connected peer in the preceding experiment.
The user explicitly requested a probing build that keeps that session alive.

The b90 probe lives inside `Switch2BluetoothWindowsInputLease`, uses its exact
`WinRtGattService`, and sends the one supported audio setup command through its
existing `Switch2BluetoothPlayerLedCommandChannel`. A small external client
speaks only to a current-user-only local named pipe. It never opens Bluetooth.
There is no second mapper or controller transport owner.

`DS4WINDOWS_SWITCH2_AUDIO_PROBE=1` plus a validated portable-lab context enables
the feature only for Bluetooth Pro controllers. The opt-in runtime suppresses
automatic idle/absolute disconnect for that Pro; it does not modify the profile,
disable manual disconnect, or change Joy-Con/USB behavior. Normal app launches
retain their original timeout policy.

Commands and lifecycle details are in
[`utils/Switch2BluetoothLabClient/README.md`](../../utils/Switch2BluetoothLabClient/README.md).
Status queries cause no radio I/O; explicit inventory/header queries run on the
worker. There is no automatic microphone subscription or audio-output write.
`configure-audio` is the exact documented `17/02` volatile request, not an
arbitrary command API. Requests are serialized with LEDs; cancelled writes retain
actual completion before retirement. An idle or disconnected probe client does
not close the controller. A failed/late probe does not dispose its live service.

## Build and software evidence

- Version: `5.0.4.90-Bluetooth-Audio-Probe`, Release x64.
- Focused protocol/lease/pipe tests: **95 passed, zero failed**.
- Full suite: **3,904 passed, zero failed, 11 opt-in skips**, 3,915 total.
- All **146 tests with allocation-related names passed**, including the new
  zero-allocation report observation check.
- Skips: three live application-audio captures and eight separately built,
  hash-pinned Go-process interop scenarios. They were not enabled for this run;
  no allocation or controller-lifetime failure was waived.
- Named-pipe tests check repeated queries with advancing report counters,
  input-generation preservation, bounded/unknown request rejection, and late
  operation ownership. Command tests check exact setup bytes, unrelated reply
  filtering, continued LED use, and cancellation during a noncooperative write.
- An overlong pipe request can be disconnected while the caller is still
  writing, or receive an error. The test now accepts both explicit rejection
  outcomes and verifies no radio operation occurs and a later valid query works.

Pinned package SHA-256:

| File | SHA-256 |
| --- | --- |
| DS4Windows.dll | `F7FA382D00C0A6160DE555248845FC4EB0F56BCFDF2C5FB8A3D952A4D6C5D59F` |
| DS4Windows.exe | `89B4926EECCFBCE176E48DD20578E9AB22A69983713A63F2FF7F3E2D1AF26F87` |
| viiper.exe (unchanged) | `7B6B00CF3AC205549AF80692E45BD7785D3B8BC558A92D4FF5A1A61060592B78` |
| xbox-one-authorized-persona.json (unchanged) | `2A85D3395529C7305F55338E4965A7FFD4E269DDE535FA41BD98BC54D67111C4` |

## Portable launch evidence

The package is in the user's Desktop controller lab under
`runtime/DS4Windows-current-2026-09-07-b90-bt-audio-probe`, with saved b88 settings
copied into its own `lab-data`. Installed Program Files builds, drivers,
associations, Windows defaults/volumes, and startup tasks were not changed.

The first launch encountered the old b88 broker. Same hash was insufficient:
the portable guard also requires the exact package-local backend path, so it
refused startup. The guard was **not weakened**. Restart Manager returned 351
with force disabled; VIIPER's Windows startup deliberately calls `FreeConsole`,
so console signaling was not available. The failed startup's normal shutdown
also did not finish while blocked before controller initialization.

After confirming exact portable executable paths/hashes, no established broker
clients, and that the failed app had never begun controller discovery or opened
a profile editor, only that failed startup and the old idle broker processes
were terminated. No user files or installed services were removed. The matching
b90 broker and app were then started normally. The launcher does not offer the
unsuccessful cross-folder broker-reuse option.

On 2026-09-07 at 12:07 local, the b90 log reported:

- Running as Admin;
- VIIPER virtual-controller backend ready;
- Searching for controllers / Shared Mode;
- Switch 2 Bluetooth discovery active, generation 1.

The app was left running. **A live connected Pro and advancing same-generation
probe results are not yet verified at this checkpoint**; the controller still
needs to wake/reconnect. No headset setup request or audio-output payload has
been sent by b90 yet. Bluetooth headphone audio remains an open research and
implementation task, not a claimed working feature.
