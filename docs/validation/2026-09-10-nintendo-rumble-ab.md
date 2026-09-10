# Nintendo rumble: controlled comparison, September 10

This is a private validation record, not release notes or a smoothness claim.
The user extended unattended work to one hour at approximately 12:56 UTC;
the current work window ends at 13:56 UTC on September 10, 2026.

## Latest status

- Complete private D is prepared from `c6a763d`, with all 551 files verified;
  neither app is running and no installed files were changed.
- SDL-style first-active held-body output and two bounded Bluetooth lifetime/
  retry corrections are implemented. The final full suite passed 5,074 with
  11 gated skips, twice. Strict allocation checks remained enabled.
- The environment-gated B experiment below is historical and has been removed
  from current production code. The later first-active section supersedes it.
- Physical smoothness still needs a controlled hands-on comparison. A separate
  exact-native Bluetooth burst-coalescing defect is reproduced and remains open.

The sections below preserve the chronological evidence and its limitations.

## Established starting point

- The September 9 installed RC4.5.4 Bluetooth left Joy-Con Heavy trace had one
  unchanged three-active-subframe waveform, no counter discontinuities and no
  intervals over 20 ms. The user still reported crackling. See the
  [preceding evidence](2026-09-09-haptics-burst-and-joycon-followup.md).
- Complete private candidate A uses DS4Windows checkpoint `db78c9d` and the
  independently built VIIPER candidate corresponding to checkpoint `9999f25`.
  All 551 composed payload files, the runtime/dependency closure and Xbox
  identity were verified. It is not an incomplete publish-directory handoff.
- A changes standalone Joy-Con 2 Bluetooth held servicing to 10 ms, retaining
  the waveform bytes. Pro/joined Bluetooth remains 15 ms; USB remains 12 ms.
  Actual scheduling and input-tail measurements are still required.
- A connected a right Joy-Con at 90% battery at 07:05 local. It was closed at
  07:07; the installed build subsequently connected a Pro. Therefore the
  user's tentative "slightly better" feedback cannot yet be attributed to a
  controlled before/after comparison. The earlier left Joy-Con's 10% battery
  and the different physical half are additional comparison confounders.
- With explicit user approval, A was resumed at 12:24 UTC using its existing
  isolated data and verified immutable payloads. Authenticated backend
  readiness and Bluetooth discovery succeeded. At the time this section was
  written, no controller had woken in the resumed session.

## One-variable packet experiment

The pinned implementations do not establish one universally correct held
layout:

