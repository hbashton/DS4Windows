# Nintendo rumble: controlled comparison, September 10

This is a private validation record, not release notes or a smoothness claim.
The user extended unattended work to one hour at approximately 12:56 UTC;
the current work window ends at 13:56 UTC on September 10, 2026.

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
| DS4Windows | Three identical active subframes |
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
