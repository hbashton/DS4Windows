# VIIPER backend architecture

VIIPER is DS4Windows' only virtual-controller backend. It exposes Xbox 360,
Xbox One / Series, DualShock 4, DualSense, DualSense Edge, and Switch 2 Pro
devices through usbip-win2 as complete USB devices, including the applicable
Sony audio interfaces.

## User setup

DS4Windows checks VIIPER and usbip-win2 at startup. When either component is
missing, the app offers its bundled self-elevating setup. Setup installs both
components and registers exact, enabled, highest-privilege `RunVIIPER` and
`RunDS4Windows` logon tasks before any driver step can require a restart. Once
the USBIP ABI is ready, setup starts the server and verifies its local API.
Settings also provides Install / Repair and Refresh actions.

The elevated tasks are rooted explicitly at `\RunVIIPER` and
`\RunDS4Windows`. The VIIPER executable and managed DS4Windows recovery copy
live in the dedicated, protected `%ProgramFiles%\DS4Windows` tree; the
installer never rewrites access controls on an arbitrary ZIP extraction
directory.

Task registration uses a schema-validated Task Scheduler XML definition. Its
principal contains the already validated target-user SID, `InteractiveToken`
logon type, and `HighestAvailable` run level; the logon trigger is deliberately
user-neutral. This avoids the ScheduledTasks CIM provider normalizing a local
SID to an ambiguous, unqualified account name on some Windows installations.
Verification enumerates the task library and then matches the exact root path
and allowlisted name, so an as-yet-unregistered task is ordinary absence rather
than a misleading failed CIM query. Actual Task Scheduler/CIM failures still
fail closed.

Each new task carries a durable DS4Windows ownership marker. Setup preflights
both fixed names before a pair registration or removal, upgrades only marked or
fully verified legacy DS4Windows tasks, and refuses to overwrite, disable, or
delete a foreign same-name task. A second-name collision therefore cannot
partially create or remove the first task. Failure containment may disable a
marker-owned malformed task, but it preserves unmarked foreign tasks.

Install / Repair copies a managed recovery build into Program Files, but its
`RunDS4Windows` task points to the exact executable that launched setup. If the
user later opens a different portable copy, DS4Windows asks once for
administrator confirmation and retargets that task to the newly chosen
portable executable. Installing VIIPER remains independent of where that
portable package was extracted.

## Profile migration

The retired serialized values `X360` and `DS4` remain readable solely for
backward compatibility. They normalize immediately to `ViiperX360` and
`ViiperDS4`; new saves never write the retired values.

## Runtime containment

DS4Windows records locally created VIIPER Sony interfaces before normal HID
enumeration and rejects them as physical inputs. Moonlight/Sunshine virtual
controllers use a separate opt-in admission policy, so accepting streamed
controllers cannot make DS4Windows recursively ingest its own output.

## Feedback and audio

VIIPER feedback is read by `ViiperOutDevice` and routed to the currently bound
physical controller. Xbox/standard rumble, Sony lightbar output, adaptive
triggers, advanced haptics, speaker playback, and microphone capture are
translated according to the physical controller's capabilities. Switch 2
targets use one generation-authenticated canonical feedback lifetime across
every virtual output type. Native Switch 2 oscillator groups and validated
DualSense PCM synthesis retain richer source information; legacy motor sources
use the audited compatibility basis described below. DS4Windows profile/macro
effects and test preview enter the same owner instead of bypassing or
duplicating the physical writer.

## Xbox 360 ordered presentation

Xbox 360 output keeps continuously varying sticks and nonzero trigger values
as one replaceable latest snapshot. Button changes and trigger zero crossings
use a bounded ordered journal, so a short press/release is not collapsed by
the DS4Windows socket writer or VIIPER endpoint scheduler. Both sides use an
immutable claim followed by final admission and commit/defer; reconnect retries
the same bytes, and lifecycle retirement fences the previous producer and
presentation generations.

There is deliberately no universal stale-edge deadline. With
`DS4W_VIIPER_X360_MAX_ORDERED_AGE_MS` unset, both processes use compatibility
mode with no age deadline. Capacity overflow still purges the complete
history, presents one mandatory neutral, rotates producer ownership, and then
explicitly resynchronizes from the newest complete producer snapshot. This is
required even without an age policy: simply rejecting a press while retaining
the old neutral baseline would allow its release to be misclassified as
replaceable continuous state. Setting the variable to a whole number from 1
through 60000 passes that duration in the Xbox 360 device-creation payload.
DS4Windows and VIIPER convert it into their own monotonic clock domains and
additionally apply the same neutral-and-resynchronize contract when an ordered
entry reaches that declared age. Choose a value only from a predeclared
workload and latency measurement; the setting is an engineering experiment,
not a shipping recommendation.

## Switch 2 Pro ordered presentation

Switch 2 Pro uses the same bounded ordered-egress foundation as Xbox 360,
without introducing a second mapping stack. Button-mask changes use the
ordered journal; sticks and motion use the replaceable latest snapshot. Claims
are immutable through retry and are admitted immediately before socket write.
VIIPER, not DS4Windows, assigns the HID report counter and motion timestamp at
actual endpoint presentation.

