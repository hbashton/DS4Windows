# Cross-controller feedback throughput and stop audit

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](../README.md).


## Scope and rate interpretation

The follow-up to RC4.5.9 asks whether other controllers have analogous feedback
backlog/skipping, and authorizes targeted fixes where needed. This work is source
review and hardware-free tests. It does not replace the running application,
install drivers, exercise a physical controller, publish a release or close #81.

Baseline DS4Windows: `254a298f1c26ae1d4c83545b4779efc1853b68c2` (publication
documentation after released source `93ce8899a66ab3702824b879e1d7a8f378386db4`).
Baseline VIIPER: `02d93e403ffde7ad24c1b373c01c6eb04ce02f2e`.

The older #81 `a.txt` records 294,903 incoming native commands in 30 seconds,
approximately **9,830 reports/second**. Their raw contents were not captured, so
their redundancy is unknown. The later `c.txt` describes a different observation:
around 200 delivered commands/second and a shallow, backed-up DS4Windows queue.
See [the original evidence](2026-09-12-issue81-feedback-delay.md).

Accepting a 10,000-report repeat burst is not equivalent to physically rendering
10,000 distinct effects/second. RC4.5.9 has a 500 Hz native Bluetooth submission
ceiling, not a 9.8 kHz actuator claim. Distinct finite commands need ordering;
complete actuator state may instead use a latest-state mailbox. These policies
must not be substituted for one another without considering the protocol.

## Findings and changes

### Virtual DS4 with V3 speaker framing: truthful bounded admission

VIIPER's old 32-entry control channel discarded the newest report when full,
including an explicit final motor-zero, while its void callback reported no
failure to USB/IP. The hardware-free pre-fix characterization reproduced this.
This writer is selected only for speaker-output V3 framing; it is not every DS4
transport. Optional/default-off downstream autostop is not a universal rescue.

The repair uses a fixed 32-entry owned control ring, exact pending-repeat folding
for static complete states, and the existing USB output-admission interface.
Accepted distinct A/B/A and LED/zero changes are not evicted. One slot is reserved
for motor-zero. Full distinct overload returns explicit rejection without
committing the device's cached state. Host retry after USB no-space is not
guaranteed, and finite capacity cannot preserve an unlimited distinct stream.
Already claimed reports, blink effects, and media/lifecycle boundaries are not
treated as redundant. Callback registration and retirement are generation-fenced.
Serial reservations also use exact ownership tokens: a rejected overlapping
handler or stale cleanup cannot release another stream's reservation. Actual API
handoff waits for the previous handler and its cleanup before starting its
successor; the added localhost reconnect test exercises that path.
The existing stream-end serial lifetime is not changed into device-retirement
ownership. A different device claiming a serial during a close-first gap now
causes explicit rejection instead of silently duplicating identity. Ordinary
overlapping API handoff is covered; this is not a universal reconnect guarantee.

### Physical DS4 during Bluetooth audio: fresh motors bypass maintenance

The existing approximately 33 ms policy delayed fresh motor changes as well as
unchanged state/LED maintenance. The real non-forced compositor reproduced the
delay in both speaker-only and full-duplex test cases.

Only motor bytes different from the last admitted report now bypass this gate,
including final zero. Repeated publications and LED-only changes retain existing
maintenance pacing. Rejected/throwing audio-mailbox admission does not advance
the admitted snapshot. No SBC packet, audio clock, gain, pool or driver change is
included. This removes an unnecessary software wait; the physical audio owner
and existing latest-state mailboxes can still coalesce very short pulses.

### Physical DS3: retry a failed final state

The real compositor/input-loop output branch used to clear dirty state and
acknowledge its previous-state snapshot even when the physical write returned
false. The regression reproduces a failed final-zero write never being retried
without another game publication.

Only an accepted write now acknowledges output. Failure retains pending state
for the next existing input/output pass. A newer stop can supersede a failed
active state; successful unchanged zero is not replayed. No worker, sleep or
driver cadence was added. This is not a hardware timeout or throughput test.