| Source | Ordinary held group |
| --- | --- |
| DS4Windows baseline A, before this change | Three identical active subframes |
| [Switch2Connect](https://github.com/TommyWabg/Switch2Connect/blob/61ac6642ce12fe7217e38a860b14863b18ca7e28/src/virtual_controller.py), lines 2323–2327 and 2391–2393 | Held fallback repeats all three |
| [Hifi SDL BLE](https://github.com/hifihedgehog/SDL/blob/d98c5804a9d20b0d96e993741797878c86b8f1e1/src/joystick/windows/SDL_ble_switch2joystick.c), `BLE_WriteRumble` | One active, two neutral-amplitude tails |
| [SDL USB](https://github.com/libsdl-org/SDL/blob/c71abd08605b8bb7078372307a93274725c99fe0/src/joystick/hidapi/SDL_hidapi_switch2.c), `UpdateRumble` | One active, remaining bytes zero |

The B experiment is disabled by default and requires both portable-lab mode
and the exact cold setting
`DS4WINDOWS_SWITCH2_HELD_SUBFRAME_EXPERIMENT=neutral-tails`. Only standalone
Joy-Con 2 Bluetooth lifetimes can opt in. Only ordinary Test Heavy/Test Light
preview output is transformed. The first subframe, every control/frequency
field, strength setting, counter, cadence, ownership and freshness metadata
remain unchanged; only the second and third subframe amplitudes become zero.
The first write and held refreshes use the same transform.

Pro, joined Joy-Cons, USB, normal launches, game feedback, profile effects,
source-preserved rich previews, finite one-shots, PCM, native packets and
impulse/release presentations are excluded. There is no shared-codec change,
amplitude boost, inferred firmware slot duration, or claim of equivalent
actuator energy. This experiment may feel weaker or worse; it is not a fix
until compared on the same physical controller under matching conditions.

Failing-first fixtures reproduced five expected trailing-amplitude failures
with three unchanged controls passing. After implementation, 33 new cases and
188 combined targeted cases passed without skips. They include actual sink to
BLE encoder bytes, cold factory configuration, finite/repeat distinctions,
uncertain exact retries, Stop/no resurrection, default identity and strict
zero allocation with an allocation-positive control. The subsequent unfiltered
full run passed **5,040**, failed **0**, and skipped **11** explicitly gated
hardware/cross-runtime rows. Allocation checks remained enabled. Evidence is
`isolated_results/held-subframe-experiment/full/held-subframe-full-reviewed.trx`.
No subjective or physical hardware acceptance is inferred from that result.

## Measurement and acceptance rules

1. Identify the actual executable, transport, physical half, pair state,
   profile and battery before every comparison. Do not mix installed and lab
   results or compare a depleted half with a charged different controller.
2. Record accepted physical-report publication counters before/after idle and
   rumble windows. Compare deltas only within matching runtime, physical and
   clock generations. Cumulative maximum gaps cannot be subtracted.
3. Observe bounded host HCI windows with start and Stop inside the capture.
   Check waveform identity, active subframes, counter progression, write gaps,
   explicit neutral output, and absence of later active output. Host evidence
   is not radio-delivery or physical motor completion evidence.
4. Change cadence and packet shape in separate comparisons. Keep strength and
   carriers fixed. Ask for tactile confirmation rather than infer it from
   green software tests or a lower average interval.
5. Reject a candidate that worsens input tails, loses finite effects, changes
   unrelated feedback, or fails to stop. No new public release is authorized
   by this private experiment.

The private observer now has separate strict parsers for a Joy-Con-shaped
36-byte event402 and a Pro-shaped 52-byte event402. The latter records the two
protocol groups separately. Synthetic malformed/layout/counter/neutral tests
pass and independent review found no concrete capture blocker. Both modes
retain the 25-second owned-session deadline, bounded rows, no raw ETL/payload,
no controller writes and explicit host-only limitations. Missing/unknown
events are inconclusive, not evidence that rumble was absent.

## Resumed A: measured host output, before the first-active-frame change

The resumed mapper is PID 416, broker PID 13920, from the complete A folder.
It connected a standalone Bluetooth right Joy-Con 2 at 12:33 UTC and a
Bluetooth Pro at 12:34 UTC, both reporting 90% battery and using the isolated
`ds` profile with virtual DualSense output. No physical left Joy-Con was
connected in these captures.

| Pro test / 25-second host capture | Packets | Median gap | p95 | p99 | Maximum | Gaps >20 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Heavy, `pro-heavy-A-0735` | 1,475 | 15.6478 ms | 16.271 ms | 16.6993 ms | 25.8153 ms | 1 |
| Light, `pro-light-A-manual` | 1,468 | 15.6517 ms | 16.2975 ms | 16.6665 ms | 20.3437 ms | 1 |

Both protocol groups retained the same waveform throughout each capture:
three active subframes, maximum amplitude code 453, no counter discontinuity,
no observed neutral packet, and no ETW event loss. These are host-stack
observations, not actuator timing or proof of a firmware timeout. The user's
small perceived skips remain unexplained by these measurements alone.

During the roughly 40.7-second Heavy input comparison, clock/runtime/physical
generations remained unchanged. Pro accepted 2,729 reports with 17 publication
gaps above 20 ms; right Joy-Con accepted 2,691 with 25 above 20 ms. Neither had
a gap above 50 ms in that delta window, and neither recorded a busy publication
drop. Earlier cumulative maximum values are not this window's maximum.

The automatic Light-click capture (`pro-light-A-0741`) had no matching output;
the user's subsequent manual click produced the measured Light capture above.
The unattended Joy-Con-shaped observation (`joycon-A-unattended-1305`) also had
no matching packets. These are inconclusive for the corresponding motor, not
evidence of output loss. The intended Stop was not captured in either primary
Pro window. A later unattended UI Stop attempt did not change the displayed
Stop Light state or establish a neutral output, so physical Stop acceptance
is explicitly **not verified** here.

## Requested first-active-frame implementation

After asking to focus on cadence while away, the user explicitly requested
the SDL active-frame change as well. Shape and timing remain separate changes.
The earlier B build at `7011175` was fully composed and prepared but never
launched; its gated preview experiment is not a hardware-tested result.

The production candidate applies first-active plus zero-amplitude tails only
at the Switch 2 delivery sink, for ordinary canonical body-compatible Apply
frames. All four arbitration origins are eligible. It retains the current
first subframe, control fields, gain, lifetime/sequence metadata and cadence.
Source-preserved/native/PCM/one-shot/adaptive designs, impulse and release
presentations, raw nonzero trigger lanes, Stop and Neutral are excluded. The
shared compatibility-group builder is deliberately unchanged because it also
provides body samples for richer DualSense mixing.

This is a BLE-shaped adaptation, not byte-identical SDL USB: the USB donor
zeros the entire trailing words, whereas the BLE donor retains neutral
carrier fields. Our actual sink coverage is Pro USB and standalone/joined
Joy-Con 2/Pro BLE; this change does not create a Joy-Con USB transport.

Other donor mechanics were reviewed, not blindly copied: SDL USB services
rumble from its input-read loop with a 12 ms gate and a 24 ms joined-child
workaround; the BLE donor uses a 10 ms update pump. DS4Windows keeps its
independent output-only maintenance worker, immediate Stop/new-frame
admission, and exact uncertain-payload retry/counter ownership. No donor
comment establishes the playback duration of an individual subframe.

The failing-first run caught 18 expected old-layout failures with 30 unchanged
controls passing. After enabling the sink policy, all 48 focused cases passed.
The first full run caught two older integration helpers that explicitly
required three identical active frames. Their body-only assertions now check
one active frame plus exact zero-amplitude, carrier-preserving tails; rich
frame assertions were not weakened. The reviewed full run passed **5,055**,
failed **0**, and skipped the same **11** gated hardware/cross-runtime cases.
Strict zero-allocation and allocation-positive controls remained enabled.
Evidence: `isolated_results/held-rumble-production/full-reviewed/first-active-full-reviewed.trx`.

The 18 new composed scheduling rows exercise the real worker and sink with a
controlled physical-writer clock at 10/12/15 ms. They cover service duration,
early/late callbacks, competing publishers, bounded contention retries, exact
uncertain retries and Stop sealing rearm. They establish no new timing defect
and are not measurements of Windows radio or actuator scheduling.

At 13:07:35 UTC the old A mapper/broker processes were terminated after the
observed UI Stop Light and close inputs did not take effect. Exact retained
PID/start-time/path/hash checks scoped termination to that private session.
No installed files changed. This ended the old host output stream; it is not
a clean Stop test or physical neutral acknowledgement. No new build has been
launched, and no user wake/manual test is requested while the user is away.

## Native DualSense rapid-fire: new cross-process software evidence

The bounded synthetic harness ran against the actual A mapper assembly
(`FA2F033F7A5375B662D8DC6885C05B24681A7FBACE0AC9AF26397F93CBA84B45`)
and reviewed VIIPER binary
(`DC2D47B49F94FA827903FD24F18AE0289EBE682F09D6EF67A09EE7A6A8005EB7`).
It uses the authenticated production client, USB/IP input commands, V5 reader,
four-slot native lane and 64-slot physical ring, terminating at a synthetic
USB physical-writer hook with no HID handle or Windows controller attach.

All **32** broker-accepted commands reached that hook exactly once, in order,
through a deliberately observed full queue held for at least 40 ms. Short
stops, duplicates, independent trigger/LED validity and truncated trigger
groups passed. There were zero compatibility fallbacks and callback failures.
The feedback workers joined, the private device was removed, the owned broker
exited, and its generated key was deleted. Result:
`Desktop/Controller-Diagnostics-2026-09-10/native-burst-A-1312/SUMMARY.json`.
Harness assembly hash:
`CB4B9776200039C3A19CCE347617D05D15914B26EFE1026D84DF74CD351853E5`.

This is stronger cross-process software evidence for the prior burst fixes,
not a GTA V Enhanced gameplay test, physical actuator confirmation, Bluetooth
transport test, media-fairness result or arbitrary-overload guarantee. These
private fixes remain outside published RC4.5.5.

## Confirmed BLE write-lifetime defect, separate from perceived crackle

The adapter formerly passed its 100 ms cancellation token into the WinRT
write and marked output idle as soon as that managed wait ended. The pinned
[CsWinRT bridge](https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/src/cswinrt/strings/additions/Windows.Foundation/Windows.Foundation.cs#L267-L290)
cancels its managed task separately from native completion. Read-only metadata
inspection corroborated that mechanism in our compiled SDK projection. Thus
timeout could admit another send or teardown while the original native write
was still pending. The captured steady rumble windows do **not** establish
that this timeout path caused their tactile skips.

The correction retains the uncancelled operation and copied payload while
bounding the output caller's wait to 100 ms. An exact retry consumes the late
receipt without duplicating the native call; another payload cannot overtake
a pending operation. Admission and its drain obligation share the short lease
state lock, but neither platform entry nor waiting holds that input-facing
lock. Teardown's public observer stays bounded while actual resource release
waits asynchronously for native completion. No global WinRT semantics or
cadence constants changed.

Eight reproductions failed before the correction. The initial combined lane
passed 135/135; six additional immediate-completion/admission-race/physical-
counter cases brought the focused set to 14/14. Independent concurrency review
found no concrete blocker. The unfiltered combined full run passed **5,069**,
failed **0**, and skipped the same **11** gated cases. Evidence:
`isolated_results/rumble-completion/full/completion-full.trx`, SHA-256
`BD46AE162CD7B8F91A4510D572EA603F424346693CFA74F3EC35245ECCFCA725`.

The real WinRT adapter already allocates detached buffers and projected tasks;
this change replaces the per-call CTS/timer with a drain completion source and
an observer for incomplete operations. No real-adapter zero-allocation or net
allocation improvement is claimed. The existing strict fake-lease hot-path
allocation tests remained enabled and passed.

## Complete portable C and repeated native burst check

The complete private C candidate was built from clean source
`fd4ac13c4b7887f56517fa17162d3f8ab609cac9`, with the first-active-frame and
native Bluetooth write-lifetime fixes. Its 551-file manifest passed the full
runtime/dependency/Xbox-identity closure verifier. It is prepared, not launched;
no new hardware or tactile result is implied. Location:
`Desktop/DS4Windows-Haptics-Lab-20260910-C/portable/DS4Windows`.

- DS4 assembly: `E7534285182DAEC6B6F3703B29FBE2DA752A63359FED1D118BE838FD2405588D`.
- Candidate broker: `DC2D47B49F94FA827903FD24F18AE0289EBE682F09D6EF67A09EE7A6A8005EB7`.
- Private manifest: `122C634B9267A1F8A0A6D11DFBEF1EC84907B17023570EC422227E1C174BDF72`.

The same native cross-process harness passed against C: 32 accepted commands,
32 ordered synthetic USB physical publications, zero compatibility fallbacks
or callback failures, and a measured forced-full stall of 55.9739 ms. Owned
workers and broker exited and the private device was removed. Evidence:
`Desktop/Controller-Diagnostics-2026-09-10/native-burst-C-1323/SUMMARY.json`.
The Bluetooth-helper and PCM/gameplay limitations of the A run also apply to C.

Reference cadence mechanics differ beyond nominal interval constants. SDL
USB's shared HID rumble thread adds a 10 ms sleep after a write; the BLE donor's
10 ms gate runs inside its update pump, not an autonomous 100 Hz timer. Our
worker anchors its next held refresh to successful write start plus the
interval, subtracts service time once, and does not emit catch-up bursts. No
firmware subframe playback duration or periodic silent gap was established.

## Actual-worker cadence isolation, without a physical transport

The separate hash-pinned harness executed C then A for about 30 seconds each.
It composes the real runtime, preview renewal, lifetime, sink and maintenance
worker, substituting only a successful generation-bound BLE lease. The dormant
worker is replaced with the same worker wrapping the actual service delegate
for bounded observations. It requests and releases the same scoped 1 ms timer
period as app startup; both APIs returned success. No controller, broker, UI,
Windows transport or input slot is opened. Raw heavy strength is byte 100, not
the UI's 100 percent setting. Each condition lasts 7.5 seconds.

| Candidate / model / condition | Writes | Median interval | p99 | Maximum |
| --- | ---: | ---: | ---: | ---: |
| C Pro / quiet | 489 | 15.2945 ms | 16.0310 ms | 16.4322 ms |
| C Pro / neutral mapper publications | 489 | 15.2836 ms | 16.1443 ms | 16.7868 ms |
| C standalone right Joy-Con / quiet | 720 | 10.3301 ms | 11.5054 ms | 12.1671 ms |
| C standalone right Joy-Con / neutral mapper publications | 716 | 10.5478 ms | 11.1519 ms | 11.7050 ms |
| A Pro / quiet | 485 | 15.4184 ms | 16.2973 ms | 16.4341 ms |
| A Pro / neutral mapper publications | 484 | 15.5010 ms | 16.3102 ms | 16.5038 ms |
| A standalone right Joy-Con / quiet | 717 | 10.4811 ms | 11.3201 ms | 11.4958 ms |
| A standalone right Joy-Con / neutral mapper publications | 717 | 10.5142 ms | 11.3156 ms | 11.4981 ms |

Every phase had zero write intervals above 20 ms, zero neutral payloads,
counter discontinuities, waveform changes, overflow or worker failures.
C's Pro groups each had one active subframe; its right Joy-Con group had one.
A retained three per group. Publishers joined and fake lifetimes retired; no
writes were observed in the subsequent 40 ms. That bounded observation and
idle wrapped service are not proof of joining every Timer callback.

These short sequential trials do not establish a performance advantage for C,
radio/firmware continuity, tactile smoothness or behavior under arbitrary load.
They did not reproduce a preview-renewal or composed scheduling stall under
the tested quiet and neutral-publication conditions. The full WinRT adapter,
GATT completion and physical input workload are intentionally absent.

Evidence (folder labels are not authoritative timestamps; JSON records UTC):
`Desktop/Controller-Diagnostics-2026-09-10/actual-schedule-C-1334/SUMMARY.json`
(13:32:48-13:33:18 UTC) and
`Desktop/Controller-Diagnostics-2026-09-10/actual-schedule-A-1335/SUMMARY.json`
(13:33:29-13:34:00 UTC). Harness SHA-256:
`6BB2FE21855FFB5A092A98A8AE844FDF013BE3C6F08989B67690F8E27F3CCCA9`.

## Open native Bluetooth ordering defect reproduced independently

The C USB burst result does not cover the physical Bluetooth helper. A separate
isolated fixture invokes the real `HelperHost.ReceiveGameStateAndTemplate`,
actual pacer loop and physical writer, with synthetic native I/O and no device
handle. Both exact commands are admitted while presentation is paused, then
the real presenter is started. C reproduces two ordering failures:

- Rumble pulse 91/173 followed by Stop: only the zero-motor Stop is submitted.
- Right-trigger A followed by B: only B is submitted.

Both spaced positive controls submit the two expected reports in order. The
helper's `pendingControllerState` accumulator merges the commands before
presentation; the existing validity composer intentionally replaces an earlier
valid field with a later valid field. This is a confirmed native-command loss
boundary, not proof of the GTA reporter's cause or of USB PCM behavior.

Evidence: `isolated_results/dualsense-native-bt-ordering/baseline-c.jsonl`,
hash-pinned to the same C assembly above. These deliberately failing diagnostic
cases are isolated from the normal test suite; they must not be represented as
fixed because the existing main suite is green.

A correct fix needs a bounded exact-native admission/acknowledgement domain,
unchanged local latest-state behavior, immutable head/retry ownership, both idle
0x31 and media-piggyback coverage, and lifecycle cancellation. Merely blocking
the shared helper command reader on a full FIFO risks starving Clear/Stop,
templates and media. This larger correction is not included in C.

## Narrow idle native Bluetooth retry correction after C

A fifth isolated C case reproduced a separate retry stall: one accepted native
command encounters an occupied oldest physical-writer slot, that credit later
returns, but the helper emits nothing for one second. Its rejected-write path
had manufactured `controllerStateReportsAhead = 1` without any queued media,
so the command could only recover when another source command or media arrived.
Evidence: `isolated_results/dualsense-native-bt-ordering/baseline-c-idle-retry.jsonl`.

The correction grants the one-media-frame fairness yield only when an actual
speaker report is queued at the head. With no queued media it leaves the current
ordering boundary unchanged, normally zero for an idle claim. In particular,
it must not clear a newer microphone boundary published concurrently during
physical I/O. This is separate from the still-open exact-native coalescing defect.

Five normal-suite tests exercise the actual helper/pacer/physical writer with
synthetic native I/O: recovered idle credit, real queued-media fairness and
subsequent piggyback, Clear into a new epoch, Stop joining before late credit,
and preservation of a concurrent two-media-frame microphone boundary. They
passed 5/5. The unfiltered Release x64 suite passed **5,074**, failed **0**, and
skipped the same **11** gated cases (total 5,085). No real controller was opened
for these tests. The complete C candidate predates this narrow correction.
An independent repeat of the full suite produced the same counts. Durable TRX:
`isolated_results/dualsense-native-bt-ordering/full-suite/native-idle-retry-full.trx`,
SHA-256 `27C3762397622F12BC955FC493C0F1F635EE15757A8608CEE159060B49640E44`.

## Final private D candidate and work-window handoff

Complete portable D contains all three corrections from this window, including
the narrow idle retry fix, built from clean source
`c6a763ddf7ecfa151fd055f10249e23519c89b4c`. Independent review found no concrete
blocker in the final idle-retry guard, its concurrent microphone-boundary
preservation, or the first-active policy's rich/impulse/finite protections.

- Portable: `Desktop/DS4Windows-Haptics-Lab-20260910-D/portable/DS4Windows`.
- App SHA-256: `F80F1378A80F927E8C730DBE77EFCDC1A5DAFD01F2BEF80A4AEB77EF04331803`.
- Broker SHA-256: `DC2D47B49F94FA827903FD24F18AE0289EBE682F09D6EF67A09EE7A6A8005EB7`.
- Manifest SHA-256: `4B60033271DE26D3E517D97727D5A5F37B4537E5EB56069C474ACF31BB50DF10`.

Both full composition and the separate preflight verified all 551 files,
including the bundled broker, self-contained runtime and Xbox identity. Fresh
isolated data was prepared without launching either application. D is a private
test candidate, not a published release or a claim of production haptics parity.
The composition's base ZIP is an intermediate with the released broker and is
**not** the final lab or a handoff artifact; only the final portable directory
contains the pinned candidate broker.

The pinned D binary also passed the same cross-process native USB burst check:
32 accepted commands, 32 synthetic physical publications, zero compatibility
fallbacks or callback failures, with an observed full-queue stall of 42.8868 ms.
Workers joined, private device removal succeeded, the owned broker exited, and
the generated key was removed. Evidence:
`Desktop/Controller-Diagnostics-2026-09-10/native-burst-D-1351/SUMMARY.json`.
This still does not cover the confirmed Bluetooth native-command coalescing
defect, physical GTA gameplay, PCM fairness, or actuator smoothness.

No installed files, drivers, profiles or public releases were changed during
this window. No controller wake or further manual test was requested after the
user left. All owned captures and synthetic runs ended; both applications are
left closed. Hands-on comparison of D's rumble remains outstanding. The next
software task is the explicitly bounded native-command credit/ordering design,
not an unvalidated FIFO or a claim that the GTA report is fully resolved.

## Later Bluetooth source follow-up

The subsequently requested Bluetooth command-ordering work is recorded in
[the dedicated validation ledger](2026-09-10-dualsense-bluetooth-native-ordering.md).
It corrects the identified native coalescing boundary and passes the expanded
5,185-test suite. D and public downloads still predate that source checkpoint;
hardware GTA/Bluetooth confirmation remains outstanding.
