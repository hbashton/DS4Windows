# Switch 2 Pro Bluetooth audio capability probe

Standalone Windows diagnostic, **not a Bluetooth headphone implementation**.
Run its published executable from the Desktop portable lab. It has no
DS4Windows/VIIPER runtime reference, driver install, pairing API, firmware or
memory-write command, GATT output/CCCD write, audio renderer, or recorder.

## Modes

- `--list`: list connected Switch 2 Pro Windows association-endpoint IDs.
  These IDs contain addresses; keep the output local.
- `--inspect <exact ID>`: require that exact currently connected Pro; query
  the Nintendo service uncached, request shared access, and list characteristic
  UUIDs/properties/handles and negotiated ATT value capacity.
- `--inspect-known <exact ID>`: allow an existing Windows ID that is currently
  disconnected. The device name must still identify a Switch 2 Pro. An
  uncached query can attempt reconnection; it never pairs or commits a bond.
- `--await-inspect <exact ID>`: if disconnected, wait up to 30 seconds for an
  advertisement with that exact address, Nintendo company ID, and validated
  Pro product/layout, then attempt the read-only inspection. No nearby-device
  payloads or remembered-host bytes are printed.
- `--wake-inspect <exact ID>`: **transmits** the reference's controller-specific
  wake manufacturer payload for two seconds, stops the publisher, then attempts
  inspection. This is explicitly opt-in, not part of the read-only modes.
  Windows controls the GAP Flags, so it cannot reproduce the console packet
  exactly. A publisher reporting Started does not prove controller wake.

The overall operation has a 20-second cancellation deadline, or 50 seconds
for `--await-inspect`. Windows owns underlying radio completion timing; these
are caller deadlines, not a guarantee of radio cancellation. The utility
exits after one operation. It does not leave a service or watcher running.
Exit codes: 0 for completed inventory, 1 for failure/cancellation, 2 for usage.
None of these codes is a headphone-delivery result.

Close DS4Windows gracefully first: b88 holds this GATT service exclusively.
SharingViolation is actionable ownership evidence, not an audio codec failure.
The controller may become unreachable after its last owner's connection closes.
Do not remove its association to work around that; wake it with A instead.

No input or microphone payload is read. In particular, ordinary Pro input
(`...0f8`) must not be decoded using the headset-input (`...0f9`) layout.
Headphone output (`...b06`) must never be confused with HD rumble (`...b05`).

## Source and verification

The wake manufacturer payload and advertisement layout follow
[ndeadly's documented interface](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md).
The [Windows publisher API](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher)
permits manufacturer data but reserves GAP Flags. No attempt is made to
bypass that restriction or change the adapter's identity.

Build and run the protocol tests (no physical-device tests are automatic):

```powershell
dotnet build utils/Switch2BluetoothAudioProbe -c Release
dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Switch2BluetoothAudioProbeTests
```

Tests cover exact wake-address byte order, invalid/broadcast address rejection,
advertisement validation, and separation from the production rumble UUID.
# Wake-only mode alongside DS4Windows

`--wake-only <exact Pro Windows ID>` emits the same bounded, targeted two-second
manufacturer wake advertisement and stops. Unlike the historical inspect modes,
it never discovers or opens a GATT service, so DS4Windows remains the connection
owner. Windows controls GAP Flags; Started/Stopped is not proof of controller
wake. No association, firmware or controller command is written.