### Switch 2 Pro and Joy-Con 2: pending duplicate admission

No Nintendo equivalent of the old DualSense fresh-command 5 ms throttle was
found. The Switch 2 sink's 10/12/15 ms limits pace unchanged sustain refreshes,
not newly changed commands. Input-independent output completion can still wait
on the actual GATT/USB operation; completion timeout budgets are not intentional
per-effect delays.

The targeted optimization is restricted to eligible control-only virtual
DualSense reports destined for an authenticated Switch 2 runtime/session, with
zero configured rumble delay. It reuses the conservative full-envelope
repeat-eligibility rule, without changing it or marking Nintendo feedback as a
raw Sony command. Exact target/session, device/transport or pair generations,
profile, stream and publication boundaries fence pending-tail comparisons.
PCM, opaque trigger fields, timed Xbox feedback and native Nintendo time slices
are not folded. Nintendo rumble encoding, amplitude tuning and cadence remain
unchanged. Only the latest identical receipt renews freshness; original admission
time and ordering remain intact for queue-age diagnostics. A near-expiry test
guards against accidentally dropping a fresh repeat with its older pending copy.
The existing **four-entry / 20 ms** non-native policy still drops old entries on
distinct overload or expiry; this change does not promise lossless distinct
Nintendo output at arbitrary rates or introduce an unbounded command queue.

Source review includes Pro USB/Bluetooth and standalone/joined Joy-Con 2 Bluetooth
owners. The new reader integration fixtures use mocked Bluetooth lifetimes; they
do not validate Joy-Con 2 USB or physical transport/actuator speed.

### Other reviewed paths

- DualSense USB and Edge USB: the actual retained native owner preserves a
  64-command burst plus retried final stop and complete 11-byte trigger blocks
  through an offline final-write hook. USB has no equivalent fixed Bluetooth
  helper gate. Edge Bluetooth uses the same repaired DualSense implementation.
- Legacy Joy-Con and Switch Pro: preallocated latest-state output, not a growing
  native-command FIFO. Floods resolve to newest state/stop. Original Joy-Con's
  explicit rich-feedback delay remains explicit; terminal stop clears its delayed
  applies. Unclaimed short pulses can still coalesce. No firmware/rate change was
  justified for either family.
- Xbox 360 uses cumulative state; Xbox One/Series delivery has one active item
  and one ACK-successor, not a large DS4Windows historical queue. Tests cover
  active-payload ownership, retained/retried terminal stop and all impulse
  projection settings. Transport/ACK backpressure remains possible.

## Validation record

Evidence directory: `isolated_results/cross-controller-feedback-20260912/`.
Characterization tests explicitly named for pulse coalescing are observations of
existing limitations, not proof that all short pulses were physically rendered.

- Existing release feedback baseline: **1,153 passed**, zero failures, three
  gated live-audio skips (`existing-feedback-baseline.trx`).
- New cross-controller audit before fixes: **48 passed**, zero failures/skips
  (`new-audit-tests.trx`).
- Old VIIPER overflow behavior reproduced: `viiper-ds4-overflow-before.log`.
  The passing characterization meant the bug was present, not stop safety.
- DS3 old failed-write acknowledgment: **two expected failures**, eight passes
  (`ds3-write-failure-before.trx`). After the fix all ten DS3 rows passed in
  `ds4-cadence-red-ds3-green.trx`.
- DS4 old fresh-effect gate: **five expected failures**, one unchanged-state
  pass in that same combined red/green run. Two earlier attempts exposed missing
  test imports before compilation; the actual red result is the compiled run.
- Rebuilt Sony/shared/cadence/profile-stop subset: **100 passed**, zero failures
  (`sony-shared-audit-green.trx`).
- Broader feedback subset: **1,219 passed**, zero failures, three existing gated
  live-audio skips (`feedback-broad-green.trx`).
- VIIPER repaired DS4 and USB-server packages pass (`viiper-ds4-usb-green.log`).
- Nintendo pending-repeat tests: **39 passed**, zero failures/skips
  (`nintendo-repeat-green.trx`), including near-expiry freshness, all ownership
  boundaries, four mocked runtime models, strict-zero allocation and a deliberate
  allocating positive control.
