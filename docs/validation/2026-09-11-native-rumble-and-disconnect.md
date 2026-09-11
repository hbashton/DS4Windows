# Native DualSense rumble and queued-disconnect investigation

## Observed environment

The user was deliberately rolling back installed releases to find a haptics
regression. The live capture below was **RC4.5.3 + VIIPER 0.1.3-rc4.5**, not
RC4.5.7. No diagnostic tool changed those versions. Physical Bluetooth
DualSense, virtual DualSense, and the existing `dsm` profile were selected.

## Hades II evidence

- Hades used SDL 2.32.6 and recognized the virtual USB DualSense VID/PID.
- Repeated non-suspending managed-memory samples found native control traffic
  advancing while atomic media remained at 138 delivered frames. The last media
  burst preceded Hades startup. CoreAudio independently showed no Hades session
  on the unmuted, active, four-channel virtual DualSense endpoint.
- This absence of PCM is **not proof of failed game support**. A separate bounded
  native-report watch captured nonzero SDL improved-rumble commands: native
  report flag 0 `0x02`, flag 2 `0x04`, light motor strengths including 43 and 76.
  Ordinary controller rumble does not need an audio stream.
- The Bluetooth helper continued completing writes. The captured samples did
  not show an active helper deadlock, media backlog, or transport write failure.
- Source review found that quiescent-state consumption erased continuous
  rumble mode bits, and the helper erased them again when merging subsequent
  media templates. This can end a correctly delivered native rumble effect at
  the next audio carrier. SDL explicitly uses a zero-mode command to return to
  audio haptics; mode bits cannot all be treated as one-shot validity strobes.

Reference: [SDL 2.32.6 PS5 implementation](https://raw.githubusercontent.com/libsdl-org/SDL/release-2.32.6/src/joystick/hidapi/SDL_hidapi_ps5.c).

The user identified 5.0.3.0 / RC4.3 as the last working DualSense-haptics build.
Exact tag `VIIPERRC4.3` resolves to `3579450dc8f50a74d9532e711249589c732c460b`.
Although the validity-consuming helper already existed there, raw game reports
used `TryPublishCachedBluetoothCombinedState` and retained the mode in the
streaming template. Commit `4d41a613` (first released in RC4.4) routed raw game
commands through `TryPublishAtomicNativeGameStateTransition`, exposing this
path to the mode-clearing helper. This matches the reported version boundary.
The repair stays on current source and retains subsequent immutable-command
ordering, retry, media scheduling, and per-trigger ownership improvements; it
does not restore the old coalescing transport.

Local diagnostic evidence is under `isolated_results/native-haptics-evening-*`
and `isolated_results/native-watch`. These are best-effort live reads, not an
atomic trace of every report. The watch ended normally with zero read errors;
it never suspended, injected into, or wrote to either controller process.

## Separate disconnect stall

After the user reported a stall, the log stopped at closing VIIPER connections.
Heap dumps of the mapper and Bluetooth helper established a circular wait:

1. The device-command worker executes queued UI `DisconnectBT`.
2. `StopOutputUpdate` waits for lifecycle completion.
3. The lifecycle worker joins the device-command worker before final output.
4. A concurrent service Stop also waits for that lifecycle completion.

The GUI was still pumping messages. Both RC4.5.3 and the then-current RC4.5.7
source contain this cycle. The fix must transfer a joined worker's disconnect
intent to the lifecycle owner, retain final neutral-output ordering, and keep
definitive completion waits for external callers—not substitute a timeout that
allows overlapping writers.

Evidence: `isolated_results/haptics-stop-stall-20260911`, including both heap
dumps, all managed stacks, and synchronization diagnostics. After capture, only
the exact stuck controller processes were closed and the same installed test
version was relaunched. The controller reconnected; profiles and installed files
were unchanged. Recovery alone is not the source fix.

## Old installation cleanup

The specific 5.0.3.0 entry was an orphaned RC4.3 Burn registration, not an
installed MSI. Its MSI product state was unknown; only the newer installed
product was registered. Three exact stale registry keys were exported and
removed. The old cached installer was moved to a recoverable Desktop backup;
the old uninstaller was not invoked against shared current-version resources.
Current MSI registration was independently checked unchanged.

## Validation status

- Four real-helper tests reproduced loss of the legacy/improved rumble mode
  on subsequent media, covering both idle-command and piggyback presentation.
  Red result: `isolated_results/rc457-native-rumble-mode/sdl-rumble-mode-red.trx`.
- Combined focused run: **116 passed, zero failed**, including 15 new
  real-helper rumble cases and four queued-disconnect lifecycle cases.
  `rumble-lifecycle-green.trx` SHA-256:
  `FC89393D6D7712FEB531E5090ECE0EDF5FAB466237D88B95ECA074DC73E612BD`.
- First full run: 5,410 passed, three failed, 11 gated skips. The failures were
  old cached-mode expectations. They were updated to assert continuous mode
  separately from one-shot triggers/LEDs; the idle case additionally checks the
  actual physical zero-motor guard. No case was removed or skipped.
- Final rebuilt, unfiltered Release x64 run: **5,413 passed, zero failed,
  11 gated skips**, 5,424 total. This includes **2,646 passing tests** in
  Switch 2/Joy-Con/Nintendo test classes. Allocation checks remained enabled.
  Result: `isolated_results/rc457-native-rumble-final-green/full-native-rumble-disconnect-green.trx`.
- Private-candidate identity and physical acceptance are pending.
- Physical acceptance in Hades II and Expedition 33 remains required. No claim
  of zero end-to-end latency or complete game coverage follows from unit tests.
