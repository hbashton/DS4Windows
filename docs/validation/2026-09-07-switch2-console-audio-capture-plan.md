# Switch 2 console headphone reference capture: feasibility and gates

Status: **not executed; dedicated sniffer unavailable**. The user confirmed
owning a Switch 2 console and explicitly confirmed having no BLE sniffer.
The live DS4Windows/VIIPER session and physical Pro connection remain untouched.
This plan is for obtaining missing protocol evidence, not a claim that Bluetooth
audio works or that a capture alone will finish implementation.

## Established capture route

A separate Nordic nRF52840 sniffer is supported by
[Nordic's setup instructions](https://academy.nordicsemi.com/courses/bluetooth-low-energy-fundamentals/lessons/lesson-6-bluetooth-le-sniffer/topic/nrf-sniffer-for-bluetooth-le/)
and demonstrated by the pinned Nintendo research's encrypted/decrypted Pro
pairing captures. The PC currently exposes only an RZ616 Bluetooth adapter;
no attached Nordic/J-Link USB device was found. The user subsequently confirmed
that no BLE sniffer is available. This capture route cannot be executed with the
currently established equipment. Do not repeat the hardware-availability
question or treat console ownership as permission for a controller handoff.

A sniffer is a prerequisite of this particular reference-capture route, not a
requirement for controller audio itself or proof that software implementation
is impossible. An existing decoded audio capture or a verified implementation
could supply the missing evidence without new hardware. Neither has been found
in the audited references. An analog-only console comparison would confirm
playback through the physical cable but would not reveal Bluetooth setup or
packet framing; it also requires a separately approved controller handoff.

Windows local HCI/ETW tracing cannot capture a link exchanged exclusively
between the console and controller. This is an explicit limitation in
[Wireshark's capture documentation](https://wiki.wireshark.org/CaptureSetup/Bluetooth),
not a reason to restart the existing observer or increase its logging.

## Encryption evidence, not an assumption

The pinned `captures/nrf52840/btle_procon2_pairing_encrypted.pcapng` contains
Nintendo's plaintext `15/04` key-component exchange at frames 416/419, then
link-encryption startup beginning at frame 433. This corroborates the
[documented custom pairing procedure](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md#pairing).
Nintendo derives its LTK from the exchanged components rather than an ordinary
SMP pairing transcript. Nordic's automatic pairing decoder must not be assumed
to recognize that custom exchange; it supports an
[explicit LTK for a bonded connection](https://docs.nordicsemi.com/r/bundle/nrfutil/page/nrfutil-ble-sniffer/guides/common_sniffing_actions.html/sniffing-a-connection-between-bonded-devices).

The console's bond key is not the PC's bond key. Keep any new user capture/key
local, do not print keys, commit them or include them in shared diagnostics.

## Execution order once prerequisites are available

1. Identify the exact dedicated sniffer model and its supported firmware/tool
   path. Do not flash an unidentified USB device, repurpose the RZ616, install
   unrelated drivers or purchase hardware automatically.
2. Prepare the separate sniffer without changing the Pro's active connection.
   Validate capture visibility and supported PHY handling before any handoff.
3. Obtain explicit user approval before moving this Pro from DS4Windows to the
   console, initiating console pairing, or changing an existing console bond.
   Leave DS4Windows and VIIPER running. A second physical Pro could avoid moving
   the current one, but its availability must not be assumed.
4. Capture the initial custom pairing/key exchange, derive that bond's LTK
   locally, then capture a fresh console reconnect from its connection/encryption
   establishment using the explicit key. Reject incomplete/decryption-failing
   capture rather than treating encrypted bytes as an audio codec.
5. Capture a short, known-working headphone session with the existing
   controller-AUX-to-PC-Line-In cable. Start console headphone volume low, avoid
   microphone capture, and confirm actual analog signal. Ordinary UI/game audio
   is enough to identify the transport initially; it does not prove channel
   isolation, precise gain or the later synthetic playback acceptance tests.
6. Recover discovery/handle identity, the complete enable/route sequence,
   headphone packet boundaries, counters/length fields, cadence and stopping.
   Do not assume this firmware uses the old reference's numeric handles.
7. Decode candidate captured output offline and corroborate it against analog
   delivery. Port only evidenced operations into the existing transport/mapper.
   A new command outside the loaded bridge requires a separately approved
   deployment; do not disguise commands as headphone payloads or inject code.
8. Resume DS4Windows verification with the reviewed quiet left/right tones,
   bounded Line-In capture, exact host-byte checks, stop/clipping checks and
   real input alongside audio. Console capture is evidence, not completion.

## Paths checked and rejected as immediate substitutes

- No documented stock-console HCI/ATT export was found. Switchbrew's internal
  services and original-Switch custom-firmware tooling are not a retail Switch 2
  capture interface.
- A WinRT mock Pro is not a quick recorder: it needs Nintendo discovery, startup
  responses, the custom-derived link key and compatible GATT layout. The public
  pairing API exposes no arbitrary LTK-import/key-request hook. The console
  reference performs actual link-layer encryption after the custom exchange.
- Separate WinRT manufacturer-data and GATT-service advertisers do not provide
  a documented way to reproduce one Nintendo connectable advertisement.
  [Microsoft documents their resource conflict](https://learn.microsoft.com/en-us/answers/questions/189891/windows10-uwp-app-conflict-ble-advertisement-and-g).
  Prior wake-publisher Started/Stopped results do not prove mock-controller
  connectability, paired encryption or one-radio coexistence.

The five available decoded/plaintext Pro donor captures were directly inspected
offline: pairing, reconnect, wake-console, common05 motion and Pro09 motion.
They contain no headphone-output/headset stream and no `17/02` or `18` audio
sequence. Discovery responses in the old motion captures must not be counted
as audio traffic merely because a handle number resembles the current firmware.