- Final rebuilt DS4Windows suite: **5,644 passed**, zero failures, 11 existing
  gated skips (`full-final.trx`). This adds 105 cases to the release baseline.
  Allocation assertions remain enabled and exact; none were weakened.
- Initial race-enabled VIIPER DS4, USB-server and API-handler package runs pass
  (`viiper-race.log`). Final lifecycle amendments are tested again below.

The first broad `-race -count=3` run (`viiper-race-final.log`) was **not green**:
the new public USB/IP fixture omitted `ConnectionTimeout`, so the server applied
an immediate deadline and closed import before DS4 admission. The test now uses
the same positive timeout required by existing server fixtures and validates the
import header before reading its descriptor. No production timeout/admission
assertion was relaxed. The repaired two-path wire test passes three in-process
race-enabled repetitions (`viiper-wire-final.log`).

That same broad repeat run also exposed existing handler-test cleanup limits:
fixed bus IDs remain allocated when `StartAPIServer` closes only the API server
and the fixture does not close its virtual bus. They fail on repeated execution
in the same Go test process. These unrelated fixtures are not silently counted
as passes or used to change production bus cleanup. Final broad race runs use
three fresh test processes with `-count=1` each; the new wire/API fixtures have
their own explicit cleanup and also tolerate in-process repetition.

Final VIIPER verification after the fixture correction:

- Full `go test ./... -count=1`: **35 tested packages passed**, zero failed;
  another 22 packages have no test files (`viiper-full-confirmed.log`).
- DS4 device, USB server, API server and API handlers: **three fresh-process
  race-enabled runs passed**, with no race reports
  (`viiper-race-process-1.log` through `-3.log`).
- The real overlapping API reconnect preserves cached feedback, subsequent
  changes and final zero with correct frame CRC/sequence, and keeps the same
  virtual device beyond the predecessor's cleanup deadline.
- New USB/IP tests preserve the accepted FIFO, reject overload with `ENOSPC`
  and actual length zero, accept the reserved final zero/LED state and retry
  another distinct state after drain on the same connection.
- Both repositories pass `git diff --check`; no updater/installer/version or
  released asset was changed.

Independent review found and resolved the Nintendo near-expiry edge and DS4
serial-reservation cleanup issue. DS3 retry and DS4 fresh-motor cadence were
reviewed separately; no remaining blocking source finding was reported for these
changes. No hardware/perceptual conclusion follows from software admission and
mocked final-write tests alone. These source changes are not yet distributed.

## Tested production-source hashes (SHA-256)

Paths below are relative to their respective repositories; baseline commit IDs
are above. Test-only files and this ledger are additional working-tree changes.

| Repository / file | SHA-256 |
| --- | --- |
| DS4Windows / `DS4Windows/DS4Control/Viiper/ViiperFeedbackDispatchBuffer.cs` | `E6769E078AE87CD2626C37FB4335020955C13F33DFDFA8F6FED768D91F90A432` |
| DS4Windows / `DS4Windows/DS4Control/Viiper/ViiperOutDevice.cs` | `50C1485AEFA8E9743A1D751B550C7754E194C53E395ECFC707B5666F27528F2D` |
| DS4Windows / `DS4Windows/DS4Library/DS4Device.cs` | `8AAC4EC6C273298001CFB83949A4839CF89469B953CE1748DA34B024074ECC61` |
| DS4Windows / `DS4Windows/DS4Library/InputDevices/DS3Device.cs` | `104EC74B1F127053336049A4A102E7DA051521F9A4A769551C7A1F84F05C7E3B` |
| VIIPER / `device/dualshock4/device.go` | `1382F705B8EC6990AD62437A018C530411AC19DACFF2ED37AB89240EFD2642E5` |
| VIIPER / `device/dualshock4/handler.go` | `D721628C9839F92A8C2F5E7C9C6374895E899509F0F99F39E8D85D2232B0C293` |
