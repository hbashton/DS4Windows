# DualShock 4 audio to physical DualSense / Sonar, 2026-09-12

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Confirmed live fault

Installed DS4Windows PID 30652, Bluetooth helper PID 27428, VIIPER PID 30124.
Read-only endpoint inspection showed Binding of Isaac rendering to Sonar Gaming,
and nonzero output on the virtual Wireless Controller render endpoint. Sonar
was not simply sending audio to an unrelated output.

Two non-suspending ClrMD samples of the same active speaker object showed:

| Counter | First sample | Second sample |
| --- | ---: | ---: |
| Direct PCM callbacks | 31,372 | 31,515 |
| Direct input frames (32 kHz) | 10,039,040 | 10,084,800 |
| Consumed frames | 0 | 0 |
| Speaker frames sent | 0 | 0 |
| Startup warmup remaining | 8 | 8 |
| Local presentation baseline | 0 | 0 |
| Reused helper lifetime presented reports | 7,489 | 7,489 |

The capture ring was full (1,152,000 frames). The helper still required priming
and had one queued report. Live heap reads are best-effort/non-atomic, but the
repeated samples, endpoint activity, and executable code agree.

The already-running-helper branch opened the source gate without recording a
new presentation baseline. The non-V5 producer interpreted the old lifetime
count as completed startup and stopped at its one-report steady-state limit.
The helper needed eight reports to prime: neither side could progress.

The fix snapshots the helper presentation counter before opening that gate.
It does not restart the helper, clear native commands, change trigger bits,
change the physical protocol, or increase the steady-state queue target.
The producer also retains its bounded startup allowance while its own warmup
reports remain: delayed prior-source or native-control acknowledgements cannot
stand in for those reports.

## Regression evidence

`DualSenseReusedSpeakerSessionTests` exercises the real lifecycle worker with an
inert, hardware-free pacer. Before the fix, the three reused-counter cases
failed; the zero-counter case passed. After the fix, all four passed. Tests
also verify native epoch/haptics generation are unchanged and no helper startup
attempt occurs.

The combined targeted run passed 207 tests, including lifecycle, prime/native
command handling, endpoint policy and Sonar capture factory coverage.
Evidence is in `isolated_results/sonar-ds4-dualsense-20260912`.

## Separate Sonar compatibility gap

Physical DualSense USB and Bluetooth endpoint capture did not reuse the proven
Sonar polling/float-format policy used by physical DualShock 4. A shared lazy
factory now selects that policy for positively identified Sonar endpoints.
Other endpoint constructors, per-app capture and direct VIIPER PCM are unchanged.
This was a separate compatibility gap, not the cause of the observed direct-PCM
startup stall.

## Source-rate and USB quality coverage

External/app capture for physical DualSense already uses 48 kHz floating-point
audio and feeds its 48 kHz Opus encoder without a 32 kHz intermediate. New tests
exercise the actual app capture factory with a virtual DS4 endpoint kind and
prove samples reach the DualSense ring unchanged. Direct virtual DS4 capture
remains stereo PCM16 at 32 kHz and is converted once for DualSense. Sustained
320-frame callbacks and fractional chunk boundaries preserve duration and phase.

The virtual DS4 headset source is distinct from the physical DS4 Bluetooth
codec: the latter's established 16 kHz SBC route is unchanged. No virtual audio
descriptor or physical DS4 codec change was made.

The physical DualSense USB writer previously copied frames one-for-one even
when capture and output rates differed, and truncated callbacks above 4,096
frames. It now converts directly to the negotiated physical rate, preserving
48 kHz float without resampling. WDL processes partial callbacks immediately;
256 frames is a maximum work chunk, not an accumulation threshold. Tests cover
32/44.1/96/192-to-48 kHz, large and tiny callbacks, continuity, channel placement,
quiet float samples and 18 kHz content. Per-sample temporary byte arrays were
removed. The mismatched-rate path is not claimed allocation-free: WDL may resize
its input array for varying callback sizes.

A 63-test focused quality run and the full suite (5,684 passed, 11 hardware-gated
skips, zero failures) passed before source-retirement coverage was added. After
USB retirement changes, 72 focused tests and 5,693 full-suite tests passed, with
the same 11 hardware-gated skips and zero failures.

## Capture replacement safety

USB capture replacement now revokes the old source identity, clears queued
samples and resets converter history before publishing the replacement. Late
callbacks from an old source are rejected before their format can be read as
the replacement's format. Ordinary callbacks preserve fractional resampler phase.

Native capture stop/disposal runs outside the manager lock: WASAPI disposal can
join a capture thread already waiting for that lock. Stop and disposal failures
are contained independently so a failed stop does not prevent disposal. Tests
exercise a capture whose disposal waits for a callback, source resets, stale
callbacks and stop-failure cleanup without opening a real endpoint.

Independent review identified a further shared-process-capture lifetime race:
the previous last-subscriber check and registry eviction were separate. A new
subscriber could acquire that same session between those operations and have
its source disposed by the retiring subscriber. A deterministic test using the
real registry reproduced the failure before the fix. Last-subscriber removal,
closing admission and identity-checked registry eviction now share the same
registry-then-subscriber lock order as acquisition. Native disposal stays
outside both locks. Tests also cover two simultaneous consumers and late cleanup
of an old object after a successor occupies the same registry key.

## Final source validation and handoff

The final full-suite run passed **5,696 tests**, with **11 hardware-gated skips**
and **zero failures**. Evidence: `final-full-audio-retirement-fix.trx` in the
isolated results directory above. The focused shared-process/USB run passed 42
tests, with three existing hardware-gated skips; its three new ownership tests
all passed. `git diff --check` was clean.

Private portable composition uses the complete hash-pinned RC4.5.9 package and
replaces only the tested managed app DLL. It verifies all 553 payload files,
including bundled VIIPER, authorized Xbox identity, native dependencies and the
self-contained runtime. Separate composition output/private manifests record
the exact source and binary hashes and whether package verification succeeded.
No official release version, tag or installed file is changed by this process.

The running installed app has not been replaced. No audible-fix claim is made
yet. Applying the portable candidate and physical listening confirmation remain
outstanding. Original profiles and installed binaries remain untouched.
