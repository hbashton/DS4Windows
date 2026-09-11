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
Current MSI registration was independently checked unchanged. The user later
deliberately reinstalled 5.0.3.0 as the known-working comparison build. That
fresh installed reference was not removed or uninstalled by the orphan cleanup
or the private-candidate handoff.

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
  Switch 2/Joy-Con/Nintendo test classes. Allocation checks remained enabled
  and passed.
  Result: `isolated_results/rc457-native-rumble-final-green/full-native-rumble-disconnect-green.trx`.
- Functional source is committed as
  `14b8920b060ea9e81b64747116da543a54c93c08`.
- The complete private portable candidate was verified and launched, as
  recorded below. It is not a new published release.
- Physical acceptance in Hades II and Expedition 33 remains required. No claim
  of zero end-to-end latency or complete game coverage follows from unit tests.

## Verified private portable handoff

Candidate root:
`C:\Users\hbash\Desktop\DS4Windows-Haptics-Disconnect-Fix-20260911`.
The archive and extracted payload both passed strict verification with all
**553 files**, including the pinned broker and required runtime/dependencies.

- `PRIVATE-CANDIDATE-MANIFEST.json` SHA-256:
  `D47D96AC468E7341BA0F3873CACFEC44E630F177CAF3E099EE2A721C2491FFFA`.
- Verified archive:
  `resume\x64\Release\DS4Windows_HAPTICS-DISCONNECT-FIX-20260911_x64.zip`.
  SHA-256:
  `BE258AD8C3372DAC7B217C2606FB11FB7F2EFDD464FB4451E00EA8AEAD72D9BD`.
- The first package failed the unchanged strict runtime-config check because
  local SDK 8 emitted different runtime metadata. The exact released runtime
  config was restored only in fresh staging, and the unchanged verifier then
  passed. The original failed archive and runtime config remain preserved in
  `isolated_results/rc457-rumble-private/original-runtimeconfig-evidence`.
  No verification rule was weakened and no installed runtime was changed.

At 18:07:18 local time on 2026-09-11, the private mapper started as PID 16132
with portable broker PID 3164. Bluetooth helper PID 28488 belonged to mapper
16132. The log found physical DualSense `10:18:49:BB:74:C7` at 18:07:25 and
recorded virtual DualSense association, active feedback, and speaker PCM
startup at 18:07:27. These are connection/startup observations, not proof that
a game supplied PCM or that the physical haptics feel correct.

The installed comparison build and original user profile were left intact.
The `dsm` profile's verified SHA-256 remained
`FA2F06C5C7345DA583D5710327FCDA42B78A9F3951D4392D6656A4FF856BE8AA`.
Normal RC4.3 shutdown removed the physical controller's HidHide entry; the
strict handoff guard detected that change and halted. Handoff resumed only
after explicit restoration of that exact entry. The private lab mode cannot
change HidHide device entries. Only the private application's allowlist entry
was added; global cloaking settings were not changed. The guarded halt,
restoration, profile copy, and final launch evidence are retained under the
candidate's `handoff-evidence` directory.

A subsequent non-suspending startup sample is retained under
`isolated_results/native-haptics-evening-private-startup`. Mapper 16132 and
helper 28488 both reported `problems=[]` and `suspended=false`. The mapper had
zero transport fault reports and zero rejected reports, with 17 control
deliveries and zero speaker deliveries. Helper rumble mode, improved mode,
and both motors were zero in this idle sample. No transport fault was observed
there; this is **not physical haptics acceptance**. Hades II and Expedition 33
feel/trigger confirmation remains pending.
