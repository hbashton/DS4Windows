# Persistent, in-process Switch 2 Pro Bluetooth probe

This client talks only to a local diagnostic pipe in an explicitly enabled
portable DS4Windows. **Leave DS4Windows open.** It owns the Nintendo connection
procedure, input notifications, LED/rumble traffic, and controller lifetime.
This client never opens a Bluetooth device or takes controller ownership.

## Enable the probing build

Launch the hash-pinned portable lab normally, with the child environment
`DS4WINDOWS_SWITCH2_AUDIO_PROBE=1`. Both this switch and a successfully validated
`--portable-lab` context are required. No pipe is created in production mode,
for USB devices, or for Joy-Cons. Windows API reads use the **existing** service
owned by each connected Bluetooth Pro's input lease.

Each connection produces a small descriptor under
`lab-data/Switch2AudioProbe/ds4w-s2audio-*.json`. Its pipe name and transport
generation identify that specific controller lifetime; it contains no address,
association key, input buttons, or recorded audio. A completed descriptor says
`stopped`; do not reuse it. The pipe admits the current Windows user only and
uses a bounded, fixed command vocabulary with one operation at a time.

```powershell
Switch2BluetoothLabClient.exe <session-descriptor.json> status
Switch2BluetoothLabClient.exe <session-descriptor.json> inventory
Switch2BluetoothLabClient.exe <session-descriptor.json> headset-header
```

- `status`: reports connection/lifetime and advancing input counters without
  radio I/O. Repeated queries must preserve the same transport generation.
- `inventory`: explicitly queries characteristic UUIDs/properties/handles and
  negotiated ATT PDU size on the already-owned service. There is no periodic
  discovery polling and no second GATT service opener.
- `headset-header`: performs one read of the dedicated headset-input UUID and
  returns only length/jack-state framing metadata. It does not subscribe to
  microphone notifications, store audio payloads, or change normal input CCCD.
- `configure-audio`: **writes** the single documented volatile `0x17/0x02`
  setup request and records its correlated reply. It uses the existing
  serialized command/response owner, queues intervening LED changes through
  the normal latest-state mechanism, and does not write firmware, association
  state, or audio-output payloads. An acknowledgement is not headphone playback.
- `stop-probe`: closes only this diagnostic pipe, not the controller or mapper.

Automatic profile idle/absolute disconnect is suppressed for the Bluetooth Pro
for this opt-in lab process lifetime, including between individual probes.
Manual Stop, disconnect and application exit still work. Saved profile settings
are not changed; normal launches retain their existing timeout behavior.

All pipe/file/radio work runs on a separate worker. The report-path hook updates
two counters without allocation. An idle probe generates no extra controller
traffic. A three-second probe deadline does not discard actual Windows operation
ownership: late operations drain before any successor probe or controller-service
disposal. No timed-out request is automatically replayed. A noncooperative radio
operation may leave the probe unavailable while the normal bounded teardown
quarantines its resources; it must not cause use-after-dispose.

Exit 0 means a valid non-error diagnostic reply, **not audio support**; 1 means
an error/failure and 2 invalid usage. Oversized/unrecognized raw pipe requests
are rejected without radio operations and may have their connection closed.

## Validation

Unit tests use fake GATT access and real local pipes, not physical controllers.
They cover repeated probes while reports advance, serialized setup/LED commands,
unrelated replies, exact setup bytes, bounded requests, cancellation/late-write
drain, and zero-allocation report observation. Physical acceptance additionally
requires the same controller generation and advancing reports before/after
repeated live queries. This feature is probing infrastructure; Bluetooth headphone
playback remains unimplemented pending output-framing/codec/delivery evidence.
