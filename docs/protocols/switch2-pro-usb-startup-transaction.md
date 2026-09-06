# Switch 2 Pro USB volatile startup transaction

Status: **production-wired through the exact owned-composite coordinator and
offline-tested; the command tuples have prior authorized capture evidence, but
this revision has not been exercised against controller hardware**.

`Switch2ProUsbStartupTransaction` makes the production command-interface lease
prove every transition in this closed volatile order:

1. enable USB HID reports (`03:03`);
2. set Player 1 LED through the capture-backed USB `09:01` tuple;
3. set `ButtonsSticksImuAndRumble` / `0x27` (`0C:02`);
4. enable that exact `0x27` mask (`0C:04`); and
5. select common input report `0x05` (`03:0A`).

The transaction emits only the existing `Switch2UsbCommandCodec` forms and
validates each generated request through that codec before crossing the
abstract lease boundary. It neither exposes an arbitrary command form nor
admits a different feature mask.

## Evidence audit and provenance

The implementation is original GPL-3.0-or-later DS4Windows code. No PadForge,
SDL, or Switch2Connect control-flow source was copied. The references below
were used only to reconcile independently expressible request bytes, ordering,
and negative evidence:

- `Switch2Connect` pin
  `4487322a306f04efa27682e3f3a508635a84fd98` (GPL-3.0-or-later):
  `src/usb_hid_controller.py:130-151` labels `0x27` as the Pro Controller 2
  fast mask and defines the feature/common-report requests;
  `src/usb_hid_controller.py:165-188` places set-feature before enable-feature
  and report selection. `README.md:89` says only “up to 500Hz.” Its
  `src/usb_hid_controller.py:3104-3155` delayed reinitialization is not copied:
  it performs elapsed-time retries and its own comments distinguish report-id
  observation from proof that the feature pair took effect.
- upstream `SDL-current` pin
  `c71abd08605b8bb7078372307a93274725c99fe0` and hifihedgehog SDL pin
  `d98c5804a9d20b0d96e993741797878c86b8f1e1` (zlib):
  `src/joystick/hidapi/SDL_hidapi_switch2.c:392-429` in upstream and
  `:467-504` in the hifi pin contain the same `0C:02/0x27`,
  `0C:04/0x27`, and `03:0A/0x05` forms. Upstream `:498-504` sends then reads
  each startup response but does not validate a feature response tuple. That
  behavior is corroboration for the requests, not sufficient ACK semantics.
- PadForge pin `0794fd01bd19f4c096b982ffc824b88bce5ed743`
  (project license CC BY-NC-SA 4.0; behavioral evidence only): `BUILD.md:133-134`
  and `:190-191` state that the shipped SDL3 binary is a custom fork with
  Switch 2 Pro WinUSB support. The pinned binary at
  `PadForge.App/Resources/SDL3/x64/SDL3.dll` is 2,904,064 bytes with SHA-256
  `AE1FFD8C537ADDC190F7C874430488AE501665AFDCAC0297682F2B2F33243487`.
  PadForge's configurable approximately-1000-Hz application poll loop
  (`BUILD.md:208`) and comparison-table claim (`README.md:442`) do not prove a
  physical Switch 2 USB report cadence or feature ACK tuple.
- existing DS4Windows evidence at repository base
  `061fab1304e77c995ce9451b7ef20e51cc870070`, plus the current uncommitted
  audited seam and an authorized 2026-08-31 run of the closed startup-evidence
  procedure. The exact bcdDevice `0x0201` feature responses were respectively
  `0C01000200F8000000000000` and `0C01000400F8000000000000`.
  `Switch2UsbCommandCodec.TryValidateFeatureResponse` now admits only those
  step-specific 12-byte tuples; exhaustive single-byte mutation tests reject
  every changed byte and a response for one feature step cannot validate the
  other. The same run recorded 256/256 Common05 reports at approximately
  250 host completions/second with every forward counter delta equal to `+4`,
  plus exact Player-1 and `AllOff` LED acknowledgements. Those cadence values
  are neither a 500-Hz claim nor calibrated input latency.

