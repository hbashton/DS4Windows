# Switch 2 Pro USB HD-rumble direct-writer foundation

Status: dormant, offline-only transport foundation. No runtime service creates
this writer, no native implementation of its transport lease exists, and this
work performs no controller I/O.

## Scope

`Switch2ProUsbHdRumblePhysicalWriter` is the narrow physical-output boundary
beneath the already separate `ISwitch2HdRumblePhysicalWriter` seam. It does
only four things:

1. authenticates one Pro Controller 2 model plus exact device and transport
   generations;
2. asks the existing frozen codec to build one 64-byte USB report;
3. owns and advances the report's modulo-16 counter; and
4. normalizes synchronous transport completion into the existing typed
   physical-write result.

It does not open a device, borrow or mutate the read-only USB input lease,
schedule periodic work, choose haptic frequencies, claim actuator fidelity, or
participate in controller registration.

## Audited source pins

The local reference trees were clean at these exact commits during this pass:

| Reference | Pin | License/use in this tranche |
| --- | --- | --- |
| SDL current | `c71abd08605b8bb7078372307a93274725c99fe0` | zlib; wire behavior corroboration |
| SDL hifihedgehog fork | `d98c5804a9d20b0d96e993741797878c86b8f1e1` | zlib; independent fork comparison |
| Switch2Connect | `4487322a306f04efa27682e3f3a508635a84fd98` | GPL-3.0-or-later; behavior/provenance only |
| HIDMaestro current | `9df50410230c11b410f43909ede0e5fc8b23d15b` | MIT; descriptor/profile negative control |
| PadForge | `0794fd01bd19f4c096b982ffc824b88bce5ed743` | CC BY-NC-SA; behavioral facts only, no code copied |

The source facts used are:

- SDL current
  `_references/SDL-current/src/joystick/hidapi/SDL_hidapi_switch2.c`
  lines 1031-1110 constructs a 64-byte output value, places the Pro report ID
  `0x02` at byte 0, uses `0x50 | (sequence & 0x0f)` for the group header, and
  increments the sequence before handing the value to SDL's rumble queue.
- The hifihedgehog SDL fork has the same relevant construction at
  `_references/SDL-hifihedgehog/src/joystick/hidapi/SDL_hidapi_switch2.c`
  lines 1106-1182.
- Switch2Connect
  `_references/Switch2Connect/src/controller.py` lines 3759 and 3788-3792
  builds independent left then right 16-byte Pro groups under the same packet
  ID. Its USB body adapter in
  `_references/Switch2Connect/src/usb_hid_controller.py` lines 965-1000 keeps
  that left-then-right order. Its direct HID paths at lines 2350-2403 expose a
  synchronous native write count.
- HIDMaestro's one-line
  `_references/HIDMaestro-current/profiles/nintendo/switch2-pro.json`
  declares output report `0x02`, but explicitly says its virtual profile cannot
  reach the SDL Switch 2 haptic path. It supplies no physical writer lifecycle.
- PadForge
  `_references/PadForge/PadForge.App/Common/Input/HapticToneService.cs`
  lines 42-54 and 94-95 deliberately excludes Switch 2 because its reviewed
  references did not establish a tone path. It is a useful negative control,
  not a transport specification.

## Exact report contract

The writer delegates packing to `Switch2UsbHdRumbleCodec` and accepts only its
strict Pro form:

| Bytes | Meaning |
| --- | --- |
| `0` | Pro Controller 2 USB output report ID `0x02` |
| `1..16` | left 16-byte group |
| `17..32` | right 16-byte group |
| `33..63` | zero-reserved tail |

Each group is `0x50 | counter` followed by three explicit five-byte subframes.
Both sides carry the same counter. The writer never mirrors one side onto the
other; it preserves the already translated side-separated groups.

One new logical physical submission reserves the current counter and advances
the owner's next counter modulo 16. This happens before interpreting write
completion, matching SDL's counter ownership order and preventing a different
report from reusing a key that an uncertain operation may already have put on
the wire.

## Synchronous completion and retry

`ISwitch2ProUsbHdRumbleTransportLease` is abstract and synchronous. Its
authentication operation must be pure and its write operation must consume,
not retain, the supplied span before returning. Every result carries the
controller model and both generations.

A result is accepted as success only when all of these are true:

- outcome is `Completed`;
- failure is `None`;
- exactly 64 bytes were transferred; and
- the result authenticates Pro Controller 2 and the writer's exact device and
  transport generations.

A malformed result is outcome-uncertain because an external write call already
ran. A well-formed result with foreign model or generations is likewise
outcome-uncertain and classified as stale lifetime. A thrown write is
outcome-uncertain. Authentication rejection or exception before the write is a
proven rejection because no write operation ran.

After either a proven transport rejection or an uncertain transport outcome,
an exact retry of the same logical submission reuses all 64 cached bytes,
including the counter. There is no automatic retry loop. A different logical
submission encodes new bytes under the next counter; it cannot reuse the prior
counter because the prior uncertain write may have applied. This is also the
behavior needed when the canonical sink permits a newer same-owner frame or a
terminal neutral to resolve uncertainty.

The writer uses one interlocked in-flight fence and no monitor lock. Lease
authentication and write calls therefore occur under no writer/table lock.
The sole report buffer is allocated in the constructor; encoding uses stack
storage and the characterized direct-write path allocates zero managed bytes
after warmup.

## Why this tranche has no cadence worker

The reviewed sources do not establish one production-safe persistent cadence:

- SDL declares 12 ms at
  `_references/SDL-current/src/joystick/hidapi/SDL_hidapi_switch2.c:36`,
  but `SDL_HIDAPI_SendRumbleAndUnlock` only queues the value. The queue returns
  the requested size at
  `_references/SDL-current/src/joystick/hidapi/SDL_hidapi_rumble.c:240`, while
  the worker's actual `SDL_hid_write` result at line 84 is not propagated.
- Switch2Connect's USB writer loops use a 15 ms minimum at
  `_references/Switch2Connect/src/usb_hid_controller.py:1932`, `:1987`, and
  `:2043`, then add congestion behavior, latest-value overwrite, sustain, and
  limited silent frames. Those policies differ from SDL's queue and 12 ms
  schedule.
- Neither shape proves, for this DS4Windows lifetime, the required watchdog
  duration, disconnect ordering, exact neutral count, last-write quiescence,
  or safe interaction with the existing input lease.

Adding a timer now would therefore turn unresolved behavior into a production
claim and could leave a voice-coil value sustained after ownership loss. The
safe result is the direct synchronous writer only.

## Remaining gates before runtime wiring

Runtime use remains blocked until authorized hardware evidence establishes:

1. the exact writable Windows interface and a lease that can coexist with the
   read owner without mutating its lifecycle;
2. complete-versus-uncertain native write semantics and teardown quiescence;
3. required active refresh interval and jitter tolerance;
4. device watchdog duration plus the exact terminal-neutral/stop sequence;
5. physical left/right sidedness and conservative nonzero basis behavior; and
6. bounded disconnect and retry behavior which cannot resurrect a retired
   handle.

Offline validation for this tranche is performed with:

```powershell
dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~Switch2ProUsbHdRumblePhysicalWriterTests
dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Switch2HdRumble|FullyQualifiedName~ControllerFeedback"
dotnet build DS4Windows/DS4WinWPF.csproj -c Release -p:Platform=x64 --no-restore
```
