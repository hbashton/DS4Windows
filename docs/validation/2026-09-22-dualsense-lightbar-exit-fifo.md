# DualSense Bluetooth lightbar restore ordering, 2026-09-22

## Status and scope

This is a follow-up to the report that the orange profile lightbar did not
return after Hades II exited. The earlier session did not record the ownership
release decision, and a subsequent reconnect retired that state. The code
regression below is reproduced; it is not proof that this exact ordering caused
that live occurrence.

RC4.6.4 (`b394ef9eac33cb132bb73a70178d65eba3eb163b`) was published and verified
separately. This follow-up is not in its assets. No running application,
controller, installed driver, or published release was replaced for this work.

## Reproduced ordering defect

The physical worker drains up to eight raw native commands before servicing a
local output generation. A Bluetooth visual-ownership release for the newest
admitted revision could be consumed while that revision was still in the raw
FIFO. An older queued visual claim then took ownership back; the final neutral
command left that ownership unchanged, so the profile color was not restored.
Admission just after an empty-FIFO observation exposes the same ordering.

USB already retains the request until its matching native template commits.
After the existing current-revision check, the Bluetooth follow-up retains the
release while any previously admitted raw command remains in the bounded FIFO,
and schedules another existing worker pass. While that revision remains current,
all pending raw commands are older or equal. A newer native revision still
cancels the older release. This does not add a timer, flush commands, reset
feedback, or alter the foreground-owner eligibility checks.

A mixed compatibility-path regression also reproduced the defect: a directly
admitted neutral revision can be newer than an older raw visual command without
itself being in that FIFO. Checking only for an exact matching queued revision
did not fix that sequence. The nonempty-FIFO check covers it without changing
either command-admission path.

## Deterministic evidence

The tests use the existing queue-only pacer fixture, actual raw-command admission,
physical command processing, and Bluetooth cache merge. They perform no HID I/O.

- Baseline: both late-admission and eight-command-burst cases failed because an
  older visual command reclaimed ownership after the release was consumed.
- Fixed: both orderings restore the profile's orange and player LEDs after the
  final neutral command. Additional cases keep a newer visual owner intact and
  preserve an authored nonvisual native effect while restoring the LEDs.
- Checks compare every non-LED byte and the non-LED bits of shared validity
  bytes before and after restoration. They also verify native command count and
  the authored trigger values in the queued exact native command.
- An initial candidate run exposed an incorrect *test* expectation that trigger
  bytes remain accumulated after a neutral command. The native merge deliberately
  accumulates only visual state; all other fields remain as authored in the
  current native packet. The assertion was corrected to check the actual queued
  command and before/after preservation, without changing that production rule.
  The failed attempt is retained alongside the baseline and passing receipts.

Local receipts are under `isolated_results/lightbar-exit-fifo-01/`:
`bt-led-fifo-red.trx` (2 failed baseline cases), `bt-led-fifo-green.trx` (initial
test-expectation failure), and `bt-led-fifo-green02.trx` (4 passing cases).
`bt-led-fifo-mixed-red.trx` retains the separate mixed-path failure against the
initial exact-revision-only guard.

Final validation of the nonempty-FIFO guard and all seven new cases:

- Focused LED lease, Bluetooth transport, backpressure, and physical-output
  tests: **158 passed, 0 failed, 0 skipped** (`bt-led-fifo-focused-final02.trx`,
  SHA256 `AD3349E8C1D4F828AA0C984737C764FCF15E5917767467D33DC7150FD97F49EA`).
- Full suite: **6,808 passed, 0 failed, 12 existing opt-in tests skipped**
  (`bt-led-fifo-full-final.trx`,
  SHA256 `EB5CA2E7110AAC8F179FBE2D8E5BED0917EBD69213F27DB241A820A420E1FB06`).
- Independent lock/revision review found no new lock-order inversion, failure
  spin, or fixed wait. The FIFO holds at most 64 commands, and an existing
  failed-head path already schedules bounded retries before local preparation.

## Remaining limits

The verified/unverified visual-claim, latest visual report, stream, target, and
revision fences are unchanged. If one of those prevents admission of a release,
this FIFO change cannot make it admissible. The actual Hades II exit still needs
an observed release decision and physical confirmation.

The mixed combined/raw regression concerns only restoration of visual ownership.
This change does not repair general native-command overtaking between those
legacy ingress paths, and must not be described as a general feedback-ordering
fix. The current exact-native command path uses the ordered raw FIFO.