The standalone verifier contains an explicit
`--capture-startup-evidence` mode. It reuses the verifier's same exclusive
MI_01 channel to capture the four mode commands, then aggregate input
cadence/counters and separately capture Player-1/`AllOff` LED evidence. The
production transaction now serializes Player-1 between enable-USB and the
feature pair on that same sole command owner. The authorized 2026-08-31
run was performed only from a Desktop portable lab and did not install or
replace DS4Windows or VIIPER. Its feature responses are admitted by the exact
production codec validator and serialized as `ExistingValidatorAccepted`;
arbitrary response bytes and every tuple mutation remain rejected. The
feature-session configuration still has no established inverse and may remain
until another owner reconfigures the connection or the controller disconnects;
only the LED receives explicit `AllOff` cleanup. The artifact remains local and
is never copied into source constants or committed automatically.

## Exact completion boundary

`ISwitch2ProUsbStartupCommandLease` is an exclusive abstract owner of one
already-admitted `Switch2PhysicalInputLifetime`. Creation requires its complete
registration, device generation, and transport generation to equal the
expected lifetime. Every command claim additionally contains an opaque private
transaction fence, the exact lease reference, step, and monotonically assigned
sequence. A copied, default, stale, wrong-step, cross-owner, or cross-generation
completion cannot advance the state machine.

For `03:03` and `03:0A`, the lease may return
`InitializationResponseValidatedByCodec` only after validating the exact
matching response with the existing codec. For `0C:02` and `0C:04`, it may
return `FeatureResponseValidatedByCodec` only after validating the exact
step-specific 12-byte response. For Player 1 it requires
`PlayerLedResponseValidatedByCodec` after the exact matching eight-byte ACK.
The transaction does not accept raw response bytes and does not infer a tuple
from the Bluetooth capture or SDL's unchecked read.

The Windows command lease implements exact transport mechanics for all five
steps. Each operation is one bounded flush/write/read/validate sequence.
A malformed or ambiguous response becomes `PossiblyConsumed` and fences replay
until the exact lease is retired. Production composition enforces one retained
MI_00 input/output handle and one retained MI_01 command lifetime; hardware
validation remains a separate release gate.

## Failure, retry, and retirement rules

- `ExactResponseCompleted` is the only successful command outcome.
- `ProvenNotConsumed` must prove that no request byte was accepted or queued
  and that no later completion is possible. It retains the exact same claim and
  request for a caller-driven retry. There is no automatic or delayed retry.
- a default or malformed result, wrong credential/step/proof, dependency
  exception, timeout, or possibly-consumed result stops command progress and
  immediately attempts bounded retirement outside the transaction lock;
- an exact release proof moves to `Retired`;
- `ProvenNotReleased` retains the exact retirement claim for an exact retry;
  and
- a default, wrong, thrown, timed-out, or possibly-released retirement result
  quarantines the entire lifetime. An uncertain release is never attempted a
  second time.

Only one command or retirement operation may be in flight. All lease calls,
including the lifetime read at creation, execute outside transaction locks.
The abstract timeout is a cumulative managed native-quiescence wait budget;
the Windows lease starts accounting before native phases and deducts elapsed
time before every wait. Synchronous begin/cancel/free/close APIs do not expose a
hard wall-clock deadline. An exact release result still proves native/managed
quiescence regardless of how long those synchronous calls took.

## Rate statement

Completion exposes only `RequiresMeasurement`. It does not expose “high rate,”
500 Hz, or any other frequency as a fact. A later authorized integration must
measure host completion cadence and device-counter progression on the same
authenticated lifetime after all five exact steps succeed; calibrated
input-to-presentation latency remains a separate measurement.

## Offline verification

`Switch2ProUsbStartupTransactionTests` pins:

- the exact five requests and order;
- typed initialization, player-LED, and feature proof requirements;
- same-claim, same-byte retry only after `ProvenNotConsumed`;
- default, malformed, wrong-step, wrong-proof, stale, and cross-owner rejection;
- throw, timeout, and possibly-consumed retirement;
- retained exact retirement retry and uncertain-release quarantine without a
  double release;
- single-flight and inline reentrant-call fencing with no lease call under the
  transaction gate;
- invalid lifetime/timeout rejection and idempotent exact release; and
- zero managed allocations across 20,000 completed-state observations and
  idempotent calls after warmup.

No test or build in this tranche accesses controller hardware.
