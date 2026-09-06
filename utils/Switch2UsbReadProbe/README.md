# Switch 2 USB passive read probe

This probe records only interrupt-IN reports from one physically attached
Nintendo Switch 2 Pro Controller (`057E:2069`). It does not issue feature,
output, WinUSB, initialization, calibration-memory, association, firmware, or
rumble requests.

The JSON Lines metadata omits the serial number and does not serialize or hash
the local HID device path. Short/error reads serialize only bytes actually
returned by Windows, with no uninitialized or stale buffer tail. Review
captured input bytes before sharing them; input includes sensors, power state,
and unknown fields that can fingerprint a session or device.

```powershell
dotnet run --project .\utils\Switch2UsbReadProbe\Switch2UsbReadProbe.csproj `
  -c Release -- --count 256 --output .\switch2-input.jsonl
```

If the device does not stream until initialized, the probe times out and exits.
That is an evidence result; this tool deliberately does not send an inferred
initialization sequence.

The QPC delta records read-completion order for deterministic replay. This
simple JSON capture tool is not a latency benchmark: filesystem serialization,
managed scheduling, queued HID reports, and the single-reader design are not
removed from its timing samples.