The producer lease contains the exact writer generation, scheduler
presentation generation, producer epoch, and DS4Windows admission generation.
Connect/disconnect and `ResetState` (including profile/physical-binding reset)
advance that admission boundary, so a callback captured before the boundary
cannot publish or stage state into its successor. Overflow, a configured
ordered-age fault, and an explicit reset all purge uncertain history, publish
one mandatory neutral, and let only the state writer resynchronize from the
newest post-boundary staged snapshot. The wire-neutral stick center is the
protocol value `0x0800`; byte-axis projection preserves exact 0, center, and
`0x0fff` endpoints.

Xbox and Switch also share one final writer-admission gate. Claim selection
captures the exact writer/presentation/admission lease; the gate revalidates it
atomically with scheduler admission immediately before the socket call.
Disconnect and reset invalidate that same gate before changing lifecycle
generations. Socket I/O remains outside the lock, so teardown can cancel and
join a write that won admission while a writer paused before admission cannot
write bytes from the retired lifecycle.

`DS4W_VIIPER_NS2PRO_MAX_ORDERED_AGE_MS` has the same explicit 1-through-60000
millisecond experimental contract as the Xbox variable. Unset means no age
deadline, but capacity overflow and lifecycle reset remain fail-closed. The
existing 30-second, verbose/latency-gated writer-health line reports the same
ordered depth/high-water, replacement, retry, overflow/age/lifecycle fault,
stale-producer, mandatory-neutral, and resynchronization fields for both
virtual types; there are no per-report scheduler logs.

## Switch 2 virtual feedback safety boundary

The 34-byte VIIPER ns2pro output state is decoded exactly as two 16-byte
rumble groups, a flags byte, and a player-LED mask. Unknown flags, payload in a
field whose flag is clear, non-`0x5?` group headers, or mismatched left/right
counter nibbles are rejected. Valid groups remain four raw 10-bit fields per
subframe; headers and frequency/control codes are never treated as motor
amplitudes.

Physical Switch 2 feedback uses one transport-specific USB or Bluetooth
HD-rumble writer under the canonical runtime. Native Switch 2 output preserves
all three subframes and all four 10-bit oscillator fields in each left/right
group; only the physical transport counter/envelope is regenerated. Validated
DualSense PCM splits each 32-sample stereo window into three chronological
slices and preserves independent left/right RMS and transient energy in the
matching three HD-rumble subframes. Compatibility motor bytes are composed
with PCM so a valid silent audio carrier cannot erase ordinary rumble.
Supported DualSense adaptive-trigger programs are decoded into a bounded
three-region, side-local HD-rumble approximation while the corresponding
physical Switch 2 trigger is held. Existing PCM/body carrier controls win on
overlap and amplitudes add with saturation. Unknown and malformed effects fail
closed; this is tactile approximation, not mechanical resistance. Xbox 360,
DS4, non-PCM Sony, profile/macro, and preview feedback use the audited SDL
low/high compatibility basis rather than raw-byte guesses, repeating each
sustained state across all three subframes.

Xbox body-low/body-high are mirrored to both physical groups. The persisted
per-profile **Preserve Xbox impulse-trigger detail in HD rumble** setting is on
by default so every decoded Xbox rumble lane reaches the physical Switch 2
voice coils. Each trigger controls only the high-frequency field in its
corresponding group; the low-frequency field remains the body lane. Dynamic
mode maps trigger intensity monotonically onto a 300–481 Hz carrier. The
profile can instead choose a fixed carrier level from 1–10 and independently
scale impulse strength from 1–10. Body and impulse high amplitudes use
saturating addition capped at the 10-bit field maximum, preserving both
contributions without wraparound.

The current configuration is applied on the dedicated feedback-delivery path,
and a transition re-presents the newest canonical frame once while an uncertain
delivery preserves its original policy, tuning, and byte-exact retry. Turning
the setting off retains the fail-safe SDL body-only compatibility path. This
does not claim that Switch 2 controllers contain trigger actuators or that the
physical effect is identical to an Xbox impulse trigger.

Every virtual-output session receives a monotonically increasing ownership
epoch from the physical Switch 2 lifetime. Profile/output replacement can
therefore never be rejected as older merely because a random identifier sorted
below its predecessor. Terminal Stop is delivered as an exact neutral report
before ownership retires. Reusable profile and preview lanes withdraw through
that same ordered Stop rule, so clearing preview can safely restore the lower
owner. An LED-only virtual Switch 2 frame does not start or
stop rumble, and malformed frames remain rejected rather than being converted
into a synthetic stop.

Native Switch 2 player-indicator output uses that same authenticated virtual-
output session. Bluetooth preserves every valid four-segment mask (`0x0`
through `0xf`) exactly on the acknowledged command lane and coalesces an
occupied lane to the newest complete mask. USB emits only the six exact
capture-backed operations: all off, players one through four, and all on.
Other USB masks are rejected instead of approximated. Both transports fence
retired device/transport generations, and LED-only state remains independent
of rumble ownership.

The byte contract, ownership fencing, and offline sidedness tests are complete.
Physical onset latency, subjective fidelity, universally safe sustain cadence,
and thermal behavior remain authorized-hardware validation gates.
